using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunThumbnailSchedulingSmokeAsync(List<string> Passed, string ImagePath)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Thumbnail scheduling check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Path.GetDirectoryName(ImagePath)!;
        var ProbePixels = Math.Clamp((int)Math.Ceiling(128 * VisualTreeHelper.GetDpi(this).DpiScaleX), 8, 256);
        foreach (var Size in new[] { 48, 96, 192, 256 })
        {
            foreach (var Folder in new[] { false, true })
            {
                var Large = await ShellIconService.GetIconAsync(null, Folder, Size);
                Check(Large is BitmapSource { IsFrozen: true } Bitmap && Bitmap.PixelWidth == Size && Bitmap.PixelHeight == Size
                    && HasVisibleIconPixels(Bitmap) && HasTransparentIconPixels(Bitmap),
                    $"Default {(Folder ? "folder" : "document")} icons load native large artwork with alpha at {Size}px");
                Check(ShellIconService.GetFallbackIcon(Folder, Size) is BitmapSource Cached && Cached.PixelWidth >= Size,
                    $"The synchronous fallback never substitutes a 32px bitmap for a warmed {Size}px icon");
                if (AppMain.ScreenshotPath is { } Output && Large is BitmapSource Capture)
                {
                    var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Capture));
                    using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, $"DefaultIcon-{Folder}-{Size}.png"));
                    Encoder.Save(Stream);
                }
            }
        }
        using var Preferences = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        if (Preferences?.GetValue("IconsOnly") is int IconsOnly && IconsOnly != 0)
        {
            Passed.Add("The user's Windows icon-only preference is preserved; thumbnail extraction scheduling checks are inapplicable");
            return;
        }
        await this.WaitForShellIconIdleAsync();
        using var Started = new CountdownEvent(ShellIconService.ThumbnailWorkerCount);
        using var Release = new ManualResetEventSlim();
        var Seen = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var Held = Enumerable.Range(0, ShellIconService.ThumbnailWorkerCount).Select(Index => Path.Combine(Root, $"Blocked-{Guid.NewGuid():N}-{Index}.png")).ToArray();
        foreach (var FilePath in Held) File.Copy(ImagePath, FilePath);
        var Jobs = new List<Task<ImageSource?>>();
        var Canceled = new CancellationTokenSource();
        var SharedCancellation = new CancellationTokenSource();
        ShellIcon? Probe = null;
        var Host = (Grid)this.Content;
        try
        {
            ShellIconService.BeforeThumbnailReadForTests = FilePath =>
            {
                Seen.AddOrUpdate(FilePath, 1, (_, Value) => Value + 1);
                if (!Held.Contains(FilePath, StringComparer.OrdinalIgnoreCase)) return;
                Started.Signal();
                if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Owned thumbnail scheduling hold expired.");
            };
            foreach (var FilePath in Held) Jobs.Add(ShellIconService.GetIconAsync(FilePath, false, ProbePixels, ShowThumbnails: true));
            Check(await Task.Run(() => Started.Wait(TimeSpan.FromSeconds(5))),
                "Two bounded STA extraction workers can process independent previews concurrently");
            var Timer = Stopwatch.StartNew();
            var TextPath = Path.Combine(Root, "Responsive icon.txt"); File.WriteAllText(TextPath, "Owned icon lane fixture");
            var Icon = await ShellIconService.GetIconAsync(TextPath, false, 96).WaitAsync(TimeSpan.FromSeconds(3));
            Check(Icon is BitmapSource && !Release.IsSet && Timer.Elapsed < TimeSpan.FromSeconds(3),
                "Slow thumbnail providers do not block ordinary Shell icons");
            // An already generated thumbnail must bypass the held extraction lanes.
            var Cached = await ShellIconService.GetIconAsync(ImagePath, false, 96, ShowThumbnails: true, ThumbnailStamp: 3).WaitAsync(TimeSpan.FromSeconds(3));
            Check(Cached is BitmapSource && !Release.IsSet,
                "The Windows thumbnail cache is probed on the fast lane before starting media extraction");
            var ExpectedIcon = await ShellIconService.GetIconAsync(Held[0], false, ProbePixels);
            Probe = new ShellIcon { Path = Held[0], IconSize = 128, ShowThumbnails = true, Width = 128, Height = 128,
                Opacity = 0, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            Host.Children.Add(Probe);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var Deadline = Environment.TickCount64 + 3000;
            while (!ReferenceEquals(Probe.Source, ExpectedIcon) && Environment.TickCount64 < Deadline) await Task.Delay(20);
            Check(ReferenceEquals(Probe.Source, ExpectedIcon) && !Release.IsSet,
                "Visible media items receive their sharp associated icon while their preview is still being extracted");
            var AbandonedPaths = Enumerable.Range(0, 12).Select(Index => Path.Combine(Root, $"Abandoned-{Index}.png")).ToArray();
            var Abandoned = AbandonedPaths.Select(FilePath => ShellIconService.GetIconAsync(FilePath, false, 128, ShowThumbnails: true, CancellationToken: Canceled.Token)).ToArray();
            Canceled.Cancel();
            foreach (var Job in Abandoned) { try { await Job; } catch (OperationCanceledException) { } }
            var SharedPath = Path.Combine(Root, "Shared preview.png"); File.Copy(ImagePath, SharedPath);
            var One = ShellIconService.GetIconAsync(SharedPath, false, 128, ShowThumbnails: true, CancellationToken: SharedCancellation.Token);
            var Other = ShellIconService.GetIconAsync(SharedPath, false, 128, ShowThumbnails: true);
            SharedCancellation.Cancel(); try { await One; } catch (OperationCanceledException) { }
            Release.Set();
            await Task.WhenAll(Jobs).WaitAsync(TimeSpan.FromSeconds(10));
            var Shared = await Other.WaitAsync(TimeSpan.FromSeconds(10));
            await this.WaitForShellIconIdleAsync();
            Check(AbandonedPaths.All(FilePath => !Seen.ContainsKey(FilePath)),
                "Recycled and offscreen items cancel queued thumbnail extraction before any provider is called");
            Check(Shared is BitmapSource && Seen.GetValueOrDefault(SharedPath) <= 1,
                "Canceling one subscriber preserves the shared thumbnail requested by another item");
            var Preview = await ShellIconService.GetIconAsync(Held[0], false, ProbePixels, ShowThumbnails: true);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(ReferenceEquals(Probe.Source, Preview),
                "A visible item replaces its associated icon as soon as its slow preview completes");
            var Fallback = ShellIconService.GetFallbackIcon(false, ProbePixels);
            var Downgrades = 0;
            var SourceProperty = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(ShellIcon));
            void SourceChanged(object? Sender, EventArgs E)
            {
                if (ReferenceEquals(Probe.Source, Fallback) || ReferenceEquals(Probe.Source, ExpectedIcon)) Downgrades++;
            }
            SourceProperty.AddValueChanged(Probe, SourceChanged);
            try
            {
                for (var Stamp = 1; Stamp <= 3; Stamp++)
                {
                    Probe.ThumbnailStamp = Stamp;
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    await this.WaitForShellIconIdleAsync();
                }
                Check(Downgrades == 0 && Probe.Source is BitmapSource,
                    "Repeated same-file thumbnail refreshes retain the displayed preview without stock/type-icon size changes");
            }
            finally { SourceProperty.RemoveValueChanged(Probe, SourceChanged); }
            Probe.Path = ImagePath;
            var Expected = await ShellIconService.GetIconAsync(ImagePath, false, ProbePixels, ShowThumbnails: true, ThumbnailStamp: Probe.ThumbnailStamp);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Deadline = Environment.TickCount64 + 3000;
            while (!ReferenceEquals(Probe.Source, Expected) && Environment.TickCount64 < Deadline) await Task.Delay(20);
            Check(ReferenceEquals(Probe.Source, Expected), "A recycled media control displays only its current path's preview");
        }
        finally
        {
            Release.Set();
            ShellIconService.BeforeThumbnailReadForTests = null;
            if (Probe is not null) Host.Children.Remove(Probe);
            await Task.WhenAll(Jobs).WaitAsync(TimeSpan.FromSeconds(15));
            Canceled.Dispose(); SharedCancellation.Dispose();
            await this.WaitForShellIconIdleAsync();
        }
    }
}
