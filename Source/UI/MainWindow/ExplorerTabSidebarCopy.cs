using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private Action<string> WriteClipboardText = Clipboard.SetText;

    private bool TryCopySidebarFolderPath(DependencyObject? Source)
    {
        if (Source is null || !this.SidebarPanel.IsAncestorOf(Source) || FindAncestor<TextBoxBase>(Source) is not null) return false;
        var Node = FindAncestor<TreeViewItem>(Source)?.DataContext as FolderNode
            ?? FindAncestor<TreeView>(Source)?.SelectedItem as FolderNode
            ?? (FindAncestor<Button>(Source)?.DataContext switch
            {
                FavoriteLocation Favorite => Favorite.Node,
                CategoryFolder Folder => Folder.Node,
                _ => null
            });
        if (Node is { IsSelected: true, IsPlaceholder: false } && Path.IsPathFullyQualified(Node.FullPath))
            this.CopyText(Path.GetFullPath(Node.FullPath), LanguageService.Text("SelectedPathsCopied"));
        // Sidebar copy must never pick up a stale selection in the file list.
        return true;
    }
}
