using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CDir.Controls;

internal sealed partial class NativeShellView
{
    // RenderTargetBitmap omits child HWNDs. Capture only the owned test host, never the desktop.
    internal void CaptureOwnedPreview(string Path)
    {
        if (!AppMain.SmokeTest || this.NavigationTarget is null || !AppMain.IsInsideSmokeScope(this.NavigationTarget))
            throw new InvalidOperationException("Native view previews require an owned smoke-test folder.");
        if (!GetClientRect(this.Host, out var Bounds) || Bounds.Right <= 0 || Bounds.Bottom <= 0) return;
        var Dc = GetDC(this.Host);
        var Memory = CreateCompatibleDC(Dc);
        var Bitmap = CreateCompatibleBitmap(Dc, Bounds.Right, Bounds.Bottom);
        var Original = SelectObject(Memory, Bitmap);
        try
        {
            if (!PrintWindow(this.Host, Memory, 2)) return;
            SelectObject(Memory, Original);
            var Bytes = new byte[checked(Bounds.Right * Bounds.Bottom * 4)];
            var Header = new BitmapHeader { Size = 40, Width = Bounds.Right, Height = -Bounds.Bottom, Planes = 1, Bits = 32 };
            if (GetDIBits(Memory, Bitmap, 0, (uint)Bounds.Bottom, Bytes, ref Header, 0) == 0) return;
            var Image = BitmapSource.Create(Bounds.Right, Bounds.Bottom, 96, 96, PixelFormats.Bgr32, null, Bytes, Bounds.Right * 4);
            var Encoder = new PngBitmapEncoder();
            Encoder.Frames.Add(BitmapFrame.Create(Image));
            using var Stream = File.Create(Path);
            Encoder.Save(Stream);
        }
        finally
        {
            SelectObject(Memory, Original);
            DeleteObject(Bitmap);
            DeleteDC(Memory);
            ReleaseDC(this.Host, Dc);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int XResolution, YResolution; public uint UsedColors, ImportantColors;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr Window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr Window, IntPtr Dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr Dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr Dc, int Width, int Height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr Dc, IntPtr Object);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr Object);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr Dc);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(IntPtr Window, IntPtr Dc, uint Flags);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr Dc, IntPtr Bitmap, uint Start, uint Lines, byte[] Pixels, ref BitmapHeader Header, uint ColorMode);
}
