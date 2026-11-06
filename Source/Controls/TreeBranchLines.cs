using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace CDir.Controls;

/// <summary>Draws branch and indentation guides through expanded folders.</summary>
public sealed class TreeBranchLines : FrameworkElement
{
    public static readonly DependencyProperty IsPinnedProperty = DependencyProperty.Register(nameof(IsPinned), typeof(bool),
        typeof(TreeBranchLines), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsPinned { get => (bool)GetValue(IsPinnedProperty); set => SetValue(IsPinnedProperty, value); }
    public static readonly DependencyProperty PinnedDepthProperty = DependencyProperty.Register(nameof(PinnedDepth), typeof(int),
        typeof(TreeBranchLines), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public int PinnedDepth { get => (int)GetValue(PinnedDepthProperty); set => SetValue(PinnedDepthProperty, value); }
    public static readonly DependencyProperty HeaderHeightProperty = DependencyProperty.Register(
        nameof(HeaderHeight), typeof(double), typeof(TreeBranchLines),
        new FrameworkPropertyMetadata(20d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double HeaderHeight { get => (double)GetValue(HeaderHeightProperty); set => SetValue(HeaderHeightProperty, value); }
    public static readonly DependencyProperty IsBookmarkRootProperty = DependencyProperty.Register(
        nameof(IsBookmarkRoot), typeof(bool), typeof(TreeBranchLines),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsBookmarkRoot { get => (bool)GetValue(IsBookmarkRootProperty); set => SetValue(IsBookmarkRootProperty, value); }
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(TreeBranchLines),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public static readonly DependencyProperty ChildCountProperty = DependencyProperty.Register(
        nameof(ChildCount), typeof(int), typeof(TreeBranchLines),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public int ChildCount { get => (int)GetValue(ChildCountProperty); set => SetValue(ChildCountProperty, value); }

    private static readonly Pen GuidePen = CreateGuidePen();
    private ItemsControl? Owner;
    internal bool HasIncomingGuide => this.Owner is TreeViewItem || this.Owner is TreeView { Tag: "SidebarFolderChildren" };
    internal double ChildGuideColumn
    {
        get
        {
            ItemsControl? Children = this.TemplatedParent is TreeViewItem Item ? Item
                : this.TemplatedParent is DependencyObject Container ? FindChildTree(Container) : null;
            if (Children?.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem Child
                && Child.Template.FindName("BranchLines", Child) is TreeBranchLines { ActualWidth: > 0 } Guides)
                return Guides.TranslatePoint(new Point(Math.Floor(Guides.ActualWidth / 2) + .5, 0), this).X;
            return double.NaN;
        }
    }
    internal IReadOnlyList<double>? PinnedColumns { get; set; }
    internal double PinnedHeaderEnd { get; set; } = double.NaN;
    internal double PinnedChildColumn { get; set; } = double.NaN;
    internal double PinnedTailColumn { get; set; } = double.NaN;

    public TreeBranchLines()
    {
        this.Loaded += (_, _) => this.AttachOwner();
        this.Unloaded += (_, _) => this.DetachOwner();
    }

    private static Pen CreateGuidePen()
    {
        var Pen = new Pen(new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)), 1)
        {
            DashStyle = new DashStyle([1d, 2d], 0)
        };
        Pen.Freeze();
        return Pen;
    }

    private void AttachOwner()
    {
        this.DetachOwner();
        // Bookmark roots use an ItemsControl data template rather than a TreeViewItem.
        if (this.TemplatedParent is DependencyObject Container)
        {
            this.Owner = ItemsControl.ItemsControlFromItemContainer(Container);
            if (this.Owner is not null)
            {
                this.Owner.ItemContainerGenerator.ItemsChanged += this.SiblingsChanged;
                this.Owner.ItemContainerGenerator.StatusChanged += this.ContainersChanged;
            }
        }
        this.InvalidateVisual();
    }

    private void DetachOwner()
    {
        if (this.Owner is not null)
        {
            this.Owner.ItemContainerGenerator.ItemsChanged -= this.SiblingsChanged;
            this.Owner.ItemContainerGenerator.StatusChanged -= this.ContainersChanged;
        }
        this.Owner = null;
    }

    private void SiblingsChanged(object Sender, ItemsChangedEventArgs E) => this.InvalidateVisual();
    private void ContainersChanged(object? Sender, EventArgs E) => this.InvalidateVisual();

    protected override void OnRenderSizeChanged(SizeChangedInfo Info)
    {
        base.OnRenderSizeChanged(Info);
        this.InvalidateVisual();
    }

    private (FrameworkElement? Header, double ChildX) LastVisibleBranch(DependencyObject Container)
    {
        var Last = Container as TreeViewItem;
        TreeViewItem? DirectChild = null;
        if (Last is null && FindChildTree(Container) is { IsVisible: true, Items.Count: > 0 } Tree)
        {
            Last = Tree.ItemContainerGenerator.ContainerFromIndex(Tree.Items.Count - 1) as TreeViewItem;
            DirectChild = Last;
        }
        while (Last is { IsExpanded: true, Items.Count: > 0 }
            && Last.ItemContainerGenerator.ContainerFromIndex(Last.Items.Count - 1) is TreeViewItem { IsVisible: true } Child)
        {
            DirectChild ??= Child;
            Last = Child;
        }
        var ChildX = this.ActualWidth + Math.Floor(this.ActualWidth / 2) + 0.5;
        if (DirectChild?.Template.FindName("BranchLines", DirectChild) is TreeBranchLines ChildGuide)
            ChildX = ChildGuide.TranslatePoint(new Point(Math.Floor(ChildGuide.ActualWidth / 2) + 0.5, 0), this).X;
        return (Last?.Template.FindName("PART_Header", Last) as FrameworkElement, ChildX);
    }

    private static TreeView? FindChildTree(DependencyObject Parent)
    {
        for (var Index = 0; Index < VisualTreeHelper.GetChildrenCount(Parent); Index++)
        {
            var Child = VisualTreeHelper.GetChild(Parent, Index);
            if (Child is TreeView Tree) return Tree;
            if (FindChildTree(Child) is { } Nested) return Nested;
        }
        return null;
    }

    protected override void OnRender(DrawingContext Context)
    {
        base.OnRender(Context);
        if (this.IsPinned)
        {
            var PinnedCenter = this.ActualHeight / 2;
            if (this.PinnedColumns is { } Columns)
            {
                foreach (var GuideX in Columns)
                    Context.DrawLine(GuidePen, new Point(GuideX, 0), new Point(GuideX, this.ActualHeight));
                if (Columns.Count > 0 && double.IsFinite(this.PinnedHeaderEnd))
                    Context.DrawLine(GuidePen, new Point(Columns[^1], PinnedCenter), new Point(this.PinnedHeaderEnd, PinnedCenter));
                if (this.IsExpanded && this.ChildCount > 0)
                    foreach (var GuideX in new[] { this.PinnedTailColumn, this.PinnedChildColumn }.Where(double.IsFinite))
                        Context.DrawLine(GuidePen, new Point(GuideX, PinnedCenter), new Point(GuideX, this.ActualHeight));
                return;
            }
            // Wait for the real tree's layout rather than briefly drawing guessed columns.
            return;
        }
        // A bookmark's visible root is outside its child TreeView; those first-level children still need guides.
        // Roots connect only their own expanded subtree. Descendants also connect sibling rows.
        var Owner = this.Owner;
        var HasParentFolder = this.HasIncomingGuide;
        if (Owner is null || this.TemplatedParent is not DependencyObject Container
            || this.ActualWidth <= 0 || this.ActualHeight <= 0) return;
        var Index = Owner.ItemContainerGenerator.IndexFromContainer(Container);
        if (Index < 0) return;
        var Center = Math.Min(this.HeaderHeight, this.ActualHeight) / 2;
        var X = Math.Floor(this.ActualWidth / 2) + 0.5;
        // Keep the ancestor's indentation visible beside an expanded last sibling.
        // End at its final visible descendant instead of cutting off at the parent header.
        var IsLast = Index == Owner.Items.Count - 1;
        var EndsHere = IsLast || !HasParentFolder;
        var Bottom = EndsHere ? Center : this.ActualHeight;
        var EndX = this.ActualWidth;
        if (EndsHere && this.IsExpanded && this.ChildCount > 0)
        {
            var Branch = this.LastVisibleBranch(Container);
            if (Branch.Header is { ActualHeight: > 0 } Header)
                Bottom = Math.Clamp(Header.TranslatePoint(new Point(0, Header.ActualHeight / 2), this).Y, Center, this.ActualHeight);
            EndX = Math.Max(X, Branch.ChildX);
        }
        if (HasParentFolder || Bottom > Center)
        {
            Context.DrawLine(GuidePen, new Point(X, HasParentFolder ? 0 : Center), new Point(X, Bottom));
            if (HasParentFolder)
                Context.DrawLine(GuidePen, new Point(X, Center), new Point(this.ActualWidth, Center));
            // Close retained ancestor guides into the next indentation column, so
            // the final descendant connects to the guides instead of leaving dangling ends.
            if (EndsHere && Bottom > Center)
                Context.DrawLine(GuidePen, new Point(X, Bottom), new Point(EndX, Bottom));
        }
        if (this.IsExpanded && this.ChildCount > 0)
        {
            // Independent bookmark child trees can add their own padding. Use the
            // child's rendered column for both the real tree and its pinned copy.
            var ChildX = this.ChildGuideColumn;
            if (double.IsFinite(ChildX))
                Context.DrawLine(GuidePen, new Point(ChildX, Center), new Point(ChildX, Math.Min(this.HeaderHeight, this.ActualHeight)));
        }
    }
}
