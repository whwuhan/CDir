using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunAddressSourceSmokeChecksAsync(List<string> Passed, string ParentPath, string ChildPath, string LongPath)
    {
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        var InitialFavorites = this.FavoritesList.ItemsSource;
        var InitialFavoritesExpanded = this.FavoritesSection.IsExpanded;
        var InitialComputerExpanded = this.DirectoryTreeSection.IsExpanded;
        var InitialPath = this.CurrentPath;
        var InitialTreeKey = this.NavigationTreeKey;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Address source UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task WaitForPath(string Path)
        {
            var Deadline = Environment.TickCount64 + 15000;
            while (this.Loading || !SamePath(this.CurrentPath, Path))
            {
                if (Environment.TickCount64 > Deadline) throw new TimeoutException("Address source navigation did not settle: " + Path);
                await Task.Delay(25);
            }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            this.UpdateLayout();
        }
        static IEnumerable<FolderNode> Visit(FolderNode Node) => new[] { Node }.Concat(Node.Children.SelectMany(Visit));
        void CheckSource(Expander Section, string Path, string Action)
        {
            var Key = this.TreeKeyForSection(Section);
            var Selected = this.Roots.Concat(this.SidebarBookmarkRoots()).SelectMany(Visit).Where(Node => Node.IsSelected).ToArray();
            Check(SamePath(this.CurrentPath, Path) && this.NavigationTreeKey == Key
                && this.HistoryTreeKeys[this.HistoryIndex] == Key && !this.DirectoryTreeSection.IsExpanded
                && Selected.Length == 1 && SamePath(Selected[0].FullPath, Path)
                && this.CategoryFolderRoots(Section).SelectMany(Visit).Contains(Selected[0]),
                Action + " retains the " + Key + " source and its sole highlight without opening My Computer");
        }
        void EnterAddress(string Path)
        {
            this.BeginAddressEdit();
            this.AddressBox.Text = Path;
            this.AddressBoxKeyDown(this.AddressBox, new KeyEventArgs(this.UnmodifiedSmokeKeyboard,
                PresentationSource.FromVisual(this.AddressBox), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        }
        void CheckComputer(string Path, string Action)
        {
            var Selected = this.Roots.Concat(this.SidebarBookmarkRoots()).SelectMany(Visit).Where(Node => Node.IsSelected).ToArray();
            Check(SamePath(this.CurrentPath, Path) && this.NavigationTreeKey == "folder-tree"
                && this.HistoryTreeKeys[this.HistoryIndex] == "folder-tree" && this.DirectoryTreeSection.IsExpanded
                && Selected.Length == 1 && SamePath(Selected[0].FullPath, Path)
                && Selected[0].IsExpanded && Selected[0].IsLoaded && this.Roots.SelectMany(Visit).Contains(Selected[0]),
                Action + " expands My Computer and selects the target as the sole sidebar highlight and history source");
            TreeViewItem? Find(ItemsControl Owner)
            {
                foreach (var Node in Owner.Items.OfType<FolderNode>())
                {
                    if (Owner.ItemContainerGenerator.ContainerFromItem(Node) is not TreeViewItem Item) continue;
                    if (ReferenceEquals(Node, Selected[0])) return Item;
                    if (Item.IsExpanded && Find(Item) is { } Match) return Match;
                }
                return null;
            }
            var Target = Find(this.FolderTree);
            var Top = Target?.TranslatePoint(new Point(), this.SidebarScroll).Y ?? double.NaN;
            Check(Target is { IsVisible: true } && Top >= -1 && Top + 20 <= this.SidebarScroll.ViewportHeight + 1,
                Action + " scrolls the selected My Computer directory into the sidebar viewport");
        }
        try
        {
            Check(this.TryAddSidebarCategory("Address source " + Guid.NewGuid().ToString("N")), "Create an owned category for address-source checks");
            var Category = this.CustomCategories[^1];
            this.AddFoldersToCategory(Category, [new FileEntry { FullPath = ParentPath, IsDirectory = true }]);
            this.FavoritesList.ItemsSource = new[] { new FavoriteLocation("Address source", ParentPath) };
            this.FavoritesSection.IsExpanded = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            this.UpdateLayout();
            foreach (var Section in new[] { this.SidebarSectionElement(Category)!, this.FavoritesSection })
            {
                var List = ReferenceEquals(Section, this.FavoritesSection) ? this.FavoritesList : FindVisualChild<ItemsControl>(Section)!;
                var Button = FindVisualChild<Button>(List.ItemContainerGenerator.ContainerFromIndex(0))!;
                var BookmarkNode = ReferenceEquals(Section, this.FavoritesSection) ? ((FavoriteLocation)Button.DataContext).Node : ((CategoryFolder)Button.DataContext).Node;
                BookmarkNode.IsExpanded = false;
                await this.OpenBookmarkFolderAsync(Button, Navigate: true);
                this.DirectoryTreeSection.IsExpanded = false;
                var Key = this.TreeKeyForSection(Section);
                Check(await this.NavigateAsync(LongPath, TreeKey: Key), "Prepare an address inside its originating category");
                this.UpdateLayout();
                var Segment = this.AddressBreadcrumbs.Items.Cast<AddressSegment>().Single(Item => SamePath(Item.FullPath, ChildPath));
                var SegmentContainer = this.AddressBreadcrumbs.ItemContainerGenerator.ContainerFromItem(Segment);
                var Breadcrumb = FindVisualChild<Button>(SegmentContainer)!;
                Breadcrumb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForPath(ChildPath);
                CheckSource(Section, ChildPath, "Clicking a breadcrumb");

                EnterAddress(LongPath);
                await WaitForPath(LongPath);
                CheckComputer(LongPath, "Submitting a typed address from " + Key + " with Enter");
                await this.BackAsync();
                await WaitForPath(ChildPath);
                this.DirectoryTreeSection.IsExpanded = false;
                CheckSource(Section, ChildPath, "Going back before the typed address jump");
                this.BeginAddressEdit();
                this.AddressBox.Text = ParentPath;
                this.AddressGoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForPath(ParentPath);
                CheckComputer(ParentPath, "Clicking Go from " + Key);
                await this.BackAsync();
                await WaitForPath(ChildPath);
                this.DirectoryTreeSection.IsExpanded = false;
                CheckSource(Section, ChildPath, "Going back after address navigation");
                await this.ForwardAsync();
                await WaitForPath(ParentPath);
                CheckComputer(ParentPath, "Going forward after address navigation");

                await this.NavigateAsync(ChildPath, TreeKey: Key);
                this.DirectoryTreeSection.IsExpanded = false;
                var HistoryCount = this.History.Count;
                EnterAddress(ChildPath);
                await WaitForPath(ChildPath);
                CheckComputer(ChildPath, "Submitting the already open path from " + Key);
                Check(this.History.Count == HistoryCount, "Submitting the current path changes its tree source without duplicating history");

                await this.NavigateAsync(ChildPath, TreeKey: Key);
                this.DirectoryTreeSection.IsExpanded = false;
                var OutsideBookmark = System.IO.Path.GetDirectoryName(ParentPath)!;
                EnterAddress(OutsideBookmark);
                await WaitForPath(OutsideBookmark);
                CheckComputer(OutsideBookmark, "Submitting an address outside " + Key);

                Check(await this.NavigateAsync(LongPath, TreeKey: Key), "Prepare the ancestor overflow menu inside its originating category");
                this.DirectoryTreeSection.IsExpanded = false;
                this.AddressOverflowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var Menu = this.AddressOverflowButton.ContextMenu!;
                Menu.Items.OfType<MenuItem>().Single(Item => Item.Tag is string Path && SamePath(Path, ChildPath))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await WaitForPath(ChildPath);
                Menu.IsOpen = false;
                CheckSource(Section, ChildPath, "Invoking an overflow ancestor");
            }
        }
        finally
        {
            this.AddressOverflowButton.ContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            this.EndAddressEdit();
            this.FavoritesList.ItemsSource = InitialFavorites;
            this.RestoreSidebarCategories(InitialCategories, InitialOrder);
            await this.NavigateAsync(InitialPath, RecordHistory: false, TreeKey: InitialTreeKey);
            this.FavoritesSection.IsExpanded = InitialFavoritesExpanded;
            this.DirectoryTreeSection.IsExpanded = InitialComputerExpanded;
        }
    }
}
