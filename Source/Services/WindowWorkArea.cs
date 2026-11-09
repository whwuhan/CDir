using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CDir.Services;

// Size borderless maximized windows to the work area of their current monitor, in native pixels.
internal sealed class WindowWorkArea : IDisposable
{
    private readonly HwndSource Source;
    private readonly Window Owner;
    internal WindowWorkArea(Window Window)
    {
        this.Owner = Window;
        this.Source = HwndSource.FromHwnd(new WindowInteropHelper(Window).EnsureHandle())!;
        this.Source.AddHook(this.WindowMessage);
    }

    internal static Rect ReadWorkArea(IntPtr Window)
    {
        var Info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(Window, 2), ref Info)) return Rect.Empty;
        return new Rect(Info.Work.Left, Info.Work.Top, Info.Work.Right - Info.Work.Left, Info.Work.Bottom - Info.Work.Top);
    }

    private IntPtr WindowMessage(IntPtr Window, int Message, IntPtr WParam, IntPtr LParam, ref bool Handled)
    {
        if (Message == 0x0084 && this.Owner.WindowState is WindowState.Normal or WindowState.Maximized
            && this.Owner.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip
            && GetWindowRect(Window, out var Frame)) // WM_NCHITTEST uses signed physical screen coordinates.
        {
            var X = (short)(LParam.ToInt64() & 0xffff);
            var Y = (short)((LParam.ToInt64() >> 16) & 0xffff);
            var Border = Math.Max(6, (int)Math.Ceiling(6 * this.Source.CompositionTarget.TransformToDevice.M11));
            if (X >= Frame.Left && X < Frame.Right && Y >= Frame.Top && Y < Frame.Bottom)
            {
                var Left = X < Frame.Left + Border;
                var Right = X >= Frame.Right - Border;
                var Top = Y < Frame.Top + Border;
                var Bottom = Y >= Frame.Bottom - Border;
                var Hit = Top ? Left ? 13 : Right ? 14 : 12 : Bottom ? Left ? 16 : Right ? 17 : 15 : Left ? 10 : Right ? 11 : 0;
                if (Hit != 0)
                {
                    Handled = true;
                    return (IntPtr)(this.Owner.WindowState == WindowState.Maximized ? Top ? 2 : 1 : Hit);
                }
            }
        }
        if (Message != 0x0024) return IntPtr.Zero; // WM_GETMINMAXINFO
        var Info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(Window, 2), ref Info)) return IntPtr.Zero;
        var Bounds = Marshal.PtrToStructure<MinMaxInfo>(LParam);
        Bounds.MaxPosition = new() { X = Info.Work.Left - Info.Monitor.Left, Y = Info.Work.Top - Info.Monitor.Top };
        Bounds.MaxSize = new() { X = Info.Work.Right - Info.Work.Left, Y = Info.Work.Bottom - Info.Work.Top };
        Marshal.StructureToPtr(Bounds, LParam, false);
        Handled = true;
        return IntPtr.Zero;
    }

    public void Dispose() => this.Source.RemoveHook(this.WindowMessage);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr Window, uint Flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr Window, out NativeRect Rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr Monitor, ref MonitorInfo Info);
}
