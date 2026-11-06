using System.IO;
using System.Windows;
using System.Windows.Controls;
using CDir.Localization;
using CDir.Models;
using CDir.UI.Dialogs;

namespace CDir.UI;

public partial class ExplorerTab
{
    private Task TreeFolderCreationTask = Task.CompletedTask;

    private static FolderNode? TreeMenuFolder(object? Context) => Context switch
    {
        FolderNode Node => Node,
        CategoryFolder Folder => Folder.Node,
        _ => null,
    };

    private async void NewTreeFolderClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        // Capture the menu target before closing its popup; the main panel can
        // be displaying a completely different directory.
        var Node = TreeMenuFolder((Sender as FrameworkElement)?.DataContext);
        CloseSidebarMenu(Sender);
        this.TreeFolderCreationTask = this.NewTreeFolderAsync(Node);
        await this.TreeFolderCreationTask;
    }

    private async Task NewTreeFolderAsync(FolderNode? Node)
    {
        if (Node is null || Node.IsPlaceholder || this.WindowClosed || this.OperationBusy || this.Loading) return;
        var Parent = Node.FullPath;
        this.CancelInlineRename();
        string? NewPath = null;
        if (!await this.RunOperationAsync(LanguageService.Text("CreatingFolder"),
            () => NewPath = this.CreateDefaultFolder(Parent), LanguageService.Text("FolderCreated"), RequiresSta: true) || NewPath is null || this.WindowClosed) return;

        await this.EnsureChildrenAsync(Node);
        if (this.WindowClosed) return;
        // A loaded tree may not have been refreshed (for example while the
        // main panel shows the Recycle Bin), or its pending read may predate creation.
        if (Node.IsLoaded && !Node.Children.Any(Child => SamePath(Child.FullPath, NewPath)))
        {
            var Child = new FolderNode(Path.GetFileName(NewPath), NewPath);
            var Index = 0;
            while (Index < Node.Children.Count && StringComparer.CurrentCultureIgnoreCase.Compare(Node.Children[Index].Name, Child.Name) < 0) Index++;
            Node.Children.Insert(Index, Child);
        }
        Node.IsExpanded = true;
        if (SamePath(this.CurrentPath, Parent)) this.BeginFileRename(NewPath);
        else this.BeginInlineRename(Node.Children.Single(Child => SamePath(Child.FullPath, NewPath)));
    }
}
