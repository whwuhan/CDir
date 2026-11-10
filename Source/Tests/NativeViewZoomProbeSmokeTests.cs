using System.Runtime.InteropServices;

namespace CDir.Controls;

internal sealed partial class NativeShellView
{
    internal void UseModernZoomView() => ((IZoomFolderOptions)this.Browser!).SetFolderViewOptions(0xffffffff, 0);
    [ComImport, Guid("3cc974d2-b302-4d36-ad3e-06d93f695d3f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IZoomFolderOptions
    {
        void SetFolderViewOptions(uint Mask, uint Flags);
        void GetFolderViewOptions(out uint Flags);
    }
    internal (int Mode, int Size) ProbeZoom(int Mode, int Size, int Delta)
    {
        if (!AppMain.SmokeTest || this.NavigationTarget is null || !AppMain.IsInsideSmokeScope(this.NavigationTarget))
            throw new InvalidOperationException("Native zoom probes require an owned test folder.");
        object? View = null;
        IntPtr Unknown = IntPtr.Zero, Folder = IntPtr.Zero;
        var Keys = new byte[256];
        GetZoomKeyboardState(Keys);
        try
        {
            var ShellId = typeof(IShellView).GUID;
            this.Browser!.GetCurrentView(ref ShellId, out View);
            ((IShellView)View).GetWindow(out var Window);
            Unknown = Marshal.GetIUnknownForObject(View);
            var FolderId = new Guid("1AF3A467-214F-4298-908E-06B03E0B39F9");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(Unknown, in FolderId, out Folder));
            var Table = Marshal.ReadIntPtr(Folder);
            // IFolderView2 slots 35/36, from the installed Windows SDK ShObjIdl_core.h.
            var Set = Marshal.GetDelegateForFunctionPointer<SetZoom>(Marshal.ReadIntPtr(Table, 35 * IntPtr.Size));
            var Get = Marshal.GetDelegateForFunctionPointer<GetZoom>(Marshal.ReadIntPtr(Table, 36 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(Set(Folder, Mode, Size));
            var Direct = ZoomFindWindow(Window, IntPtr.Zero, "DirectUIHWND", null);
            if (Direct != IntPtr.Zero) Window = Direct;
            var Controlled = (byte[])Keys.Clone(); Controlled[0x11] = Controlled[0xA2] = 0x80;
            SetZoomKeyboardState(Controlled); // Only this owned UI thread's key-state table; no global input.
            var Point = new ZoomPoint { X = 20, Y = 20 }; ZoomClientToScreen(Window, ref Point);
            var Position = (nint)((Point.X & 0xffff) | (Point.Y << 16));
            var Wheel = (nint)((Delta << 16) | 8);
            ZoomSendMessage(Window, 0x20A, Wheel, Position);
            Marshal.ThrowExceptionForHR(Get(Folder, out var ActualMode, out var ActualSize));
            return (ActualMode, ActualSize);
        }
        finally
        {
            SetZoomKeyboardState(Keys);
            if (Folder != IntPtr.Zero) Marshal.Release(Folder);
            if (Unknown != IntPtr.Zero) Marshal.Release(Unknown);
            if (View is not null) Marshal.ReleaseComObject(View);
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetZoom(IntPtr Self, int Mode, int Size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetZoom(IntPtr Self, out int Mode, out int Size);
    [StructLayout(LayoutKind.Sequential)] private struct ZoomPoint { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint ZoomSendMessage(IntPtr Window, uint Message, nint WParam, nint LParam);
    [DllImport("user32.dll", EntryPoint = "GetKeyboardState")] private static extern bool GetZoomKeyboardState([Out] byte[] Keys);
    [DllImport("user32.dll", EntryPoint = "SetKeyboardState")] private static extern bool SetZoomKeyboardState(byte[] Keys);
    [DllImport("user32.dll", EntryPoint = "ClientToScreen")] private static extern bool ZoomClientToScreen(IntPtr Window, ref ZoomPoint Point);
    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)] private static extern IntPtr ZoomFindWindow(IntPtr Parent, IntPtr After, string Class, string? Title);
}
