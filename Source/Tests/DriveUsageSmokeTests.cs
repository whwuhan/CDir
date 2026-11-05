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
            => Children<T>((TreeViewItem)this.FolderTree.ItemContainerGenerator.ContainerFromItem(Node)).Single(Item => Item.Name == Name);
        async Task Refresh(bool Force = true)
        {
            this.QueueSidebarDriveUsageRefresh(Force);
            await this.SidebarDriveUsageTask;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        try
        {
            this.SidebarColumn.Width = new GridLength(390);
            var Reads = 0;
            this.ReadDriveUsage = Root =>
            {
                Interlocked.Increment(ref Reads);
                return Root == @"Q:\" ? new(Root, 256 * GB, 512 * GB) : Root == @"R:\" ? new(Root, 950 * GB, 1000 * GB) : null;
            };
            await Refresh();
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
            void CheckUsageLayout(double FontSize)
            {
                this.UpdateLayout();
                var Capacity = Element<TextBlock>(Nodes[0], "SidebarDriveCapacity");
                var Meter = Element<ProgressBar>(Nodes[0], "SidebarDriveMeter");
                var Percentage = Element<TextBlock>(Nodes[0], "SidebarDrivePercentage");
                var Summary = Element<Grid>(Nodes[0], "SidebarDriveUsage");
                var CapacityRight = Capacity.TranslatePoint(new Point(Capacity.ActualWidth, 0), Summary).X;
                var MeterLeft = Meter.TranslatePoint(new Point(), Summary).X;
                var MeterRight = Meter.TranslatePoint(new Point(Meter.ActualWidth, 0), Summary).X;
                var PercentLeft = Percentage.TranslatePoint(new Point(), Summary).X;
                Check(Capacity.FontSize == FontSize && Percentage.FontSize == FontSize
                    && Element<TextBlock>(Nodes[0], "SidebarDriveName").FontSize == FontSize,
                    "Drive usage and percentage always match the sidebar font size: " + FontSize);
                Check(CapacityRight + 11 <= MeterLeft && MeterRight + 11 <= PercentLeft
                    && Math.Abs(Capacity.TranslatePoint(new Point(0, Capacity.ActualHeight / 2), Summary).Y
                        - Percentage.TranslatePoint(new Point(0, Percentage.ActualHeight / 2), Summary).Y) < 1
                    && Summary.ActualHeight < Capacity.ActualHeight * 1.5,
                    "Usage, meter and percentage appear left to right on one spacious row");
                var NameWidths = Nodes.Select(Node => Element<TextBlock>(Node, "SidebarDriveName").ActualWidth).ToArray();
                var CapacityLefts = Nodes.Select(Node => Element<TextBlock>(Node, "SidebarDriveCapacity").TranslatePoint(new Point(), this.FolderTree).X).ToArray();
                var MeterLefts = Nodes.Take(2).Select(Node => Element<ProgressBar>(Node, "SidebarDriveMeter").TranslatePoint(new Point(), this.FolderTree).X).ToArray();
                var PercentLefts = Nodes.Take(2).Select(Node => Element<TextBlock>(Node, "SidebarDrivePercentage").TranslatePoint(new Point(), this.FolderTree).X).ToArray();
                Check(NameWidths.Max() - NameWidths.Min() < 1 && CapacityLefts.Max() - CapacityLefts.Min() < 1
                    && MeterLefts.Max() - MeterLefts.Min() < 1 && PercentLefts.Max() - PercentLefts.Min() < 1,
                    "Drive names, capacity, meters and percentages share aligned columns: " + FontSize);
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
            CheckUsageLayout(this.Settings.FontSizes!["Sidebar"]);
            await this.RunPreferencesSmokeChecksAsync(Passed, FontSizes: new Dictionary<string, double> { ["Sidebar"] = 20 });
            CheckUsageLayout(20);
            await this.RunPreferencesSmokeChecksAsync(Passed, FontSizes: new Dictionary<string, double> { ["Sidebar"] = 13 });
            CheckUsageLayout(13);
            double? FixedCapacityLeft = null;
            double? FixedMeterLeft = null;
            foreach (var Width in new[] { 690.0, 390.0, 238.0, 690.0 })
            {
                this.SidebarColumn.Width = new GridLength(Width);
                var Viewer = FindVisualChild<ScrollViewer>(this.FolderTree)!;
                Viewer.ScrollToLeftEnd();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                this.UpdateLayout();
                CheckUsageLayout(13);
                var Summary = Element<Grid>(Nodes[0], "SidebarDriveUsage");
                var Right = Summary.TranslatePoint(new Point(Summary.ActualWidth, 0), this.FolderTree).X;
                Check(Summary.IsVisible && Summary.ActualWidth > 0 && Right <= Viewer.ExtentWidth + 2,
                    "Narrow sidebars retain the full-size usage row with horizontal scrolling available: " + Width);
                var CapacityLeft = Element<TextBlock>(Nodes[0], "SidebarDriveCapacity").TranslatePoint(new Point(), this.FolderTree).X;
                var MeterLeft = Element<ProgressBar>(Nodes[0], "SidebarDriveMeter").TranslatePoint(new Point(), this.FolderTree).X;
                FixedCapacityLeft ??= CapacityLeft;
                FixedMeterLeft ??= MeterLeft;
                Check(Math.Abs(CapacityLeft - FixedCapacityLeft.Value) < 1 && Math.Abs(MeterLeft - FixedMeterLeft.Value) < 1,
                    "Shrinking and expanding the sidebar keeps capacity and meter columns fixed: " + Width);
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
            this.ChangeLanguage(PreviousLanguage, Persist: false);
        }
    }
}
