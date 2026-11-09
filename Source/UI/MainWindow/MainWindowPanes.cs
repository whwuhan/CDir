using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CDir.Models;

namespace CDir.UI;

public partial class MainWindow
{
    internal enum PaneDock { Left, Right, Top, Bottom }
    private sealed class PaneNode
    {
        internal WorkspacePaneView? Pane;
        internal PaneNode? First, Second;
        internal bool Horizontal;
        internal double Ratio = .5;
        internal Grid? Surface;
        internal FrameworkElement View => (FrameworkElement?)this.Pane ?? this.Surface!;
    }

    private PaneNode LayoutRoot = null!;
    internal WorkspacePaneView ActivePane = null!;
    internal readonly List<WorkspacePaneView> Panes = [];
    public ObservableCollection<TabHeader> VisibleHeaders => this.ActivePane.VisibleHeaders;
    public ObservableCollection<TabHeader> PinnedHeaders => this.ActivePane.PinnedHeaders;
    internal Border TabBar => this.ActivePane.TabBar;
    internal Grid TabPages => this.ActivePane.TabPages;
    internal Border PinnedTabRow => this.ActivePane.PinnedTabRow;
    internal Border NormalTabRow => this.ActivePane.NormalTabRow;
    internal ScrollViewer PinnedTabScroll => this.ActivePane.PinnedTabScroll;
    internal ScrollViewer TabScroll => this.ActivePane.TabScroll;
    internal ItemsControl PinnedTabHeaders => this.ActivePane.PinnedTabHeaders;
    internal ItemsControl TabHeaders => this.ActivePane.TabHeaders;
    internal Button NewTabButton => this.ActivePane.NewTabButton;
    internal Border PinnedTabDropMarker => this.ActivePane.PinnedTabDropMarker;
    internal Border NormalTabDropMarker => this.ActivePane.NormalTabDropMarker;
    internal ExplorerTab? PresentedTab { get => this.ActivePane.Presented; private set => this.ActivePane.Presented = value; }
    private ExplorerTab[] PaneTabs(WorkspacePaneView Pane) => this.Tabs.Where(Page => ReferenceEquals(Page.Parent, Pane.TabPages)).ToArray();
    private WorkspacePaneView PaneFor(ExplorerTab Page) => this.Panes.Single(Pane => ReferenceEquals(Page.Parent, Pane.TabPages));

    private WorkspacePaneView? PaneFromSource(DependencyObject? Source)
    {
        for (var Current = Source; Current is not null; Current = Current is Visual ? VisualTreeHelper.GetParent(Current) : LogicalTreeHelper.GetParent(Current))
            if (Current is WorkspacePaneView Pane) return Pane;
        return null;
    }

    private WorkspacePaneView CreatePane()
    {
        var Pane = new WorkspacePaneView { Host = this };
        this.Panes.Add(Pane);
        Pane.PreviewMouseDown += (_, _) => this.ActivatePane(Pane);
        Pane.GotKeyboardFocus += (_, _) => this.ActivatePane(Pane);
        Pane.AllowDrop = true;
        Pane.PreviewDragLeave += (_, E) =>
        {
            if (!new Rect(Pane.RenderSize).Contains(E.GetPosition(Pane))) this.ClearSplitFeedback();
        };
        return Pane;
    }

    internal void ActivatePane(WorkspacePaneView Pane)
    {
        if (!this.Panes.Contains(Pane) || ReferenceEquals(this.ActivePane, Pane)) return;
        this.ActiveTab?.DismissSearchTips();
        this.ActivePane = Pane;
        this.ActiveTab = Pane.Selected;
        this.ActiveTab?.RefreshActiveActions();
    }

    private PaneNode? FindNode(PaneNode Node, WorkspacePaneView Pane)
        => ReferenceEquals(Node.Pane, Pane) ? Node : Node.Pane is not null ? null
            : this.FindNode(Node.First!, Pane) ?? this.FindNode(Node.Second!, Pane);

    private void BuildBranch(PaneNode Node)
    {
        var Grid = Node.Surface = new Grid { ClipToBounds = true };
        var Splitter = new GridSplitter
        {
            Background = (Brush)this.FindResource("Line"), ShowsPreview = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = Node.Horizontal ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext, Focusable = false
        };
        if (Node.Horizontal)
        {
            Grid.ColumnDefinitions.Add(new() { Width = new GridLength(Node.Ratio, GridUnitType.Star), MinWidth = 440 });
            Grid.ColumnDefinitions.Add(new() { Width = new GridLength(5) });
            Grid.ColumnDefinitions.Add(new() { Width = new GridLength(1 - Node.Ratio, GridUnitType.Star), MinWidth = 440 });
            Grid.SetColumn(Splitter, 1); Grid.SetColumn(Node.Second!.View, 2);
        }
        else
        {
            Grid.RowDefinitions.Add(new() { Height = new GridLength(Node.Ratio, GridUnitType.Star), MinHeight = 180 });
            Grid.RowDefinitions.Add(new() { Height = new GridLength(5) });
            Grid.RowDefinitions.Add(new() { Height = new GridLength(1 - Node.Ratio, GridUnitType.Star), MinHeight = 180 });
            Grid.SetRow(Splitter, 1); Grid.SetRow(Node.Second!.View, 2);
        }
        Grid.Children.Add(Node.First!.View); Grid.Children.Add(Splitter); Grid.Children.Add(Node.Second!.View);
        Splitter.DragCompleted += (_, _) => this.PersistWorkspaceNow();
    }

    private void UpdatePaneMinimums()
    {
        Size Measure(PaneNode Node)
        {
            if (Node.Pane is not null) return new Size(440, 180);
            var First = Measure(Node.First!); var Second = Measure(Node.Second!);
            if (Node.Horizontal)
            {
                Node.Surface!.ColumnDefinitions[0].MinWidth = First.Width;
                Node.Surface.ColumnDefinitions[2].MinWidth = Second.Width;
                return new Size(First.Width + 5 + Second.Width, Math.Max(First.Height, Second.Height));
            }
            Node.Surface!.RowDefinitions[0].MinHeight = First.Height;
            Node.Surface.RowDefinitions[2].MinHeight = Second.Height;
            return new Size(Math.Max(First.Width, Second.Width), First.Height + 5 + Second.Height);
        }
        var Minimum = Measure(this.LayoutRoot);
        var ChromeHeight = Math.Max(56, this.ActualHeight - this.WorkspaceSurface.ActualHeight);
        this.MinWidth = Math.Min(SystemParameters.WorkArea.Width, Math.Max(960, Minimum.Width + 2));
        this.MinHeight = Math.Min(SystemParameters.WorkArea.Height, Math.Max(600, Minimum.Height + ChromeHeight));
    }

    private bool ValidTabPayload(IDataObject Data, out ExplorerTab Page)
    {
        Page = null!;
        if (!Data.GetDataPresent(TabDragFormat, false) || Data.GetData(TabDragFormat, false) is not TabDragPayload Payload
            || !ReferenceEquals(Payload, this.ActiveTabDrag) || !ReferenceEquals(Payload.Owner, this) || !this.Tabs.Contains(Payload.Page)) return false;
        Page = Payload.Page;
        return true;
    }

    internal PaneDock? SplitDockAt(WorkspacePaneView Pane, Point Position)
    {
        if (Pane.Selected is not { } Page || !Page.IsInitialLayoutReady) return null;
        var Body = Page.SplitDropBounds(Pane);
        Body.Intersect(new Rect(Pane.RenderSize));
        if (Body.IsEmpty || Body.Width <= 0 || Body.Height <= 0) return null;
        // Include the painted window edge and scrollbar gutter. A one-pixel
        // frame must not turn a valid right-edge snap into an invalid drop.
        Body = new Rect(0, Body.Top, Pane.ActualWidth, Body.Height);
        if (Position.Y < Body.Top || Position.Y > Body.Bottom || Position.X < -8 || Position.X > Body.Right + 8) return null;
        var X = Math.Clamp((Position.X - Body.Left) / Body.Width, 0, 1);
        var Y = (Position.Y - Body.Top) / Body.Height;
        // Leave the middle clear so accidental releases do not split the workspace.
        var Nearest = Math.Min(Math.Min(X, 1 - X), Math.Min(Y, 1 - Y));
        if (Nearest > .28) return null;
        var Dock = Position.X <= 12 ? PaneDock.Left : Position.X >= Body.Right - 12 ? PaneDock.Right
            : Nearest == X ? PaneDock.Left : Nearest == 1 - X ? PaneDock.Right
            : Nearest == Y ? PaneDock.Top : PaneDock.Bottom;
        if ((Dock is PaneDock.Left or PaneDock.Right ? Pane.ActualWidth < 885 : Pane.ActualHeight < 365) || this.Panes.Count >= 16) return null;
        return Dock;
    }

    internal DragDropEffects UpdateSplitFeedback(IDataObject Data, WorkspacePaneView Pane, Point Position)
    {
        this.ClearTabDropFeedback();
        this.ClearSplitFeedback();
        if (!this.Panes.Contains(Pane) || !this.ValidTabPayload(Data, out var Page)
            || ReferenceEquals(this.PaneFor(Page), Pane) && this.PaneTabs(Pane).Length < 2
            || this.SplitDockAt(Pane, Position) is not { } Dock) return DragDropEffects.None;
        var Preview = Pane.SplitPreview;
        Preview.HorizontalAlignment = Dock == PaneDock.Left ? HorizontalAlignment.Left : Dock == PaneDock.Right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        Preview.VerticalAlignment = Dock == PaneDock.Top ? VerticalAlignment.Top : Dock == PaneDock.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        Preview.Width = Dock is PaneDock.Left or PaneDock.Right ? (Pane.ActualWidth - 5) / 2 - 6 : double.NaN;
        Preview.Height = Dock is PaneDock.Top or PaneDock.Bottom ? (Pane.ActualHeight - 5) / 2 - 6 : double.NaN;
        Preview.Visibility = Visibility.Visible;
        return DragDropEffects.Move;
    }

    private void ClearSplitFeedback()
    { foreach (var Pane in this.Panes) Pane.SplitPreview.Visibility = Visibility.Collapsed; }

    private WorkspacePaneView? SplitTargetAt(Point WindowPoint)
    {
        if (!new Rect(this.RenderSize).Contains(WindowPoint)) return null;
        return this.Panes.Where(Pane => Pane.IsVisible)
            .Select(Pane => (Pane, Position: this.TranslatePoint(WindowPoint, Pane)))
            .Where(Item => Item.Position.Y >= 0 && Item.Position.Y <= Item.Pane.ActualHeight
                && Item.Position.X >= -8 && Item.Position.X <= Item.Pane.ActualWidth + 8)
            .OrderBy(Item => Math.Max(0, -Item.Position.X) + Math.Max(0, Item.Position.X - Item.Pane.ActualWidth))
            .Select(Item => Item.Pane).FirstOrDefault();
    }

    private void InitializePaneDropRouting()
    {
        this.AllowDrop = true;
        this.PreviewDragEnter += this.WindowPaneDragOver;
        this.PreviewDragOver += this.WindowPaneDragOver;
        this.PreviewDrop += this.WindowPaneDrop;
        this.PreviewDragLeave += (_, E) =>
        {
            if (this.ActiveTabDrag is not null && !new Rect(this.RenderSize).Contains(E.GetPosition(this))) this.ClearSplitFeedback();
        };
    }

    private void WindowPaneDragOver(object Sender, DragEventArgs E)
    {
        // External file managers need only standard file formats; do not query
        // private tab formats unless this window owns a live tab-drag session.
        if (this.ActiveTabDrag is null || !E.Data.GetDataPresent(TabDragFormat, false)) return;
        var Pane = this.SplitTargetAt(E.GetPosition(this));
        if (Pane is null || E.GetPosition(Pane).Y <= Pane.TabBar.ActualHeight) { this.ClearSplitFeedback(); return; }
        E.Effects = (E.AllowedEffects & DragDropEffects.Move) != 0
            ? this.UpdateSplitFeedback(E.Data, Pane, E.GetPosition(Pane)) : DragDropEffects.None;
        E.Handled = true;
    }

    private void WindowPaneDrop(object Sender, DragEventArgs E)
    {
        if (this.ActiveTabDrag is null || !E.Data.GetDataPresent(TabDragFormat, false)) return;
        var Pane = this.SplitTargetAt(E.GetPosition(this));
        if (Pane is null || E.GetPosition(Pane).Y <= Pane.TabBar.ActualHeight) return;
        E.Effects = (E.AllowedEffects & DragDropEffects.Move) != 0 && this.CompleteSplitDrop(E.Data, Pane, E.GetPosition(Pane))
            ? DragDropEffects.Move : DragDropEffects.None;
        E.Handled = true;
    }

    internal bool CompleteSplitDrop(IDataObject Data, WorkspacePaneView Pane, Point Position)
    {
        try
        {
            return this.UpdateSplitFeedback(Data, Pane, Position) == DragDropEffects.Move
                && this.ValidTabPayload(Data, out var Page) && this.SplitDockAt(Pane, Position) is { } Dock && this.SplitPane(Page, Pane, Dock);
        }
        finally { this.ClearSplitFeedback(); }
    }

    private bool SplitPane(ExplorerTab Page, WorkspacePaneView Target, PaneDock Dock)
    {
        var Node = this.FindNode(this.LayoutRoot, Target)!;
        var Source = this.PaneFor(Page);
        var Parent = (Grid)Target.Parent;
        var Row = Grid.GetRow(Target); var Column = Grid.GetColumn(Target);
        Parent.Children.Remove(Target);
        Grid.SetRow(Target, 0); Grid.SetColumn(Target, 0);
        var NewPane = this.CreatePane();
        var Old = new PaneNode { Pane = Target };
        var New = new PaneNode { Pane = NewPane };
        Node.Pane = null; Node.Horizontal = Dock is PaneDock.Left or PaneDock.Right;
        Node.First = Dock is PaneDock.Left or PaneDock.Top ? New : Old;
        Node.Second = ReferenceEquals(Node.First, New) ? Old : New;
        this.BuildBranch(Node);
        Grid.SetRow(Node.View, Row); Grid.SetColumn(Node.View, Column); Parent.Children.Add(Node.View);
        this.TransferTab(Page, Source, NewPane);
        this.UpdatePaneMinimums();
        this.SelectTab(Page);
        this.PersistWorkspaceNow();
        return true;
    }

    private void TransferTab(ExplorerTab Page, WorkspacePaneView Source, WorkspacePaneView Target)
    {
        var Others = this.PaneTabs(Source).Where(Other => !ReferenceEquals(Other, Page)).ToArray();
        Source.TabPages.Children.Remove(Page);
        Target.TabPages.Children.Add(Page);
        if (ReferenceEquals(Source.Presented, Page)) Source.Presented = null;
        if (ReferenceEquals(Source.Selected, Page))
        {
            Source.Selected = Others.FirstOrDefault();
            if (Source.Selected is { } Remaining) this.PresentTab(Remaining);
        }
        if (Target.Selected is { } Previous) Previous.SetTabSelected(false);
        Target.Selected = Page;
        Page.SetTabSelected(true);
        this.PresentTab(Page);
        if (Others.Length == 0) this.RemoveEmptyPane(Source);
        this.RefreshTabHeaders();
    }

    private void RemoveEmptyPane(WorkspacePaneView Empty)
    {
        if (this.Panes.Count <= 1) return;
        PaneNode? ParentOf(PaneNode Node)
            => Node.Pane is not null ? null : ReferenceEquals(Node.First!.Pane, Empty) || ReferenceEquals(Node.Second!.Pane, Empty)
                ? Node : ParentOf(Node.First) ?? ParentOf(Node.Second);
        var Branch = ParentOf(this.LayoutRoot)!;
        var Keep = ReferenceEquals(Branch.First!.Pane, Empty) ? Branch.Second! : Branch.First;
        var Surface = Branch.Surface!;
        var Parent = (Grid)Surface.Parent;
        var Row = Grid.GetRow(Surface); var Column = Grid.GetColumn(Surface);
        Surface.Children.Clear(); Parent.Children.Remove(Surface);
        Branch.Pane = Keep.Pane; Branch.First = Keep.First; Branch.Second = Keep.Second;
        Branch.Surface = Keep.Surface; Branch.Horizontal = Keep.Horizontal; Branch.Ratio = Keep.Ratio;
        Grid.SetRow(Branch.View, Row); Grid.SetColumn(Branch.View, Column); Parent.Children.Add(Branch.View);
        this.Panes.Remove(Empty);
        this.UpdatePaneMinimums();
        if (ReferenceEquals(this.ActivePane, Empty)) this.ActivatePane(this.Panes[0]);
    }

    private WorkspacePaneState CapturePane(PaneNode Node)
    {
        if (Node.Pane is { } Pane) return new()
        {
            Tabs = this.PaneTabs(Pane).Select(Page => this.Tabs.IndexOf(Page)).ToList(),
            ActiveTab = this.Tabs.IndexOf(Pane.Selected!)
        };
        var First = Node.Horizontal ? Node.Surface!.ColumnDefinitions[0].Width.Value : Node.Surface!.RowDefinitions[0].Height.Value;
        var Last = Node.Horizontal ? Node.Surface!.ColumnDefinitions[2].Width.Value : Node.Surface!.RowDefinitions[2].Height.Value;
        return new() { Split = Node.Horizontal ? "Horizontal" : "Vertical", Ratio = First / (First + Last),
            First = this.CapturePane(Node.First!), Second = this.CapturePane(Node.Second!) };
    }

    private void RestorePanes(WorkspacePaneState? Saved)
    {
        if (Saved is null) return;
        var Seen = new HashSet<int>();
        bool Valid(WorkspacePaneState State, int Depth)
        {
            if (Depth > 16) return false;
            if (State.Split is null) return State.Tabs is { Count: > 0 } && State.Tabs.All(Index => Index >= 0 && Index < this.Tabs.Count && Seen.Add(Index)) && State.Tabs.Contains(State.ActiveTab);
            return State.Split is "Horizontal" or "Vertical" && State.First is not null && State.Second is not null
                && double.IsFinite(State.Ratio) && State.Ratio >= .05 && State.Ratio <= .95 && Valid(State.First, Depth + 1) && Valid(State.Second, Depth + 1);
        }
        if (!Valid(Saved, 0) || Seen.Count != this.Tabs.Count) return;
        foreach (var Pane in this.Panes) Pane.TabPages.Children.Clear();
        this.WorkspaceSurface.Children.Clear(); this.Panes.Clear();
        PaneNode Restore(WorkspacePaneState State)
        {
            if (State.Split is null)
            {
                var Pane = this.CreatePane();
                foreach (var Index in State.Tabs) Pane.TabPages.Children.Add(this.Tabs[Index]);
                Pane.Selected = this.Tabs[State.ActiveTab];
                foreach (var Page in this.PaneTabs(Pane)) Page.SetTabSelected(ReferenceEquals(Page, Pane.Selected));
                this.PresentTab(Pane.Selected);
                return new() { Pane = Pane };
            }
            var Node = new PaneNode { Horizontal = State.Split == "Horizontal", Ratio = State.Ratio, First = Restore(State.First!), Second = Restore(State.Second!) };
            this.BuildBranch(Node); return Node;
        }
        this.LayoutRoot = Restore(Saved);
        this.WorkspaceSurface.Children.Add(this.LayoutRoot.View);
        this.UpdatePaneMinimums();
        this.ActivePane = this.Panes[0]; this.ActiveTab = this.ActivePane.Selected;
        this.RefreshTabHeaders();
    }
}
