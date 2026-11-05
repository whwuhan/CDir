using System.IO;
using System.Windows;
using System.Windows.Controls;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private bool AddFoldersToCategory(SidebarCategory Category, IEnumerable<FileEntry> Selection)
    {
        if (!this.CustomCategories.Contains(Category)) return false;
        var Changed = false;
        foreach (var Entry in Selection.Where(Entry => Entry.IsDirectory))
        {
            if (Category.Folders.Any(Folder => SamePath(Folder.FullPath, Entry.FullPath))) continue;
            Category.Folders.Add(new CategoryFolder { FullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Entry.FullPath)) });
            Changed = true;
        }
        if (Changed) this.SaveSidebarCategories();
        return Changed;
    }

    private async void CategoryFolderClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (Sender is Button Button) await this.OpenSidebarFolderClickAsync(() => this.OpenCategoryFolderAsync(Button, Navigate: true), Button);
    }

    private Task OpenCategoryFolderAsync(Button Button, bool Navigate) => this.OpenBookmarkFolderAsync(Button, Navigate);

    private async Task OpenBookmarkFolderAsync(Button Button, bool Navigate, bool ToggleExpansion = false)
    {
        if (this.WindowClosed || this.CategorySection(Button) is not { } Section) return;
        var Node = Button.DataContext switch { CategoryFolder Folder => Folder.Node, FavoriteLocation Location => Location.Node, _ => null };
        if (Node is null) return;
        ++this.PanelFocusVersion;
        this.PanelTreeSyncRequested = false;
        var WasSyncing = this.SyncingTree;
        this.SyncingTree = true;
        try
        {
            if (Navigate) this.SelectSidebarNode(Node);
            if (!Navigate || ToggleExpansion)
            {
                Node.IsExpanded = !Node.IsExpanded;
                if (!Node.IsExpanded) ++this.TreeExpansionVersion;
            }
        }
        finally { this.SyncingTree = WasSyncing; }
        var Navigation = Navigate ? this.NavigateFromTreeAsync(Node, this.TreeKeyForSection(Section)) : Task.CompletedTask;
        if ((!Navigate || ToggleExpansion) && Node.IsExpanded) await this.EnsureChildrenAsync(Node);
        await Navigation;
    }

    private void RemoveCategoryFolderClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        CloseSidebarMenu(Sender);
        if (Sender is not FrameworkElement { DataContext: CategoryFolder Folder }) return;
        var Category = this.CustomCategories.FirstOrDefault(Category => Category.Folders.Contains(Folder));
        if (Category is not null && Category.Folders.Remove(Folder)) this.SaveSidebarCategories();
    }

    private bool TryMoveCategoryFolder(SidebarCategory Category, CategoryFolder Folder, int InsertionIndex)
    {
        if (!this.CustomCategories.Contains(Category)) return false;
        var OldIndex = Category.Folders.IndexOf(Folder);
        if (OldIndex < 0 || InsertionIndex < 0 || InsertionIndex > Category.Folders.Count) return false;
        var NewIndex = InsertionIndex > OldIndex ? InsertionIndex - 1 : InsertionIndex;
        if (NewIndex == OldIndex) return false;
        Category.Folders.Move(OldIndex, NewIndex);
        this.SaveSidebarCategories();
        return true;
    }
}
