using CDir.Models;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunGridLinesSmokeChecksAsync(List<string> Passed)
    {
        var InitialEnabled = this.Settings.ShowGridLines;
        var InitialPath = this.CurrentPath;
        var InitialSearch = this.SearchBox.Text;
        var InitialColumnWidth = this.FileGrid.Columns[0].Width;
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirGridLines-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Fixture);
        var SettingsFile = Path.Combine(Fixture, "settings.json");
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Grid-lines UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        (double[] Columns, double[] Rows) RenderGuides()
        {
            // Inspect physical pixels so a one-pixel guide is not split across
            // two faint pixels when the desktop uses scaling above 100%.
            var Dpi = VisualTreeHelper.GetDpi(this.FileGridLines);
            var Width = (int)Math.Ceiling(this.FileGridLines.ActualWidth * Dpi.DpiScaleX);
            var Height = (int)Math.Ceiling(this.FileGridLines.ActualHeight * Dpi.DpiScaleY);
            var Bitmap = new RenderTargetBitmap(Width, Height, 96 * Dpi.DpiScaleX, 96 * Dpi.DpiScaleY, PixelFormats.Pbgra32);
            Bitmap.Render(this.FileGridLines);
            var Pixels = new byte[Width * Height * 4];
            Bitmap.CopyPixels(Pixels, Width * 4, 0);
            var Columns = new int[Width];
            var Rows = new int[Height];
            for (var Y = 0; Y < Height; Y++)
                for (var X = 0; X < Width; X++)
                    if (Pixels[(Y * Width + X) * 4 + 3] > 127) { Columns[X]++; Rows[Y]++; }
            return (Enumerable.Range(0, Width).Where(X => Columns[X] > Height / 2).Select(X => X / Dpi.DpiScaleX).ToArray(),
                Enumerable.Range(0, Height).Where(Y => Rows[Y] > Width / 2).Select(Y => Y / Dpi.DpiScaleY).ToArray());
        }
        void Screenshot(string Name)
        {
            if (AppMain.ScreenshotPath is not { } Destination) return;
            var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            Bitmap.Render(this);
            var Encoder = new PngBitmapEncoder();
            Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
            using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Destination)!, Name + ".png"));
            Encoder.Save(Stream);
        }
        try
        {
            Check(new AppSettings().ShowGridLines && JsonSerializer.Deserialize<AppSettings>("{\"ShowHidden\":true}")!.ShowGridLines,
                "Fresh and older settings use the saved baseline with grid lines on");
            Check(!JsonSerializer.Deserialize<AppSettings>("{\"ShowGridLines\":false}")!.ShowGridLines,
                "An explicitly disabled grid preference remains disabled");
            this.ChangeGridLines(true);
            await Settle();
            var Guides = RenderGuides();
            Screenshot("GridLines-On");
            Check(Guides.Columns.Length >= 3 && Guides.Rows.Length >= 5
                && Guides.Rows[^1] > this.FileGridLines.ActualHeight - 60 && Guides.Rows[0] > 10,
                $"Enabled grid renders horizontal and vertical lines through the empty viewport below the file rows (columns={string.Join(',', Guides.Columns)}, rows={string.Join(',', Guides.Rows)}, height={this.FileGridLines.ActualHeight})");
            Check(!this.FileGridLines.IsHitTestVisible && !this.FileGridLines.Focusable,
                "Grid lines do not intercept file selection column resizing or context menus");
            Check(this.SaveSettings(SettingsFile) && ReadSettings(SettingsFile).ShowGridLines,
                "Enabled grid lines survive saving and reading settings");
            // Reopening Preferences must reflect the persisted enabled value as well.
            await this.RunPreferencesSmokeChecksAsync(Passed);
            this.Settings.ShowGridLines = ReadSettings(SettingsFile).ShowGridLines;
            this.ApplyGridLines();
            await Settle();
            Check(RenderGuides().Rows.Length >= 5 && this.FileGridLines.IsVisible,
                "Loading a saved enabled preference restores the rendered grid");

            this.FileGrid.Columns[0].Width = new DataGridLength(this.FileGrid.Columns[0].ActualWidth + 40);
            await Settle();
            var Resized = RenderGuides();
            Check(Math.Abs(Resized.Columns[0] - Guides.Columns[0] - 40) <= 1,
                "Vertical grid lines follow resized column boundaries");
            this.FileGrid.Columns[0].Width = new DataGridLength(this.FileGrid.ActualWidth + 120);
            await Settle();
            var BeforeScroll = RenderGuides();
            var Scroll = FindVisualChild<ScrollViewer>(this.FileGrid)!;
            Scroll.ScrollToHorizontalOffset(240);
            await Settle();
            var Scrolled = RenderGuides();
            Check(Scroll.HorizontalOffset > 0 && Scrolled.Columns.Length > 0
                && !Scrolled.Columns.SequenceEqual(BeforeScroll.Columns),
                "Horizontal scrolling keeps the guides aligned with the scrolled columns");
            this.FileGrid.Columns[0].Width = InitialColumnWidth;
            Scroll.ScrollToHorizontalOffset(0);
            await Settle();

            var Empty = Directory.CreateDirectory(Path.Combine(Fixture, "empty")).FullName;
            this.SearchBox.Text = "";
            Check(await this.NavigateAsync(Empty), "Grid-lines test opens an owned empty folder");
            await Settle();
            Check(this.FileGrid.Items.Count == 0 && RenderGuides().Rows.Length >= 5 && RenderGuides().Columns.Length >= 3,
                "An empty folder retains the full row and column grid");
            Screenshot("GridLines-Empty");
            for (var Index = 0; Index < 70; Index++) File.WriteAllText(Path.Combine(Fixture, $"item-{Index:00}.txt"), "test");
            Check(await this.NavigateAsync(Fixture), "Grid-lines test opens an owned scrolling file list");
            await Settle();
            Scroll.ScrollToVerticalOffset(20);
            await Settle();
            var Row = (DataGridRow)this.FileGrid.ItemContainerGenerator.ContainerFromIndex((int)Scroll.VerticalOffset);
            var RowBottom = Row.TranslatePoint(new Point(0, Row.ActualHeight), this.FileGridLines).Y;
            Screenshot("GridLines-Scrolled");
            Check(Scroll.VerticalOffset > 0 && RenderGuides().Rows.Any(Y => Math.Abs(Y - (Math.Round(RowBottom) - 1)) <= 1),
                "Horizontal guides remain aligned with actual rows after vertical scrolling");
            this.SearchBox.Text = "no-matching-gridline-file";
            await Settle();
            Check(this.FileGrid.Items.Count == 0 && RenderGuides().Rows.Length >= 5,
                "Filtering to no results keeps the blank-area grid visible");
            this.ChangeGridLines(false);
            await Settle();
            Check(this.FileGridLines.Visibility == Visibility.Collapsed
                && this.SaveSettings(SettingsFile) && !ReadSettings(SettingsFile).ShowGridLines,
                "Turning grid lines off hides the guides and persists the disabled preference");
            Screenshot("GridLines-Off");
        }
        finally
        {
            this.SearchBox.Text = "";
            this.FileGrid.Columns[0].Width = InitialColumnWidth;
            this.ChangeGridLines(InitialEnabled);
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.SearchBox.Text = InitialSearch;
            await Settle();
        }
    }
}
