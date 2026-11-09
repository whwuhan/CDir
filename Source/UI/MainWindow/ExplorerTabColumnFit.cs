using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
        if (this.WindowClosed) return;
        this.FitVisibleColumns();
        this.HostWindow.PersistWorkspaceNow();
    }

    private void FitVisibleColumns()
    {
        foreach (var Column in this.FileGrid.Columns.Where(Item => Item.Visibility == Visibility.Visible).ToArray()) this.AutoFitColumn(Column);
    }

    private void AutoFitColumn(DataGridColumn Column)
    {
        if (this.WindowClosed || !this.FileGrid.Columns.Contains(Column) || Column.Visibility != Visibility.Visible) return;
        var Typeface = new Typeface(this.FileGrid.FontFamily, this.FileGrid.FontStyle, this.FileGrid.FontWeight, this.FileGrid.FontStretch);
        var Dpi = VisualTreeHelper.GetDpi(this.FileGrid).PixelsPerDip;
        double Measure(string Text, double Size) => new FormattedText(Text, LanguageService.Culture, this.FileGrid.FlowDirection,
            Typeface, Size, Brushes.Black, Dpi).WidthIncludingTrailingWhitespace;
        // The header template reserves its arrow gutter even when it is unsorted.
        var Width = Measure(Convert.ToString(Column.Header, LanguageService.Culture) ?? "", (double)this.FindResource("UIFontHeaders")) + 39;
        foreach (var Entry in this.FileGrid.Items.OfType<FileEntry>())
        {
            var Text = Column.SortMemberPath switch
            {
                nameof(FileEntry.Name) => Entry.Name,
                nameof(FileEntry.Modified) => Entry.DisplayModified,
                nameof(FileEntry.TypeName) => Entry.TypeName,
                nameof(FileEntry.Size) => Entry.DisplaySize,
                nameof(FileEntry.FullPath) => Entry.FullPath,
                nameof(FileEntry.OriginalLocation) => Entry.OriginalLocation ?? "",
                nameof(FileEntry.DateDeleted) => Entry.DisplayDateDeleted,
                _ => ""
            };
            Width = Math.Max(Width, Measure(Text, this.FileGrid.FontSize) + (Column.SortMemberPath == nameof(FileEntry.Name) ? 43 : 21));
        }
        // Measure model values so virtualized, off-screen rows also participate.
        // A fitted short name must be able to shrink below the initial 230px minimum.
        Column.MinWidth = 0;
        Column.Width = new DataGridLength(Math.Min(Column.MaxWidth, Math.Ceiling(Width)));
    }
}
