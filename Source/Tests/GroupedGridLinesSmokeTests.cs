using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunGroupedGridLinesSmokeAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Grouped grid-lines check failed: " + Name);
            Passed.Add(Name);
        }
        IEnumerable<T> Visuals<T>(DependencyObject Parent) where T : DependencyObject
        {
            for (var Index = 0; Index < VisualTreeHelper.GetChildrenCount(Parent); Index++)
            {
                var Child = VisualTreeHelper.GetChild(Parent, Index);
                if (Child is T Match) yield return Match;
                foreach (var Nested in Visuals<T>(Child)) yield return Nested;
            }
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Grouped Grid Lines")).FullName;
        for (var Index = 0; Index < 72; Index++)
        {
            var PathValue = Path.Combine(Root, $"{(char)('A' + Index % 4)} Sample {Index:00}" + new[] { ".txt", ".bin", ".md" }[Index % 3]);
            File.WriteAllBytes(PathValue, new byte[Index % 2 == 0 ? 20 : 20000]);
            File.SetLastWriteTime(PathValue, DateTime.Today.AddDays(-Index % 3));
        }
        var Small = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Short Grouped List")).FullName;
        File.WriteAllText(Path.Combine(Small, "Only.txt"), "Owned grid fixture");
        var Configuration = Path.Combine(AppMain.SmokeRoot!, "GroupedGridSettings.json");
        File.WriteAllText(Configuration, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ShowGridLines = true, ExitOnClose = true }));
        var Probe = new MainWindow(Configuration, RestoreWorkspace: true) { Owner = this.HostWindow, Width = 1280, Height = 820, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            Probe.Show(); await Probe.WorkspaceRestored; var Page = Probe.ActiveTab!; await Page.WaitUntilReadyAsync();
            async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); Probe.UpdateLayout(); }
            BitmapSource Render(double Scale = 1)
            {
                var Dpi = VisualTreeHelper.GetDpi(Page.FileGridLines);
                var Bitmap = new RenderTargetBitmap((int)Math.Ceiling(Page.FileGridLines.ActualWidth * Dpi.DpiScaleX * Scale),
                    (int)Math.Ceiling(Page.FileGridLines.ActualHeight * Dpi.DpiScaleY * Scale), 96 * Dpi.DpiScaleX * Scale, 96 * Dpi.DpiScaleY * Scale, PixelFormats.Pbgra32);
                Bitmap.Render(Page.FileGridLines); return Bitmap;
            }
            bool Ink(BitmapSource Bitmap, Point Point)
            {
                var X = (int)Math.Round(Point.X * Bitmap.DpiX / 96);
                var Y = (int)Math.Round(Point.Y * Bitmap.DpiY / 96);
                var Left = Math.Max(0, X - 1); var Top = Math.Max(0, Y - 1);
                var Width = Math.Min(Bitmap.PixelWidth, X + 2) - Left; var Height = Math.Min(Bitmap.PixelHeight, Y + 2) - Top;
                if (Width <= 0 || Height <= 0) return false;
                var Pixels = new byte[Width * Height * 4]; Bitmap.CopyPixels(new Int32Rect(Left, Top, Width, Height), Pixels, Width * 4, 0);
                return Enumerable.Range(0, Width * Height).Any(Index => Pixels[Index * 4 + 3] > 127);
            }
            DataGridRow[] Rows() => Visuals<DataGridRow>(Page.FileGrid).Where(Row => Row.IsVisible && Row.ActualHeight > 0
                && Row.TranslatePoint(new Point(), Page.FileGridLines).Y > 35
                && Row.TranslatePoint(new Point(0, Row.ActualHeight), Page.FileGridLines).Y < Page.FileGridLines.ActualHeight - 25).ToArray();
            void CheckGuides(string Stage, double Scale = 1)
            {
                var Bitmap = Render(Scale); var VisibleRows = Rows();
                Check(Page.FileGridLines.IsVisible && VisibleRows.Length > 0, Stage + " keeps the enabled grid visible");
                Check(VisibleRows.All(Row => Ink(Bitmap, new Point(80, Row.TranslatePoint(new Point(0, Row.ActualHeight), Page.FileGridLines).Y))),
                    Stage + " horizontal lines align with actual file-row bottoms");
                var Boundaries = VisibleRows.SelectMany(Row => Visuals<DataGridCell>(Row)
                    .Where(Cell => Cell.Column.Visibility == Visibility.Visible && Cell.ActualWidth > 0)
                    .Select(Cell => Cell.TranslatePoint(new Point(Cell.ActualWidth, Cell.ActualHeight / 2), Page.FileGridLines)))
                    .Where(Point => Point.X > 5 && Point.X < Page.FileGridLines.ActualWidth - 25).ToArray();
                Check(Boundaries.Length > 0 && Boundaries.All(Point => Ink(Bitmap, Point)), Stage + " vertical lines align with visible cell boundaries (missing="
                    + string.Join(';', Boundaries.Where(Point => !Ink(Bitmap, Point)).Take(8)) + ")");
                var Headers = Visuals<TextBlock>(Page.FileGrid).Where(Text => Text.IsVisible && Text.DataContext is CollectionViewGroup)
                    .Select(Text => Text.TranslatePoint(new Point(0, Text.ActualHeight / 2), Page.FileGridLines).Y)
                    .Where(Y => Y > 30 && Y < Page.FileGridLines.ActualHeight - 25).ToArray();
                Check(Headers.All(Y => Enumerable.Range(0, 12).All(Index => !Ink(Bitmap, new Point(40 + Index * (Page.FileGridLines.ActualWidth - 80) / 12, Y)))),
                    Stage + " group captions stay clear of row and column lines");
            }
            var Name = Page.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Name));
            Name.Width = new DataGridLength(260);
            Page.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.TypeName)).Width = new DataGridLength(160);
            foreach (var Key in BackgroundSortKeys)
            {
                await Page.ExecuteBackgroundCommandAsync("CDir.Group." + Key); await Settle();
                CheckGuides("Grouping by " + Key);
            }
            await Page.ExecuteBackgroundCommandAsync("CDir.Group.TypeName"); await Settle();
            var Selected = Page.View!.Cast<FileEntry>().First(); Page.SelectPath(Selected.FullPath);
            var Scroll = FindVisualChild<ScrollViewer>(Page.FileGrid)!;
            Scroll.ScrollToTop(); await Settle();
            var HeightBefore = Rows()[0].ActualHeight;
            Page.ChangeGridLines(false); await Settle();
            Check(Page.FileGridLines.Visibility == Visibility.Collapsed, "Disabling grid lines also hides grouped guides");
            Page.ChangeGridLines(true); await Settle(); CheckGuides("Re-enabling grouped guides");
            Check(Page.SelectedEntries().Single() == Selected && Math.Abs(Rows()[0].ActualHeight - HeightBefore) < .01,
                "Grouped grid toggles preserve selection and row height");
            if (AppMain.ScreenshotPath is { } Output)
            {
                var Bitmap = new RenderTargetBitmap((int)Probe.ActualWidth, (int)Probe.ActualHeight, 96, 96, PixelFormats.Pbgra32); Bitmap.Render(Probe);
                var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, "GroupedGridLines.png")); Encoder.Save(Stream);
            }
            Scroll.ScrollToVerticalOffset(12); await Settle(); CheckGuides("Grouped vertical scrolling");
            Page.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Size)).DisplayIndex = 0;
            Page.ChangeColumnVisibility(nameof(FileEntry.TypeName), false); await Settle(); CheckGuides("Reordered and hidden columns");
            Name.Width = new DataGridLength(600); await Settle();
            Scroll.ScrollToHorizontalOffset(240); await Settle(); CheckGuides("Grouped horizontal scrolling");
            Check(Scroll.HorizontalOffset > 0, "Wide grouped columns exercise a nonzero horizontal scroll offset");
            Name.Width = new DataGridLength(260); Scroll.ScrollToHorizontalOffset(0);
            Page.ChangeColumnVisibility(nameof(FileEntry.TypeName), true);
            Page.Settings.FontSizes = new() { ["FileList"] = 24 }; Page.ApplyUIFonts(); await Settle(); CheckGuides("Large file font");
            CheckGuides("150 percent guide rendering", 1.5);
            await Page.ExecuteBackgroundCommandAsync("CDir.Group.None"); await Settle(); CheckGuides("Removing grouping");
            await Page.ExecuteBackgroundCommandAsync("CDir.Group.TypeName");
            Page.ChangeFileView(FileViewMode.LargeIcons); await Settle();
            Check(Page.FileGridLines.Visibility == Visibility.Collapsed, "Grouped icon views do not display details-grid guides");
            Page.ChangeFileView(FileViewMode.Details); await Settle(); CheckGuides("Returning to grouped Details");
            await Page.NavigateAsync(Small); await Settle(); CheckGuides("Short grouped list");
            var LastRow = Rows().Single(); var Bottom = LastRow.TranslatePoint(new Point(70, LastRow.ActualHeight), Page.FileGridLines);
            Check(Ink(Render(), new Point(Bottom.X, Bottom.Y + LastRow.ActualHeight * 2)), "Grid rows continue through the empty area after the last group");
            Page.SearchBox.Text = "__no_grouped_grid_match__"; await Page.WaitForSearchSmokeAsync(); await Settle();
            var EmptyBitmap = Render();
            var EmptyPixels = new byte[EmptyBitmap.PixelWidth * EmptyBitmap.PixelHeight * 4];
            EmptyBitmap.CopyPixels(EmptyPixels, EmptyBitmap.PixelWidth * 4, 0);
            var HorizontalLines = Enumerable.Range(0, EmptyBitmap.PixelHeight).Count(Y => Enumerable.Range(0, EmptyBitmap.PixelWidth)
                .Count(X => EmptyPixels[(Y * EmptyBitmap.PixelWidth + X) * 4 + 3] > 127) > EmptyBitmap.PixelWidth / 2);
            var VerticalLines = Enumerable.Range(0, EmptyBitmap.PixelWidth).Count(X => Enumerable.Range(0, EmptyBitmap.PixelHeight)
                .Count(Y => EmptyPixels[(Y * EmptyBitmap.PixelWidth + X) * 4 + 3] > 127) > EmptyBitmap.PixelHeight / 2);
            Check(Page.FileGrid.Items.Count == 0 && Page.FileGridLines.IsVisible
                && HorizontalLines >= 5 && VerticalLines >= 2,
                $"A grouped search with no results retains the blank-area grid (items={Page.FileGrid.Items.Count}, visible={Page.FileGridLines.IsVisible}, rows={HorizontalLines}, columns={VerticalLines})");
        }
        finally { Probe.RequestExit(); this.ApplyUIFonts(); }
    }
}
