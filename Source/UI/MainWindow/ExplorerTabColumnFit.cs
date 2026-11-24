using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private void ColumnHeaderContextOpening(object Sender, ContextMenuEventArgs E)
    {
        E.Handled = true;
        if (Sender is not DataGridColumnHeader { Column: { } Column } Header || this.WindowClosed) return;
        var Menu = new ContextMenu { PlacementTarget = Header, Placement = PlacementMode.Bottom,
            Style = (Style)this.FindResource("ColumnFitContextMenu") };
        var Fit = new MenuItem { Header = LanguageService.Text("AutoFitColumns"),
            InputGestureText = this.ShortcutBindings["AutoFitColumns"], Style = (Style)this.FindResource("FileMenuShortcutItem") };
        Fit.Click += (_, Args) => { Args.Handled = true; this.AutoFitColumns(); };
        Menu.Items.Add(Fit);
        Header.ContextMenu = Menu;
        Menu.IsOpen = true;
    }

    private void AutoFitColumns()
    {
        if (this.WindowClosed || !this.IsDetailsView) return;
        ++this.ColumnFitVersion;
        this.FitVisibleColumns();
        this.HostWindow.PersistWorkspaceNow();
    }

    private void FitVisibleColumns()
    {
        if (!this.IsDetailsView) return;
        foreach (var Column in this.FileGrid.Columns.Where(Item => Item.Visibility == Visibility.Visible).ToArray()) this.AutoFitColumn(Column);
        this.CompleteColumnFit();
    }

    private void CompleteColumnFit()
    {
        // Fitting can move every formerly visible column out of the viewport.
        // Start at Name again and let WPF realize the new horizontal range;
        // retaining the old far-right offset can leave headers and rows blank.
        FindVisualChild<ScrollViewer>(this.FileGrid)?.ScrollToHorizontalOffset(0);
    }

    private async Task RefreshAndFitColumnsAsync()
    {
        this.InvalidateVersionStatuses();
        CDir.Services.ShellIconService.InvalidateCache();
        if (this.NavigationTreeKey == "folder-tree") await this.RefreshRootsAsync();
        await this.RefreshAsync(true, RebuildRows: true);
        if (!this.Settings.AutoFitColumnsOnNavigation || this.WindowClosed) return;
        var Version = this.NavigationVersion;
        var Search = this.SearchVersion;
        // A filtered refresh completes its search asynchronously. Fit the final list.
        await this.SearchTask;
        if (Version != this.NavigationVersion || Search != this.SearchVersion
            || !this.Settings.AutoFitColumnsOnNavigation || this.WindowClosed) return;
        await this.FitVisibleColumnsAsync(Version);
        this.HostWindow.PersistWorkspaceNow();
    }

    private int ColumnFitVersion;
    private Func<Task>? BeforeColumnMeasurementForTests;

    private async Task FitVisibleColumnsAsync(int Version)
    {
        if (!this.IsDetailsView) return;
        var Fit = ++this.ColumnFitVersion;
        var Search = this.SearchVersion;
        bool Current() => Version == this.NavigationVersion && Search == this.SearchVersion
            && Fit == this.ColumnFitVersion && !this.WindowClosed && this.IsDetailsView;
        // Input and the first frame take precedence over optional text sizing.
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (!Current()) return;
        var Entries = this.FileGrid.Items.OfType<FileEntry>().ToArray();
        var Typeface = new Typeface(this.FileGrid.FontFamily, this.FileGrid.FontStyle, this.FileGrid.FontWeight, this.FileGrid.FontStretch);
        var Dpi = VisualTreeHelper.GetDpi(this.FileGrid).PixelsPerDip;
        var Culture = LanguageService.Culture;
        var Direction = this.FileGrid.FlowDirection;
        var FontSize = this.FileGrid.FontSize;
        var HeaderSize = (double)this.FindResource("UIFontHeaders");
        var Slice = Stopwatch.StartNew();
        foreach (var Column in this.FileGrid.Columns.Where(Item => Item.Visibility == Visibility.Visible).ToArray())
        {
            var Header = Convert.ToString(Column.Header, Culture) ?? "";
            var Padding = Column.SortMemberPath == nameof(FileEntry.Name) ? 43 : 21;
            var Texts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var Entry in Entries)
            {
                if (!Current()) return;
                Texts.Add(ColumnText(Entry, Column.SortMemberPath));
                if (Slice.ElapsedMilliseconds < 8) continue;
                await Dispatcher.Yield(DispatcherPriority.Background);
                Slice.Restart();
            }
            // Snapshot model strings on their dispatcher; font shaping can then
            // run without occupying it during cold font-cache/device recovery.
            var Probe = AppMain.SmokeTest ? this.BeforeColumnMeasurementForTests : null;
            var Width = await Task.Run(async () =>
            {
                if (Probe is not null) await Probe();
                double Measure(string Text, double Size) => new FormattedText(Text, Culture, Direction,
                    Typeface, Size, Brushes.Black, Dpi).WidthIncludingTrailingWhitespace;
                var Maximum = Measure(Header, HeaderSize) + 39;
                foreach (var Text in Texts)
                {
                    if (Volatile.Read(ref this.ColumnFitVersion) != Fit || Volatile.Read(ref this.NavigationVersion) != Version
                        || Volatile.Read(ref this.SearchVersion) != Search || Volatile.Read(ref this.WindowClosed)) return Maximum;
                    Maximum = Math.Max(Maximum, Measure(Text, FontSize) + Padding);
                }
                return Maximum;
            });
            if (!Current()) return;
            Column.MinWidth = 0;
            Column.Width = new DataGridLength(Math.Min(Column.MaxWidth, Math.Ceiling(Width)));
        }
        if (Current()) this.CompleteColumnFit();
    }

    private static string ColumnText(FileEntry Entry, string Key) => Key switch
    {
        nameof(FileEntry.Name) => Entry.Name,
        nameof(FileEntry.Modified) => Entry.DisplayModified,
        nameof(FileEntry.TypeName) => Entry.TypeName,
        nameof(FileEntry.Size) => Entry.DisplaySize,
        nameof(FileEntry.FullPath) => Entry.FullPath,
        nameof(FileEntry.OriginalLocation) => Entry.OriginalLocation ?? "",
        nameof(FileEntry.DateDeleted) => Entry.DisplayDateDeleted,
        _ => Entry[Key]
    };

    private void AutoFitColumn(DataGridColumn Column)
    {
        if (this.WindowClosed || !this.FileGrid.Columns.Contains(Column) || Column.Visibility != Visibility.Visible) return;
        var Typeface = new Typeface(this.FileGrid.FontFamily, this.FileGrid.FontStyle, this.FileGrid.FontWeight, this.FileGrid.FontStretch);
        var Dpi = VisualTreeHelper.GetDpi(this.FileGrid).PixelsPerDip;
        double Measure(string Text, double Size) => new FormattedText(Text, LanguageService.Culture, this.FileGrid.FlowDirection,
            Typeface, Size, Brushes.Black, Dpi).WidthIncludingTrailingWhitespace;
        // The header template reserves its arrow gutter even when it is unsorted.
        var Width = Measure(Convert.ToString(Column.Header, LanguageService.Culture) ?? "", (double)this.FindResource("UIFontHeaders")) + 39;
        var Measured = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var Entry in this.FileGrid.Items.OfType<FileEntry>())
        {
            var Text = ColumnText(Entry, Column.SortMemberPath);
            if (!Measured.TryGetValue(Text, out var TextWidth)) Measured[Text] = TextWidth = Measure(Text, this.FileGrid.FontSize);
            Width = Math.Max(Width, TextWidth + (Column.SortMemberPath == nameof(FileEntry.Name) ? 43 : 21));
        }
        // Measure model values so virtualized, off-screen rows also participate.
        // A fitted short name must be able to shrink below the initial 230px minimum.
        Column.MinWidth = 0;
        Column.Width = new DataGridLength(Math.Min(Column.MaxWidth, Math.Ceiling(Width)));
    }
}
