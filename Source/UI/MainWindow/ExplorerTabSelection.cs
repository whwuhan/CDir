using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CDir.UI;

public partial class ExplorerTab
{
    private void ClearFileSelection()
    {
        this.EndMarqueeSelection();
        this.MainPanelDragCandidate = null;
        this.MainPanelDeferredSelection = null;
        this.PendingSearchSelection.Clear();
        this.FileGrid.SelectedItems.Clear();
    }

    private void ClearSelectionOnBlankMouseDown(MouseButtonEventArgs E)
    {
        if (E.Handled || E.ChangedButton != MouseButton.Left || this.FileGrid.SelectedItems.Count == 0) return;
        // Let the grid snapshot the selection before a blank press clears it, so Esc can restore it.
        if (this.CanStartMarqueeSelection(E.OriginalSource as DependencyObject, E.GetPosition(this.FileGrid), Keyboard.Modifiers, E.ClickCount)) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0
            && FindAncestor<DataGrid>(E.OriginalSource as DependencyObject) == this.FileGrid) return;
        for (var Source = E.OriginalSource as DependencyObject; Source is not null;)
        {
            // Preserve selection while operating on items, editing fields, scrolling,
            // resizing panels, or using actions that consume the current selection.
            if (Source is ButtonBase or TextBoxBase or PasswordBox or ComboBox or MenuItem
                or DataGridRow or DataGridColumnHeader or TreeViewItem or ListBoxItem or ScrollBar or Thumb
                || ReferenceEquals(Source, this.AddressBar) || ReferenceEquals(Source, this.SearchContainer)
                || ReferenceEquals(Source, this.TerminalPanel)) return;
            Source = Source is FrameworkContentElement Content ? Content.Parent : VisualTreeHelper.GetParent(Source);
        }
        this.ClearFileSelection();
    }
}
