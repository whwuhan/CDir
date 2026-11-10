using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Services;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private bool UsageBarHasNoInset(System.Windows.Controls.ControlTemplate Template)
    {
        var Bar = new System.Windows.Controls.ProgressBar { Width = 64, Height = 10, Minimum = 0, Maximum = 100, Value = 50,
            Template = Template, Foreground = (Brush)this.FindResource("DriveUsageLow"), Background = Brushes.White,
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), SnapsToDevicePixels = true, UseLayoutRounding = true };
        Bar.Measure(new Size(64, 10)); Bar.Arrange(new Rect(0, 0, 64, 10)); Bar.UpdateLayout();
        var Bitmap = new RenderTargetBitmap(64, 10, 96, 96, PixelFormats.Pbgra32); Bitmap.Render(Bar);
        var Bytes = new byte[64 * 10 * 4]; Bitmap.CopyPixels(Bytes, 64 * 4, 0);
        bool IsFilled(int X, int Y) { var Offset = (Y * 64 + X) * 4; return Bytes[Offset + 1] > Bytes[Offset] && Bytes[Offset + 1] > Bytes[Offset + 2]; }
        return IsFilled(1, 1) && IsFilled(1, 8) && IsFilled(30, 1) && IsFilled(30, 8);
    }
    private async Task RunDriveUsageSmokeChecksAsync(List<string> Passed)
    {
        var InitialReader = this.ReadDriveUsage;
        var InitialLanguage = LanguageService.CurrentLanguage;
        var InitialFonts = new Dictionary<string, double>(this.Settings.FontSizes!);
        var InitialWidth = this.HostWindow.Width;
        this.DriveUsageTimer.Stop();
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Drive usage UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Refresh(string Path)
        {
            this.QueueDriveUsageRefresh(Path, Force: true);
            await this.DriveUsageRefreshTask;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        const long GB = 1073741824;
        try
        {
            Check(((SolidColorBrush)this.FindResource("DriveUsageLow")).Color == Color.FromRgb(0x2E, 0x8B, 0x57)
                && ((SolidColorBrush)this.FindResource("DriveUsageWarning")).Color == Color.FromRgb(0xE6, 0x95, 0x00)
                && ((SolidColorBrush)this.FindResource("DriveUsageCritical")).Color == Color.FromRgb(0xD1, 0x34, 0x38),
                "Drive usage restores the original green, orange and red palette");
            await this.DriveUsageRefreshTask;
            Check(this.CurrentDriveUsage is { TotalBytes: > 0 } Actual && Actual.Root == DriveUsage.GetRoot(this.CurrentPath)
                && this.DriveUsagePanel.IsVisible && this.DriveUsageText.Text.EndsWith(" GB", StringComparison.Ordinal)
                && this.DriveUsageBar.Value is >= 0 and <= 100,
                "Status bar reads the current directory's actual drive with GB values and a bounded usage meter");
            Check(DriveUsage.GetRoot(@"G:\Folder\Child") == @"G:\"
                && DriveUsage.GetRoot(@"\\server\share\Child").TrimEnd('\\') == @"\\server\share"
                && DriveUsage.GetRoot("relative") == "",
                "Drive roots resolve nested folders and UNC shares without using a relative working directory");
            this.ReadDriveUsage = Root => new(Root, 256 * GB, 512 * GB);
            await Refresh(@"Q:\Folder");
            Check(this.DriveUsageText.Text == LanguageService.Text("DriveUsageFormat", "Q:", 256.0, 512.0)
                && this.DriveUsageText.Text.Contains(" GB / ", StringComparison.Ordinal)
                && this.DriveUsageBar.Value == 50 && this.DriveUsageBar.IsVisible
                && this.DriveUsagePercentageText.IsVisible && this.DriveUsagePercentageText.Text == 0.5.ToString("P1", LanguageService.Culture),
                "Used and total capacities each show GB with an accurate meter and visible percentage");
            Check(this.UsageBarHasNoInset(this.DriveUsageBar.Template), "Status usage fill touches the frame on all inner edges without a white inset");
            Check(this.DriveUsageBar.Template.FindName("UsageFrame", this.DriveUsageBar) is Border Frame
                && Frame.BorderThickness == new Thickness(1) && Frame.Effect is null,
                "The status usage frame restores one-DIP thickness without a shadow");
            var Track = (FrameworkElement)this.DriveUsageBar.Template.FindName("PART_Track", this.DriveUsageBar);
            var Indicator = (FrameworkElement)this.DriveUsageBar.Template.FindName("PART_Indicator", this.DriveUsageBar);
            Check(Math.Abs(Indicator.ActualWidth - Track.ActualWidth / 2) < 1
                && this.DriveUsageBar.Foreground is SolidColorBrush Fill && Fill.Color.G > Fill.Color.R && Fill.Color.G > Fill.Color.B
                && this.DriveUsageBar.Background is SolidColorBrush Background && Background.Color == Colors.White,
                "The half-full meter draws the correct proportion using a green fill and white unused capacity");
            foreach (var Language in LanguageService.SupportedLanguages)
            {
                this.ChangeLanguage(Language.Code, Persist: false);
                Check(this.DriveUsagePanel.ToolTip?.ToString() == LanguageService.Text("DriveUsageTooltip", "Q:", 256.0, 512.0, 0.5)
                    && this.DriveUsageText.Text.Contains(" GB / ", StringComparison.Ordinal)
                    && this.DriveUsagePercentageText.Text == 0.5.ToString("P1", LanguageService.Culture),
                    Language.Code + " updates capacity units, percentage and tooltip immediately");
            }
            this.ChangeLanguage(InitialLanguage, Persist: false);
            this.HostWindow.Width = this.HostWindow.MinWidth;
            this.Settings.FontSizes!["StatusBar"] = 24;
            this.ApplyUIFonts();
            this.UpdateLayout();
            Check(this.DriveUsageText.FontSize == 24 && this.DriveUsagePercentageText.FontSize == 24 && this.DriveUsagePanel.IsVisible
                && this.DriveUsagePanel.TranslatePoint(new Point(), this).X < this.StatusText.TranslatePoint(new Point(), this).X
                && this.DriveUsagePercentageText.TranslatePoint(new Point(), this).X >= this.DriveUsageBar.TranslatePoint(new Point(this.DriveUsageBar.ActualWidth, 0), this).X
                && this.DriveUsagePercentageText.TranslatePoint(new Point(this.DriveUsagePercentageText.ActualWidth, 0), this).X <= this.StatusText.TranslatePoint(new Point(), this).X
                && this.StatusText.ActualWidth > 0 && this.TerminalToggleButton.ActualWidth > 0
                && this.DriveUsagePanel.ActualHeight <= ((FrameworkElement)this.DriveUsagePanel.Parent).ActualHeight,
                "The drive display precedes item counts and fits a narrow window with large status text");
            this.HostWindow.Width = InitialWidth;
            this.Settings.FontSizes = new(InitialFonts);
            this.ApplyUIFonts();
            await Refresh(@"Q:\Preview");
            if (AppMain.ScreenshotPath is { } Output)
            {
                this.UpdateLayout();
                var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(this);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, "DriveUsage.png"));
                Encoder.Save(Stream);
            }
            foreach (var (Used, ExpectedBand) in new[]
            {
                (0L, "DriveUsageLow"), (699L, "DriveUsageLow"),
                (700L, "DriveUsageWarning"), (899L, "DriveUsageWarning"),
                (900L, "DriveUsageCritical"), (1000L, "DriveUsageCritical")
            })
            {
                this.ReadDriveUsage = Root => new(Root, Used * GB, 1000 * GB);
                await Refresh(@"Q:\Folder");
                Check(this.DriveUsageBar.Foreground == this.FindResource(ExpectedBand)
                    && Math.Abs(this.DriveUsageBar.Value - Used / 10.0) < 0.001
                    && this.DriveUsagePercentageText.Text == (Used / 1000.0).ToString("P1", LanguageService.Culture),
                    $"The meter and percentage use the correct capacity band at {Used / 10.0}%");
            }
            foreach (var Used in new[] { 0L, 512 * GB })
            {
                this.ReadDriveUsage = Root => new(Root, Used, 512 * GB);
                await Refresh(@"Q:\Folder");
                Check(this.DriveUsageBar.Value == (Used == 0 ? 0 : 100), "The usage meter supports empty and full drives: " + Used);
            }
            this.ReadDriveUsage = _ => null;
            await Refresh(@"R:\Unavailable");
            Check(this.DriveUsageText.Text == LanguageService.Text("DriveUsageUnavailable", "R:")
                && !this.DriveUsageBar.IsVisible && !this.DriveUsagePercentageText.IsVisible && this.DriveUsagePercentageText.Text.Length == 0,
                "An unavailable drive clears stale capacity, meter and percentage");
            using var ReleaseOld = new ManualResetEventSlim();
            var OldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.ReadDriveUsage = Root =>
            {
                if (Root == @"R:\")
                {
                    OldStarted.TrySetResult();
                    ReleaseOld.Wait(TimeSpan.FromSeconds(10));
                    return new(Root, 10 * GB, 100 * GB);
                }
                return new(Root, 300 * GB, 400 * GB);
            };
            this.QueueDriveUsageRefresh(@"R:\Old", Force: true);
            var OldRead = this.DriveUsageRefreshTask;
            try
            {
                await OldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Refresh(@"S:\New");
            }
            finally { ReleaseOld.Set(); await OldRead; }
            Check(this.CurrentDriveUsage?.Root == @"S:\" && this.DriveUsageBar.Value == 75
                && this.DriveUsageBar.Foreground == this.FindResource("DriveUsageWarning")
                && this.DriveUsagePercentageText.Text == 0.75.ToString("P1", LanguageService.Culture),
                "A slow previous drive response cannot overwrite the newly selected drive");
            await this.RunSidebarDriveUsageSmokeChecksAsync(Passed);
        }
        finally
        {
            this.ReadDriveUsage = InitialReader;
            this.HostWindow.Width = InitialWidth;
            this.Settings.FontSizes = InitialFonts;
            this.ApplyUIFonts();
            this.ChangeLanguage(InitialLanguage, Persist: false);
            await Refresh(this.CurrentPath);
            this.DriveUsageTimer.Start();
        }
    }

    private async Task RunSidebarDriveUsageSmokeChecksAsync(List<string> Passed)
    {
        const long GB = 1073741824;
        var PreviousReader = this.ReadDriveUsage;
        var PreviousWidth = this.SidebarColumn.Width;
        var PreviousLanguage = LanguageService.CurrentLanguage;
        var PreviousPaths = this.Settings.ShowBookmarkPaths;
        var PreviousUsage = this.Settings.ShowSidebarDriveUsage;
        var Nodes = new[] { new FolderNode("Longest drive name (Q:)", @"Q:\", true), new FolderNode("R:", @"R:\", true), new FolderNode("Unavailable drive", @"T:\", true) };
        foreach (var Node in Nodes) { Node.IsLoaded = true; Node.Children.Clear(); this.Roots.Add(Node); }
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Sidebar drive usage check failed: " + Name);
            Passed.Add(Name);
        }
        IEnumerable<T> Children<T>(DependencyObject Parent) where T : FrameworkElement
        {
            for (var Index = 0; Index < VisualTreeHelper.GetChildrenCount(Parent); Index++)
            {
                var Child = VisualTreeHelper.GetChild(Parent, Index);
                if (Child is T Match) yield return Match;
                foreach (var Descendant in Children<T>(Child)) yield return Descendant;
            }
        }
        T Element<T>(FolderNode Node, string Name) where T : FrameworkElement
        {
            var Container = (TreeViewItem)this.FolderTree.ItemContainerGenerator.ContainerFromItem(Node);
            return Name == "SidebarDriveMeter" ? (T)Container.Template.FindName(Name, Container)
                : Children<T>(Container).Single(Item => Item.Name == Name);
        }
        bool MeterFitsHeader(FolderNode Node)
        {
            var Container = (TreeViewItem)this.FolderTree.ItemContainerGenerator.ContainerFromItem(Node);
            var Header = (FrameworkElement)Container.Template.FindName("HeaderHost", Container);
            var Meter = Element<ProgressBar>(Node, "SidebarDriveMeter");
            var Left = Meter.TranslatePoint(new Point(), this.FolderTree).X;
            var Right = Meter.TranslatePoint(new Point(Meter.ActualWidth, 0), this.FolderTree).X;
            return Math.Abs(Left - Header.TranslatePoint(new Point(), this.FolderTree).X) < .1
                && Math.Abs(Right - (this.FolderTree.ActualWidth - Container.Padding.Right - 8)) < .1;
        }
        async Task Refresh(bool Force = true)
        {
            this.QueueSidebarDriveUsageRefresh(Force);
            await this.SidebarDriveUsageTask;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        try
        {
            Check(!new AppSettings().ShowSidebarDriveUsage
                && !System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.ShowSidebarDriveUsage,
                "Fresh and older settings hide My Computer drive usage by default");
            this.ChangeShowSidebarDriveUsage(false);
            this.UpdateLayout();
            Check(Nodes.All(Node => !Element<Grid>(Node, "SidebarDriveUsage").IsVisible
                && !Element<ProgressBar>(Node, "SidebarDriveMeter").IsVisible),
                "Disabling drive usage hides capacity text, bars and percentages for every drive");
            this.ChangeShowSidebarDriveUsage(true);
            this.SidebarColumn.Width = new GridLength(390);
            var Reads = 0;
            this.ReadDriveUsage = Root =>
            {
                Interlocked.Increment(ref Reads);
                return Root == @"Q:\" ? new(Root, 256 * GB, 512 * GB) : Root == @"R:\" ? new(Root, 950 * GB, 1000 * GB) : null;
            };
            await Refresh();
            this.ChangeShowBookmarkPaths(true);
            this.SidebarColumn.Width = new GridLength(700);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
            foreach (var Node in Nodes)
            {
                var Meter = Element<ProgressBar>(Node, "SidebarDriveMeter");
                Meter.ApplyTemplate();
                Check(ReferenceEquals(Meter.Template, this.DriveUsageBar.Template) && Meter.Height == this.DriveUsageBar.Height
                    && Meter.Template.FindName("UsageFrame", Meter) is Border Frame && Frame.BorderThickness == new Thickness(1),
                    $"Tree and status meters share the same height, template and one-DIP frame (shared={ReferenceEquals(Meter.Template, this.DriveUsageBar.Template)}, height={Meter.Height}/{this.DriveUsageBar.Height}, frame={Meter.Template.FindName("UsageFrame", Meter)})");
                Check(this.UsageBarHasNoInset(Element<ProgressBar>(Node, "SidebarDriveMeter").Template),
                    "Sidebar usage fill touches the frame without a white inset");
                Check(Element<ProgressBar>(Node, "SidebarDriveMeter").Height == 10,
                    "The sidebar drive meter has a thicker ten-DIP outline and fill");
                Check(!Children<TextBlock>((TreeViewItem)this.FolderTree.ItemContainerGenerator.ContainerFromItem(Node))
                    .Any(Text => Text.IsVisible && Text.Text == Node.FullPath),
                    "My Computer drive rows do not repeat the drive root beside their name: " + Node.Name);
            }
            var FavoriteHeader = FindVisualChild<Button>(this.FavoritesList)!;
            var FavoritePath = Children<TextBlock>(FavoriteHeader).Single(Text => Text.Name == "BookmarkPath");
            Check(Nodes.All(Node => Math.Abs(Element<Grid>(Node, "SidebarDriveUsage").TranslatePoint(new Point(), this.SidebarScroll).X
                - FavoritePath.TranslatePoint(new Point(), this.SidebarScroll).X) < 1),
                "All drive capacity panels align exactly with the folder path column");
            foreach (var Width in new[] { 850d, 620d })
            {
                this.SidebarColumn.Width = new GridLength(Width);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                this.UpdateLayout();
                Check(Nodes.All(Node => Math.Abs(Element<Grid>(Node, "SidebarDriveUsage").TranslatePoint(new Point(), this.SidebarScroll).X
                    - FavoritePath.TranslatePoint(new Point(), this.SidebarScroll).X) < 1),
                    "Drive capacity and folder path columns stay aligned after resizing the sidebar to " + Width);
                Check(Nodes.Take(2).All(MeterFitsHeader),
                    "Drive meters align with header content edges while folder paths are visible: " + Width);
                if (Width == 850 && AppMain.ScreenshotPath is { } AlignedOutput)
                {
                    var Bitmap = new RenderTargetBitmap((int)this.HostWindow.ActualWidth, (int)this.HostWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    Bitmap.Render(this.HostWindow);
                    var Encoder = new PngBitmapEncoder();
                    Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                    using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(AlignedOutput)!, "SidebarAlignedPaths.png"));
                    Encoder.Save(Stream);
                }
            }
            // Reproduce a narrow My Computer section with both metadata options enabled.
            this.SidebarColumn.Width = new GridLength(390);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
            var PathViewer = FindVisualChild<ScrollViewer>(this.FolderTree)!;
            PathViewer.ScrollToLeftEnd();
            this.UpdateLayout();
            var CapacityLabel = Element<TextBlock>(Nodes[0], "SidebarDriveCapacity");
            var CapacityX = CapacityLabel.TranslatePoint(new Point(), this.FolderTree).X;
            var Extent = PathViewer.ExtentWidth;
            var OuterOffset = this.SidebarScroll.VerticalOffset;
            Check(PathViewer.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto && PathViewer.ScrollableWidth > 0,
                "Folder paths and drive usage retain horizontal scrolling in a narrow directory tree");
            ScrollInteraction.HandleWheel(CapacityLabel, -120, System.Windows.Input.ModifierKeys.Shift);
            this.UpdateLayout();
            Check(PathViewer.HorizontalOffset > 0
                && Math.Abs(CapacityLabel.TranslatePoint(new Point(), this.FolderTree).X - CapacityX + PathViewer.HorizontalOffset) < 1
                && Math.Abs(PathViewer.ExtentWidth - Extent) < 1 && this.SidebarScroll.VerticalOffset == OuterOffset,
                "Shift wheel moves the capacity column with stable widths and no vertical movement when folder paths are shown");
            ScrollInteraction.HandleWheel(CapacityLabel, 120, System.Windows.Input.ModifierKeys.Shift);
            this.UpdateLayout();
            Check(PathViewer.HorizontalOffset == 0, "Shift wheel returns the path-enabled directory tree to the left edge");
            PathViewer.ScrollToRightEnd();
            this.UpdateLayout();
            var PercentageLabel = Element<TextBlock>(Nodes[0], "SidebarDrivePercentage");
            Check(PercentageLabel.TranslatePoint(new Point(PercentageLabel.ActualWidth, 0), PathViewer).X <= PathViewer.ViewportWidth + 1,
                "The right edge exposes the complete drive percentage instead of clipping it permanently");
            PathViewer.ScrollToLeftEnd();
            this.UpdateLayout();
            this.ChangeShowBookmarkPaths(false);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
            Check(Nodes[0].DriveCapacityText == LanguageService.Text("DriveUsageCompact", 256.0, 512.0)
                && !Nodes[0].DriveCapacityText.Contains("Q:", StringComparison.Ordinal)
                && Element<TextBlock>(Nodes[0], "SidebarDriveCapacity").IsVisible
                && Element<TextBlock>(Nodes[0], "SidebarDrivePercentage").Text == 0.5.ToString("P1", LanguageService.Culture)
                && Element<ProgressBar>(Nodes[0], "SidebarDriveMeter").Value == 50,
                "Each My Computer drive shows used and total GB, a usage meter and percentage without repeating its drive letter");
            Check(Element<ProgressBar>(Nodes[0], "SidebarDriveMeter").Foreground == this.FindResource("DriveUsageLow")
                && Element<ProgressBar>(Nodes[1], "SidebarDriveMeter").Foreground == this.FindResource("DriveUsageCritical")
                && Element<ProgressBar>(Nodes[1], "SidebarDriveMeter").Value == 95,
                "Sidebar meters use each drive's own capacity and warning color independently");
            Check(Nodes[2].DriveCapacityText == "—" && !Element<ProgressBar>(Nodes[2], "SidebarDriveMeter").IsVisible
                && Element<TextBlock>(Nodes[2], "SidebarDrivePercentage").Text.Length == 0,
                "Unavailable drives show a placeholder and hide stale meter and percentage values");
            var ReadCount = Reads;
            await Refresh(Force: false);
            Check(Reads == ReadCount && ReadCount == 3, "Sidebar capacity reads are cached across refreshes without new disk requests");
            foreach (var Language in LanguageService.SupportedLanguages)
            {
                this.ChangeLanguage(Language.Code, Persist: false);
                Check(Element<TextBlock>(Nodes[0], "SidebarDriveCapacity").Text == LanguageService.Text("DriveUsageCompact", 256.0, 512.0)
                    && Element<TextBlock>(Nodes[0], "SidebarDrivePercentage").Text == 0.5.ToString("P1", LanguageService.Culture),
                    Language.Code + " refreshes sidebar capacity and percentage formatting immediately");
            }
            this.ChangeLanguage(PreviousLanguage, Persist: false);
            foreach (var (Used, Expected) in new[] { (0L, "0.0"), (55L, "5.5"), (100L, "10.0"), (1000L, "100.0") })
            {
                Nodes[0].UpdateDriveUsage(new(@"Q:\", Used * GB, 1000 * GB));
                Check(Element<TextBlock>(Nodes[0], "SidebarDrivePercentage").Text.Contains(Expected, StringComparison.Ordinal),
                    "Sidebar percentages retain exactly one decimal place at " + Expected + " percent");
            }
            Nodes[0].UpdateDriveUsage(new(@"Q:\", 55 * GB, 1000 * GB));
            async Task CheckUsageLayoutAsync(double FontSize)
            {
                // Toggling the preference intentionally refreshes drive capacity.
                // Reapply the single-digit fixture after that refresh has finished.
                await this.SidebarDriveUsageTask;
                Nodes[0].UpdateDriveUsage(new(@"Q:", 55 * GB, 1000 * GB));
                this.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                this.UpdateLayout();
                var Capacity = Element<TextBlock>(Nodes[0], "SidebarDriveCapacity");
                var Meter = Element<ProgressBar>(Nodes[0], "SidebarDriveMeter");
                var Percentage = Element<TextBlock>(Nodes[0], "SidebarDrivePercentage");
                var Summary = Element<Grid>(Nodes[0], "SidebarDriveUsage");
                var CapacityRight = Capacity.TranslatePoint(new Point(Capacity.ActualWidth, 0), Summary).X;
                var PercentLeft = Percentage.TranslatePoint(new Point(), Summary).X;
                Check(Capacity.FontSize == FontSize && Percentage.FontSize == FontSize
                    && Element<TextBlock>(Nodes[0], "SidebarDriveName").FontSize == FontSize,
                    "Drive usage and percentage always match the sidebar font size: " + FontSize);
                Check(CapacityRight + 11 <= PercentLeft
                    && Math.Abs(Capacity.TranslatePoint(new Point(0, Capacity.ActualHeight / 2), Summary).Y
                        - Percentage.TranslatePoint(new Point(0, Percentage.ActualHeight / 2), Summary).Y) < 1
                    && Meter.TranslatePoint(new Point(), this.FolderTree).Y
                        >= Capacity.TranslatePoint(new Point(0, Capacity.ActualHeight), this.FolderTree).Y
                    && MeterFitsHeader(Nodes[0]),
                    "Capacity and percentage retain their row while the lower meter aligns with the header content edges: " + FontSize
                    + $"; capacity right={CapacityRight}, percentage left={PercentLeft}, meter left={Meter.TranslatePoint(new Point(), this.FolderTree).X}, meter width={Meter.ActualWidth}, tree width={this.FolderTree.ActualWidth}, meter top={Meter.TranslatePoint(new Point(), this.FolderTree).Y}, text bottom={Capacity.TranslatePoint(new Point(0, Capacity.ActualHeight), this.FolderTree).Y}");
                var NameWidths = Nodes.Select(Node => Element<TextBlock>(Node, "SidebarDriveName").ActualWidth).ToArray();
                // Inline-rename labels size to their text; their parent grid owns the aligned column.
                var NameColumnWidths = Nodes.Select(Node => FindAncestor<Grid>(Element<TextBlock>(Node, "SidebarDriveName"))!.ColumnDefinitions[0].ActualWidth).ToArray();
                var CapacityLefts = Nodes.Select(Node => Element<TextBlock>(Node, "SidebarDriveCapacity").TranslatePoint(new Point(), this.FolderTree).X).ToArray();
                var MeterLefts = Nodes.Take(2).Select(Node => Element<ProgressBar>(Node, "SidebarDriveMeter").TranslatePoint(new Point(), this.FolderTree).X).ToArray();
                var PercentLefts = Nodes.Take(2).Select(Node => Element<TextBlock>(Node, "SidebarDrivePercentage").TranslatePoint(new Point(), this.FolderTree).X).ToArray();
                Check(NameColumnWidths.Max() - NameColumnWidths.Min() < 1 && CapacityLefts.Max() - CapacityLefts.Min() < 1
                    && MeterLefts.Max() - MeterLefts.Min() < 1 && PercentLefts.Max() - PercentLefts.Min() < 1,
                    "Drive names, capacity, meters and percentages share aligned columns: " + FontSize
                    + "; name columns=" + string.Join(",", NameColumnWidths) + "; capacity=" + string.Join(",", CapacityLefts)
                    + "; meters=" + string.Join(",", MeterLefts) + "; percentages=" + string.Join(",", PercentLefts));
                var Percentages = Nodes.Take(2).Select(Node => Element<TextBlock>(Node, "SidebarDrivePercentage")).ToArray();
                var PercentRights = Percentages.Select(Text => Text.TranslatePoint(new Point(Text.ActualWidth, 0), this.FolderTree).X).ToArray();
                Check(Percentages.All(Text => Text.TextAlignment == TextAlignment.Right)
                    && PercentRights.Max() - PercentRights.Min() < 1 && Percentages[0].Text.Length < Percentages[1].Text.Length,
                    "Single- and double-digit percentages align at their right edges: " + FontSize);
                var NaturalNameWidth = Nodes.Max(Node =>
                {
                    var Name = Element<TextBlock>(Node, "SidebarDriveName");
                    return new FormattedText(Node.Name, LanguageService.Culture, Name.FlowDirection,
                        new Typeface(Name.FontFamily, Name.FontStyle, Name.FontWeight, Name.FontStretch),
                        Name.FontSize, Name.Foreground, VisualTreeHelper.GetDpi(Name).PixelsPerDip).WidthIncludingTrailingWhitespace;
                });
                Check(Math.Abs(NameWidths[0] - NaturalNameWidth) < 2,
                    "The name column fits the longest drive label without including expanded folder names");
            }
            await CheckUsageLayoutAsync(this.Settings.FontSizes!["Sidebar"]);
            await this.RunPreferencesSmokeChecksAsync(Passed, FontSizes: new Dictionary<string, double> { ["Sidebar"] = 20 });
            await CheckUsageLayoutAsync(20);
            await this.RunPreferencesSmokeChecksAsync(Passed, FontSizes: new Dictionary<string, double> { ["Sidebar"] = 13 });
            await CheckUsageLayoutAsync(13);
            double? FixedCapacityLeft = null;

            foreach (var Width in new[] { 690.0, 390.0, 238.0, 690.0 })
            {
                this.SidebarColumn.Width = new GridLength(Width);
                var Viewer = FindVisualChild<ScrollViewer>(this.FolderTree)!;
                Viewer.ScrollToLeftEnd();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                this.UpdateLayout();
                await CheckUsageLayoutAsync(13);
                var Summary = Element<Grid>(Nodes[0], "SidebarDriveUsage");
                var Right = Summary.TranslatePoint(new Point(Summary.ActualWidth, 0), this.FolderTree).X;
                Check(Summary.IsVisible && Summary.ActualWidth > 0 && Right <= Viewer.ExtentWidth + 2,
                    "Narrow sidebars retain the full-size usage row with horizontal scrolling available: " + Width);
                var CapacityLeft = Element<TextBlock>(Nodes[0], "SidebarDriveCapacity").TranslatePoint(new Point(), this.FolderTree).X;
                var MeterLeft = Element<ProgressBar>(Nodes[0], "SidebarDriveMeter").TranslatePoint(new Point(), this.FolderTree).X;
                FixedCapacityLeft ??= CapacityLeft;

                Check(Math.Abs(CapacityLeft - FixedCapacityLeft.Value) < 1 && Nodes.Take(2).All(MeterFitsHeader),
                    "Resizing keeps capacity text in place and aligns the lower meter with the drive content row: " + Width);
            }
            this.SidebarColumn.Width = new GridLength(690);
            this.UpdateLayout();
            if (AppMain.ScreenshotPath is { } Output)
            {
                var Bitmap = new RenderTargetBitmap((int)this.HostWindow.ActualWidth, (int)this.HostWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(this.HostWindow);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, "SidebarDriveUsage.png"));
                Encoder.Save(Stream);
            }
            this.ChangeShowSidebarDriveUsage(false);
            this.UpdateLayout();
            Check(Nodes.All(Node => !Element<Grid>(Node, "SidebarDriveUsage").IsVisible
                && !Element<ProgressBar>(Node, "SidebarDriveMeter").IsVisible),
                "Turning off drive usage hides both populated capacity text and the separate lower bars");
            this.ChangeShowSidebarDriveUsage(true);
            await this.SidebarDriveUsageTask;
            Nodes[0].Children.Add(new FolderNode("Owned child", Path.Combine(AppMain.SmokeRoot!, "Owned child")));
            Nodes[0].IsExpanded = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(200);
            this.UpdateLayout();
            var ChildItem = (TreeViewItem)((TreeViewItem)this.FolderTree.ItemContainerGenerator.ContainerFromItem(Nodes[0]))
                .ItemContainerGenerator.ContainerFromItem(Nodes[0].Children[0]);
            var RootMeter = Element<ProgressBar>(Nodes[0], "SidebarDriveMeter");
            Check(ChildItem.TranslatePoint(new Point(), this.FolderTree).Y
                >= RootMeter.TranslatePoint(new Point(0, RootMeter.ActualHeight), this.FolderTree).Y
                && !Children<ProgressBar>(ChildItem).Single(Item => Item.Name == "SidebarDriveMeter").IsVisible,
                "Expanded child folders start below the full-width drive meter and do not show their own meter");
            Nodes[0].IsExpanded = false;
            this.ReadDriveUsage = _ => null;
            await Refresh();
            Check(Nodes.All(Node => !Node.HasDriveUsage && Node.DriveUsagePercentageText.Length == 0),
                "Refreshing unavailable sidebar drives removes their previously cached usage");
        }
        finally
        {
            ++this.SidebarDriveUsageEpoch;
            foreach (var Node in Nodes) this.Roots.Remove(Node);
            this.ReadDriveUsage = PreviousReader;
            this.SidebarColumn.Width = PreviousWidth;
            this.ChangeShowBookmarkPaths(PreviousPaths);
            this.ChangeShowSidebarDriveUsage(PreviousUsage);
            this.ChangeLanguage(PreviousLanguage, Persist: false);
        }
    }
}
