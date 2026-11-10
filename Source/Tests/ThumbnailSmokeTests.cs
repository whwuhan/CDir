using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CDir.Controls;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunThumbnailSmokeAsync(List<string> Passed)
    {
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Thumbnails")).FullName;
        var ImagePath = Path.Combine(Root, "Landscape.png");
        var VideoPath = Path.Combine(Root, "Colored frames.avi");
        const int Width = 64, Height = 48;
        var Pixels = new byte[Width * Height * 3];
        for (var I = 0; I < Width * Height; I++)
        {
            Pixels[I * 3] = I % Width < Width / 2 ? (byte)220 : (byte)15;
            Pixels[I * 3 + 2] = I % Width < Width / 2 ? (byte)15 : (byte)220;
        }
        var Encoder = new PngBitmapEncoder();
        Encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr24, null, Pixels, Width * 3)));
        using (var Stream = File.Create(ImagePath)) Encoder.Save(Stream);
        WriteThumbnailAvi(VideoPath, Width, Height, Pixels);
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Thumbnail check failed: " + Name);
            Passed.Add(Name);
        }
        using var Preferences = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        var ThumbnailsEnabled = Preferences?.GetValue("IconsOnly") is not int IconsOnly || IconsOnly == 0;
        foreach (var Path in new[] { ImagePath, VideoPath })
        {
            var Thumbnail = await ShellIconService.GetIconAsync(Path, false, 96, ShowThumbnails: true, ThumbnailStamp: 1);
            var Icon = await ShellIconService.GetIconAsync(Path, false, 96);
            Check(Thumbnail is BitmapSource { IsFrozen: true } Bitmap && Bitmap.PixelWidth > 0 && Bitmap.PixelHeight > 0,
                System.IO.Path.GetExtension(Path) + " obtains a frozen Windows Shell thumbnail or the system provider's icon fallback");
            if (ThumbnailsEnabled && Path == ImagePath)
                Check(!IconPixelsEqual(Thumbnail, Icon), "Large image items display image contents rather than the associated application's icon");
            var Again = await ShellIconService.GetIconAsync(Path, false, 96, ShowThumbnails: true, ThumbnailStamp: 1);
            Check(ReferenceEquals(Thumbnail, Again) && !ReferenceEquals(Thumbnail, Icon),
                "Thumbnail and icon requests have independent, reusable cache entries: " + System.IO.Path.GetExtension(Path));
            if (Thumbnail is BitmapSource Result && AppMain.ScreenshotPath is { } Output)
            {
                var Capture = new PngBitmapEncoder(); Capture.Frames.Add(BitmapFrame.Create(Result));
                using var Stream = File.Create(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Output)!, "Thumbnail" + System.IO.Path.GetExtension(Path).Replace(".", "-") + ".png"));
                Capture.Save(Stream);
            }
        }
        var First = await ShellIconService.GetIconAsync(ImagePath, false, 96, ShowThumbnails: true, ThumbnailStamp: 1);
        var Updated = await ShellIconService.GetIconAsync(ImagePath, false, 96, ShowThumbnails: true, ThumbnailStamp: 2);
        Check(!ReferenceEquals(First, Updated), "A changed file timestamp requests a fresh thumbnail without flushing unrelated icons");
        var Control = new ShellIcon { Path = ImagePath, ShowThumbnails = true, ThumbnailStamp = 3, IconSize = 96 };
        Check(Control.ShowThumbnails && Control.IconSize == 96 && Control.ThumbnailStamp == 3,
            "Main-panel thumbnail controls carry the requested preview size and modification timestamp");
        await this.RunThumbnailSchedulingSmokeAsync(Passed, ImagePath);
    }

    private static void WriteThumbnailAvi(string Path, int Width, int Height, byte[] Frame)
    {
        byte[] Data(Action<BinaryWriter> Write)
        {
            using var Stream = new MemoryStream(); using (var Writer = new BinaryWriter(Stream, Encoding.ASCII, true)) Write(Writer); return Stream.ToArray();
        }
        byte[] Chunk(string Name, byte[] Payload) => Data(W => { W.Write(Encoding.ASCII.GetBytes(Name)); W.Write(Payload.Length); W.Write(Payload); if ((Payload.Length & 1) != 0) W.Write((byte)0); });
        byte[] List(string Name, params byte[][] Children) => Chunk("LIST", Data(W => { W.Write(Encoding.ASCII.GetBytes(Name)); foreach (var Child in Children) W.Write(Child); }));
        var Avih = Chunk("avih", Data(W =>
        {
            foreach (var Value in new[] { 200000, Frame.Length * 5, 0, 16, 10, 0, 1, Frame.Length, Width, Height, 0, 0, 0, 0 }) W.Write(Value);
        }));
        var Strh = Chunk("strh", Data(W =>
        {
            W.Write(Encoding.ASCII.GetBytes("vidsDIB ")); W.Write(0); W.Write((ushort)0); W.Write((ushort)0);
            foreach (var Value in new[] { 0, 1, 5, 0, 10, Frame.Length, -1, 0 }) W.Write(Value);
            W.Write((short)0); W.Write((short)0); W.Write((short)Width); W.Write((short)Height);
        }));
        var Strf = Chunk("strf", Data(W =>
        {
            W.Write(40); W.Write(Width); W.Write(Height); W.Write((ushort)1); W.Write((ushort)24);
            foreach (var Value in new[] { 0, Frame.Length, 0, 0, 0, 0 }) W.Write(Value);
        }));
        var Frames = Enumerable.Range(0, 10).Select(_ => Chunk("00db", Frame)).ToArray();
        var Index = Chunk("idx1", Data(W =>
        {
            for (var I = 0; I < 10; I++) { W.Write(Encoding.ASCII.GetBytes("00db")); W.Write(16); W.Write(4 + I * (Frame.Length + 8)); W.Write(Frame.Length); }
        }));
        var Body = Data(W => { W.Write(Encoding.ASCII.GetBytes("AVI ")); W.Write(List("hdrl", Avih, List("strl", Strh, Strf))); W.Write(List("movi", Frames)); W.Write(Index); });
        File.WriteAllBytes(Path, Chunk("RIFF", Body));
    }
}
