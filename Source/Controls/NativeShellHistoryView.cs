using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CDir.Controls;

internal sealed partial class NativeShellView
{
    // Folder extension menus omit view commands such as Undo. Obtain the menu
    // from a real Shell view, with its own hidden host and message pump instead.
    internal static void WithHistoryMenu(IntPtr Owner, string Folder, Action<object> UseMenu)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Shell history requires an STA thread.");
        using var Host = new HwndSource(new HwndSourceParameters("CDir history")
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            Width = 1, Height = 1, ParentWindow = Owner
        }); // Hidden, no activation, and no taskbar entry.
        var Browser = (IExplorerBrowser)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("71F96385-DDD6-48D3-A0C1-AE06E8B055FB"), true)!)!;
        object? View = null, Menu = null;
        uint Cookie = 0;
        var Initialized = false;
        var Advised = false;
        var Frame = new DispatcherFrame();
        var Completed = false;
        var Succeeded = false;
        var Events = new ShellViewEvents(Success => { Completed = true; Succeeded = Success; Frame.Continue = false; });
        var Timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(10) };
        Timeout.Tick += (_, _) => Frame.Continue = false;
        try
        {
            var Bounds = new NativeRect { Right = 1, Bottom = 1 };
            var Settings = new FolderSettings { ViewMode = 4, Flags = 0x10000 };
            Browser.SetOptions(0x40 | 0x80);
            Browser.Initialize(Host.Handle, ref Bounds, ref Settings);
            Initialized = true;
            Browser.Advise(Events, out Cookie);
            Advised = true;
            Marshal.ThrowExceptionForHR(SHParseDisplayName(Folder, IntPtr.Zero, out var IdList, 0, out _));
            try { Browser.BrowseToIDList(IdList, 0); }
            finally { Marshal.FreeCoTaskMem(IdList); }
            if (!Completed)
            {
                Timeout.Start();
                Dispatcher.PushFrame(Frame);
            }
            if (!Completed || !Succeeded) throw new IOException("Windows could not prepare file operation history.");
            var ViewId = typeof(IShellView).GUID;
            Browser.GetCurrentView(ref ViewId, out View);
            var MenuId = new Guid("000214E4-0000-0000-C000-000000000046");
            ((IShellView)View).GetItemObject(0, ref MenuId, out Menu); // SVGIO_BACKGROUND
            UseMenu(Menu);
        }
        finally
        {
            Timeout.Stop();
            if (Menu is not null) Marshal.ReleaseComObject(Menu);
            if (View is not null) Marshal.ReleaseComObject(View);
            try
            {
                if (Advised) Browser.Unadvise(Cookie);
                if (Initialized) Browser.Destroy();
            }
            finally { Marshal.ReleaseComObject(Browser); GC.KeepAlive(Events); }
        }
    }
}
