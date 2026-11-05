using System.Windows;
using System.Windows.Controls;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private IEnumerable<FolderNode> SidebarBookmarkRoots() => this.FavoritesList.Items.OfType<FavoriteLocation>().Select(Folder => Folder.Node)
        .Concat(this.CustomCategories.SelectMany(Category => Category.Folders).Select(Folder => Folder.Node));

    private Expander? CategoryMenuSection(ContextMenu Menu)
        => this.CategorySection(Menu.PlacementTarget);

    private Expander? CategorySection(DependencyObject? Source)
    {
        var Section = Source as Expander ?? FindAncestor<Expander>(Source);
        return Section is not null && (ReferenceEquals(Section, this.FavoritesSection) || ReferenceEquals(Section, this.DirectoryTreeSection)
            || Section.DataContext is SidebarCategory Category && this.CustomCategories.Contains(Category)) ? Section : null;
    }

    private Expander? CategoryFolderActionSection(object Sender)
    {
        if (Sender is Button Button) return this.CategorySection(Button);
        if (Sender is not MenuItem Item || ItemsControl.ItemsControlFromItemContainer(Item) is not ContextMenu Menu) return null;
        var Section = this.CategoryMenuSection(Menu);
        Menu.IsOpen = false;
        return Section;
    }

    private FolderNode[] CategoryFolderRoots(Expander Section) => ReferenceEquals(Section, this.DirectoryTreeSection) ? this.Roots.ToArray()
        : ReferenceEquals(Section, this.FavoritesSection) ? this.FavoritesList.Items.OfType<FavoriteLocation>().Select(Folder => Folder.Node).ToArray()
        : Section.DataContext is SidebarCategory Category && this.CustomCategories.Contains(Category) ? Category.Folders.Select(Folder => Folder.Node).ToArray() : [];

    private void CategoryContextOpened(object Sender, RoutedEventArgs E)
    {
        if (Sender is not ContextMenu Menu) return;
        UpdateMenuHeaders(Menu);
        var Section = this.CategoryMenuSection(Menu);
        if (Section is not null) Menu.DataContext = Section.DataContext;
        var HasFolders = Section is not null && this.CategoryFolderRoots(Section).Length > 0;
        foreach (var Item in Menu.Items.OfType<MenuItem>())
            if (Item.Tag is "ExpandCategoryFolders" or "CollapseCategoryFolders") Item.IsEnabled = HasFolders;
    }

    private async void ExpandCategoryFoldersClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (this.CategoryFolderActionSection(Sender) is not { } Section) return;
        Section.IsExpanded = true;
        this.TreeExpansionTask = this.ExpandCategoryRootsAsync(this.CategoryFolderRoots(Section), ++this.TreeExpansionVersion);
        await this.TreeExpansionTask;
    }

    private async Task ExpandCategoryRootsAsync(FolderNode[] Roots, int Version)
    {
        foreach (var Root in Roots)
        {
            if (Version != this.TreeExpansionVersion || this.WindowClosed) return;
            await this.ExpandTreeBranchAsync(Root, Version);
        }
    }

    private void CollapseCategoryFoldersClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (this.CategoryFolderActionSection(Sender) is not { } Section) return;
        ++this.TreeExpansionVersion;
        this.CollapseTreeBranches(this.CategoryFolderRoots(Section));
        Section.IsExpanded = true;
    }

    private async void SidebarFolderToggleClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        this.CancelSidebarFolderClick();
        ++this.PanelFocusVersion;
        this.PanelTreeSyncRequested = false;
        var Node = (Sender as FrameworkElement)?.DataContext switch
        {
            CategoryFolder Folder => Folder.Node,
            FavoriteLocation Location => Location.Node,
            _ => null
        };
        if (Node is null) return;
        if (Node.IsExpanded) await this.EnsureChildrenAsync(Node);
        else ++this.TreeExpansionVersion;
    }

    private async void SidebarChildTreeSelectedItemChanged(object Sender, RoutedPropertyChangedEventArgs<object> E)
    {
        E.Handled = true;
        if (!this.SyncingTree && E.NewValue is FolderNode { IsPlaceholder: false } Node
            && !IsCollapsedAncestorSelection(E.OldValue, Node))
        {
            if (this.CategorySection(Sender as DependencyObject) is not { } Section) return;
            this.SyncingTree = true;
            try { this.SelectSidebarNode(Node); }
            finally { this.SyncingTree = false; }
            await this.NavigateFromTreeAsync(Node, this.TreeKeyForSection(Section));
        }
    }
}
