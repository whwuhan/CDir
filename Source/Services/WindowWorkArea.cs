using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CDir.Services;

// Size borderless maximized windows to the work area of their current monitor, in native pixels.
internal sealed class WindowWorkArea : IDisposable
{
    private readonly HwndSource Source;
    internal WindowWorkArea(Window Window)
    {
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr Monitor, ref MonitorInfo Info);
}
