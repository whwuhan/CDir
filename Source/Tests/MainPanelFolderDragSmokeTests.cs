using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunMainPanelFolderDragSmokeChecksAsync(List<string> Passed)
    {
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        var InitialPath = this.CurrentPath;
        var InitialFavorites = this.FavoritesSection.IsExpanded;
        var InitialTree = this.DirectoryTreeSection.IsExpanded;
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirMainPanelDrag-" + FixtureId);
        var Paths = new[] { "Alpha 中文", "Beta With Spaces", "Gamma" }.Select(Name => Directory.CreateDirectory(Path.Combine(Fixture, Name)).FullName).ToArray();
        var Marker = Path.Combine(Fixture, ".CDirMainPanelDragOwner");
        File.WriteAllText(Marker, FixtureId);
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Main-panel folder drag UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        void Select(params FileEntry[] Entries)
        {
            this.FileGrid.SelectedItems.Clear();
            foreach (var Entry in Entries) this.FileGrid.SelectedItems.Add(Entry);
        }
        DataGridRow Row(FileEntry Entry) => (DataGridRow)this.FileGrid.ItemContainerGenerator.ContainerFromItem(Entry);
        Point HeaderPoint(object Category) => FindVisualChild<ToggleButton>(this.SidebarSectionElement(Category)!)!
            .TranslatePoint(new Point(8, 8), this.SidebarPanel);
        IDataObject DragData(MainPanelFolderDragPayload Payload)
        {
            var Data = new DataObject();
            Data.SetData(MainPanelFolderDragFormat, Payload);
            return Data;
        }
        try
        {
            this.RestoreSidebarCategories([]);
            await this.NavigateAsync(Fixture);
            this.FavoritesSection.IsExpanded = false;
            this.DirectoryTreeSection.IsExpanded = false;
            this.TryAddSidebarCategory("Drop Work");
            this.TryAddSidebarCategory("Drop Archive");
            var Work = this.CustomCategories[0];
            var Archive = this.CustomCategories[1];
            this.SidebarScroll.ScrollToTop();
            await Settle();
            this.SidebarSectionElement(Archive)!.IsExpanded = false;
            await Settle();
            var Folders = Paths.Select(Path => this.Entries.Single(Entry => SamePath(Entry.FullPath, Path))).ToArray();
            var File = this.Entries.Single(Entry => SamePath(Entry.FullPath, Marker));
            var History = this.History.ToArray();
            var NavigationVersion = this.NavigationVersion;
            async Task HoldFolderPress()
            {
                this.DirectoryTreeSection.IsExpanded = false;
                this.SidebarScroll.ScrollToTop();
                await Settle();
                var Offset = this.SidebarScroll.VerticalOffset;
                this.PrepareMainPanelFolderDrag(Row(Folders[0]), new Point(10, 10), ModifierKeys.None, 1);
                Select(Folders[0]);
                FocusManager.SetFocusedElement(this, this.FileGrid);
                this.FileGridKeyboardFocusWithinChanged(this.FileGrid,
                    new DependencyPropertyChangedEventArgs(UIElement.IsKeyboardFocusWithinProperty, false, true));
                await this.PanelTreeSyncTask;
                await Settle();
                Check(!this.DirectoryTreeSection.IsExpanded && this.SidebarScroll.VerticalOffset == Offset
                    && this.PanelTreeSyncRequested && this.NavigationVersion == NavigationVersion,
                    "Pressing a folder defers the focus reveal and preserves the sidebar viewport until release");
            }
            await HoldFolderPress();
            this.FileGridDragMouseUp(this.FileGrid, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = Row(Folders[0]) });
            await this.PanelTreeSyncTask;
            await Settle();
            Check(this.DirectoryTreeSection.IsExpanded && !this.PanelTreeSyncRequested
                && this.FolderTree.SelectedItem is FolderNode ClickNode && SamePath(ClickNode.FullPath, Fixture),
                "Releasing a normal folder click reveals the current directory without opening the selected child");

            await HoldFolderPress();
            this.SearchBox.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.MouseUpEvent, Handled = true });
            await this.PanelTreeSyncTask;
            await Settle();
            Check(this.DirectoryTreeSection.IsExpanded && !this.PanelTreeSyncDeferred && !this.PanelTreeSyncRequested,
                "A handled release outside the file grid also resumes the pending tree reveal");

            await HoldFolderPress();
            this.ActiveMainPanelFolderDrag = this.CreateMainPanelFolderDrag();
            this.MainPanelFileDragActive = true;
            this.MainPanelDragCandidate = null;
            this.FileGridKeyboardFocusWithinChanged(this.FileGrid,
                new DependencyPropertyChangedEventArgs(UIElement.IsKeyboardFocusWithinProperty, true, false));
            this.ResumePendingPanelTreeSync();
            await Settle();
            Check(!this.DirectoryTreeSection.IsExpanded && this.PanelTreeSyncRequested,
                "Losing file-grid focus during an active drag retains the deferred reveal and stable category targets");
            Check(this.CompleteSidebarDrop(DragData(this.ActiveMainPanelFolderDrag!), HeaderPoint(Work))
                && Work.Folders.Single().FullPath == Folders[0].FullPath && !this.DirectoryTreeSection.IsExpanded,
                "A held folder can be added to the visible category before the pending source-tree reveal runs");
            this.CompleteMainPanelFileDrag();
            await this.PanelTreeSyncTask;
            await Settle();
            Check(this.DirectoryTreeSection.IsExpanded && !this.PanelTreeSyncRequested && !this.MainPanelFileDragActive,
                "Completing a category drop resumes the tree reveal after the drag has ended");
            Work.Folders.Clear();

            await HoldFolderPress();
            this.MainPanelFileDragActive = true;
            this.MainPanelDragCandidate = null;
            this.CompleteMainPanelFileDrag();
            await this.PanelTreeSyncTask;
            await Settle();
            Check(this.DirectoryTreeSection.IsExpanded && Work.Folders.Count == 0 && this.History.SequenceEqual(History)
                && this.NavigationVersion == NavigationVersion,
                "Canceling a drag resumes synchronization without adding folders or changing navigation history");
            this.DirectoryTreeSection.IsExpanded = false;
            this.SidebarScroll.ScrollToTop();
            await Settle();
            Select(Folders[0], Folders[1]);
            Check(this.PrepareMainPanelFolderDrag(Row(Folders[0]), new Point(10, 10), ModifierKeys.None, 1)
                && this.SelectedEntries().SequenceEqual(Folders.Take(2)), "Pressing a selected folder preserves a multi-folder group for dragging");
            var Payload = this.CreateMainPanelFolderDrag()!;
            Check(Payload.Folders.SequenceEqual(Folders.Take(2)), "A main-panel drag snapshots the selected folders in order");
            var Release = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = Row(Folders[0]) };
            this.FileGridDragMouseUp(this.FileGrid, Release);
            Check(this.SelectedEntries().SequenceEqual(Folders.Take(1)) && this.MainPanelDragCandidate is null
                && this.MainPanelDeferredSelection is null, "Releasing without dragging retains ordinary single-click selection");
            Select(Folders[0], Folders[1]);
            Check(!this.PrepareMainPanelFolderDrag(Row(Folders[0]), new Point(), ModifierKeys.Control, 1)
                && this.MainPanelDeferredSelection is null, "Control-click selection remains handled by the file grid");
            Check(!this.PrepareMainPanelFolderDrag(Row(Folders[0]), new Point(), ModifierKeys.None, 2)
                && this.CreateMainPanelFolderDrag() is null, "Double-click opening never starts a folder drag");
            Check(!this.PrepareMainPanelFolderDrag(this.FileGrid, new Point(), ModifierKeys.None, 1)
                && this.CreateMainPanelFolderDrag() is null, "Grid background and headers never initiate folder drags");
            Select(File);
            Check(!this.PrepareMainPanelFolderDrag(Row(File), new Point(), ModifierKeys.None, 1)
                && this.CreateMainPanelFolderDrag() is null, "Files cannot initiate an add-to-category drag");
            Select(Folders[0], File);
            this.PrepareMainPanelFolderDrag(Row(Folders[0]), new Point(), ModifierKeys.None, 1);
            Check(this.CreateMainPanelFolderDrag() is null, "Mixed file and folder selections cannot be dragged into a category");
            Select(Folders[0]);
            this.PrepareMainPanelFolderDrag(Row(Folders[1]), new Point(), ModifierKeys.None, 1);
            Check(this.CreateMainPanelFolderDrag() is null, "An unselected drag candidate waits for ordinary row selection");
            Select(Folders[1]);
            Check(this.CreateMainPanelFolderDrag()?.Folders.Single() == Folders[1], "A newly selected folder becomes a valid drag source");

            Select(Folders[0], Folders[1]);
            this.PrepareMainPanelFolderDrag(Row(Folders[0]), new Point(), ModifierKeys.None, 1);
            Payload = this.CreateMainPanelFolderDrag()!;
            var Data = DragData(Payload);
            Check(this.DraggedMainPanelFolders(Data) is null && !this.CompleteSidebarDrop(Data, HeaderPoint(Work)),
                "Inactive main-panel drag data cannot add folders");
            this.ActiveMainPanelFolderDrag = Payload;
            var Forged = DragData(new MainPanelFolderDragPayload(this, Payload.Folders));
            Check(this.DraggedMainPanelFolders(Forged) is null
                && this.UpdateSidebarDragFeedback(new DataObject(DataFormats.FileDrop, Paths), HeaderPoint(Work)) == DragDropEffects.None,
                "Forged and external file-drop data cannot impersonate a main-panel folder drag");
            foreach (var Point in new[] { HeaderPoint(this.FavoritesSection), HeaderPoint(this.DirectoryTreeSection), new Point(-1, 10), new Point(10, 1) })
                Check(this.UpdateSidebarDragFeedback(Data, Point) == DragDropEffects.None && !this.CompleteSidebarDrop(Data, Point),
                    "Built-in sections sidebar margins and out-of-bounds positions reject folder additions");
            Check(this.UpdateSidebarDragFeedback(Data, HeaderPoint(Work)) == DragDropEffects.Link
                && this.SidebarCategoryDropHighlight.IsVisible && this.SidebarDropMarker.Visibility == Visibility.Collapsed,
                "Hovering an empty custom category highlights it with shortcut-link feedback");
            await Settle();
            if (AppMain.ScreenshotPath is { } Screenshot)
            {
                var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(this);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = System.IO.File.Create(Path.Combine(Path.GetDirectoryName(Screenshot)!, "MainPanelFolderDrop.png"));
                Encoder.Save(Stream);
            }
            Check(this.CompleteSidebarDrop(Data, HeaderPoint(Work)) && Work.Folders.Select(Folder => Folder.FullPath).SequenceEqual(Paths.Take(2))
                && this.SidebarCategoryDropHighlight.Visibility == Visibility.Collapsed,
                "Dropping multiple folders onto an empty category appends shortcuts and clears the highlight");
            await Settle();
            Check(this.UpdateSidebarDragFeedback(Data, HeaderPoint(Work)) == DragDropEffects.None
                && !this.CompleteSidebarDrop(Data, HeaderPoint(Work)) && Work.Folders.Count == 2,
                "Repeated drops do not duplicate folders already in the target category");
            Check(this.CompleteSidebarDrop(Data, HeaderPoint(Archive)), "A collapsed category accepts main-panel folder drops on its header");
            await Settle();
            Check(Archive.Folders.Count == 2 && Archive.IsExpanded && this.SidebarSectionElement(Archive)!.IsExpanded,
                "The target category opens to reveal newly added folders");

            Select(Folders[0], Folders[2]);
            this.PrepareMainPanelFolderDrag(Row(Folders[0]), new Point(), ModifierKeys.None, 1);
            Payload = this.CreateMainPanelFolderDrag()!;
            Data = DragData(Payload);
            this.ActiveMainPanelFolderDrag = Payload;
            var List = FindVisualChild<ItemsControl>(this.SidebarSectionElement(Work)!)!;
            var BodyPoint = List.TranslatePoint(new Point(8, 8), this.SidebarPanel);
            Check(this.UpdateSidebarDragFeedback(Data, BodyPoint) == DragDropEffects.Link && this.CompleteSidebarDrop(Data, BodyPoint)
                && Work.Folders.Select(Folder => Folder.FullPath).SequenceEqual(Paths),
                "Category content accepts drops and appends only new folders while preserving existing order");
            this.UpdateSidebarDragFeedback(Data, HeaderPoint(Archive));
            this.ActiveMainPanelFolderDrag = null;
            this.ClearSidebarDropFeedback();
            Check(!this.CompleteSidebarDrop(Data, HeaderPoint(Archive)) && Archive.Folders.Count == 2
                && this.SidebarCategoryDropHighlight.Visibility == Visibility.Collapsed,
                "Cancelled drags leave categories unchanged and remove the target highlight");
            this.ActiveMainPanelFolderDrag = Payload;
            this.OperationBusy = true;
            try { Check(this.DraggedMainPanelFolders(Data) is null && this.CreateMainPanelFolderDrag() is null, "Busy file operations disable category drag additions"); }
            finally { this.OperationBusy = false; }
            this.Loading = true;
            try { Check(this.DraggedMainPanelFolders(Data) is null, "Navigation loading disables category drag additions"); }
            finally { this.Loading = false; }
            Check(SamePath(this.CurrentPath, Fixture) && this.History.SequenceEqual(History) && Paths.All(Directory.Exists)
                && System.IO.File.Exists(Marker), "Drag additions preserve navigation history and all original files and folders");

            for (var Index = 0; Index < 18; Index++) this.TryAddSidebarCategory("Padding " + Index);
            var LastCategory = this.CustomCategories[^1];
            await Settle();
            FindVisualChild<ToggleButton>(this.SidebarSectionElement(LastCategory)!)!.BringIntoView();
            await Settle();
            Check(this.SidebarScroll.VerticalOffset > 0 && this.UpdateSidebarDragFeedback(Data, HeaderPoint(LastCategory)) == DragDropEffects.Link
                && this.CompleteSidebarDrop(Data, HeaderPoint(LastCategory)) && LastCategory.Folders.Count == 2,
                "Category targeting remains correct after scrolling the sidebar");
            this.ActiveMainPanelFolderDrag = null;
            var SettingsFile = Path.Combine(Fixture, "Settings.json");
            Check(this.SaveSettings(SettingsFile), "Drag-added category shortcuts save to owned settings");
            var Saved = ReadSettings(SettingsFile);
            this.RestoreSidebarCategories(Saved.CustomCategories, Saved.SidebarOrder);
            Check(this.CustomCategories[0].Folders.Select(Folder => Folder.FullPath).SequenceEqual(Paths)
                && this.CustomCategories[1].Folders.Count == 2 && this.CustomCategories[^1].Folders.Count == 2,
                "Restart restoration retains drag-added folders and their category order");
        }
        finally
        {
            this.MainPanelFileDragActive = false;
            this.ActiveMainPanelFolderDrag = null;
            this.MainPanelDragCandidate = null;
            this.MainPanelDeferredSelection = null;
            this.ClearSidebarDropFeedback();
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.RestoreSidebarCategories(InitialCategories, InitialOrder);
            this.FavoritesSection.IsExpanded = InitialFavorites;
            this.DirectoryTreeSection.IsExpanded = InitialTree;
            this.SidebarScroll.ScrollToTop();
            if (!AppMain.IsInsideSmokeScope(Fixture) || !System.IO.File.Exists(Marker) || System.IO.File.ReadAllText(Marker) != FixtureId
                || (System.IO.File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned main-panel drag fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
