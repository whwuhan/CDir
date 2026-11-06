using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using VirtualTerminal.Session;

namespace CDir.Services;

/// <summary>An owned PowerShell/ConPTY session. UI decoding stays on the dispatcher; pipe I/O never blocks it.</summary>
internal sealed class EmbeddedTerminalSession : TerminalSession, IDisposable
{
    private readonly Dispatcher Dispatcher;
    private readonly Channel<byte[]> Input = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly FileStream Writer;
    private readonly FileStream Reader;
    private readonly SafeFileHandle Process;
    private readonly SafeFileHandle Job;
    private readonly Task ReadTask;
    private readonly Task WriteTask;
    private readonly Task WaitTask;
    private IntPtr ConsoleHandle;
    private volatile bool Closing;
    private volatile bool Exited;
    private DispatcherTimer? ResizeTimer;
    private (ushort Columns, ushort Rows) PendingSize;
    private (ushort Columns, ushort Rows) ConsoleSize = (100, 20);
    internal int ProcessId { get; }
    internal bool HasExited => this.Exited;
    internal string InitialDirectory { get; }
    internal Task CleanupTask { get; private set; } = Task.CompletedTask;

    internal EmbeddedTerminalSession(string Directory, Dispatcher Dispatcher)
    {
        this.Dispatcher = Dispatcher;
        this.InitialDirectory = Path.GetFullPath(Directory);
        if (!System.IO.Directory.Exists(this.InitialDirectory)) throw new DirectoryNotFoundException(this.InitialDirectory);
        var Shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        this.Title = "PowerShell";
        SafeFileHandle? Job = null, Process = null;
        FileStream? Reader = null, Writer = null;
        Task? ReadTask = null;
        try
        {
            if (!CreatePipe(out var InputRead, out var InputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
            using (InputRead)
            {
                Writer = new FileStream(InputWrite, FileAccess.Write, 4096, isAsync: false);
                this.Writer = Writer;
                if (!CreatePipe(out var OutputRead, out var OutputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
                using (OutputWrite)
                {
                    // Streams own the host ends after construction; ConPTY duplicates its own ends.
                    Reader = new FileStream(OutputRead, FileAccess.Read, 8192, isAsync: false);
                    Marshal.ThrowExceptionForHR(CreatePseudoConsole(new(100, 20), InputRead.DangerousGetHandle(), OutputWrite.DangerousGetHandle(), 0, out this.ConsoleHandle));
                    this.Writer = Writer;
                    this.Reader = Reader;
                    // Drain before starting a client, including on failure, so ClosePseudoConsole cannot deadlock on output.
                    ReadTask = Task.Run(this.ReadOutputAsync);
                    this.ReadTask = ReadTask;
                    Job = CreateJobObject(IntPtr.Zero, null);
                    if (Job.IsInvalid) throw new Win32Exception();
                    var Limits = new JobLimits { Basic = new() { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE, no CPU or memory caps.
                    if (!SetInformationJobObject(Job, 9, ref Limits, (uint)Marshal.SizeOf<JobLimits>())) throw new Win32Exception();
                    this.Job = Job;
                    var Info = StartProcess(Shell, this.InitialDirectory, this.ConsoleHandle);
                    Process = new SafeFileHandle(Info.Process, ownsHandle: true);
                    this.Process = Process;
                    this.ProcessId = (int)Info.ProcessId;
                    try
                    {
                        // The child also inherits any outer QuietRunner job. Never break away from that protection.
                        if (!AssignProcessToJobObject(Job, Process)) throw new Win32Exception();
                        if (ResumeThread(Info.Thread) == uint.MaxValue) throw new Win32Exception();
                    }
                    catch { TerminateProcess(Process, 1); throw; }
                    finally { CloseHandle(Info.Thread); }
                    this.WriteTask = Task.Run(this.WriteInputAsync);
                    this.WaitTask = Task.Run(() =>
                    {
                        WaitForSingleObject(Process, uint.MaxValue);
                        this.Exited = true;
                        if (!this.Closing && !Dispatcher.HasShutdownStarted)
                            Dispatcher.BeginInvoke(() => { if (!this.Closing) this.NotifyDisconnected(); });
                    });
                }
            }
        }
        catch
        {
            this.Closing = true;
            Job?.Dispose();
            var Console = this.ConsoleHandle;
            this.ConsoleHandle = IntPtr.Zero;
            this.CleanupTask = Task.Run(async () =>
            {
                if (Console != IntPtr.Zero) ClosePseudoConsole(Console);
                if (ReadTask is not null) await ReadTask;
                Reader?.Dispose(); Writer?.Dispose(); Process?.Dispose();
            });
            throw;
        }
    }

    private async Task ReadOutputAsync()
    {
        var Bytes = new byte[8192];
        try
        {
            while (true)
            {
                var Count = await this.Reader.ReadAsync(Bytes);
                if (Count == 0) return;
                if (this.Closing || this.Dispatcher.HasShutdownStarted) continue;
                var Chunk = Bytes.AsSpan(0, Count).ToArray();
                try { await this.Dispatcher.InvokeAsync(() =>
                {
                    if (this.Closing) return;
                    this.Decoder.Write(Chunk);
                    this.NotifyBufferUpdated();
                    // Send terminal query replies (cursor position, capabilities) back to the client.
                    var Reply = this.ReadAll();
                    if (Reply.Length > 0) this.Write(Reply);
                }, DispatcherPriority.Background); }
                catch (OperationCanceledException) { /* Keep draining if the dispatcher shuts down during ConPTY closure. */ }
            }
        }
        catch (Exception Error) when (Error is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private async Task WriteInputAsync()
    {
        try
        {
            await foreach (var Bytes in this.Input.Reader.ReadAllAsync())
            {
                if (this.Closing) return;
                await this.Writer.WriteAsync(Bytes);
                // Flush promptly for the first key, but drain already queued
                // repeats in order instead of scheduling a flush per character.
                var Buffered = Bytes.Length;
                while (Buffered < 65536 && this.Input.Reader.TryRead(out var More))
                {
                    if (this.Closing) return;
                    await this.Writer.WriteAsync(More);
                    Buffered += More.Length;
                }
                await this.Writer.FlushAsync();
            }
        }
        catch (Exception Error) when (Error is IOException or ObjectDisposedException) { }
    }

    public override void Write(ReadOnlySpan<byte> Data)
    {
        if (!this.Closing && !this.Exited && !Data.IsEmpty) this.Input.Writer.TryWrite(Data.ToArray());
    }

    internal event EventHandler? ViewportResized;

    public override void Resize(ushort Columns, ushort Rows, bool PushScrollback = false)
    {
        if (this.Closing) return;
        Columns = Math.Clamp(Columns, (ushort)10, (ushort)1000);
        Rows = Math.Clamp(Rows, (ushort)3, (ushort)500);
        this.PendingSize = (Columns, Rows);
        this.ResizeTimer?.Stop();
        if (this.Buffer.Columns == Columns && this.Buffer.Rows == Rows && (this.Exited || this.ConsoleSize == this.PendingSize)) return;
        // Font changes call Resize synchronously for every wheel notch. Coalesce
        // decoder and ConPTY resizing together so PSReadLine receives one final
        // grid, rather than repeatedly reflowing and redrawing its path prompt.
        this.ResizeTimer ??= new DispatcherTimer(DispatcherPriority.Background, this.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(150) };
        this.ResizeTimer.Tick -= this.ApplyPendingResize;
        this.ResizeTimer.Tick += this.ApplyPendingResize;
        this.ResizeTimer.Start();
    }

    private void ApplyPendingResize(object? Sender, EventArgs E)
    {
        this.ResizeTimer?.Stop();
        if (this.Closing) return;
        var (Columns, Rows) = this.PendingSize;
        base.Resize(Columns, Rows, false);
        if (!this.Exited)
        {
            var Result = ResizePseudoConsole(this.ConsoleHandle, new(Columns, Rows));
            // An exit racing a layout change must not surface as an application error.
            if (Result < 0 && WaitForSingleObject(this.Process, 0) != 0) Marshal.ThrowExceptionForHR(Result);
            this.ConsoleSize = (Columns, Rows);
        }
        this.ViewportResized?.Invoke(this, EventArgs.Empty);
    }

    public new void Dispose()
    {
        if (this.Closing) return;
        this.Closing = true;
        this.ResizeTimer?.Stop();
        this.Input.Writer.TryComplete();
        this.Job.Dispose(); // Only this terminal's process tree belongs to this job.
        var Console = this.ConsoleHandle;
        this.ConsoleHandle = IntPtr.Zero;
        this.CleanupTask = Task.Run(async () =>
        {
            try
            {
                await this.WaitTask;
                // Keep draining while ConPTY closes, even when output was pending at disposal.
                if (Console != IntPtr.Zero) ClosePseudoConsole(Console);
                await this.ReadTask;
                await this.WriteTask;
            }
            finally { this.Reader.Dispose(); this.Writer.Dispose(); this.Process.Dispose(); }
        });
        base.Dispose();
    }

    void IDisposable.Dispose() => this.Dispose();
    protected override void Dispose(bool Disposing) { }

    private static ProcessInfo StartProcess(string Shell, string Directory, IntPtr Console)
    {
        nuint Size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref Size);
        var Attributes = Marshal.AllocHGlobal(checked((int)Size));
        var Initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(Attributes, 1, 0, ref Size)) throw new Win32Exception();
            Initialized = true;
            if (!UpdateProcThreadAttribute(Attributes, 0, (IntPtr)0x20016, Console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            // Prevent redirected host stdio (including QuietRunner's pipes) from overriding ConPTY.
            var Startup = new StartupInfoEx { Info = new() { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100 }, Attributes = Attributes };
            // ConPTY hosts do not always auto-load PSReadLine (for example when accessibility is enabled).
            // Let the shell own editing/history so arrows still reach programs running inside it.
            // Keep history local to this session rather than sharing the user's external console history.
            // A startup file also keeps the initialization commands out of interactive history.
            var StartupFile = Path.Combine(AppContext.BaseDirectory, "Config", "TerminalStartup.ps1");
            var Command = new StringBuilder('"' + Shell + "\" -NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -File \"" + StartupFile + "\"");
            if (!CreateProcess(Shell, Command, IntPtr.Zero, IntPtr.Zero,
                false, 0x80000 | 0x4, IntPtr.Zero, Directory, ref Startup, out var Process)) throw new Win32Exception();
            return Process;
        }
        finally { if (Initialized) DeleteProcThreadAttributeList(Attributes); Marshal.FreeHGlobal(Attributes); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Coord
    {
        public short X, Y;
        public Coord(int X, int Y) { this.X = (short)X; this.Y = (short)Y; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicJobLimits
    {
        public long ProcessTime, JobTime; public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits
    {
        public BasicJobLimits Basic; public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle Read, out SafeFileHandle Write, IntPtr Security, uint Size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord Size, IntPtr Input, IntPtr Output, uint Flags, out IntPtr Console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr Console, Coord Size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr Console);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr List, int Count, uint Flags, ref nuint Size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr List, uint Flags, IntPtr Attribute, IntPtr Value, nuint Size, IntPtr Previous, IntPtr Result);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr List);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string Application, StringBuilder Command, IntPtr ProcessSecurity, IntPtr ThreadSecurity, [MarshalAs(UnmanagedType.Bool)] bool Inherit, uint Flags, IntPtr Environment, string Directory, ref StartupInfoEx Startup, out ProcessInfo Process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr Security, string? Name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle Job, int Kind, ref JobLimits Info, uint Size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle Job, SafeFileHandle Process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr Thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeFileHandle Process, uint Milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeFileHandle Process, uint ExitCode);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr Handle);
}
