using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunFileViewSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("File view check failed: " + Name);
            Passed.Add(Name);
            AppMain.RecordSmokeProgress("File view: " + Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "File Views")).FullName;
        var Folder = Directory.CreateDirectory(Path.Combine(Root, "Folder")).FullName;
        for (var Index = 0; Index < 300; Index++) File.WriteAllText(Path.Combine(Root, $"Document {Index:000}.txt"), "Owned fixture");
        var Before = this.CurrentPath;
        var PreviousMode = this.Settings.FileView;
        var SettingsFile = Path.Combine(Root, "ViewSettings.json");
        async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); this.UpdateLayout(); }
        void Capture(string Name)
        {
            if (AppMain.ScreenshotPath is not { } Output) return;
            var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            Bitmap.Render(this);
            var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
            using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, Name + ".png")); Encoder.Save(Stream);
        }
        try
        {
            await this.NavigateAsync(Root);
            Check(JsonSerializer.Deserialize<AppSettings>("{}")!.FileView == FileViewMode.Details,
                "Older settings keep the details view by default");
            this.SelectPath(Folder);
            this.ColumnMenuClick(this.ColumnMenuButton, new RoutedEventArgs(Button.ClickEvent));
            var Menu = this.ColumnMenuButton.ContextMenu!;
            var Modes = Menu.Items.OfType<MenuItem>().Where(Item => Item.Tag is FileViewMode).ToArray();
            Check(Modes.Length == 8 && Menu.Items.IndexOf(Modes[^1]) < Menu.Items.IndexOf(Menu.Items.OfType<MenuItem>().Single(Item => Equals(Item.Tag, "ShowHidden"))),
                "The compact dropdown offers all eight Windows view choices above Show Hidden Items");
            Capture("FileViews-Menu");
            foreach (var Mode in Enum.GetValues<FileViewMode>())
            {
                AppMain.RecordSmokeProgress("File view: switching to " + Mode);
                Modes.Single(Item => Equals(Item.Tag, Mode)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Settle();
                Check(this.Settings.FileView == Mode && Modes.Count(Item => Item.IsChecked) == 1
                    && this.FileGrid.IsVisible == (Mode == FileViewMode.Details)
                    && this.FileTiles.IsVisible == (Mode != FileViewMode.Details)
                    && this.SelectedEntries().Length == 1 && this.SelectedEntries()[0].FullPath == Folder,
                    Mode + " switches the main panel and preserves the selection with exactly one menu check"
                    + $" (selection={string.Join(',', this.SelectedEntries().Select(Entry => Entry.Name))})");
                Check(this.CaptureWorkspaceTab().Settings.FileView == Mode && this.SaveSettings(SettingsFile)
                    && ReadSettings(SettingsFile).FileView == Mode, Mode + " survives settings and workspace snapshot capture");
                if (Mode == FileViewMode.Details) continue;
                var Widths = this.FileGrid.Columns.Select(Column => Column.Width).ToArray();
                await this.HandleShortcutAsync(Key.E, ModifierKeys.Alt, false, true);
                Check(this.FileGrid.Columns.Select(Column => Column.Width).SequenceEqual(Widths), Mode + " makes Alt+E a no-op for column widths");
                var Panel = FindVisualChild<FileTilePanel>(this.FileTiles)!;
                var First = Panel.ItemBounds(0); var Second = Panel.ItemBounds(1);
                Check(Panel is not null && (Mode == FileViewMode.List ? Second.Top > First.Top
                    : Mode == FileViewMode.Content ? Second.Top > First.Top && First.Width == Panel.ViewportWidth
                    : Panel.ViewportWidth < Panel.CellWidth * 2 || Second.Left > First.Left), Mode + " uses the expected item arrangement");
                Menu.IsOpen = false;
                await Task.Delay(250);
                Capture("FileView-" + Mode);
                this.ClearFileSelection(); this.SelectPath(Folder);
                var Container = (ListBoxItem)this.FileTiles.ItemContainerGenerator.ContainerFromItem(this.FileTiles.Items[0]);
                Check(Container is not null && this.FileContextEntries(Container).Length == 1 && this.FileContextEntries(Container)[0].FullPath == Folder
                    && this.FileTransferTarget(Container, false)?.Path == Folder,
                    Mode + " routes item context menus and folder drops through the visible item");
                this.BeginFileRename(Folder); await Settle();
                Check(this.InlineRenameBox is { IsVisible: true }, Mode + " displays the inline rename editor");
                this.CancelInlineRename();
                var Press = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.None), PresentationSource.FromVisual(this.FileTiles), 0, Key.Down)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                this.FileTiles.RaiseEvent(Press); await Settle();
                Check(Press.Handled && this.SelectedEntries().Length == 1 && this.SelectedEntries()[0].FullPath != Folder,
                    Mode + " moves file selection through the visible keyboard layout");
                this.SearchBox.Text = "Document 29"; await this.WaitForSearchSmokeAsync(); await Settle();
                Check(this.FileTiles.Items.Count == 10 && this.FileGrid.Items.Count == 10,
                    Mode + " shares the current search results with the file operation model");
                this.SearchBox.Clear(); await this.WaitForSearchSmokeAsync(); await Settle();
                this.ClearFileSelection(); this.SelectPath(Folder);
                this.SelectPath(((FileEntry)this.FileTiles.Items[^1]).FullPath); await Settle();
                Check(this.FileTiles.ItemContainerGenerator.ContainerFromIndex(this.FileTiles.Items.Count - 1) is ListBoxItem
                    && VisualTreeHelper.GetChildrenCount(Panel) < this.FileTiles.Items.Count,
                    Mode + " reveals off-screen selections while virtualizing the remaining items");
                this.ClearFileSelection(); this.SelectPath(Folder); await Settle();
                if (this.MarqueeViewport() is { } Viewport)
                {
                    var Origin = new Point(Viewport.Bounds.Left + 2, Viewport.Bounds.Top + 2);
                    Check(this.PrepareMarqueeSelection(this.FileTiles, Origin, ModifierKeys.None, 1)
                        && this.UpdateMarqueeSelection(new Point(Viewport.Bounds.Right - 2, Viewport.Bounds.Bottom - 2))
                        && this.SelectedEntries().Length > 1, Mode + " selects an item rectangle using its actual layout geometry");
                    this.EndMarqueeSelection(Cancel: true);
                    Check(this.SelectedEntries().Length == 1 && this.SelectedEntries()[0].FullPath == Folder, Mode + " restores the selection when marquee selection is cancelled");
                }
            }
            this.ChangeFileView(FileViewMode.Details); await Settle();
            Check(this.FileGrid.Columns.All(Column => Column.CanUserSort), "Ordinary folders retain sortable columns");
            // Inject drive entries, avoiding any reads of user drive contents.
            var SavedSort = this.SortProperty; var SavedDirection = this.SortDirection;
            this.CurrentPath = ShellLocations.Computer;
            this.SortProperty = nameof(FileEntry.Size); this.SortDirection = ListSortDirection.Descending;
            this.ShowEntries(new[] {
                new FileEntry { Name = "A label (Z:)", FullPath = @"Z:\", IsDirectory = true, IsDrive = true },
                new FileEntry { Name = "Z label (C:)", FullPath = @"C:\", IsDirectory = true, IsDrive = true },
                new FileEntry { Name = "M label (D:)", FullPath = @"D:\", IsDirectory = true, IsDrive = true } });
            await Settle();
            Check(this.View!.Cast<FileEntry>().Select(Entry => Entry.FullPath).SequenceEqual(new[] { @"C:\", @"D:\", @"Z:\" })
                && !this.FileGrid.CanUserSortColumns && this.FileGrid.Columns.All(Column => !Column.CanUserSort),
                "My Computer always sorts by drive letter ascending and disables header sorting");
            var NameColumn = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Name));
            this.FileGrid.ScrollIntoView(this.FileGrid.Items[0], NameColumn); await Settle();
            var Header = FindVisualChild<DataGridColumnHeader>(this.FileGrid)!;
            // Use the visible Name header rather than the inert filler header.
            IEnumerable<DataGridColumnHeader> Headers(DependencyObject Parent)
            {
                for (var I = 0; I < VisualTreeHelper.GetChildrenCount(Parent); I++)
                {
                    var Child = VisualTreeHelper.GetChild(Parent, I);
                    if (Child is DataGridColumnHeader H && H.Column is not null) yield return H;
                    foreach (var H2 in Headers(Child)) yield return H2;
                }
            }
            Header = Headers(this.FileGrid).Single(H => H.Column == NameColumn);
            typeof(DataGridColumnHeader).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Header, null);
            var Arrow = (System.Windows.Shapes.Path)Header.Template.FindName("SortArrow", Header);
            Check(Arrow.Stroke is SolidColorBrush Brush && Brush.Color == Color.FromRgb(204, 204, 204)
                && NameColumn.SortDirection == ListSortDirection.Ascending,
                "My Computer shows a light-gray ascending arrow and ignores actual header clicks");
            this.FileGridSorting(this.FileGrid, new DataGridSortingEventArgs(NameColumn));
            Check(this.SortProperty == nameof(FileEntry.Size) && this.SortDirection == ListSortDirection.Descending,
                "My Computer rejects sorting without overwriting the folder sort preference");
            Capture("MyComputer-FixedSort");
            this.SortProperty = SavedSort; this.SortDirection = SavedDirection;
            await this.NavigateAsync(Root);
            Check(this.FileGrid.CanUserSortColumns && this.FileGrid.Columns.All(Column => Column.CanUserSort),
                "Leaving My Computer restores normal header sorting");
            this.Settings.FileView = (FileViewMode)999; this.ApplyFileView();
            Check(this.IsDetailsView, "Invalid persisted view modes safely fall back to Details");
            await this.RunFileViewRestoreSmokeAsync(Passed, Root);
        }
        finally
        {
            if (this.ColumnMenuButton.ContextMenu is { } Menu) Menu.IsOpen = false;
            this.CancelInlineRename(); this.ChangeFileView(PreviousMode);
            await this.NavigateAsync(Before);
        }
    }

    private async Task RunFileViewRestoreSmokeAsync(List<string> Passed, string Root)
    {
        var SettingsFile = Path.Combine(Root, "RestoreViewSettings.json");
        var OtherFolder = Directory.CreateDirectory(Path.Combine(Root, "Another Folder")).FullName;
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ExitOnClose = true }));
        MainWindow? Probe = null;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("View restoration check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Open()
        {
            Probe = new MainWindow(SettingsFile, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show(); await Probe.WorkspaceRestored;
            await Probe.ActiveTab!.WaitUntilReadyAsync();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); Probe.UpdateLayout();
        }
        try
        {
            await Open();
            var A = Probe!.ActiveTab!; A.SetTabAlias("List view"); A.ChangeFileView(FileViewMode.List);
            var B = Probe.AddTab(); await B.WaitUntilReadyAsync(); B.SetTabAlias("Icon view");
            Check(B.Settings.FileView == FileViewMode.List, "A new tab uses the current folder's remembered view");
            await B.NavigateAsync(OtherFolder);
            Check(B.IsDetailsView, "A folder without a saved view starts in Details");
            B.ChangeFileView(FileViewMode.LargeIcons);
            Check(A.Settings.FileView == FileViewMode.List && B.Settings.FileView == FileViewMode.LargeIcons,
                "Different folders retain different view choices in the same window");
            var C = Probe.AddTab(); await C.WaitUntilReadyAsync();
            Check(C.Settings.FileView == FileViewMode.LargeIcons, "Another tab opening the same folder uses its saved icon view");
            C.ChangeFileView(FileViewMode.Tiles);
            Check(B.Settings.FileView == FileViewMode.Tiles, "Changing a folder view updates other tabs showing that same folder");
            B.ChangeFileView(FileViewMode.LargeIcons); Probe.CloseTab(C);
            Probe.SelectTab(A); A.UpdateLayout();
            await A.NavigateAsync(OtherFolder);
            Check(A.Settings.FileView == FileViewMode.LargeIcons, "Navigating to another folder restores that folder's view");
            await A.BackAsync();
            Check(A.Settings.FileView == FileViewMode.List, "Back restores the previous folder's list view");
            A.SearchBox.Text = "/file"; await A.WaitForSearchSmokeAsync();
            Check(A.Settings.FileView == FileViewMode.List && A.FileTiles.IsVisible,
                "Recursive search retains the current folder's selected view");
            A.SearchBox.Clear(); await A.WaitForSearchSmokeAsync();
            A.SelectPath(((FileEntry)A.FileTiles.Items[^1]).FullPath);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var State = A.CaptureWorkspaceTab();
            Check(State.HorizontalScrollOffset > 0, "List view snapshots preserve horizontal scrolling");
            Probe.PersistWorkspaceNow(); Probe.RequestExit(); Probe = null;
            await Open();
            A = Probe!.Tabs.Single(Tab => Tab.TabAlias == "List view");
            B = Probe.Tabs.Single(Tab => Tab.TabAlias == "Icon view");
            Probe.SelectTab(A); A.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(A.Settings.FileView == FileViewMode.List && B.Settings.FileView == FileViewMode.LargeIcons
                && A.FileTiles.IsVisible && A.FileGrid.Visibility == Visibility.Collapsed,
                "Restart restores independent view modes and visible layouts");
            var Restored = A.CaptureWorkspaceTab();
            Check(Math.Abs(Restored.HorizontalScrollOffset - State.HorizontalScrollOffset) < .5,
                $"Restart restores the horizontal list position ({Restored.HorizontalScrollOffset}/{State.HorizontalScrollOffset})");
            Check(A.SelectedEntries().Select(Entry => Entry.FullPath).SequenceEqual(State.SelectedPaths),
                "Restart restores the selected icon item");
            Check(A.CaptureSaveSettings().FolderViews!.Count >= 2,
                "Folder view choices persist for folders outside the current tab");
            await A.NavigateAsync(OtherFolder.ToUpperInvariant() + Path.DirectorySeparatorChar);
            Check(A.Settings.FileView == FileViewMode.LargeIcons,
                "Reopened folders keep their icon view across restarts, path casing and trailing separators");
            await A.NavigateAsync(Root);
            Check(A.Settings.FileView == FileViewMode.List, "Reopening the first folder restores its saved list view after restart");
            Probe.RegisterFolderViews(new AppSettings { LastPath = null, FolderViews = new()
                { ["relative-path"] = FileViewMode.Tiles, [Path.Combine(Root, "Invalid mode")] = (FileViewMode)999 } });
            Check(!Probe.CaptureFolderViews().ContainsKey("relative-path")
                && Probe.FolderViewFor(Path.Combine(Root, "Invalid mode")) == FileViewMode.Details,
                "Invalid folder keys and saved modes cannot corrupt view restoration");
        }
        finally { Probe?.RequestExit(); }
    }
}
