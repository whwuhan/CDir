using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CDir.Testing;

/// <summary>A fail-closed process-tree budget for development work, never used by the normal app.</summary>
internal static class QuietRunner
{
    private const uint CreateSuspended = 0x00000004;
    private const uint IdlePriorityClass = 0x00000040;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint KillOnJobClose = 0x00002000;
    private const uint LimitPriorityClass = 0x00000020;
    private const uint ProcessModeBackgroundBegin = 0x00100000;
    private const uint WaitTimeout = 0x00000102;
    private static volatile bool Cancelled;

    public static int Main(string[] Arguments)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("QuietRunner requires Windows.");
            if (Arguments.Length == 1 && Arguments[0] is "--help" or "-h" or "--?")
            {
                Console.WriteLine("QuietRunner --exe <absolute executable> --workdir <absolute directory> [--cpu-percent 0..100] [--timeout-seconds 1..86400] [--temp-root <existing directory>] -- <child arguments>");
                Console.WriteLine("Default: no CPU rate cap or affinity restriction, idle/background scheduling, serialized per user. Set --cpu-percent 1..100 only when a CPU hard cap is explicitly wanted. Timeout starts when the child resumes. Ctrl+C cancels running or queued work.");
                return 0;
            }
            var Options = Parse(Arguments);
            ConsoleCancelEventHandler CancelHandler = (_, Event) => { Event.Cancel = true; Cancelled = true; };
            Console.CancelKeyPress += CancelHandler;
            try { return Run(Options); }
            finally { Console.CancelKeyPress -= CancelHandler; }
        }
        catch (Exception Error) when (Error is ArgumentException or InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine("QuietRunner: " + Error.Message);
            return 2;
        }
    }

    private static Options Parse(string[] Arguments)
    {
        string? Executable = null, WorkingDirectory = null, TempRoot = null;
        var CpuPercent = 0;
        var TimeoutSeconds = 300;
        var Index = 0;
        for (; Index < Arguments.Length; Index++)
        {
            var Argument = Arguments[Index];
            if (Argument == "--") { Index++; break; }
            if (++Index >= Arguments.Length) throw new ArgumentException("Missing value for " + Argument);
            var Value = Arguments[Index];
            switch (Argument)
            {
                case "--exe": Executable = Value; break;
                case "--workdir": WorkingDirectory = Value; break;
                case "--temp-root": TempRoot = Value; break;
                case "--cpu-percent":
                    if (!int.TryParse(Value, out CpuPercent) || CpuPercent is < 0 or > 100)
                        throw new ArgumentException("--cpu-percent must be between 0 and 100 (0 means no CPU rate cap).");
                    break;
                case "--timeout-seconds":
                    if (!int.TryParse(Value, out TimeoutSeconds) || TimeoutSeconds is < 1 or > 86400)
                        throw new ArgumentException("--timeout-seconds must be between 1 and 86400.");
                    break;
                default: throw new ArgumentException("Unknown option: " + Argument);
            }
        }
        if (Executable is null || !Path.IsPathFullyQualified(Executable) || !File.Exists(Executable))
            throw new ArgumentException("--exe must name an existing absolute executable path.");
        if (WorkingDirectory is null || !Path.IsPathFullyQualified(WorkingDirectory) || !Directory.Exists(WorkingDirectory))
            throw new ArgumentException("--workdir must name an existing absolute directory.");
        if (TempRoot is not null && (!Path.IsPathFullyQualified(TempRoot) || !Directory.Exists(TempRoot)))
            throw new ArgumentException("--temp-root must name an existing absolute directory; the caller owns its cleanup.");
        return new(Path.GetFullPath(Executable), Path.GetFullPath(WorkingDirectory), CpuPercent, TimeoutSeconds,
            TempRoot is null ? null : Path.GetFullPath(TempRoot), Arguments[Index..]);
    }

    private static int Run(Options Options)
    {
        // Background mode lowers this wrapper's CPU, I/O and memory scheduling priorities. It affects
        // no pre-existing process; children also receive explicit job and process priorities below.
        if (!Native.SetPriorityClass(Native.GetCurrentProcess(), ProcessModeBackgroundBegin))
            Console.Error.WriteLine("QuietRunner: background mode unavailable; child startup will require explicit idle and low I/O priorities.");
        if (!Native.SetPriorityClass(Native.GetCurrentProcess(), IdlePriorityClass))
            throw LastError("Cannot lower the runner priority");

        using var Identity = WindowsIdentity.GetCurrent();
        var UserSid = Identity.User?.Value ?? throw new InvalidOperationException("Cannot identify the current user for the test queue.");
        using var Gate = new Mutex(false, "Local\\CDirQuietRunner-" + UserSid);
        var Held = false;
        try
        {
            while (!Held)
            {
                if (Cancelled) return 130;
                try { Held = Gate.WaitOne(200); }
                catch (AbandonedMutexException) { Held = true; }
            }
            if (Cancelled) return 130;
            return RunExclusive(Options);
        }
        finally { if (Held) Gate.ReleaseMutex(); }
    }

    private static int RunExclusive(Options Options)
    {
        using var Job = Native.CreateJobObject(IntPtr.Zero, null);
        if (Job.IsInvalid) throw LastError("Cannot create the resource job");
        var Limits = new JobExtendedLimits
        {
            BasicLimitInformation = new JobBasicLimits
            {
                LimitFlags = KillOnJobClose | LimitPriorityClass,
                PriorityClass = IdlePriorityClass
            }
        };
        SetJob(Job, 9, Limits, "Cannot apply process-tree lifetime and priority limits");
        if (Options.CpuPercent > 0)
        {
            var Rate = new JobCpuRate { ControlFlags = 0x1 | 0x4, CpuRate = (uint)Options.CpuPercent * 100 };
            SetJob(Job, 15, Rate, "Cannot enforce the requested CPU hard cap");
        }

        using var Handles = new InheritedStandardHandles();
        using var Attributes = new StartupAttributes(Handles.Values);
        var Startup = new StartupInfoEx
        {
            StartupInfo = new StartupInfo
            {
                Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                // CREATE_NO_WINDOW already hides console children. Do not force SW_HIDE on WPF windows.
                Flags = 0x100,
                StandardInput = Handles.Values[0], StandardOutput = Handles.Values[1], StandardError = Handles.Values[2]
            },
            Attributes = Attributes.Pointer
        };
        var CommandLine = new StringBuilder(string.Join(" ", new[] { Options.Executable }.Concat(Options.Arguments).Select(QuoteArgument)));
        if (CommandLine.Length >= 32767) throw new ArgumentException("The native command line is too long.");
        var EnvironmentBlock = BuildEnvironment(Options.TempRoot);
        ProcessInfo Process = default;
        var Created = false;
        try
        {
            var Flags = CreateSuspended | IdlePriorityClass | CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow;
            if (!Native.CreateProcess(Options.Executable, CommandLine, IntPtr.Zero, IntPtr.Zero, true, Flags,
                EnvironmentBlock, Options.WorkingDirectory, ref Startup, out Process))
                throw LastError("Cannot create the bounded child process");
            Created = true;
            if (!Native.AssignProcessToJobObject(Job, Process.Process))
                throw LastError("Cannot put the suspended child in the resource job; refusing an unlimited run");

            uint VeryLowIo = 0;
            var IoResult = Native.NtSetInformationProcess(Process.Process, 33, ref VeryLowIo, sizeof(uint));
            if (IoResult != 0) throw new InvalidOperationException($"Cannot lower child I/O priority (NTSTATUS 0x{IoResult:X8}); refusing an unrestricted I/O run.");
            // Keep normal page priority: inheriting the wrapper's background memory
            // policy makes WPF startup thrash even when ample physical RAM is free.
            var Memory = new MemoryPriority { Priority = 5 };
            if (!Native.SetProcessInformation(Process.Process, 0, ref Memory, (uint)Marshal.SizeOf<MemoryPriority>()))
                Console.Error.WriteLine("QuietRunner: normal memory priority unavailable.");

            if (Native.ResumeThread(Process.Thread) == uint.MaxValue) throw LastError("Cannot resume the bounded child");
            Native.CloseHandle(Process.Thread);
            Process.Thread = IntPtr.Zero;
            var CpuDescription = Options.CpuPercent == 0 ? "no CPU rate cap" : $"CPU <= {Options.CpuPercent}% for the child tree";
            Console.Error.WriteLine($"QuietRunner: {CpuDescription}; no affinity restriction; idle/background priority; desktop=current.");
            var Timer = Stopwatch.StartNew();
            while (true)
            {
                if (Cancelled) { Console.Error.WriteLine("QuietRunner: cancelled; stopping its child tree."); return 130; }
                if (Timer.Elapsed.TotalSeconds >= Options.TimeoutSeconds)
                {
                    Console.Error.WriteLine("QuietRunner: timeout; stopping its child tree.");
                    return 124;
                }
                var Wait = Native.WaitForSingleObject(Process.Process, 100);
                if (Wait == 0)
                {
                    if (!Native.GetExitCodeProcess(Process.Process, out var ExitCode)) throw LastError("Cannot read the child exit code");
                    return unchecked((int)ExitCode);
                }
                if (Wait != WaitTimeout) throw LastError("Cannot wait for the child process");
            }
        }
        finally
        {
            if (Created)
            {
                // Terminate only this newly-created job tree, including compiler servers that might
                // otherwise outlive a successful command. Never enumerate or stop unrelated processes.
                Native.TerminateJobObject(Job, 0xC000013A);
                // Assignment may have failed: a suspended root still belongs to this invocation.
                Native.TerminateProcess(Process.Process, 0xC000013A);
                Native.WaitForSingleObject(Process.Process, 3000);
                var Deadline = Environment.TickCount64 + 3000;
                while (Environment.TickCount64 < Deadline && Native.QueryInformationJobObject(Job, 1, out var Accounting,
                    (uint)Marshal.SizeOf<JobAccounting>(), IntPtr.Zero) && Accounting.ActiveProcesses != 0) Thread.Sleep(20);
                if (Process.Thread != IntPtr.Zero) Native.CloseHandle(Process.Thread);
                Native.CloseHandle(Process.Process);
            }
            Job.Dispose(); // Kill-on-close is also the fail-safe if cancellation interrupts cleanup.
        }
    }

    private static void SetJob<T>(KernelHandle Job, int Kind, T Value, string Failure) where T : struct
    {
        var Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(Value, Pointer, false);
            if (!Native.SetInformationJobObject(Job, Kind, Pointer, (uint)Marshal.SizeOf<T>())) throw LastError(Failure);
        }
        finally { Marshal.FreeHGlobal(Pointer); }
    }

    private static string BuildEnvironment(string? TempRoot)
    {
        var Values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry Entry in Environment.GetEnvironmentVariables()) Values[(string)Entry.Key] = (string?)Entry.Value ?? "";
        if (TempRoot is not null) { Values["TEMP"] = TempRoot; Values["TMP"] = TempRoot; }
        Values["CDIR_QUIET_RUNNER"] = "1";
        Values["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        Values["MSBUILDDISABLENODEREUSE"] = "1";
        Values["UseSharedCompilation"] = "false";
        return string.Join('\0', Values.Select(Pair => Pair.Key + "=" + Pair.Value)) + "\0\0";
    }

    private static string QuoteArgument(string Argument)
    {
        if (Argument.Length > 0 && !Argument.Any(Character => char.IsWhiteSpace(Character) || Character == '"')) return Argument;
        var Result = new StringBuilder("\"");
        var Backslashes = 0;
        foreach (var Character in Argument)
        {
            if (Character == '\\') { Backslashes++; continue; }
            Result.Append('\\', Character == '"' ? Backslashes * 2 + 1 : Backslashes);
            Backslashes = 0;
            Result.Append(Character);
        }
        Result.Append('\\', Backslashes * 2);
        return Result.Append('"').ToString();
    }

    private static Win32Exception LastError(string Message) => new(Marshal.GetLastWin32Error(), Message);
    private sealed record Options(string Executable, string WorkingDirectory, int CpuPercent, int TimeoutSeconds, string? TempRoot, string[] Arguments);

    private sealed class InheritedStandardHandles : IDisposable
    {
        public IntPtr[] Values { get; } = new IntPtr[3];
        public InheritedStandardHandles()
        {
            try
            {
                for (var Index = 0; Index < 3; Index++)
                {
                    var Existing = Native.GetStdHandle(-10 - Index);
                    if (Existing != IntPtr.Zero && Existing != new IntPtr(-1))
                    {
                        if (!Native.DuplicateHandle(Native.GetCurrentProcess(), Existing, Native.GetCurrentProcess(), out var Copy, 0, true, 2))
                            throw LastError("Cannot duplicate a standard stream");
                        Values[Index] = Copy;
                    }
                    else
                    {
                        var Security = new SecurityAttributes { Size = (uint)Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
                        Values[Index] = Native.CreateFile("NUL", Index == 0 ? 0x80000000u : 0x40000000u, 3, ref Security, 3, 0x80, IntPtr.Zero);
                        if (Values[Index] == new IntPtr(-1)) throw LastError("Cannot open a missing standard stream");
                    }
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            for (var Index = 0; Index < Values.Length; Index++)
                if (Values[Index] != IntPtr.Zero && Values[Index] != new IntPtr(-1)) { Native.CloseHandle(Values[Index]); Values[Index] = IntPtr.Zero; }
        }
    }

    private sealed class StartupAttributes : IDisposable
    {
        public IntPtr Pointer { get; private set; }
        private IntPtr Handles;
        private bool Initialized;
        public StartupAttributes(IntPtr[] InheritedHandles)
        {
            nuint Size = 0;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref Size);
            Pointer = Marshal.AllocHGlobal(checked((int)Size));
            try
            {
                if (!Native.InitializeProcThreadAttributeList(Pointer, 1, 0, ref Size)) throw LastError("Cannot initialize inherited handle restrictions");
                Initialized = true;
                Handles = Marshal.AllocHGlobal(InheritedHandles.Length * IntPtr.Size);
                Marshal.Copy(InheritedHandles, 0, Handles, InheritedHandles.Length);
                if (!Native.UpdateProcThreadAttribute(Pointer, 0, 0x00020002, Handles, (nuint)(InheritedHandles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                    throw LastError("Cannot restrict inherited handles");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                if (Initialized) Native.DeleteProcThreadAttributeList(Pointer);
                Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero; Initialized = false;
            }
            if (Handles != IntPtr.Zero) { Marshal.FreeHGlobal(Handles); Handles = IntPtr.Zero; }
        }
    }

    private sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private KernelHandle() : base(true) { }
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimits
    {
        public JobBasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobCpuRate { public uint ControlFlags, CpuRate; }
    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryPriority { public uint Priority; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    {
        public uint Size;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public uint Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW")] internal static extern KernelHandle CreateJobObject(IntPtr Attributes, string? Name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(KernelHandle Job, int Kind, IntPtr Information, uint Length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(KernelHandle Job, IntPtr Process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(KernelHandle Job, uint ExitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryInformationJobObject(KernelHandle Job, int Kind, out JobAccounting Information, uint Length, IntPtr ReturnLength);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetPriorityClass(IntPtr Process, uint Priority);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr GetStdHandle(int Kind);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DuplicateHandle(IntPtr SourceProcess, IntPtr Source, IntPtr TargetProcess, out IntPtr Target, uint Access, [MarshalAs(UnmanagedType.Bool)] bool Inherit, uint Options);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")] internal static extern IntPtr CreateFile(string Path, uint Access, uint Sharing, ref SecurityAttributes Security, uint Creation, uint Flags, IntPtr Template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr Handle);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(IntPtr List, uint Count, uint Flags, ref nuint Size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(IntPtr List, uint Flags, nuint Attribute, IntPtr Value, nuint Size, IntPtr Previous, IntPtr ReturnSize);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr List);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcess(string Application, StringBuilder CommandLine, IntPtr ProcessAttributes, IntPtr ThreadAttributes, [MarshalAs(UnmanagedType.Bool)] bool InheritHandles, uint Flags, [MarshalAs(UnmanagedType.LPWStr)] string EnvironmentBlock, string Directory, ref StartupInfoEx Startup, out ProcessInfo Process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr Thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr Handle, uint Milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(IntPtr Process, out uint ExitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(IntPtr Process, uint ExitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetProcessInformation(IntPtr Process, int Kind, ref MemoryPriority Information, uint Length);
        [DllImport("ntdll.dll")] internal static extern uint NtSetInformationProcess(IntPtr Process, int Kind, ref uint Information, uint Length);
    }
}
