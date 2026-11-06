using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CDir.Controls;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private Expander? StickySectionTarget;
    private sealed record StickyRowLayout(double HeaderX, Thickness Padding, double ArrowX, double[] Columns,
        double HeaderEnd, double ChildColumn, double TailColumn);
    private readonly Dictionary<FolderNode, StickyRowLayout> StickyRowLayouts = new();

    private void UpdateStickySidebar()
    {
        if (this.StickySidebar is null || this.WindowClosed) return;
        var Section = this.SidebarSections.Select(this.SidebarSectionElement).OfType<Expander>()
            .FirstOrDefault(Item => Item.IsExpanded && Item.IsVisible
                && Item.TranslatePoint(new Point(), this.SidebarScroll).Y < 0
                && Item.TranslatePoint(new Point(0, Item.ActualHeight), this.SidebarScroll).Y > 0);
        if (Section is null)
        {
            this.StickySidebar.Visibility = Visibility.Collapsed;
            this.StickySidebarRoot.DataContext = null;
            this.StickySidebarAncestors.ItemsSource = null;
            this.StickyAncestorScroll.Visibility = Visibility.Collapsed;
            this.StickySectionTarget = null;
            this.StickyRowLayouts.Clear();
            return;
        }
        if (!ReferenceEquals(Section, this.StickySectionTarget))
        {
            this.StickySectionTarget = Section;
            this.StickySidebarSection.DataContext = Section.DataContext;
            this.StickySidebarSection.SetBinding(HeaderedContentControl.HeaderProperty, new Binding("Header") { Source = Section });
            this.StickySidebarSection.SetBinding(Expander.IsExpandedProperty, new Binding("IsExpanded") { Source = Section, Mode = BindingMode.TwoWay });
            this.StickySidebarSection.ContextMenu = Section.ContextMenu;
        }
        var Header = Section.Template.FindName("CategoryHeaderToggle", Section) as FrameworkElement;
        var HeaderHeight = Header?.ActualHeight ?? 32;
        // The following category pushes this header out instead of being covered
        // by an old pinned row. Cover the full width, including wide drive cells.
        var SectionBottom = this.SidebarRootBottom(Section);
        this.StickySidebar.RenderTransform = new TranslateTransform(0, Math.Min(0, SectionBottom - HeaderHeight));
        this.StickySidebar.MaxHeight = Math.Max(HeaderHeight, SectionBottom);
        this.StickySidebarSection.Height = HeaderHeight;
        this.StickySidebar.Visibility = Visibility.Visible;
        // Use the root's full subtree bounds, never the currently selected descendant.
        var Root = this.CategoryFolderRoots(Section).Select(Node => (Node, Header: this.SidebarRootHeader(Section, Node)))
            .FirstOrDefault(Item => Item.Node.IsExpanded && Item.Header is not null
                && Item.Header.TranslatePoint(new Point(), this.SidebarScroll).Y < HeaderHeight
                && this.SidebarRootBottom(Item.Header) > HeaderHeight + this.SidebarRootHeaderHeight(Item.Header));
        this.StickySidebarRoot.DataContext = Root.Node;
        this.StickySidebarRoot.Visibility = Root.Node is null ? Visibility.Collapsed : Visibility.Visible;
        this.StickyRowLayouts.Clear();
        IReadOnlyList<double> Columns = [];
        if (Root.Node is { } RootNode && Root.Header is { } RootContainer
            && this.ReadStickyRowLayout(RootContainer, Columns, IsRoot: true) is { } RootLayout)
        {
            this.StickyRowLayouts[RootNode] = RootLayout;
            Columns = double.IsFinite(RootLayout.TailColumn) ? [.. RootLayout.Columns, RootLayout.TailColumn] : RootLayout.Columns;
            this.ApplyStickyRowLayout(this.StickyRootRow, RootNode);
        }
        var AncestorLimit = Math.Max(24, this.SidebarScroll.ViewportHeight / 2);
        var RootHeaderHeight = Root.Header is TreeViewItem RootItem ? TreeHeaderHeight(RootItem)
            : Root.Header is null ? 0 : FindVisualChild<Button>(Root.Header)?.ActualHeight ?? 20;
        this.StickyFolderRows.MaxHeight = Root.Header is null ? 0 : Math.Max(0, this.SidebarRootBottom(Root.Header) - HeaderHeight);
        var Boundary = HeaderHeight + RootHeaderHeight;
        var AncestorHeight = 0d;
        ItemsControl? Owner = Root.Header is TreeViewItem Item ? Item
            : Root.Header is null ? null : FindVisualChild<TreeView>(Root.Header);
        var Ancestors = new List<FolderNode>();
        // Follow the expanded branch crossing the viewport's pinned edge. The
        // browser may stay at the root while the user independently scrolls deep
        // into this tree, so the main pane path must not choose these ancestors.
        while (Root.Node is { IsExpanded: true } && Owner is not null)
        {
            var Child = Owner.Items.OfType<FolderNode>().Where(Node => !Node.IsPlaceholder && Node.IsExpanded)
                .Select(Node => (Node, Item: Owner.ItemContainerGenerator.ContainerFromItem(Node) as TreeViewItem))
                .FirstOrDefault(Child => Child.Item is { IsVisible: true }
                    && Child.Item.TranslatePoint(new Point(), this.SidebarScroll).Y < Boundary
                    && this.SidebarRootBottom(Child.Item) > Boundary + TreeHeaderHeight(Child.Item));
            if (Child.Item is null) break;
            Ancestors.Add(Child.Node);
            if (this.ReadStickyRowLayout(Child.Item, Columns, IsRoot: false) is { } Layout)
            {
                this.StickyRowLayouts[Child.Node] = Layout;
                Columns = Layout.Columns;
            }
            AncestorHeight += TreeHeaderHeight(Child.Item);
            Boundary = HeaderHeight + RootHeaderHeight + Math.Min(AncestorHeight, AncestorLimit);
            Owner = Child.Item;
        }
        if (!this.StickySidebarAncestors.Items.OfType<FolderNode>().SequenceEqual(Ancestors))
            this.StickySidebarAncestors.ItemsSource = Ancestors;
        this.StickyAncestorScroll.MaxHeight = AncestorLimit;
        this.StickyAncestorScroll.Visibility = Ancestors.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var Node in Ancestors)
            if (this.StickySidebarAncestors.ItemContainerGenerator.ContainerFromItem(Node) is FrameworkElement Row)
                this.ApplyStickyRowLayout(Row, Node);
    }

    private StickyRowLayout? ReadStickyRowLayout(FrameworkElement Source, IReadOnlyList<double> Parents, bool IsRoot)
    {
        var Guides = Source is TreeViewItem Item ? Item.Template.FindName("BranchLines", Item) as TreeBranchLines
            : FindVisualChild<TreeBranchLines>(Source);
        var Header = Source is TreeViewItem TreeItem ? TreeItem.Template.FindName("PART_Header", TreeItem) as FrameworkElement
            : FindVisualChild<Button>(Source);
        var Arrow = Source is TreeViewItem TreeNode ? TreeNode.Template.FindName("Expander", TreeNode) as ToggleButton
            : FindVisualChild<ToggleButton>(Source);
        if (Guides is null || Header is null || Arrow is null) return null;
        var Center = Math.Floor(Guides.ActualWidth / 2) + .5;
        var Column = Guides.TranslatePoint(new Point(Center, 0), this.SidebarScroll).X;
        var Columns = IsRoot && !Guides.HasIncomingGuide ? Parents.ToArray() : Parents.Append(Column).ToArray();
        var Padding = Header switch { Control Control => Control.Padding, Border Border => Border.Padding, _ => new Thickness() };
        return new(Header.TranslatePoint(new Point(), this.SidebarScroll).X, Padding,
            Arrow.TranslatePoint(new Point(), this.SidebarScroll).X, Columns,
            Guides.TranslatePoint(new Point(Guides.ActualWidth, 0), this.SidebarScroll).X,
            double.IsFinite(Guides.ChildGuideColumn) ? Guides.TranslatePoint(new Point(Guides.ChildGuideColumn, 0), this.SidebarScroll).X : double.NaN,
            IsRoot && !Guides.HasIncomingGuide ? Column : double.NaN);
    }

    private void ApplyStickyRowLayout(FrameworkElement Row, FolderNode Node)
    {
        if (!Row.IsLoaded || !this.StickyRowLayouts.TryGetValue(Node, out var Layout)) return;
        var Button = FindVisualChild<Button>(Row);
        var Arrow = FindVisualChild<ToggleButton>(Row);
        var Guides = FindVisualChild<TreeBranchLines>(Row);
        if (Button is null || Arrow is null || Guides is null) return;
        var Origin = Row.TranslatePoint(new Point(), this.SidebarScroll).X;
        Button.Margin = new Thickness(Layout.HeaderX - Origin, 0, 0, 0);
        Button.Padding = Layout.Padding;
        Arrow.Margin = new Thickness(Layout.ArrowX - Origin, 0, 0, 0);
        var GuideOrigin = Guides.TranslatePoint(new Point(), this.SidebarScroll).X;
        Guides.PinnedColumns = Layout.Columns.Select(Column => Column - GuideOrigin).ToArray();
        Guides.PinnedHeaderEnd = Layout.HeaderEnd - GuideOrigin;
        Guides.PinnedChildColumn = Layout.ChildColumn - GuideOrigin;
        Guides.PinnedTailColumn = Layout.TailColumn - GuideOrigin;
        Guides.InvalidateVisual();
    }

    private void StickyRowLoaded(object Sender, RoutedEventArgs E)
    {
        if (Sender is FrameworkElement Row
            && (ReferenceEquals(Row, this.StickyRootRow) ? this.StickySidebarRoot.DataContext : Row.DataContext) is FolderNode Node)
            this.ApplyStickyRowLayout(Row, Node);
    }

    private async void StickyFolderToggleClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (Sender is not FrameworkElement { DataContext: FolderNode Node }) return;
        this.CancelSidebarFolderClick();
        this.SuppressCollapseFocusSync();
        if (Node.IsExpanded) await this.EnsureChildrenAsync(Node);
        else ++this.TreeExpansionVersion;
        this.UpdateStickySidebar();
    }

    private static double TreeHeaderHeight(TreeViewItem Item)
        => (Item.Template.FindName("PART_Header", Item) as FrameworkElement)?.ActualHeight ?? 20;

    private double SidebarRootHeaderHeight(FrameworkElement Root)
        => Root is TreeViewItem Item ? TreeHeaderHeight(Item) : FindVisualChild<Button>(Root)?.ActualHeight ?? 20;

    private FrameworkElement? SidebarRootHeader(Expander Section, FolderNode Node)
    {
        if (ReferenceEquals(Section, this.DirectoryTreeSection))
            return this.FolderTree.ItemContainerGenerator.ContainerFromItem(Node) is TreeViewItem Item ? Item : null;
        var List = ReferenceEquals(Section, this.FavoritesSection) ? this.FavoritesList : FindVisualChild<ItemsControl>(Section);
        if (List is null) return null;
        var Entry = List.Items.Cast<object>().FirstOrDefault(Item => Item switch
        {
            CategoryFolder Folder => ReferenceEquals(Folder.Node, Node),
            FavoriteLocation Folder => ReferenceEquals(Folder.Node, Node),
            _ => false
        });
        return Entry is null ? null : List.ItemContainerGenerator.ContainerFromItem(Entry) as FrameworkElement;
    }

    private double SidebarRootBottom(FrameworkElement Root)
        => Root.TranslatePoint(new Point(0, Root.ActualHeight), this.SidebarScroll).Y;

    private async void StickySidebarRootClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (this.StickySectionTarget is { } Section && Sender is FrameworkElement { DataContext: FolderNode Node })
            await this.NavigateFromTreeAsync(Node, this.TreeKeyForSection(Section));
    }

    private void StickySidebarMouseWheel(object Sender, MouseWheelEventArgs E)
    {
        if (this.StickyAncestorScroll.ScrollableHeight > 0 && E.OriginalSource is DependencyObject Target
            && this.StickyAncestorScroll.IsAncestorOf(Target)) return;
        this.FolderTreeMouseWheel(Sender, E);
    }
}
