using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CDir.Services;

internal sealed class ShellFolderWatcher : IDisposable
{
    private readonly HwndSource Source;
    private readonly Action Changed;
    private IntPtr Folder;
    private uint Registration;
    private const int ChangeMessage = 0x8000 + 0xCD1;

    internal ShellFolderWatcher(Window Owner, string Location, Action Changed)
    {
        this.Changed = Changed;
        this.Source = HwndSource.FromHwnd(new WindowInteropHelper(Owner).Handle) ?? throw new InvalidOperationException("A Shell watcher requires an initialized window.");
        this.Folder = RecycleBinService.GetIdList(Location);
        var Entry = new NotifyEntry { Folder = this.Folder, Recursive = true };
        this.Registration = SHChangeNotifyRegister(this.Source.Handle, 3, 0x7FFFFFFF, ChangeMessage, 1, ref Entry);
        if (this.Registration == 0)
        {
            Marshal.FreeCoTaskMem(this.Folder);
            this.Folder = IntPtr.Zero;
            throw new System.ComponentModel.Win32Exception();
        }
        this.Source.AddHook(this.Message);
    }

    private IntPtr Message(IntPtr Window, int Message, IntPtr WParam, IntPtr LParam, ref bool Handled)
    {
        if (Message == ChangeMessage && this.Registration != 0) this.Changed();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        this.Source.RemoveHook(this.Message);
        if (this.Registration != 0) { SHChangeNotifyDeregister(this.Registration); this.Registration = 0; }
        if (this.Folder != IntPtr.Zero) { Marshal.FreeCoTaskMem(this.Folder); this.Folder = IntPtr.Zero; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NotifyEntry
    {
        public IntPtr Folder;
        [MarshalAs(UnmanagedType.Bool)] public bool Recursive;
    }
    [DllImport("shell32.dll", SetLastError = true)] private static extern uint SHChangeNotifyRegister(IntPtr Window, int Sources, int Events, uint Message, int Count, ref NotifyEntry Entry);
    [DllImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SHChangeNotifyDeregister(uint Id);
}
