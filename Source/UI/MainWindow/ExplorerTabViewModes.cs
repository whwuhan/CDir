using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CDir.Controls;
using CDir.Models;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private bool SyncingFileViews;
    private FileViewMode? AppliedFileView;
    private int FileViewWheelRemainder;
    private string FileViewWheelFolder = "";
    private long FileViewWheelTime;
    private Func<ModifierKeys> ReadFileViewWheelModifiers = () => Keyboard.Modifiers;
    private System.Windows.Threading.DispatcherTimer? FileViewSaveTimer;

    private void QueueFileViewSave()
    {
        if (this.FileViewSaveTimer is null)
        {
            this.FileViewSaveTimer = new(System.Windows.Threading.DispatcherPriority.Background, this.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
            this.FileViewSaveTimer.Tick += (_, _) =>
            {
                this.FileViewSaveTimer.Stop();
                if (!this.WindowClosed && !this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
            };
        }
        this.FileViewSaveTimer.Stop();
        this.FileViewSaveTimer.Start();
    }

    private void MainPanelViewMouseLeave(object Sender, MouseEventArgs E) => this.FileViewWheelRemainder = 0;

    private void MainPanelViewMouseWheel(object Sender, MouseWheelEventArgs E)
    {
        if (E.Handled) return;
        if (this.ReadFileViewWheelModifiers() != ModifierKeys.Control)
        {
            this.FileViewWheelRemainder = 0;
            return;
        }
        if (this.Loading || this.OperationBusy || this.CurrentPath.Length == 0
            || E.OriginalSource is DependencyObject Source && FindAncestor<TextBoxBase>(Source) is not null) return;
        E.Handled = true;
        var Now = Environment.TickCount64;
        if (this.FileViewWheelFolder != this.CurrentPath || Now - this.FileViewWheelTime > 1000) this.FileViewWheelRemainder = 0;
        this.FileViewWheelFolder = this.CurrentPath;
        this.FileViewWheelTime = Now;
        this.FileViewWheelRemainder = (int)Math.Clamp((long)this.FileViewWheelRemainder + E.Delta, int.MinValue, int.MaxValue);
        var Steps = this.FileViewWheelRemainder / Mouse.MouseWheelDeltaForOneLine;
        this.FileViewWheelRemainder %= Mouse.MouseWheelDeltaForOneLine;
        if (Steps == 0) return;
        var Mode = this.Settings.FileView;
        var Size = this.Settings.FileViewIconSize is >= 16 and <= 256 ? this.Settings.FileViewIconSize : FileViewZoom.DefaultSize(Mode);
        var Previous = (Mode, Size);
        for (var Index = 0; Index < Math.Min(Math.Abs((long)Steps), 256); Index++)
        {
            var Next = FileViewZoom.Step(Mode, Size, Steps > 0);
            if (Next == (Mode, Size)) break;
            (Mode, Size) = Next;
        }
        if ((Mode, Size) == Previous) return;
        var Anchor = this.FileItemContainer(E.OriginalSource as DependencyObject)?.DataContext as FileEntry
            ?? this.FileGrid.CurrentItem as FileEntry ?? this.FileGrid.SelectedItem as FileEntry;
        var AnchorIndex = Anchor is null ? -1 : this.ActiveFileItems.Items.IndexOf(Anchor);
        var Panel = !this.IsDetailsView && Mode != FileViewMode.Details && AnchorIndex >= 0
            ? FindVisualChild<FileTilePanel>(this.FileTiles) : null;
        Panel?.RevealAfterResize(AnchorIndex);
        var Remainder = this.FileViewWheelRemainder;
        this.ChangeFileView(Mode, FileViewZoom.IsIconView(Mode) ? Size : 0, FocusPanel: false, SaveImmediately: false);
        this.FileViewWheelRemainder = Remainder;
        if (Panel is null && Anchor is not null && this.ActiveFileItems.Items.Contains(Anchor)) this.RevealFile(Anchor);
    }
    private bool IsDetailsView => this.Settings.FileView == FileViewMode.Details;
    private Control ActiveFileControl => this.IsDetailsView ? this.FileGrid : this.FileTiles;
    private ItemsControl ActiveFileItems => this.IsDetailsView ? this.FileGrid : this.FileTiles;
    private bool IsFileInput(DependencyObject? Source) => Source is null
        ? this.ActiveFileControl.IsKeyboardFocusWithin
        : ReferenceEquals(Source, this.ActiveFileControl) || this.ActiveFileControl.IsAncestorOf(Source);
    private FrameworkElement? FileItemContainer(DependencyObject? Source)
    {
        var Container = Source is null ? null : ItemsControl.ContainerFromElement(this.ActiveFileItems, Source) as FrameworkElement;
        return Container?.DataContext is FileEntry ? Container : null;
    }
    private void RevealFile(FileEntry Entry)
    {
        if (this.IsDetailsView) this.FileGrid.ScrollIntoView(Entry);
        else
        {
            this.FileTiles.UpdateLayout();
            FindVisualChild<FileTilePanel>(this.FileTiles)?.Reveal(this.FileTiles.Items.IndexOf(Entry));
            this.FileTiles.ScrollIntoView(Entry);
        }
    }
    private void ChangeFileView(FileViewMode Mode, int IconSize = 0, bool FocusPanel = true, bool SaveImmediately = true)
    {
        this.FileViewWheelRemainder = 0;
        this.HostWindow.RememberFolderView(this.CurrentPath, Mode, IconSize);
        this.ApplyRememberedFolderView(Mode, IconSize);
        if (SaveImmediately)
        {
            this.FileViewSaveTimer?.Stop();
            if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
        }
        else this.QueueFileViewSave();
        if (FocusPanel) this.FocusMainFilePanel();
    }
    internal void ApplyRememberedFolderView(FileViewMode Mode, int? SavedIconSize = null)
    {
        var IconSize = SavedIconSize ?? this.HostWindow.FolderIconSizeFor(this.CurrentPath);
        if (this.Settings.FileView == Mode && this.AppliedFileView == Mode && this.Settings.FileViewIconSize == IconSize) return;
        this.FileViewWheelRemainder = 0;
        this.CancelInlineRename();
        this.EndMarqueeSelection();
        this.Settings.FileView = Mode;
        this.Settings.FileViewIconSize = IconSize;
        this.ApplyFileView();
    }
    private void ApplyFileView()
    {
        if (!Enum.IsDefined(this.Settings.FileView)) this.Settings.FileView = FileViewMode.Details;
        var Mode = this.Settings.FileView;
        var IconSize = FileViewZoom.IsIconView(Mode) && this.Settings.FileViewIconSize is >= 16 and <= 256
            ? this.Settings.FileViewIconSize : FileViewZoom.DefaultSize(Mode);
        void SetResource(string Key, object Value) { if (!Equals(this.Resources[Key], Value)) this.Resources[Key] = Value; }
        SetResource("FileViewNameHeight", this.FileGrid.FontSize * 2.8);
        SetResource("FileViewIconSize", (double)IconSize);
        SetResource("FileViewIconPixels", IconSize);
        this.FileGrid.Visibility = this.IsDetailsView ? Visibility.Visible : Visibility.Collapsed;
        this.FileTiles.Visibility = this.IsDetailsView ? Visibility.Collapsed : Visibility.Visible;
        this.ApplyGridLines();
        if (!this.IsDetailsView)
        {
            var Compact = Mode is FileViewMode.SmallIcons or FileViewMode.List;
            this.FileTiles.ItemTemplate = (DataTemplate)this.FindResource(Compact ? "CompactFileTemplate"
                : Mode == FileViewMode.Tiles ? "TileFileTemplate" : Mode == FileViewMode.Content ? "ContentFileTemplate" : "IconFileTemplate");
            var Font = this.FileGrid.FontSize;
            // Resize the existing virtualized panel. Replacing ItemsPanel discards
            // every visible container, thumbnail request and scroll position.
            SetResource("FileViewCellWidth", Compact ? 240d : Mode is FileViewMode.Tiles or FileViewMode.Content ? 300d : IconSize + 48d);
            SetResource("FileViewCellHeight", Compact ? Math.Max(IconSize + 4, Math.Max(28, Font + 10)) : Mode == FileViewMode.Content ? Math.Max(88, Font * 4 + 16)
                : Mode == FileViewMode.Tiles ? Math.Max(72, Font * 3 + 16) : IconSize + Font * 2.8 + 20);
            SetResource("FileViewGrouped", !this.IsComputer && this.FileGroupProperty.Length > 0);
            SetResource("FileViewFillDown", Mode == FileViewMode.List);
            SetResource("FileViewFullWidth", Mode == FileViewMode.Content);
            if (this.AppliedFileView is null or FileViewMode.Details) this.SyncTileSelection();
        }
        // Only Details uses WPF group descriptions. Icon-size and tile-layout
        // changes keep the existing sorted view and its selection intact.
        if (this.View is not null && this.AppliedFileView != Mode
            && (this.AppliedFileView is null || this.AppliedFileView == FileViewMode.Details || this.IsDetailsView)
            && (this.FileGroupProperty.Length > 0 || this.View.GroupDescriptions.Count > 0)) this.ApplySort();
        this.AppliedFileView = Mode;
        this.UpdateFileViewMenu();
    }
    private void UpdateFileViewMenu()
    {
        this.UpdateGroupingMenu();
        if (this.ColumnMenuButton.ContextMenu is not { } Menu) return;
        foreach (var Item in Menu.Items.OfType<MenuItem>())
        {
            if (Item.Tag is FileViewMode Mode) Item.IsChecked = Mode == this.Settings.FileView;
            else if (Item.Tag is string Key && ColumnKeys.Contains(Key)) Item.IsEnabled = this.IsDetailsView && !IsRecycleColumn(Key);
        }
    }
    private void SyncTileSelection()
    {
        if (this.RefreshingFileLayout || this.SyncingFileViews || this.FileTiles is null) return;
        this.SyncingFileViews = true;
        try
        {
            var Selection = this.SelectedEntries().ToHashSet();
            var TileSelection = this.FileTiles.SelectedItems.Cast<FileEntry>().ToHashSet();
            foreach (var Old in this.FileTiles.SelectedItems.Cast<FileEntry>().Where(Entry => !Selection.Contains(Entry)).ToArray()) this.FileTiles.SelectedItems.Remove(Old);
            foreach (var Entry in Selection.Where(Entry => !TileSelection.Contains(Entry))) this.FileTiles.SelectedItems.Add(Entry);
        }
        finally { this.SyncingFileViews = false; }
    }
    private void FileTilesSelectionChanged(object Sender, SelectionChangedEventArgs E)
    {
        if (this.RefreshingFileLayout || this.SyncingFileViews || this.IsDetailsView) return;
        this.SyncingFileViews = true;
        try
        {
            foreach (var Entry in E.RemovedItems.OfType<FileEntry>()) this.FileGrid.SelectedItems.Remove(Entry);
            foreach (var Entry in E.AddedItems.OfType<FileEntry>())
                if (!this.FileGrid.SelectedItems.Contains(Entry)) this.FileGrid.SelectedItems.Add(Entry);
        }
        finally { this.SyncingFileViews = false; }
    }
    private void FileTilesKeyDown(object Sender, KeyEventArgs E)
    {
        if (E.Handled || E.OriginalSource is DependencyObject Source && FindAncestor<TextBoxBase>(Source) is not null) return;
        if (E.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            if (this.FileTiles.Items.Count == 0 || FindVisualChild<FileTilePanel>(this.FileTiles) is not { } Panel) return;
            var Index = this.FileTiles.Items.IndexOf(this.FileGrid.CurrentItem ?? this.FileGrid.SelectedItem);
            var Across = Math.Max(1, (int)(Panel.ViewportWidth / Panel.CellWidth));
            var Down = Math.Max(1, (int)(Panel.ViewportHeight / Panel.CellHeight));
            var Step = Panel.FillDown ? Down : Panel.FullWidth ? 1 : Across;
            var Next = E.Key switch
            {
                Key.Home => 0, Key.End => this.FileTiles.Items.Count - 1,
                Key.Left => Index - (Panel.FillDown ? Step : 1), Key.Right => Index + (Panel.FillDown ? Step : 1),
                Key.Up => Index - (Panel.FillDown ? 1 : Step), Key.Down => Index + (Panel.FillDown ? 1 : Step),
                Key.PageUp => Index - Step * Down, _ => Index + Step * Down
            };
            var Entry = (FileEntry)this.FileTiles.Items[Math.Clamp(Index < 0 ? 0 : Next, 0, this.FileTiles.Items.Count - 1)];
            this.SelectFileByClick(Entry, E.KeyboardDevice.Modifiers);
            this.RevealFile(Entry);
            E.Handled = true;
        }
        else this.FileGridPreviewKeyDown(Sender, E);
    }
}
