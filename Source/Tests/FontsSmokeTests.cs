using CDir.Models;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunFontsSmokeChecksAsync(List<string> Passed)
    {
        var InitialFonts = this.Settings.FontSizes is null ? null : new Dictionary<string, double>(this.Settings.FontSizes);
        var InitialPath = this.CurrentPath;
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirFonts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Fixture);
        File.WriteAllText(Path.Combine(Fixture, "Example.txt"), "Font preview");
        var SettingsFile = Path.Combine(Fixture, "settings.json");
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Fonts UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        IEnumerable<T> Children<T>(DependencyObject Parent) where T : DependencyObject
        {
            for (var Index = 0; Index < VisualTreeHelper.GetChildrenCount(Parent); Index++)
            {
                var Child = VisualTreeHelper.GetChild(Parent, Index);
                if (Child is T Match) yield return Match;
                foreach (var Descendant in Children<T>(Child)) yield return Descendant;
            }
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
            var ApplicationVersion = typeof(ExplorerTab).Assembly.GetName().Version!;
            Check(this.HostWindow.TitleVersion.IsVisible
                && this.HostWindow.TitleVersion.Text == $"v{ApplicationVersion.Major}.{ApplicationVersion.Minor}.{ApplicationVersion.Build}"
                && this.HostWindow.TitleVersion.TranslatePoint(new Point(), this.HostWindow.TitleBrand).X >= this.HostWindow.TitleBrand.ActualWidth,
                "The title displays the three-part compiled release version after the application name");
            this.Settings.FontSizes = JsonSerializer.Deserialize<AppSettings>("{}")!.FontSizes;
            this.ApplyUIFonts();
            Check(this.GetFontOptions().All(Option => Option.Size == Option.DefaultSize),
                "Older settings restore the original font sizes for all nine panels");
            TextBlock TabCaption()
            {
                this.HostWindow.UpdateLayout();
                var Container = (DependencyObject)this.HostWindow.TabHeaders.ItemContainerGenerator.ContainerFromIndex(0);
                return Children<TextBlock>(Container).First(Text => Text.Text == this.TabTitle);
            }
            Check(this.Settings.FontSizes!["Tabs"] == 13 && TabCaption().FontSize == 13,
                "The new tab font defaults to 13 for legacy settings");
            await this.RunPreferencesSmokeChecksAsync(Passed, FontSizes: new Dictionary<string, double> { ["Tabs"] = 20 });
            await Settle();
            Check(TabCaption().FontSize == 20 && this.HostWindow.FileMenuButton.FontSize == 13 && this.FileGrid.FontSize == 13,
                "Preferences UI changes the tab font independently of menu and file fonts");
            await this.RunPreferencesSmokeChecksAsync(Passed, FontSizes: new Dictionary<string, double> { ["Menus"] = 18 });
            await Settle();
            Check(TabCaption().FontSize == 20 && this.HostWindow.FileMenuButton.FontSize == 18,
                "Changing menu font leaves the tab font unchanged");
            this.Settings.FontSizes = JsonSerializer.Deserialize<AppSettings>("{}")!.FontSizes;
            this.ApplyUIFonts();
            Check(this.HostWindow.TitleBrand.FontSize == this.Settings.FontSizes!["Sidebar"]
                && this.HostWindow.TitleVersion.FontSize == this.HostWindow.TitleBrand.FontSize && this.HostWindow.TitleIcon.Width == 16 && this.HostWindow.TitleIcon.Height == 16,
                "The default title and version match sidebar text with a compact sidebar-sized icon");
            File.WriteAllText(SettingsFile, "{\"FontSizes\":{\"TitleBar\":15,\"Sidebar\":13}}");
            Check(ReadSettings(SettingsFile).FontSizes!["TitleBar"] == 13,
                "Upgrading old settings reduces the title to the existing sidebar font size");
            File.WriteAllText(SettingsFile, "{\"TitleBarFontRevision\":1,\"FontSizes\":{\"TitleBar\":20,\"Sidebar\":13}}");
            Check(ReadSettings(SettingsFile).FontSizes!["TitleBar"] == 20,
                "Explicit title font choices after the compact-title upgrade survive loading");
            File.WriteAllText(SettingsFile, "null");
            Check(ReadSettings(SettingsFile).FontSizes!["TitleBar"] == 13,
                "Null legacy settings continue to restore the compact defaults");
            Check(await this.NavigateAsync(Fixture), "Font fixture navigation");
            await Settle();
            var Row = (DataGridRow)this.FileGrid.ItemContainerGenerator.ContainerFromIndex(0);
            Screenshot("FontsCompact");
            Check(Math.Abs(Row.ActualHeight - 22) <= 1 && Row.ActualHeight < 28,
                 $"Default rows: actual={Row.ActualHeight}, requested={this.FileGrid.RowHeight}, min={Row.MinHeight}, font={Row.FontSize}");
            await this.RunPreferencesSmokeChecksAsync(Passed, FontSizes: new Dictionary<string, double> { ["FileList"] = 18 });
            await Settle();
            Check(this.FileGrid.FontSize == 18 && this.AddressBox.FontSize == 13 && this.StatusText.FontSize == 13,
                "Changing the file-list font leaves other panels independent");
            var Choices = FontPanels.ToDictionary(Panel => Panel.Key, _ => 24.0);
            await this.RunPreferencesSmokeChecksAsync(Passed, FontSizes: Choices);
            await Settle();
            Row = (DataGridRow)this.FileGrid.ItemContainerGenerator.ContainerFromIndex(0);
            Check(Children<TextBlock>(Row).Count() >= 4 && Children<TextBlock>(Row).All(Text => Text.FontSize == 24),
                "File names and all metadata cells immediately use the selected font size");
            Check(Children<DataGridColumnHeader>(this.FileGrid).Where(Header => Header.Column is not null).All(Header => Header.FontSize == 24),
                "All visible column headers use the separate header font setting");
            Check(this.AddressBox.FontSize == 24 && this.SearchBox.FontSize == 24
                && this.HostWindow.FileMenuButton.FontSize == 24 && this.HostWindow.PreferencesMenuItem.FontSize == 24
                && this.StatusText.FontSize == 24 && this.HostWindow.TitleBrand.FontSize == 24 && this.HostWindow.TitleVersion.FontSize == 24
                && Children<TextBlock>(this.FolderTree).Any(Text => Text.FontSize == 24),
                $"Live fonts: address={this.AddressBox.FontSize}, search={this.SearchBox.FontSize}, menu={this.HostWindow.FileMenuButton.FontSize}/{this.HostWindow.PreferencesMenuItem.FontSize}, status={this.StatusText.FontSize}, title={this.HostWindow.TitleBrand.FontSize}, tree={string.Join(',', Children<TextBlock>(this.FolderTree).Select(Text => Text.FontSize))}");
            Check(Row.ActualHeight >= 36 && Children<TextBlock>(Row).All(Text => Text.ActualHeight <= Row.ActualHeight)
                && this.SearchBox.ActualHeight >= 36 && this.AddressDisplay.ActualHeight >= 35,
                "Large fonts expand row and navigation heights without vertical clipping");
            Check(TabCaption().FontSize == 24 && TabCaption().ActualHeight <= this.HostWindow.TabBar.ActualHeight - 4,
                "The tab strip grows to fit the largest custom tab font without vertical clipping");
            Screenshot("FontsLarge");
            Check(this.HostWindow.TitleVersion.ActualHeight <= this.HostWindow.TitleBar.ActualHeight
                && this.HostWindow.TitleVersion.TranslatePoint(new Point(this.HostWindow.TitleVersion.ActualWidth, 0), this.HostWindow.TitleBar).X
                    < this.HostWindow.MaximizeButton.TranslatePoint(new Point(), this.HostWindow.TitleBar).X - this.HostWindow.MaximizeButton.ActualWidth,
                "The version stays unclipped and clear of caption buttons with the largest title font");
            Check(this.SaveSettings(SettingsFile) && ReadSettings(SettingsFile).FontSizes!.OrderBy(Item => Item.Key).SequenceEqual(Choices.OrderBy(Item => Item.Key)),
                "Every panel font size survives settings serialization");
            this.Settings.FontSizes = ReadSettings(SettingsFile).FontSizes;
            this.ApplyUIFonts();
            Check(this.GetFontOptions().All(Option => Option.Size == 24), "Startup font restoration applies every saved size");
            Check(!this.ChangeUIFontSize("Unknown", 12) && !this.ChangeUIFontSize("FileList", double.NaN)
                && !this.ChangeUIFontSize("FileList", 7) && !this.ChangeUIFontSize("FileList", 25),
                "Invalid font choices are rejected");
            this.Settings.FontSizes = new() { ["FileList"] = 999, ["Navigation"] = -1, ["Sidebar"] = double.NaN };
            this.ApplyUIFonts();
            Check(this.FileGrid.FontSize == 24 && this.AddressBox.FontSize == 8 && this.Settings.FontSizes["Sidebar"] == 13,
                "Damaged saved font sizes are bounded or replaced by defaults");
            this.SearchBox.Text = "/file";
            await this.WaitForSearchSmokeAsync();
            var Version = this.SearchVersion;
            var Results = this.Entries.Select(Entry => Entry.FullPath).ToArray();
            this.ChangeUIFontSize("FileList", 8);
            await Settle();
            Check(this.SearchVersion == Version && this.Entries.Select(Entry => Entry.FullPath).SequenceEqual(Results) && this.FileGrid.RowHeight == 22,
                "Font changes preserve completed search results and minimum compact row height");
            this.SearchBox.Clear();
            await this.WaitForSearchSmokeAsync();
            this.Settings.FontSizes = null;
            this.ApplyUIFonts();
            await this.RunGridLinesSmokeChecksAsync(Passed);
        }
        finally
        {
            this.SearchBox.Clear();
            this.Settings.FontSizes = InitialFonts;
            this.ApplyUIFonts();
            await this.NavigateAsync(InitialPath);
            this.SaveSettings();
            Directory.Delete(Fixture, true);
        }
    }
}
