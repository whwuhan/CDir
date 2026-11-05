using System.Windows;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private bool RestoringWindowLayout = true;
    private readonly string? LayoutProbeSettingsFile;
    private readonly TaskCompletionSource InitialLayoutReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RestoreSidebarWidth(double Width)
    {
        this.ExpandedSidebarWidth = new GridLength(double.IsFinite(Width) && Width >= 190 ? Width : DefaultSettings.Current.SidebarWidth);
        this.SidebarColumn.Width = this.ExpandedSidebarWidth;
        this.ApplySidebarVisibility();
    }

    private FolderNode[] LayoutRoots(object Section) => Section is SidebarCategory Category
        ? Category.Folders.Select(Folder => Folder.Node).ToArray()
        : ReferenceEquals(Section, this.FavoritesSection)
            ? this.FavoritesList.Items.OfType<FavoriteLocation>().Select(Folder => Folder.Node).ToArray()
            : this.Roots.ToArray();

    private Dictionary<string, SidebarTreeLayout> CaptureSidebarTreeLayouts()
    {
        var Layouts = new Dictionary<string, SidebarTreeLayout>(StringComparer.Ordinal);
        foreach (var Item in this.SidebarSections)
        {
            var Layout = new SidebarTreeLayout();
            var Pending = new Stack<FolderNode>(this.LayoutRoots(Item));
            while (Pending.TryPop(out var Node))
            {
                if (Node.IsPlaceholder) continue;
                if (Node.IsExpanded) Layout.ExpandedPaths!.Add(Node.FullPath);
                if (Node.IsSelected) Layout.SelectedPaths!.Add(Node.FullPath);
                foreach (var Child in Node.Children) Pending.Push(Child);
            }
            Layouts[this.SidebarSectionKey(Item)] = Layout;
        }
        return Layouts;
    }

    private async Task RestoreSidebarTreeLayoutsAsync(Dictionary<string, SidebarTreeLayout>? Layouts, int Version)
    {
        if (Layouts is null) return;
        this.SyncingTree = true;
        var WasSuppressed = this.SuppressTreeSelectionScroll;
        this.SuppressTreeSelectionScroll = true;
        try
        {
            FolderNode? RestoredSelection = null;
            foreach (var (Key, Layout) in Layouts)
            {
                var Item = this.SidebarSections.FirstOrDefault(Section => this.SidebarSectionKey(Section) == Key);
                if (Layout is null || Item is null) continue;
                static string PathKey(string Path) => Path.Replace('/', '\\').TrimEnd('\\');
                var Expanded = (Layout.ExpandedPaths ?? []).Where(Path => !string.IsNullOrWhiteSpace(Path))
                    .Select(PathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var Selected = (Layout.SelectedPaths ?? []).Where(Path => !string.IsNullOrWhiteSpace(Path))
                    .Select(PathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var Targets = Expanded.Concat(Selected).ToArray();
                foreach (var Root in this.LayoutRoots(Item)) await RestoreNodeAsync(Root);

                async Task RestoreNodeAsync(FolderNode Node)
                {
                    if (Version != this.NavigationVersion || this.WindowClosed || Node.IsPlaceholder) return;
                    var Path = PathKey(Node.FullPath);
                    var HasDescendants = Targets.Any(Target => Target.StartsWith(Path + "\\", StringComparison.OrdinalIgnoreCase));
                    if (Expanded.Contains(Path) || HasDescendants)
                    {
                        await this.EnsureChildrenAsync(Node);
                        if (Version != this.NavigationVersion || this.WindowClosed) return;
                        foreach (var Child in Node.Children.ToArray()) await RestoreNodeAsync(Child);
                    }
                    // Restore descendants before collapsing their parent, retaining the
                    // remembered expansion of branches hidden inside a collapsed folder.
                    Node.IsExpanded = Expanded.Contains(Path);
                    Node.IsSelected = false;
                    if (Key == this.NavigationTreeKey && Selected.Contains(Path)
                        && (RestoredSelection is null || SamePath(Node.FullPath, this.CurrentPath))) RestoredSelection = Node;
                }
            }
            if (Version == this.NavigationVersion && !this.WindowClosed)
            {
                if (RestoredSelection is not null) await this.EnsureChildrenAsync(RestoredSelection);
                if (Version == this.NavigationVersion && !this.WindowClosed) this.SelectSidebarNode(RestoredSelection);
            }
        }
        finally
        {
            this.SuppressTreeSelectionScroll = WasSuppressed;
            if (Version == this.NavigationVersion) this.SyncingTree = false;
        }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }
}
