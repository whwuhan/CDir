using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;
using CDir.Localization;
using CDir.Models;
using CDir.Services;

namespace CDir.Controls;

/// <summary>Loads a Windows Shell icon without blocking navigation or recycled item containers.</summary>
public sealed class ShellIcon : Image
{
    public static readonly DependencyProperty VersionStatusProperty = DependencyProperty.Register(
        nameof(VersionStatus), typeof(VersionStatus), typeof(ShellIcon),
        new FrameworkPropertyMetadata(VersionStatus.None, FrameworkPropertyMetadataOptions.AffectsRender, VersionStatusChanged));
    public VersionStatus VersionStatus { get => (VersionStatus)GetValue(VersionStatusProperty); set => SetValue(VersionStatusProperty, value); }
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(ShellIcon), new PropertyMetadata(null, IconPropertyChanged));
    public static readonly DependencyProperty IsDirectoryProperty = DependencyProperty.Register(
        nameof(IsDirectory), typeof(bool), typeof(ShellIcon), new PropertyMetadata(false, IconPropertyChanged));
    public static readonly DependencyProperty IsDriveProperty = DependencyProperty.Register(
        nameof(IsDrive), typeof(bool), typeof(ShellIcon), new PropertyMetadata(false, IconPropertyChanged));
    public static readonly DependencyProperty OpenFolderProperty = DependencyProperty.Register(
        nameof(OpenFolder), typeof(bool), typeof(ShellIcon), new PropertyMetadata(false, IconPropertyChanged));
    public static readonly DependencyProperty HideWhenEmptyProperty = DependencyProperty.Register(
        nameof(HideWhenEmpty), typeof(bool), typeof(ShellIcon), new PropertyMetadata(false, IconPropertyChanged));
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(int), typeof(ShellIcon), new PropertyMetadata(16, IconPropertyChanged),
        Value => Value is int Size && Size is >= 8 and <= 256);

    public static readonly DependencyProperty ShowThumbnailsProperty = DependencyProperty.Register(nameof(ShowThumbnails), typeof(bool), typeof(ShellIcon), new PropertyMetadata(false, IconPropertyChanged));
    public bool ShowThumbnails { get => (bool)GetValue(ShowThumbnailsProperty); set => SetValue(ShowThumbnailsProperty, value); }
    public static readonly DependencyProperty ThumbnailStampProperty = DependencyProperty.Register(nameof(ThumbnailStamp), typeof(long), typeof(ShellIcon), new PropertyMetadata(0L, IconPropertyChanged));
    public long ThumbnailStamp { get => (long)GetValue(ThumbnailStampProperty); set => SetValue(ThumbnailStampProperty, value); }
    public string? Path { get => (string?)GetValue(PathProperty); set => SetValue(PathProperty, value); }
    public bool IsDirectory { get => (bool)GetValue(IsDirectoryProperty); set => SetValue(IsDirectoryProperty, value); }
    public bool IsDrive { get => (bool)GetValue(IsDriveProperty); set => SetValue(IsDriveProperty, value); }
    public bool OpenFolder { get => (bool)GetValue(OpenFolderProperty); set => SetValue(OpenFolderProperty, value); }
    public bool HideWhenEmpty { get => (bool)GetValue(HideWhenEmptyProperty); set => SetValue(HideWhenEmptyProperty, value); }
    public int IconSize { get => (int)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    private CancellationTokenSource? PendingLoad;
    private int LoadVersion;
    private bool RefreshQueued;

    public ShellIcon()
    {
        this.SnapsToDevicePixels = true;
        this.UseLayoutRounding = true;
        this.Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        this.Loaded += (_, _) =>
        {
            ShellIconService.CacheInvalidated -= this.CacheInvalidated;
            ShellIconService.CacheInvalidated += this.CacheInvalidated;
            LanguageService.LanguageChanged -= this.VersionLanguageChanged;
            LanguageService.LanguageChanged += this.VersionLanguageChanged;
            this.QueueRefresh();
            this.UpdateVersionTooltip();
        };
        this.Unloaded += (_, _) =>
        {
            ShellIconService.CacheInvalidated -= this.CacheInvalidated;
            LanguageService.LanguageChanged -= this.VersionLanguageChanged;
            this.CancelLoad();
        };
    }

    private static void IconPropertyChanged(DependencyObject Target, DependencyPropertyChangedEventArgs E)
    {
        if (Target is ShellIcon { IsLoaded: true } Icon) { Icon.QueueRefresh(); Icon.UpdateVersionTooltip(); }
    }

    private static void VersionStatusChanged(DependencyObject Target, DependencyPropertyChangedEventArgs E) => ((ShellIcon)Target).UpdateVersionTooltip();
    private void VersionLanguageChanged(object? Sender, EventArgs E) => this.UpdateVersionTooltip();
    private void UpdateVersionTooltip() => ToolTip = this.VersionStatus.State == VersionState.None ? null : this.Path + Environment.NewLine + this.VersionStatus.Description;

    protected override void OnRender(DrawingContext Context)
    {
        base.OnRender(Context);
        var State = this.VersionStatus.State;
        if (State == VersionState.None || ActualWidth < 8 || ActualHeight < 8) return;
        var (Glyph, Color) = State switch
        {
            VersionState.Clean => ("✓", "#25854B"),
            VersionState.Modified => ("!", "#C94B35"),
            VersionState.Conflict => ("×", "#BE2737"),
            VersionState.Added => ("+", "#25854B"),
            VersionState.Deleted => ("−", "#C94B35"),
            VersionState.Renamed => ("↗", "#386ECB"),
            VersionState.Untracked => ("?", "#386ECB"),
            VersionState.Ignored => ("−", "#8993A1"),
            _ => ("?", "#8993A1")
        };
        var Size = Math.Min(10, Math.Min(ActualWidth, ActualHeight));
        var Center = new Point(ActualWidth - Size / 2, ActualHeight - Size / 2);
        Context.DrawEllipse((Brush)new BrushConverter().ConvertFromString(Color)!, new Pen(Brushes.White, 0.8), Center, Size / 2, Size / 2);
        if (State == VersionState.Unknown && string.IsNullOrEmpty(this.VersionStatus.Error))
        {
            // Draw the pending ellipsis as geometry so all three dots remain
            // legible in a 16px icon, independently of the installed font.
            for (var Index = -1; Index <= 1; Index++)
                Context.DrawEllipse(Brushes.White, null, new Point(Center.X + Index * Size * 0.22, Center.Y + Size * 0.1), Size * 0.075, Size * 0.075);
            return;
        }
        var Text = new FormattedText(Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 8, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        Context.DrawText(Text, new Point(Center.X - Text.Width / 2, Center.Y - Text.Height / 2 - 0.2));
    }

    protected override void OnDpiChanged(DpiScale OldDpi, DpiScale NewDpi)
    {
        base.OnDpiChanged(OldDpi, NewDpi);
        if (this.IsLoaded) this.QueueRefresh();
    }

    private void CacheInvalidated(object? Sender, EventArgs E)
    {
        if (this.Dispatcher.CheckAccess())
        {
            if (this.IsLoaded) this.QueueRefresh();
        }
        else if (!this.Dispatcher.HasShutdownStarted)
            this.Dispatcher.BeginInvoke(() => { if (this.IsLoaded) this.QueueRefresh(); });
    }

    private void QueueRefresh()
    {
        this.CancelLoad();
        if (this.RefreshQueued) return;
        this.RefreshQueued = true;
        this.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            this.RefreshQueued = false;
            if (this.IsLoaded) this.RefreshIcon();
        });
    }

    private void CancelLoad()
    {
        this.LoadVersion++;
        this.PendingLoad?.Cancel();
        this.PendingLoad?.Dispose();
        this.PendingLoad = null;
    }

    private async void RefreshIcon()
    {
        this.CancelLoad();
        if (this.HideWhenEmpty && string.IsNullOrWhiteSpace(this.Path))
        {
            this.Source = null;
            return;
        }
        var Version = this.LoadVersion;
        var PixelSize = Math.Clamp((int)Math.Ceiling(this.IconSize * VisualTreeHelper.GetDpi(this).DpiScaleX), 8, 256);
        var Fallback = this.IsDrive
            ? ShellIconService.GetDriveFallbackIcon(PixelSize)
            : ShellIconService.GetFallbackIcon(this.IsDirectory, PixelSize);
        var Cancellation = new CancellationTokenSource();
        this.PendingLoad = Cancellation;
        try
        {
            var Thumbnails = this.ShowThumbnails && !this.IsDirectory && PixelSize > 32 && this.IconSize > 16;
            var Pending = ShellIconService.GetIconAsync(this.Path, this.IsDirectory, PixelSize, this.OpenFolder, Thumbnails, this.ThumbnailStamp, Cancellation.Token);
            // A cache hit paints immediately, without flashing a generic icon.
            if (Pending.IsCompletedSuccessfully)
            {
                if (this.IsLoaded && Version == this.LoadVersion) this.Source = Pending.Result ?? Fallback;
                return;
            }
            this.Source = Fallback;
            if (Thumbnails)
            {
                // The fast icon lane provides a sharp associated-type icon while
                // the independent preview workers wait for a media provider.
                var Default = ShellIconService.GetIconAsync(this.Path, this.IsDirectory, PixelSize, this.OpenFolder, CancellationToken: Cancellation.Token);
                if (await Task.WhenAny(Pending, Default) == Default)
                {
                    var IconSource = await Default;
                    if (this.IsLoaded && Version == this.LoadVersion) this.Source = IconSource ?? Fallback;
                }
            }
            var Icon = await Pending;
            if (this.IsLoaded && Version == this.LoadVersion) this.Source = Icon ?? Fallback;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (this.IsLoaded && Version == this.LoadVersion) this.Source = Fallback;
        }
        finally
        {
            if (ReferenceEquals(this.PendingLoad, Cancellation)) this.PendingLoad = null;
            Cancellation.Dispose();
        }
    }
}
