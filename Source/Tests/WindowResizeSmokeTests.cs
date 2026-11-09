using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CDir.UI;

public partial class ExplorerTab
{
    private static void CheckResizeBorders(MainWindow Probe, List<string> Passed)
    {
        var Handle = new WindowInteropHelper(Probe).Handle;
        if (!GetResizeSmokeRect(Handle, out var Frame)) throw new System.ComponentModel.Win32Exception();
        var MiddleX = (Frame.Left + Frame.Right) / 2;
        var MiddleY = (Frame.Top + Frame.Bottom) / 2;
        var Points = new[]
        {
            (Frame.Left, MiddleY, 10), (Frame.Right - 1, MiddleY, 11),
            (MiddleX, Frame.Top, 12), (MiddleX, Frame.Bottom - 1, 15),
            (Frame.Left, Frame.Top, 13), (Frame.Right - 1, Frame.Top, 14),
            (Frame.Left, Frame.Bottom - 1, 16), (Frame.Right - 1, Frame.Bottom - 1, 17)
        };
        foreach (var (X, Y, Expected) in Points)
        {
            var Position = new IntPtr(unchecked((int)(((uint)(ushort)Y << 16) | (ushort)X)));
            var Hit = SendTraySmokeMessage(Handle, 0x84, IntPtr.Zero, Position).ToInt32();
            if (Probe.WindowState == WindowState.Normal ? Hit != Expected : Hit is >= 10 and <= 17)
                throw new InvalidOperationException($"Window resize hit test failed: state={Probe.WindowState}, point={X},{Y}, hit={Hit}, expected={Expected}.");
            Passed.Add($"The outermost window edge/corner preserves native resize behavior: {Probe.WindowState} {Expected}");
        }
        var Button = Probe.MaximizeButton;
        var Center = Button.PointToScreen(new Point(Button.ActualWidth / 2, Button.ActualHeight / 2));
        var Packed = new IntPtr(unchecked((int)(((uint)(ushort)(int)Center.Y << 16) | (ushort)(int)Center.X)));
        if (SendTraySmokeMessage(Handle, 0x84, IntPtr.Zero, Packed) != (IntPtr)1)
            throw new InvalidOperationException("Caption buttons must remain client controls.");
        Passed.Add("Resize hit testing keeps the maximize button clickable: " + Probe.WindowState);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResizeSmokeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetResizeSmokeRect(IntPtr Window, out ResizeSmokeRect Rect);
}
