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
            Check(RootLines.IsBookmarkRoot && VisualTreeHelper.GetDrawing(RootLines)!.Bounds.Height > Button.ActualHeight
                && LastRootLines.IsBookmarkRoot && !VisualTreeHelper.GetDrawing(LastRootLines)!.Bounds.IsEmpty
                && VisualTreeHelper.GetDrawing(LastRootLines)!.Bounds.Bottom <= LastRootLines.ActualHeight + 1,
                "Category folders retain indentation guides within each visible subtree");
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
                .SequenceEqual(new[] { "ExpandAllSubfolders", "CollapseAllSubfolders", "RemoveFromCategory" }),
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
            this.SidebarScroll.ScrollToTop();
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != FixtureId
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned category tree fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
