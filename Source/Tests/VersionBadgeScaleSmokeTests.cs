using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunVersionBadgeScaleSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Version badge scale check failed: " + Name);
            Passed.Add(Name);
        }
        // Render a real control on the current desktop, without activating it.
        // A transparent base image keeps Image layout active while measuring
        // only the independently drawn badge.
        var Icon = new ShellIcon();
        var Canvas = new Canvas(); Canvas.Children.Add(Icon);
        var Host = new Window { Owner = this.HostWindow, Content = Canvas, Width = 350, Height = 350, ShowActivated = false, ShowInTaskbar = false };
        Host.Show();
        try
        {
            async Task<RenderTargetBitmap> Render(double Width, double Height, VersionStatus Status, double Scale = 1)
            {
                Icon.Width = Width; Icon.Height = Height; Icon.VersionStatus = Status;
                Host.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Icon.Source = new DrawingImage(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, Width, Height))));
                Icon.InvalidateVisual(); Host.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var Bitmap = new RenderTargetBitmap((int)Math.Ceiling(Width * Scale), (int)Math.Ceiling(Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
                Bitmap.Render(Icon);
                return Bitmap;
            }
            (Rect Bounds, int White) Ink(BitmapSource Bitmap)
            {
                var Stride = Bitmap.PixelWidth * 4;
                var Pixels = new byte[Stride * Bitmap.PixelHeight]; Bitmap.CopyPixels(Pixels, Stride, 0);
                var Left = Bitmap.PixelWidth; var Top = Bitmap.PixelHeight; var Right = -1; var Bottom = -1; var White = 0;
                for (var Y = 0; Y < Bitmap.PixelHeight; Y++)
                    for (var X = 0; X < Bitmap.PixelWidth; X++)
                    {
                        var Offset = Y * Stride + X * 4;
                        if (Pixels[Offset + 3] < 32) continue;
                        Left = Math.Min(Left, X); Top = Math.Min(Top, Y); Right = Math.Max(Right, X); Bottom = Math.Max(Bottom, Y);
                        if (Pixels[Offset] > 230 && Pixels[Offset + 1] > 230 && Pixels[Offset + 2] > 230) White++;
                    }
                return (Right < Left ? Rect.Empty : new Rect(Left, Top, Right - Left + 1, Bottom - Top + 1), White);
            }
            var Samples = new List<(int Size, RenderTargetBitmap Bitmap)>();
            foreach (var State in Enum.GetValues<VersionState>().Where(State => State != VersionState.None))
            {
                var Status = new VersionStatus(State, "Git");
                var Small = await Render(16, 16, Status);
                var SmallInk = Ink(Small);
                Check(SmallInk.Bounds.Width is >= 9 and <= 12, State + " keeps compact list/tree badges readable (bounds=" + SmallInk.Bounds + ", actual=" + Icon.ActualWidth + "x" + Icon.ActualHeight + ")");
                var LastWidth = SmallInk.Bounds.Width;
                foreach (var Size in new[] { 48, 96, 192, 256 })
                {
                    var Bitmap = await Render(Size, Size, Status);
                    var Painted = Ink(Bitmap);
                    Check(Painted.Bounds.Width > LastWidth && Painted.Bounds.Right == Size && Painted.Bounds.Bottom == Size
                        && Painted.Bounds.Width < Size / 2 && Painted.Bounds.Height < Size / 2,
                        State + " badge grows with " + Size + "px icons and stays in the lower-right corner");
                    if (Size == 96) Check(Painted.White > SmallInk.White * 2, State + " white symbol and outline scale with the badge");
                    if (State == VersionState.Modified) Samples.Add((Size, Bitmap));
                    LastWidth = Painted.Bounds.Width;
                }
                Check(IconPixelsEqual(Small, await Render(16, 16, Status)), State + " shrinking restores the original compact badge");
                var Git = await Render(96, 96, Status);
                var Svn = await Render(96, 96, new VersionStatus(State, "SVN"));
                Check(IconPixelsEqual(Git, Svn), State + " Git and SVN badges share identical scaling");
                if (State == VersionState.Modified) Samples.Insert(0, (16, Small));
            }
            var Clean = new VersionStatus(VersionState.Clean, "Git");
            var Wide = Ink(await Render(120, 60, Clean)).Bounds;
            var WideLarge = Ink(await Render(240, 120, Clean)).Bounds;
            Check(WideLarge.Width >= Wide.Width * 1.8 && WideLarge.Right == 240 && WideLarge.Bottom == 120,
                "Wide thumbnail badges scale and remain anchored to the visible image corner");
            var Normal = Ink(await Render(96, 96, Clean)).Bounds;
            var HighDpi = Ink(await Render(96, 96, Clean, 1.5)).Bounds;
            Check(Math.Abs(HighDpi.Width / 1.5 - Normal.Width) < 2 && HighDpi.Right == 144 && HighDpi.Bottom == 144,
                "Status badge proportions survive 150 percent rendering");
            var FailedGit = await Render(96, 96, new VersionStatus(VersionState.Unknown, "Git", Error: "Owned test error"));
            var PendingGit = await Render(96, 96, new VersionStatus(VersionState.Unknown, "Git"));
            Check(!IconPixelsEqual(FailedGit, PendingGit), "Scaled pending dots remain distinct from status lookup errors");
            Check(Ink(await Render(256, 256, VersionStatus.None)).Bounds.IsEmpty, "Unversioned icons have no badge at any size");
            if (AppMain.ScreenshotPath is { } Output)
            {
                var Visual = new DrawingVisual();
                using (var Context = Visual.RenderOpen())
                {
                    Context.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, 780, 310));
                    var X = 12.0;
                    foreach (var Sample in Samples)
                    {
                        Context.DrawRectangle(Brushes.LightSteelBlue, null, new Rect(X, 30, Sample.Size, Sample.Size));
                        Context.DrawImage(Sample.Bitmap, new Rect(X, 30, Sample.Size, Sample.Size));
                        X += Sample.Size + 24;
                    }
                }
                var Preview = new RenderTargetBitmap(780, 310, 96, 96, PixelFormats.Pbgra32); Preview.Render(Visual);
                var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Preview));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, "VersionBadgeScaling.png")); Encoder.Save(Stream);
            }
        }
        finally { Host.Close(); }
    }
}
