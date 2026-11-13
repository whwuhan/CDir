using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Security.Principal;
using System.IO;

namespace CDir.Services;

// Use the verified interactive shell's normal runtime context for the helper.
// Tests retain an owned kill-on-close job, idle CPU and very-low I/O scheduling.
internal static class DesktopIntegrationProcess
{
    internal static Lifetime Start(string Executable, params string[] Arguments)
    {
        var Shell=OpenInteractiveShell();
        nuint Size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref Size);
        var Attributes = Marshal.AllocHGlobal((int)Size);
        var Policy = Marshal.AllocHGlobal(sizeof(int));
        var Parent=Marshal.AllocHGlobal(IntPtr.Size);
        IntPtr Job=IntPtr.Zero;
        var Initialized = false;
        ProcessInfo Process = default;
        try
        {
            if (!InitializeProcThreadAttributeList(Attributes, 2, 0, ref Size)) throw new Win32Exception();
            Initialized = true;
            Marshal.WriteInt32(Policy, 1); // PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE
            if (!UpdateProcThreadAttribute(Attributes, 0, (IntPtr)0x20012, Policy, sizeof(int), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            Marshal.WriteIntPtr(Parent,Shell);
            if(!UpdateProcThreadAttribute(Attributes,0,(IntPtr)0x20000,Parent,(nuint)IntPtr.Size,IntPtr.Zero,IntPtr.Zero)) throw new Win32Exception();
            var Startup = new StartupInfoEx { Info = new() { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 1, ShowWindow = 0 }, Attributes = Attributes };
            var Quiet = Environment.GetEnvironmentVariable("CDIR_QUIET_RUNNER") == "1";
            var Command = new StringBuilder(string.Join(" ", new[] { Executable }.Concat(Arguments).Select(Quote)));
            if (!CreateProcess(Executable, Command, IntPtr.Zero, IntPtr.Zero, false, 0x80000 | 0x08000000 | 0x4 | (Quiet ? 0x40u : 0),
                IntPtr.Zero, System.IO.Path.GetDirectoryName(Executable)!, ref Startup, out Process)) throw new Win32Exception();
            if (Quiet)
            {
                Job=CreateJobObject(IntPtr.Zero,null);
                if(Job==IntPtr.Zero) throw new Win32Exception();
                var Limits=new JobLimits { Basic=new(){Flags=0x2000|0x20,Priority=0x40} };
                if(!SetInformationJobObject(Job,9,ref Limits,(uint)Marshal.SizeOf<JobLimits>()) || !AssignProcessToJobObject(Job,Process.Process)) throw new Win32Exception();
                if (!IsProcessInJob(Process.Process, IntPtr.Zero, out var InJob) || !InJob)
                    throw new InvalidOperationException("The integration test helper must remain in the runner's owned job.");
                uint Io = 0;
                if (NtSetInformationProcess(Process.Process, 33, ref Io, sizeof(uint)) != 0)
                    throw new InvalidOperationException("The integration test helper requires very-low I/O priority.");
                var Memory = new MemoryPriority { Priority = 5 };
                if (!SetProcessInformation(Process.Process, 0, ref Memory, sizeof(uint))) throw new Win32Exception();
            }
            if (ResumeThread(Process.Thread) == uint.MaxValue) throw new Win32Exception();
            var Result=new Lifetime(Process.Process,Job);
            Process.Process=IntPtr.Zero; Job=IntPtr.Zero;
            return Result;
        }
        catch
        {
            if (Process.Process != IntPtr.Zero) TerminateProcess(Process.Process, 1);
            throw;
        }
        finally
        {
            if (Process.Thread != IntPtr.Zero) CloseHandle(Process.Thread);
            if (Process.Process != IntPtr.Zero) CloseHandle(Process.Process);
            if(Job!=IntPtr.Zero) CloseHandle(Job);
            if (Initialized) DeleteProcThreadAttributeList(Attributes);
            Marshal.FreeHGlobal(Policy); Marshal.FreeHGlobal(Attributes); Marshal.FreeHGlobal(Parent); CloseHandle(Shell);
        }
    }
    internal sealed class Lifetime(IntPtr Process,IntPtr Job):IDisposable
    {
        internal void Wait() => WaitForSingleObject(Process,3000);
        public void Dispose(){CloseHandle(Process);if(Job!=IntPtr.Zero)CloseHandle(Job);}
    }
    private static IntPtr OpenInteractiveShell()
    {
        using var Current=Process.GetCurrentProcess();
        using var CurrentIdentity=WindowsIdentity.GetCurrent();
        var Expected=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe");
        foreach(var Peer in Process.GetProcessesByName("explorer"))
        using(Peer)
        {
            try
            {
                if(Peer.SessionId!=Current.SessionId || !string.Equals(Peer.MainModule?.FileName,Expected,StringComparison.OrdinalIgnoreCase)) continue;
                var Handle=OpenProcess(0x1080,false,(uint)Peer.Id);
                if(Handle==IntPtr.Zero) continue;
                var Owned=false;
                try
                {
                    if(OpenProcessToken(Handle,8,out var Token))
                    {
                        try{using var Identity=new WindowsIdentity(Token);Owned=Identity.User==CurrentIdentity.User;}
                        finally{CloseHandle(Token);}
                    }
                    if(Owned) return Handle;
                }
                finally{if(!Owned)CloseHandle(Handle);}
            }
            catch(Exception Error) when(Error is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
        }
        throw new IOException("The current user's Windows Shell could not be verified for registry integration.");
    }
    private static string Quote(string Value)
        => '"' + System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(Value, "(\\\\*)\"", "$1$1\\\""), "(\\\\+)$", "$1$1") + '"';
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo { public int Size; public IntPtr Reserved, Desktop, Title; public uint X,Y,Width,Height,CharsX,CharsY,Fill,Flags; public short ShowWindow,ReservedSize; public IntPtr ReservedData,Input,Output,Error; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process,Thread; public uint Id,ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryPriority { public uint Priority; }
    [StructLayout(LayoutKind.Sequential)] private struct JobBasic { public long ProcessTime,JobTime;public uint Flags;public nuint Min,Max;public uint Active;public nuint Affinity;public uint Priority,Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public JobBasic Basic;public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;public nuint ProcessMemory,JobMemory,PeakProcess,PeakJob; }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr OpenProcess(uint Access,[MarshalAs(UnmanagedType.Bool)] bool Inherit,uint Id);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr Process,uint Access,out IntPtr Token);
    [DllImport("kernel32.dll",EntryPoint="CreateJobObjectW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr Security,string? Name);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr Job,int Class,ref JobLimits Limits,uint Size);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr Job,IntPtr Process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr Object,uint Milliseconds);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr List,int Count,uint Flags,ref nuint Size);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr List,uint Flags,IntPtr Attribute,IntPtr Value,nuint Size,IntPtr Previous,IntPtr Result);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr List);
    [DllImport("kernel32.dll", EntryPoint="CreateProcessW", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string Application,StringBuilder Command,IntPtr ProcessSecurity,IntPtr ThreadSecurity,[MarshalAs(UnmanagedType.Bool)] bool Inherit,uint Flags,IntPtr Environment,string Directory,ref StartupInfoEx Startup,out ProcessInfo Process);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr Process,IntPtr Job,[MarshalAs(UnmanagedType.Bool)] out bool Result);
    [DllImport("ntdll.dll")] private static extern int NtSetInformationProcess(IntPtr Process,int Class,ref uint Value,int Size);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetProcessInformation(IntPtr Process,int Class,ref MemoryPriority Value,int Size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint ResumeThread(IntPtr Thread);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr Process,uint Code);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr Handle);
}
