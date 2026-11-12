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
    private async Task RunCategoryTreeNavigationSmokeChecksAsync(List<string> Passed)
    {
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        var InitialPath = this.CurrentPath;
        var InitialTree = this.DirectoryTreeSection.IsExpanded;
        var InitialFavoritesSource = this.FavoritesList.ItemsSource;
        var InitialFavorites = this.FavoritesSection.IsExpanded;
        var InitialBookmarkPaths = this.Settings.ShowBookmarkPaths;
        var InitialSidebarWidth = this.SidebarColumn.Width;
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirCategoryTree-" + FixtureId);
        var Alpha = Directory.CreateDirectory(Path.Combine(Fixture, "Alpha 中文")).FullName;
        var ChildPath = Directory.CreateDirectory(Path.Combine(Alpha, "Child With Spaces")).FullName;
        var Deep = Directory.CreateDirectory(Path.Combine(ChildPath, "Deep")).FullName;
        var Empty = Directory.CreateDirectory(Path.Combine(Fixture, "Empty")).FullName;
        var Marker = Path.Combine(Fixture, ".CDirCategoryTreeOwner");
        File.WriteAllText(Marker, FixtureId);
        ContextMenu? Menu = null;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Category tree navigation UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
            // UpdateLayout can invalidate a newly expanded guide's drawing.
            // Let the subsequent render finish before inspecting retained lines.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        async Task WaitForNavigation(string Expected)
        {
            await this.SidebarClickTask;
            var Deadline = DateTime.UtcNow.AddSeconds(10);
            while (this.Loading && DateTime.UtcNow < Deadline) await Task.Delay(20);
            await Settle();
            Check(!this.Loading && SamePath(this.CurrentPath, Expected), "Category navigation opens " + Path.GetFileName(Expected));
            Check(SidebarSelection() is { Length: 1 } Selection && SamePath(Selection[0].FullPath, Expected),
                "Category navigation leaves exactly one selected directory across the sidebar");
        }
        static IEnumerable<FolderNode> Visit(FolderNode Node) => new[] { Node }.Concat(Node.Children.SelectMany(Visit));
        // Expansion and lazy-loading remain independent; selection is shared across categories.
        (FolderNode Node, bool Expanded, bool Loaded, int Children)[] ComputerState() => this.Roots.SelectMany(Visit)
            .Select(Node => (Node, Node.IsExpanded, Node.IsLoaded, Node.Children.Count)).ToArray();
        (FolderNode Node, bool Expanded, bool Loaded, int Children)[] BookmarkState(IEnumerable<FolderNode> Roots) => Roots.SelectMany(Visit)
            .Select(Node => (Node, Node.IsExpanded, Node.IsLoaded, Node.Children.Count)).ToArray();
        FolderNode[] SidebarSelection() => this.Roots.Concat(this.SidebarBookmarkRoots()).SelectMany(Visit).Where(Node => Node.IsSelected).ToArray();
        bool OnlySelected(FolderNode Expected) => SidebarSelection() is { Length: 1 } Selection && ReferenceEquals(Selection[0], Expected);
        bool HeaderVisible(FrameworkElement Header)
        {
            var Top = Header.TranslatePoint(new Point(), this.SidebarScroll).Y;
            var Height = Header is TreeViewItem Item ? TreeHeaderHeight(Item) : Header.ActualHeight;
            var PinnedBottom = this.StickySidebar.Visibility == Visibility.Visible
                ? this.StickySidebar.TranslatePoint(new Point(0, this.StickySidebar.ActualHeight), this.SidebarScroll).Y : 0;
            var Visible = Header.IsVisible && Top >= Math.Max(0, PinnedBottom) - 1 && Top + Height <= this.SidebarScroll.ViewportHeight + 1;
            if (!Visible) AppMain.RecordSmokeProgress($"Bookmark header geometry: {Header.DataContext}, top={Top}, height={Height}, pinned={PinnedBottom}, viewport={this.SidebarScroll.ViewportHeight}, offset={this.SidebarScroll.VerticalOffset}");
            return Visible;
        }
        async Task ScrollAway(FrameworkElement Header)
        {
            this.SidebarScroll.ScrollToBottom();
            await Settle();
            Check(!HeaderVisible(Header), "The selected bookmark header is outside the visible sidebar before explicit refresh");
        }
        async Task RefreshWithoutHorizontalJump(Expander Section, double Offset)
        {
            var Viewer = (ScrollViewer)Section.Template.FindName("CategoryScroll", Section);
            Viewer.ScrollToHorizontalOffset(Offset);
            await Settle();
            Check(Viewer.ScrollableWidth > 96 && Math.Abs(Viewer.HorizontalOffset - Offset) < 0.01,
                "The refresh regression starts with overflowing paths at the requested horizontal position");
            var Changes = 0;
            void Changed(object Sender, ScrollChangedEventArgs E)
            {
                if (ReferenceEquals(E.OriginalSource, Viewer) && E.HorizontalChange != 0) Changes++;
            }
            Viewer.ScrollChanged += Changed;
            try
            {
                Check(await this.HandleShortcutAsync(Key.F5, ModifierKeys.None, InText: false, InFileList: false),
                    "F5 dispatches refresh from a category bookmark");
                await Settle();
                Check(Math.Abs(Viewer.HorizontalOffset - Offset) < 0.01 && Changes == 0,
                    "F5 reveals the selected header vertically without any horizontal jump: " + Offset);
            }
            finally { Viewer.ScrollChanged -= Changed; }
        }
        TreeViewItem? RenderedNode(ItemsControl Owner, FolderNode Target)
        {
            foreach (var Node in Owner.Items.OfType<FolderNode>())
            {
                if (Owner.ItemContainerGenerator.ContainerFromItem(Node) is not TreeViewItem Item) continue;
                if (ReferenceEquals(Node, Target)) return Item;
                if (Item.IsExpanded && RenderedNode(Item, Target) is { } Found) return Found;
            }
            return null;
        }
        static void Invoke(ContextMenu Menu, string Tag) => Menu.Items.OfType<MenuItem>().Single(Item => Item.Tag?.ToString() == Tag)
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        static void DoubleClickHeader(Button Button)
        {
            Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!MouseEvent(Button, Mouse.PreviewMouseDownEvent, 2).Handled)
                throw new InvalidOperationException("The category double-click did not handle its second press.");
            MouseEvent(Button, Mouse.PreviewMouseUpEvent, 2);
        }
        static MouseButtonEventArgs MouseEvent(UIElement Target, RoutedEvent Event, int Count)
        {
            var Args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Event };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(Args, Count);
            Target.RaiseEvent(Args);
            return Args;
        }
        try
        {
            this.RestoreSidebarCategories([]);
            this.TryAddSidebarCategory("Independent Trees 工作");
            this.TryAddSidebarCategory("Other");
            var Category = this.CustomCategories[0];
            var Other = this.CustomCategories[1];
            this.AddFoldersToCategory(Category, new[] { Alpha, Empty }.Select(FullPath => new FileEntry { FullPath = FullPath, IsDirectory = true }));
            this.AddFoldersToCategory(Other, [new FileEntry { FullPath = Alpha, IsDirectory = true }]);
            Other.Folders[0].Node.IsSelected = true;
            var Root = Category.Folders[0].Node;
            var EmptyRoot = Category.Folders[1].Node;
            this.SidebarScroll.ScrollToTop();
            await Settle();
            var Section = this.SidebarSectionElement(Category)!;
            var List = FindVisualChild<ItemsControl>(Section)!;
            var Container = List.ItemContainerGenerator.ContainerFromIndex(0);
            var Button = FindVisualChild<Button>(Container)!;
            var Toggle = FindVisualChild<ToggleButton>(Container)!;
            var Tree = FindVisualChild<TreeView>(Container)!;
            var OriginalPath = this.CurrentPath;
            var OriginalVersion = this.NavigationVersion;
            Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(this.NavigationVersion == OriginalVersion + 1 && ReferenceEquals(this.SidebarClickTarget, Button),
                "A category-root label starts navigation on the first click without waiting for double-click confirmation");
            Check(MouseEvent(Button, Mouse.PreviewMouseDownEvent, 2).Handled,
                "A category-root second press handles expansion together with first-click navigation");
            MouseEvent(Button, Mouse.PreviewMouseUpEvent, 2);
            await this.SidebarClickTask;
            await Settle();
            Check(Root.IsExpanded && SamePath(this.CurrentPath, Alpha) && this.NavigationVersion == OriginalVersion + 1 && OnlySelected(Root),
                "A category-root double-click expands the tree and synchronizes the main panel and sidebar selection");
            DoubleClickHeader(Button);
            await this.SidebarClickTask;
            await Settle();
            Check(!Root.IsExpanded && SamePath(this.CurrentPath, Alpha) && this.NavigationVersion == OriginalVersion + 1,
                "A category-root double-click collapses its tree while retaining the current folder without duplicate navigation");
            // Restore lazy-loading state for the independent-tree checks below.
            Root.IsLoaded = false;
            Root.Children.Clear();
            Root.Children.Add(new FolderNode("", "", NameKey: "TreeExpandHint"));
            await this.NavigateAsync(OriginalPath);
            this.DirectoryTreeSection.IsExpanded = true;
            var Before = ComputerState();
            var HistoryCount = this.History.Count;
            Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForNavigation(Alpha);
            Check(this.DirectoryTreeSection.IsExpanded && ComputerState().SequenceEqual(Before)
                && this.FolderTree.SelectedItem is null && !Root.IsExpanded && !Root.IsLoaded && OnlySelected(Root)
                && this.History.Count == HistoryCount + 1 && !Other.Folders[0].Node.IsSelected,
                "Clicking a category root clears other categories and My Computer highlights while preserving their branches");
            DoubleClickHeader(Button);
            await this.SidebarClickTask;
            await Settle();
            Check(Root.IsExpanded && Root.IsLoaded && Tree.IsVisible && Root.Children.Any(Node => SamePath(Node.FullPath, ChildPath))
                && ComputerState().SequenceEqual(Before) && !Other.Folders[0].Node.IsExpanded,
                "Double-clicking a category root lazily expands its own tree without changing My Computer or another category");
            var RootLines = FindVisualChild<CDir.Controls.TreeBranchLines>(Container)!;
            var LastRootLines = FindVisualChild<CDir.Controls.TreeBranchLines>(List.ItemContainerGenerator.ContainerFromIndex(1))!;
            var CachedGuideOwner = typeof(CDir.Controls.TreeBranchLines).GetField("Owner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(RootLines) as ItemsControl;
            AppMain.RecordSmokeProgress($"Root guide owner: loaded={RootLines.IsLoaded}, template={RootLines.TemplatedParent?.GetType().Name}, owner={ItemsControl.ItemsControlFromItemContainer(RootLines.TemplatedParent)?.GetType().Name}, cached={CachedGuideOwner?.GetType().Name}, index={CachedGuideOwner?.ItemContainerGenerator.IndexFromContainer(RootLines.TemplatedParent)}");
            RootLines.InvalidateVisual();
            this.UpdateLayout();
            await Settle();
            // Render this owned visual explicitly: an inactive test window can
            // be occluded by the user's window and have no retained drawing yet.
            if (Container is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } RenderedRoot)
            {
                var GuideBitmap = new RenderTargetBitmap((int)Math.Ceiling(RenderedRoot.ActualWidth),
                    (int)Math.Ceiling(RenderedRoot.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                GuideBitmap.Render(RenderedRoot);
            }
            Check(RootLines.IsBookmarkRoot && !RootLines.HasIncomingGuide
                && VisualTreeHelper.GetDrawing(RootLines) is { } RootDrawing
                && RootDrawing.Bounds.Top >= Button.ActualHeight / 2 - 1
                && RootDrawing.Bounds.Bottom > Button.ActualHeight
                && LastRootLines.IsBookmarkRoot && !LastRootLines.IsExpanded
                && (VisualTreeHelper.GetDrawing(LastRootLines) is not { } LastDrawing || LastDrawing.Bounds.IsEmpty),
                "Category roots retain their expanded subtree guide, with no incoming line from the category title and no collapsed-root stub"
                + $" (root={RootLines.IsBookmarkRoot}, incoming={RootLines.HasIncomingGuide}, expanded={RootLines.IsExpanded}, header={RootLines.HeaderHeight}, button={Button.ActualHeight}, size={RootLines.RenderSize}, visible={RootLines.IsVisible}, children={RootLines.ChildCount}, childColumn={RootLines.ChildGuideColumn}, bounds={VisualTreeHelper.GetDrawing(RootLines)?.Bounds}, lastExpanded={LastRootLines.IsExpanded}, lastBounds={VisualTreeHelper.GetDrawing(LastRootLines)?.Bounds})");
            DoubleClickHeader(Button);
            await this.SidebarClickTask;
            await Settle();
            Check(!Root.IsExpanded && !Tree.IsVisible && Section.IsExpanded && SamePath(this.CurrentPath, Alpha),
                "Double-clicking the current category root collapses its tree without navigating");
            Root.IsExpanded = true;
            Toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await this.EnsureChildrenAsync(Root);
            await Settle();
            Check(Tree.IsVisible && SamePath(this.CurrentPath, Alpha) && ComputerState().SequenceEqual(Before),
                "A category root's arrow expands child folders without navigating");
            var ChildItem = (TreeViewItem)Tree.ItemContainerGenerator.ContainerFromIndex(0);
            var Child = (FolderNode)ChildItem.DataContext;
            await this.OpenSidebarTreeFolderAsync(ChildItem, Navigate: true);
            await WaitForNavigation(ChildPath);
            Check(!Root.IsSelected && Child.IsSelected && !Child.IsExpanded && !Child.IsLoaded && ComputerState().SequenceEqual(Before),
                "Clicking a category child moves the category highlight without selecting or expanding My Computer");
            Child.IsExpanded = false;
            await this.OpenSidebarTreeFolderAsync(ChildItem, Navigate: false);
            await this.EnsureChildrenAsync(Child);
            await Settle();
            var DeepItem = (TreeViewItem)ChildItem.ItemContainerGenerator.ContainerFromIndex(0);
            ((FolderNode)DeepItem.DataContext).IsSelected = true;
            await WaitForNavigation(Deep);
            Check(ComputerState().SequenceEqual(Before) && !Root.IsSelected,
                "Keyboard-style selection of a deep category descendant leaves the My Computer tree unchanged");
            var BeforeChildDoubleClick = this.NavigationVersion;
            var ChildHeader = (FrameworkElement)ChildItem.Template.FindName("PART_Header", ChildItem);
            var PendingChildClick = this.CompleteSidebarFolderClick(this.SidebarFolderAddFromSource(ChildHeader, 1), ChildHeader);
            Check(MouseEvent(ChildHeader, Mouse.PreviewMouseDownEvent, 2).Handled,
                "A nested category double-click handles expansion together with first-click navigation");
            MouseEvent(ChildHeader, Mouse.PreviewMouseUpEvent, 2);
            await this.SidebarClickTask;
            await PendingChildClick;
            Check(SamePath(this.CurrentPath, ChildPath) && this.NavigationVersion == BeforeChildDoubleClick + 1
                && !Child.IsExpanded && OnlySelected(Child),
                "Double-clicking an expanded category child collapses its subtree and navigates the main panel to that child");
            this.DirectoryTreeSection.IsExpanded = false;
            Root.IsExpanded = false;
            await this.OpenCategoryFolderAsync(Button, Navigate: true);
            await WaitForNavigation(Alpha);
            await this.RefreshAsync();
            await Settle();
            Check(!this.DirectoryTreeSection.IsExpanded && ComputerState().SequenceEqual(Before) && Root.IsSelected,
                "Category navigation and a subsequent background refresh preserve a collapsed My Computer section and its complete tree state");
            Menu = Button.ContextMenu;
            Menu.PlacementTarget = Button;
            Menu.IsOpen = true;
            await Settle();
            Check(Menu.IsOpen && Menu.Items.OfType<MenuItem>().Select(Item => Item.Tag?.ToString())
                .SequenceEqual(new[] { "ExpandAllSubfolders", "CollapseAllSubfolders", "NewFolder", "RemoveFromCategory" }),
                "Category roots expose the same recursive folder commands as My Computer together with shortcut removal");
            Invoke(Menu, "CollapseAllSubfolders");
            Check(!Root.IsExpanded && !Child.IsExpanded && Section.IsExpanded && SamePath(this.CurrentPath, Alpha),
                "A category collapse command closes its current root without changing the main panel");
            Invoke(Menu, "ExpandAllSubfolders");
            await this.TreeExpansionTask;
            await Settle();
            Check(Visit(Root).Where(Node => !Node.IsPlaceholder).All(Node => Node.IsLoaded && Node.IsExpanded)
                && ComputerState().SequenceEqual(Before) && !Other.Folders[0].Node.IsExpanded,
                "A category root's recursive expansion loads all descendants without touching My Computer or other categories");
            var EmptyContainer = List.ItemContainerGenerator.ContainerFromIndex(1);
            var EmptyToggle = FindVisualChild<ToggleButton>(EmptyContainer)!;
            EmptyRoot.IsExpanded = true;
            EmptyToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await this.EnsureChildrenAsync(EmptyRoot);
            await Settle();
            Check(EmptyRoot.IsLoaded && EmptyRoot.Children.Count == 0 && !EmptyToggle.IsVisible && SamePath(this.CurrentPath, Alpha),
                "An empty category folder hides its expansion arrow after loading, like a My Computer leaf");
            if (AppMain.ScreenshotPath is { } Output)
            {
                var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(this);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, "IndependentCategoryTree.png"));
                Encoder.Save(Stream);
            }
            Check(await this.NavigateAsync(Deep) && this.DirectoryTreeSection.IsExpanded
                && this.FolderTree.SelectedItem is FolderNode ComputerSelected && SamePath(ComputerSelected.FullPath, Deep),
                "Explicit address-style navigation still reveals the requested folder in My Computer");
            var ComputerNode = this.FolderTree.SelectedItem as FolderNode ?? throw new InvalidOperationException("My Computer selection was not restored.");
            Check(OnlySelected(ComputerNode), "Address navigation clears every bookmark highlight");
            var ComputerBeforeScopedNavigation = ComputerState();
            var OtherBeforeScopedNavigation = BookmarkState(Other.Folders.Select(Folder => Folder.Node));
            Root.IsExpanded = false;
            await this.OpenCategoryFolderAsync(Button, Navigate: true);
            this.SelectPath(ChildPath);
            await this.OpenSelectedAsync();
            this.SelectPath(Deep);
            await this.OpenSelectedAsync();
            await this.BackAsync();
            Check(SamePath(this.CurrentPath, ChildPath) && this.NavigationTreeKey == this.TreeKeyForSection(Section),
                "Back navigation restores the category that opened the history entry");
            await this.ForwardAsync();
            Check(SamePath(this.CurrentPath, Deep) && ComputerState().SequenceEqual(ComputerBeforeScopedNavigation)
                && BookmarkState(Other.Folders.Select(Folder => Folder.Node)).SequenceEqual(OtherBeforeScopedNavigation)
                && SidebarSelection() is { Length: 1 } ForwardSelection && SamePath(ForwardSelection[0].FullPath, Deep),
                "Opening main-panel subfolders and traversing history stays within the originating category");
            Button.Focus();
            await this.OpenCategoryFolderAsync(Button, Navigate: false);
            var FocusHistory = this.History.ToArray();
            var FocusVersion = this.NavigationVersion;
            this.DirectoryTreeSection.IsExpanded = false;
            this.FileGrid.Focus();
            if (!this.FileGrid.IsKeyboardFocusWithin)
            {
                FocusManager.SetFocusedElement(this, this.FileGrid);
                this.FileGridKeyboardFocusWithinChanged(this.FileGrid, new DependencyPropertyChangedEventArgs(UIElement.IsKeyboardFocusWithinProperty, false, true));
            }
            await this.PanelTreeSyncTask;
            await Settle();
            Check(Root.IsExpanded && Visit(Root).Single(Node => SamePath(Node.FullPath, Deep)).IsSelected && !Root.IsSelected
                && !this.DirectoryTreeSection.IsExpanded && ComputerState().SequenceEqual(ComputerBeforeScopedNavigation)
                && BookmarkState(Other.Folders.Select(Folder => Folder.Node)).SequenceEqual(OtherBeforeScopedNavigation)
                && this.NavigationVersion == FocusVersion && this.History.SequenceEqual(FocusHistory),
                "Main-panel focus reveals the path only in its originating category and preserves other trees and history");
            this.FolderTree.Focus();
            var Navigation = this.NavigateAsync(ChildPath, TreeKey: this.TreeKeyForSection(Section));
            FocusManager.SetFocusedElement(this, this.FileGrid);
            this.FileGridKeyboardFocusWithinChanged(this.FileGrid, new DependencyPropertyChangedEventArgs(UIElement.IsKeyboardFocusWithinProperty, false, true));
            await Navigation;
            await this.PanelTreeSyncTask;
            Check(SamePath(this.CurrentPath, ChildPath) && ComputerState().SequenceEqual(ComputerBeforeScopedNavigation)
                && Visit(Root).Single(Node => SamePath(Node.FullPath, ChildPath)).IsSelected,
                "Focus gained during category navigation synchronizes the final path only in that category");

            var OtherSection = this.SidebarSectionElement(Other)!;
            var OtherList = FindVisualChild<ItemsControl>(OtherSection)!;
            var OtherButton = FindVisualChild<Button>(OtherList.ItemContainerGenerator.ContainerFromIndex(0))!;
            Root.IsExpanded = false;
            Other.Folders[0].Node.IsExpanded = false;
            var OlderSource = this.OpenCategoryFolderAsync(Button, Navigate: true);
            var LatestSource = this.OpenCategoryFolderAsync(OtherButton, Navigate: true);
            Check(OnlySelected(Other.Folders[0].Node), "The latest category click clears the earlier highlight before asynchronous navigation completes");
            await Task.WhenAll(OlderSource, LatestSource);
            Check(SamePath(this.CurrentPath, Alpha) && this.NavigationTreeKey == this.TreeKeyForSection(OtherSection)
                && ComputerState().SequenceEqual(ComputerBeforeScopedNavigation) && OnlySelected(Other.Folders[0].Node),
                "Rapid navigation to the same path from different categories keeps the latest clicked category as its source");
            var SamePathHistoryCount = this.History.Count;
            Root.IsExpanded = false;
            await this.OpenCategoryFolderAsync(Button, Navigate: true);
            Check(this.NavigationTreeKey == this.TreeKeyForSection(Section) && this.History.Count == SamePathHistoryCount
                && OnlySelected(Root) && !Other.Folders[0].Node.IsSelected,
                "Clicking the same path in another category moves the only highlight without adding history");
            this.SelectPath(ChildPath);
            await this.OpenSelectedAsync();
            var CategoryBeforeQuickAccess = BookmarkState(Category.Folders.Select(Folder => Folder.Node));
            Check(!Other.Folders[0].Node.IsExpanded,
                "Navigating another category with a single-click preserves its collapsed state");
            var OtherBeforeQuickAccess = BookmarkState(Other.Folders.Select(Folder => Folder.Node));
            this.FavoritesList.ItemsSource = new[] { new FavoriteLocation("Quick Alpha", Alpha) };
            this.FavoritesSection.IsExpanded = true;
            await Settle();
            var QuickContainer = this.FavoritesList.ItemContainerGenerator.ContainerFromIndex(0);
            var QuickButton = FindVisualChild<Button>(QuickContainer)!;
            QuickButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await this.SidebarClickTask;
            var QuickRoot = ((FavoriteLocation)QuickButton.DataContext).Node;
            Check(OnlySelected(QuickRoot) && this.NavigationTreeKey == "quick-access"
                && ComputerState().SequenceEqual(ComputerBeforeScopedNavigation)
                && BookmarkState(Category.Folders.Select(Folder => Folder.Node)).SequenceEqual(CategoryBeforeQuickAccess),
                "A Quick Access root clears category highlights even when they contain the same path");
            await this.NavigateAsync(Empty, RevealTree: false, SyncNavigationTree: false, TreeKey: "quick-access");
            var QuickNavigationVersion = this.NavigationVersion;
            DoubleClickHeader(QuickButton);
            await this.SidebarClickTask;
            await Settle();
            Check(QuickRoot.IsExpanded && QuickRoot.IsLoaded && SamePath(this.CurrentPath, Alpha)
                && this.NavigationVersion == QuickNavigationVersion + 1 && OnlySelected(QuickRoot),
                "A Quick Access double-click opens its tree and updates the main panel exactly once");
            QuickNavigationVersion = this.NavigationVersion;
            QuickButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await this.SidebarClickTask;
            Check(QuickRoot.IsExpanded, "A Quick Access single-click navigates without collapsing an expanded root");
            DoubleClickHeader(QuickButton);
            await this.SidebarClickTask;
            await Settle();
            Check(!QuickRoot.IsExpanded && !FindVisualChild<TreeView>(QuickContainer)!.IsVisible
                && SamePath(this.CurrentPath, Alpha) && this.NavigationVersion == QuickNavigationVersion && OnlySelected(QuickRoot),
                "A Quick Access double-click closes its tree while preserving the main panel and history");
            QuickRoot.IsExpanded = true;
            await this.EnsureChildrenAsync(QuickRoot);
            await Settle();
            var QuickTree = FindVisualChild<TreeView>(QuickContainer)!;
            var QuickChild = (TreeViewItem)QuickTree.ItemContainerGenerator.ContainerFromIndex(0);
            await this.OpenSidebarTreeFolderAsync(QuickChild, Navigate: true);
            await this.SyncPanelTreesAsync(this.NavigationVersion);
            await this.RefreshAsync(true);
            Check(SamePath(this.CurrentPath, ChildPath) && ComputerState().SequenceEqual(ComputerBeforeScopedNavigation)
                && BookmarkState(Category.Folders.Select(Folder => Folder.Node)).SequenceEqual(CategoryBeforeQuickAccess)
                && BookmarkState(Other.Folders.Select(Folder => Folder.Node)).SequenceEqual(OtherBeforeQuickAccess)
                && SidebarSelection() is { Length: 1 } QuickSelection && SamePath(QuickSelection[0].FullPath, ChildPath),
                "Quick Access child navigation, panel synchronization and a full refresh preserve every other section");

            var QuickBeforeComputer = BookmarkState(this.FavoritesList.Items.OfType<FavoriteLocation>().Select(Folder => Folder.Node));
            this.DirectoryTreeSection.IsExpanded = true;
            await Settle();
            var ComputerItem = RenderedNode(this.FolderTree, ComputerNode)!;
            ComputerNode.IsExpanded = false;
            await this.OpenSidebarTreeFolderAsync(ComputerItem, Navigate: true);
            await this.SyncPanelTreesAsync(this.NavigationVersion);
            await this.RefreshAsync(true);
            Check(this.NavigationTreeKey == "folder-tree" && this.FolderTree.SelectedItem is FolderNode CurrentComputer && OnlySelected(CurrentComputer)
                && BookmarkState(this.FavoritesList.Items.OfType<FavoriteLocation>().Select(Folder => Folder.Node)).SequenceEqual(QuickBeforeComputer)
                && BookmarkState(Category.Folders.Select(Folder => Folder.Node)).SequenceEqual(CategoryBeforeQuickAccess)
                && BookmarkState(Other.Folders.Select(Folder => Folder.Node)).SequenceEqual(OtherBeforeQuickAccess),
                "My Computer navigation and a full refresh preserve Quick Access and all categories");

            // A parent bookmark and an independent child bookmark share a path,
            // but F5 must reveal only the tree whose node was clicked.
            this.AddFoldersToCategory(Category, [new FileEntry { FullPath = ChildPath, IsDirectory = true }]);
            await Settle();
            var DirectChildRoot = Category.Folders.Single(Folder => SamePath(Folder.FullPath, ChildPath)).Node;
            var DirectChildContainer = List.ItemContainerGenerator.ContainerFromIndex(Category.Folders.IndexOf(
                Category.Folders.Single(Folder => ReferenceEquals(Folder.Node, DirectChildRoot))));
            var DirectChildButton = FindVisualChild<Button>(DirectChildContainer)!;
            var PaddingPath = Directory.CreateDirectory(Path.Combine(Fixture, "Scroll padding")).FullName;
            for (var Index = 0; Index < 80; Index++) Directory.CreateDirectory(Path.Combine(PaddingPath, $"Folder {Index:00}"));
            this.AddFoldersToCategory(Other, [new FileEntry { FullPath = PaddingPath, IsDirectory = true }]);
            var PaddingRoot = Other.Folders.Single(Folder => SamePath(Folder.FullPath, PaddingPath)).Node;
            await this.EnsureChildrenAsync(PaddingRoot);
            PaddingRoot.IsExpanded = true;
            OtherSection.IsExpanded = true;
            this.CollapseTreeBranches([Root, DirectChildRoot]);
            await this.OpenCategoryFolderAsync(DirectChildButton, Navigate: true);
            var ParentBeforeRefresh = BookmarkState([Root]);
            var OtherBeforeRefresh = BookmarkState(Other.Folders.Select(Folder => Folder.Node));
            var ComputerBeforeRefresh = ComputerState();
            var RefreshHistory = this.History.ToArray();
            this.SidebarColumn.Width = new GridLength(360);
            this.ChangeShowBookmarkPaths(true);
            await Settle();
            await ScrollAway(DirectChildButton);
            var BeforePassiveRefresh = this.SidebarScroll.VerticalOffset;
            await this.RefreshAsync();
            await Settle();
            Check(!HeaderVisible(DirectChildButton) && Math.Abs(this.SidebarScroll.VerticalOffset - BeforePassiveRefresh) < 0.5,
                "A passive refresh preserves a manually scrolled bookmark viewport");
            await RefreshWithoutHorizontalJump(Section, 0);
            await Settle();
            Check(DirectChildRoot.IsExpanded && DirectChildRoot.IsLoaded && OnlySelected(DirectChildRoot)
                && BookmarkState([Root]).SequenceEqual(ParentBeforeRefresh)
                && BookmarkState(Other.Folders.Select(Folder => Folder.Node)).SequenceEqual(OtherBeforeRefresh)
                && ComputerState().SequenceEqual(ComputerBeforeRefresh) && this.History.SequenceEqual(RefreshHistory),
                "F5 expands the clicked independent child bookmark and refreshes content without opening its overlapping parent or changing history");
            Check(HeaderVisible(DirectChildButton), "F5 scrolls the already selected independent bookmark header into view below pinned headers");
            await this.SyncPanelTreesAsync(this.NavigationVersion);
            Check(OnlySelected(DirectChildRoot) && BookmarkState([Root]).SequenceEqual(ParentBeforeRefresh),
                "Main-panel synchronization keeps the clicked child bookmark when its parent is also bookmarked");
            var AddedPath = Directory.CreateDirectory(Path.Combine(ChildPath, "Added Before F5")).FullName;
            await this.HandleShortcutAsync(Key.F5, ModifierKeys.None, InText: false, InFileList: false);
            Check(OnlySelected(DirectChildRoot) && DirectChildRoot.Children.Any(Node => SamePath(Node.FullPath, AddedPath))
                && this.Entries.Any(Entry => SamePath(Entry.FullPath, AddedPath)) && !Root.IsExpanded,
                "F5 refreshes newly added child folders inside the selected bookmark without expanding its parent");

            // Select the same path through the parent's tree instead. Choosing the
            // longest matching path alone would incorrectly switch to the shortcut.
            await this.EnsureChildrenAsync(Root);
            Root.IsExpanded = true;
            this.CollapseTreeBranches([DirectChildRoot]);
            await Settle();
            var NestedChild = Root.Children.Single(Node => SamePath(Node.FullPath, ChildPath));
            await this.OpenSidebarTreeFolderAsync(RenderedNode(Tree, NestedChild)!, Navigate: true);
            await ScrollAway(RenderedNode(Tree, NestedChild)!);
            Directory.Delete(AddedPath);
            await RefreshWithoutHorizontalJump(Section, 96);
            await Settle();
            Check(OnlySelected(NestedChild) && NestedChild.IsExpanded && !DirectChildRoot.IsExpanded
                && !NestedChild.Children.Any(Node => SamePath(Node.FullPath, AddedPath))
                && !this.Entries.Any(Entry => SamePath(Entry.FullPath, AddedPath)),
                "F5 preserves the selected nested node instead of switching to an overlapping direct bookmark and removes deleted children");
            this.CaptureAddressBreadcrumbPreview("TreeRefreshScroll.png");
            Check(HeaderVisible(RenderedNode(Tree, NestedChild)!), "F5 reveals the selected category descendant instead of only its root");
            var DeepNode = NestedChild.Children.Single(Node => SamePath(Node.FullPath, Deep));
            await this.OpenSidebarTreeFolderAsync(RenderedNode(Tree, DeepNode)!, Navigate: true);
            await ScrollAway(RenderedNode(Tree, DeepNode)!);
            await RefreshWithoutHorizontalJump(Section, 0);
            await Settle();
            Check(OnlySelected(DeepNode) && HeaderVisible(RenderedNode(Tree, DeepNode)!),
                "F5 reveals the exact deep category node without hiding it under pinned ancestors");

            var QuickDirectChild = new FavoriteLocation("Quick Child", ChildPath);
            this.FavoritesList.ItemsSource = new[] { new FavoriteLocation("Quick Parent", Alpha), QuickDirectChild };
            await Settle();
            var QuickDirectButton = FindVisualChild<Button>(this.FavoritesList.ItemContainerGenerator.ContainerFromIndex(1))!;
            await this.OpenCategoryFolderAsync(QuickDirectButton, Navigate: true);
            await ScrollAway(QuickDirectButton);
            await RefreshWithoutHorizontalJump(this.FavoritesSection, 96);
            await Settle();
            Check(OnlySelected(QuickDirectChild.Node) && QuickDirectChild.Node.IsExpanded
                && !this.FavoritesList.Items.OfType<FavoriteLocation>().First().Node.IsExpanded,
                "F5 keeps the clicked Quick Access child tree when Quick Access also contains its parent");
            Check(HeaderVisible(QuickDirectButton), "F5 scrolls the exact Quick Access root header into view");
            var QuickDirectTree = FindVisualChild<TreeView>(this.FavoritesList.ItemContainerGenerator.ContainerFromIndex(1))!;
            var QuickDeepNode = QuickDirectChild.Node.Children.Single(Node => SamePath(Node.FullPath, Deep));
            await this.OpenSidebarTreeFolderAsync(RenderedNode(QuickDirectTree, QuickDeepNode)!, Navigate: true);
            await ScrollAway(RenderedNode(QuickDirectTree, QuickDeepNode)!);
            await RefreshWithoutHorizontalJump(this.FavoritesSection, 0);
            await Settle();
            Check(OnlySelected(QuickDeepNode) && HeaderVisible(RenderedNode(QuickDirectTree, QuickDeepNode)!),
                "F5 scrolls the selected Quick Access descendant into view");
            this.FolderTree.Focus();
        }
        finally
        {
            ++this.TreeExpansionVersion;
            await this.TreeExpansionTask;
            if (Menu is not null) Menu.IsOpen = false;
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.RestoreSidebarCategories(InitialCategories, InitialOrder);
            this.FavoritesList.ItemsSource = InitialFavoritesSource;
            this.FavoritesSection.IsExpanded = InitialFavorites;
            this.DirectoryTreeSection.IsExpanded = InitialTree;
            this.ChangeShowBookmarkPaths(InitialBookmarkPaths);
            this.SidebarColumn.Width = InitialSidebarWidth;
            this.SidebarScroll.ScrollToTop();
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != FixtureId
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned category tree fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
