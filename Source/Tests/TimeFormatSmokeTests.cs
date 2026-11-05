using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunTimeFormatSmokeChecksAsync(List<string> Passed)
    {
        var InitialClock = this.Settings.Use12HourClock;
        var InitialPath = this.CurrentPath;
        var InitialSearch = this.SearchBox.Text;
        var InitialSelection = this.SelectedEntries().Select(Entry => Entry.FullPath).ToArray();
        var InitialSort = this.SortProperty;
        var InitialDirection = this.SortDirection;
        var InitialColumnDirections = this.FileGrid.Columns.Select(Column => Column.SortDirection).ToArray();
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirTimeFormat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Fixture);
        var SettingsFile = Fixture + ".json";
        var Dates = new[]
        {
            new DateTime(2025, 12, 31, 23, 59, 58),
            new DateTime(2026, 1, 1, 0, 0, 7),
            new DateTime(2026, 1, 1, 12, 4, 56),
            new DateTime(2026, 1, 1, 13, 4, 56)
        };
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Time-format UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
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
            Check(!new AppSettings().Use12HourClock && !JsonSerializer.Deserialize<AppSettings>("{}")!.Use12HourClock,
                "Fresh and older settings default to 24-hour time");
            for (var Index = 0; Index < Dates.Length; Index++)
            {
                var FilePath = Path.Combine(Fixture, $"sample-{Index}.txt");
                File.WriteAllText(FilePath, "test");
                File.SetLastWriteTime(FilePath, Dates[Index]);
            }
            Check(await this.NavigateAsync(Fixture), "Time-format test opens an owned date fixture");
            this.SearchBox.Text = "sample-";
            this.SelectPath(Path.Combine(Fixture, "sample-2.txt"));
            var Column = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Modified));
            Column.SortDirection = null;
            this.FileGridSorting(this.FileGrid, new DataGridSortingEventArgs(Column));
            await Settle();
            var History = this.History.ToArray();
            var HistoryIndex = this.HistoryIndex;
            var Entry = this.Entries.Single(Entry => Entry.Name == "sample-2.txt");
            void CheckBrowserState()
            {
                Check(SamePath(this.CurrentPath, Fixture) && this.SearchBox.Text == "sample-"
                    && this.History.SequenceEqual(History) && this.HistoryIndex == HistoryIndex
                    && this.SelectedEntries() is { Length: 1 } Selected && SamePath(Selected[0].FullPath, Entry.FullPath)
                    && this.SortProperty == nameof(FileEntry.Modified) && this.SortDirection == ListSortDirection.Descending
                    && this.View!.Cast<FileEntry>().Select(Entry => Entry.Modified).SequenceEqual(Dates.Reverse()),
                    "Changing clock format preserves navigation filter selection and chronological date sorting");
            }
            await this.RunPreferencesSmokeChecksAsync(Passed, Use12HourClock: true);
            await Settle();
            Check(this.Entries.All(Entry => Entry.DisplayModified == Entry.Modified.ToString("yyyy/MM/dd hh:mm:ss tt", CultureInfo.InvariantCulture))
                && Column.GetCellContent(Entry) is TextBlock Cell12 && Cell12.Text == "2026/01/01 12:04:56 PM",
                "12-hour preference immediately renders year/month/day and seconds with AM/PM in the actual date cells");
            CheckBrowserState();
            Check(this.SaveSettings(SettingsFile) && ReadSettings(SettingsFile).Use12HourClock,
                "12-hour preference persists to the settings file");
            Screenshot("ModifiedDates-12Hour");
            await this.RunPreferencesSmokeChecksAsync(Passed);
            Check(await this.NavigateAsync(Fixture, RecordHistory: false, PreserveFilter: true)
                && this.Entries.All(Entry => Entry.Use12HourClock && Entry.DisplayModified.EndsWith("M", StringComparison.Ordinal)),
                "Refreshing file entries retains the saved 12-hour preference");
            this.SelectPath(Entry.FullPath);
            await this.RunPreferencesSmokeChecksAsync(Passed, Use12HourClock: false);
            await Settle();
            Entry = this.Entries.Single(Current => Current.Name == "sample-2.txt");
            Check(this.Entries.All(Entry => Entry.DisplayModified == Entry.Modified.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture))
                && Column.GetCellContent(Entry) is TextBlock Cell24 && Cell24.Text == "2026/01/01 12:04:56",
                "24-hour preference immediately renders year/month/day and seconds without AM/PM in the actual date cells");
            CheckBrowserState();
            Check(this.SaveSettings(SettingsFile) && !ReadSettings(SettingsFile).Use12HourClock,
                "24-hour preference persists to the settings file");
            Screenshot("ModifiedDates-24Hour");
        }
        finally
        {
            this.SearchBox.Text = "";
            this.ChangeTimeFormat(InitialClock);
            this.SortProperty = InitialSort;
            this.SortDirection = InitialDirection;
            for (var Index = 0; Index < this.FileGrid.Columns.Count; Index++) this.FileGrid.Columns[Index].SortDirection = InitialColumnDirections[Index];
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.SearchBox.Text = InitialSearch;
            foreach (var Path in InitialSelection) this.SelectPath(Path, Append: true);
            await Settle();
        }
    }
}
