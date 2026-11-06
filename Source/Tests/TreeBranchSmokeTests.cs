using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunTreeBranchSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Tree branch UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        TreeViewItem Node(string Name) => new()
        {
            Header = new TextBlock { Text = Name, FontSize = 12, MinHeight = 16 },
            IsExpanded = true
        };
        var Root = Node("Drive");
        var First = Node("First folder");
        var Last = Node("Last folder");
        First.Items.Add(Node("Child A"));
        First.Items.Add(Node("Child B"));
        Last.Items.Add(Node("Only child"));
        Root.Items.Add(First);
        Root.Items.Add(Last);
        var Tree = new TreeView { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        ScrollViewer.SetCanContentScroll(Tree, false);
        ScrollViewer.SetVerticalScrollBarVisibility(Tree, ScrollBarVisibility.Auto);
        Tree.Items.Add(Root);
        Tree.Items.Add(Node("Next drive"));
        var Host = new Border
        {
            Child = Tree, Width = 280, Height = 240, Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF9, 0xFC)),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRowSpan(Host, 4);
        var Panel = (Grid)this.Content;
        Panel.Children.Add(Host);
        try
        {
            async Task Settle()
            {
                UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                UpdateLayout();
            }
            TreeBranchLines Guides(TreeViewItem Item) =>
                (TreeBranchLines)Item.Template.FindName("BranchLines", Item);
            bool HasChildBridge(TreeBranchLines Lines) => VisualTreeHelper.GetDrawing(Lines) is { } Drawing
                && Drawing.Bounds.Right > Lines.ActualWidth * 1.25;
            int LastInkRow(TreeViewItem Item, double Scale = 1)
                => LastGuideInkRow(Guides(Item), Scale);
            int LastGuideInkRow(TreeBranchLines Lines, double Scale = 1)
            {
                var Bitmap = new RenderTargetBitmap((int)Math.Ceiling(Lines.ActualWidth * Scale), (int)Math.Ceiling(Lines.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
                Bitmap.Render(Lines);
                var Pixels = new byte[Bitmap.PixelWidth * Bitmap.PixelHeight * 4];
                Bitmap.CopyPixels(Pixels, Bitmap.PixelWidth * 4, 0);
                for (var Y = Bitmap.PixelHeight - 1; Y >= 0; Y--)
                    for (var X = 0; X < Bitmap.PixelWidth; X++)
                        if (Pixels[(Y * Bitmap.PixelWidth + X) * 4 + 3] > 0) return Y;
                return -1;
            }
            bool EndsAtVisibleFolder(TreeBranchLines Lines, TreeViewItem Final, double Scale = 1)
            {
                var Header = (FrameworkElement)Final.Template.FindName("PART_Header", Final);
                var Expected = Final.TranslatePoint(new Point(0, Header.ActualHeight / 2), Lines).Y * Scale;
                var LastPixel = LastGuideInkRow(Lines, Scale);
                return LastPixel >= Expected - 3 && LastPixel <= Expected + 2;
            }
            bool HasClosingLine(TreeBranchLines Lines, TreeViewItem Final, double Scale = 1)
            {
                var Header = (FrameworkElement)Final.Template.FindName("PART_Header", Final);
                var End = Header.TranslatePoint(new Point(0, Header.ActualHeight / 2), Lines);
                var Bitmap = new RenderTargetBitmap((int)Math.Ceiling(Lines.ActualWidth * 2 * Scale),
                    (int)Math.Ceiling(Lines.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
                Bitmap.Render(Lines);
                var Pixels = new byte[Bitmap.PixelWidth * Bitmap.PixelHeight * 4];
                Bitmap.CopyPixels(Pixels, Bitmap.PixelWidth * 4, 0);
                var X = (int)Math.Round(Lines.ActualWidth * Scale);
                var Y = (int)Math.Round(End.Y * Scale);
                for (var Row = Math.Max(0, Y - 2); Row <= Math.Min(Bitmap.PixelHeight - 1, Y + 2); Row++)
                    for (var Column = Math.Max(0, X - 2); Column <= Math.Min(Bitmap.PixelWidth - 1, X + 2); Column++)
                        if (Pixels[(Row * Bitmap.PixelWidth + Column) * 4 + 3] > 0) return true;
                return false;
            }
            await Settle();
            Check(HasChildBridge(Guides(Root)) && HasChildBridge(Guides(First)) && HasChildBridge(Guides(Last))
                && !HasChildBridge(Guides((TreeViewItem)Last.Items[0])),
                "Expanded parents bridge their folder icons to the child branch column while leaf folders add no orphan connector");
            Check(EndsAtVisibleFolder(Guides(Root), (TreeViewItem)Last.Items[0])
                && HasClosingLine(Guides(Root), (TreeViewItem)Last.Items[0])
                && LastInkRow(First) >= Guides(First).ActualHeight - 3,
                "An expanded drive closes its outer guide at its final visible descendant before the next drive");
            Root.IsExpanded = false;
            await Settle();
            Check(LastInkRow(Root) == -1, "A collapsed drive has no dangling outer guide");
            Root.IsExpanded = true;
            await Settle();
            Check(EndsAtVisibleFolder(Guides(Last), (TreeViewItem)Last.Items[0]),
                "The final expanded sibling keeps its indentation guide through its last visible child");
            var Tail = (TreeViewItem)Last.Items[0];
            Tail.Margin = new Thickness(0, 3, 0, 9);
            ((TextBlock)Tail.Header).FontSize = 24;
            await Settle();
            Screenshot("TreeGuidesClosingMargins");
            Check(EndsAtVisibleFolder(Guides(Last), Tail) && HasClosingLine(Guides(Last), Tail),
                "A final guide closes horizontally at the actual last header, excluding trailing margins and accommodating large text"
                + $" (end={EndsAtVisibleFolder(Guides(Last), Tail)}, cap={HasClosingLine(Guides(Last), Tail)}, ink={LastInkRow(Last)}, expected={Tail.TranslatePoint(new Point(0, Guides(Tail).HeaderHeight / 2), Guides(Last)).Y}, bounds={VisualTreeHelper.GetDrawing(Guides(Last))!.Bounds})");
            Tail.Margin = new Thickness(0);
            ((TextBlock)Tail.Header).FontSize = 12;
            await Settle();
            Last.IsExpanded = false;
            await Settle();
            Check(!HasChildBridge(Guides(Last)), "Collapsing a folder removes its outgoing child connector");
            Check(LastInkRow(Last) is >= 0 && LastInkRow(Last) <= Guides(Last).HeaderHeight / 2 + 1,
                "a collapsed last sibling still ends its guide at the header");
            var Extra = Node("New sibling");
            Root.Items.Add(Extra);
            await Settle();
            Check(LastInkRow(Last) >= Guides(Last).ActualHeight - 3, "adding a sibling extends the previous last branch");
            Root.Items.Remove(Extra);
            await Settle();
            Check(LastInkRow(Last) <= Guides(Last).HeaderHeight / 2 + 1, "removing a sibling trims the new last branch");
            Last.IsExpanded = true;
            await Settle();
            Check(EndsAtVisibleFolder(Guides(Last), (TreeViewItem)Last.Items[0]),
                "Reopening the final sibling restores its full subtree indentation guide");
            var Toggle = (ToggleButton)First.Template.FindName("Expander", First);
            var Peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(Toggle);
            ((System.Windows.Automation.Provider.IToggleProvider)Peer).Toggle();
            await Settle();
            Check(!First.IsExpanded && First.ActualHeight < 30, "tree expander collapses its children with guides enabled");
            ((System.Windows.Automation.Provider.IToggleProvider)Peer).Toggle();
            await Settle();
            Check(First.IsExpanded && First.ActualHeight >= 60
                && ((ToggleButton)((TreeViewItem)Last.Items[0]).Template.FindName("Expander", (TreeViewItem)Last.Items[0])).Visibility == Visibility.Hidden,
                "tree expander restores descendants and leaves have no expand button");
            void Screenshot(string Name)
            {
                if (AppMain.ScreenshotPath is not { } Screenshot) return;
                var Bitmap = new RenderTargetBitmap(280, 240, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(Host);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = System.IO.File.Create(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Screenshot)!, Name + ".png"));
                Encoder.Save(Stream);
            }
            Screenshot("TreeGuides");
            var Ancestors = new List<TreeViewItem> { Root, Last };
            var Current = (TreeViewItem)Last.Items[0];
            Ancestors.Add(Current);
            for (var Depth = 0; Depth < 6; Depth++)
            {
                var Next = Node("Nested level " + Depth);
                Current.Items.Add(Next);
                Current = Next;
                Ancestors.Add(Current);
            }
            for (var Index = 0; Index < 180; Index++) Current.Items.Add(Node("Folder " + Index));
            await Settle();
            Check(Ancestors.All(Item => EndsAtVisibleFolder(Guides(Item), (TreeViewItem)Current.Items[Current.Items.Count - 1])),
                "Every ancestor indentation guide reaches the final visible folder in a deeply expanded chain");
            Check(Ancestors.All(Item => HasClosingLine(Guides(Item), (TreeViewItem)Current.Items[Current.Items.Count - 1])),
                "Every retained ancestor guide closes into the next indentation column at the final descendant");
            Check(Guides(Last).ActualHeight > Host.ActualHeight * 10,
                "the deep guide fixture extends well beyond the visible viewport");
            var Scroll = FindVisualChild<ScrollViewer>(Tree)!;
            Scroll.ScrollToVerticalOffset(800);
            await Settle();
            Check(Scroll.VerticalOffset > 0 && Ancestors.All(Item => EndsAtVisibleFolder(Guides(Item), (TreeViewItem)Current.Items[Current.Items.Count - 1])),
                "Scrolling a tall subtree preserves every ancestor indentation guide and its correct endpoint");
            Screenshot("TreeGuidesDeepScrolled");
            Scroll.ScrollToBottom();
            await Settle();
            Screenshot("TreeGuidesDeepEnd");
            ((TextBlock)Ancestors[2].Header).FontSize = 24;
            await Settle();
            Check(Ancestors.All(Item => EndsAtVisibleFolder(Guides(Item), (TreeViewItem)Current.Items[Current.Items.Count - 1], 1.5)),
                "Mixed header heights and 150-percent rendering keep ancestor guides aligned with the final descendant");
            Check(Ancestors.All(Item => HasClosingLine(Guides(Item), (TreeViewItem)Current.Items[Current.Items.Count - 1], 1.5)),
                "Closing connectors remain visible and aligned after scrolling and at 150-percent scale");
            var OldHeight = Guides(Last).ActualHeight;
            Current.Items.RemoveAt(Current.Items.Count - 1);
            await Settle();
            Check(Guides(Last).ActualHeight < OldHeight && Ancestors.All(Item => EndsAtVisibleFolder(Guides(Item), (TreeViewItem)Current.Items[Current.Items.Count - 1])),
                "Removing the final descendant shortens every ancestor indentation guide to the new endpoint");

            var BookmarkTree = new TreeView { Tag = "SidebarFolderChildren", BorderThickness = new Thickness(0) };
            var BookmarkChild = Node("Bookmark child");
            BookmarkChild.Items.Add(Node("Nested bookmark folder"));
            var BookmarkLast = Node("Bookmark leaf");
            BookmarkTree.Items.Add(BookmarkChild);
            BookmarkTree.Items.Add(BookmarkLast);
            Host.Child = BookmarkTree;
            await Settle();
            Check(LastInkRow(BookmarkChild) >= Guides(BookmarkChild).ActualHeight - 3
                && LastInkRow(BookmarkLast) is >= 0 && LastInkRow(BookmarkLast) <= Guides(BookmarkLast).HeaderHeight / 2 + 1,
                "first-level bookmark children draw incoming guides while the final leaf ends at its header");
            Screenshot("TreeGuidesBookmark");
            BookmarkTree.Items.Remove(BookmarkChild);
            Tree.Items.Add(BookmarkChild);
            Host.Child = Tree;
            await Settle();
            Check(EndsAtVisibleFolder(Guides(BookmarkChild), (TreeViewItem)BookmarkChild.Items[0])
                && HasClosingLine(Guides(BookmarkChild), (TreeViewItem)BookmarkChild.Items[0]),
                "A reused container switches from a sibling guide to a closed subtree guide when moved to a drive-root tree");
            Tree.Items.Remove(BookmarkChild);
            BookmarkTree.Items.Add(BookmarkChild);
            Host.Child = BookmarkTree;
            await Settle();
            Check(EndsAtVisibleFolder(Guides(BookmarkChild), (TreeViewItem)BookmarkChild.Items[0]),
                "A reused expanded bookmark container restores its guide through the last visible descendant");

            var BookmarkRoots = new ItemsControl { ItemTemplate = this.FavoritesList.ItemTemplate };
            FavoriteLocation Bookmark(string Name)
            {
                var Folder = new FavoriteLocation(Name, AppMain.SmokeRoot!);
                Folder.Node.IsLoaded = true;
                Folder.Node.Children.Clear();
                var Child = new FolderNode("Child", AppMain.SmokeRoot!) { IsLoaded = true };
                Child.Children.Clear();
                Folder.Node.Children.Add(Child);
                Folder.Node.IsExpanded = true;
                return Folder;
            }
            var FirstRoot = Bookmark("First root");
            var LastRoot = Bookmark("Last root");
            BookmarkRoots.Items.Add(FirstRoot);
            BookmarkRoots.Items.Add(LastRoot);
            Host.Child = BookmarkRoots;
            await Settle();
            TreeBranchLines RootGuides(FavoriteLocation Folder) => FindVisualChild<TreeBranchLines>(BookmarkRoots.ItemContainerGenerator.ContainerFromItem(Folder))!;
            Check(HasChildBridge(RootGuides(FirstRoot)) && HasChildBridge(RootGuides(LastRoot)),
                "Bookmark roots bridge to their independent child trees, including a final expanded root");
            TreeViewItem BookmarkLeaf(FavoriteLocation Folder) => (TreeViewItem)FindVisualChild<TreeView>(BookmarkRoots.ItemContainerGenerator.ContainerFromItem(Folder))!.ItemContainerGenerator.ContainerFromIndex(0);
            bool RootBridgeAligned(FavoriteLocation Folder, double Scale)
            {
                var Guide = RootGuides(Folder);
                var Child = Guides(BookmarkLeaf(Folder));
                var ChildX = Child.TranslatePoint(new Point(Math.Floor(Child.ActualWidth / 2) + .5, 0), Guide).X;
                var Bitmap = new RenderTargetBitmap((int)Math.Ceiling((ChildX + 6) * Scale),
                    (int)Math.Ceiling(Guide.HeaderHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
                Bitmap.Render(Guide);
                var Pixels = new byte[Bitmap.PixelWidth * Bitmap.PixelHeight * 4];
                Bitmap.CopyPixels(Pixels, Bitmap.PixelWidth * 4, 0);
                var FoundChild = false;
                var FoundOuter = false;
                var OuterX = Math.Floor(Guide.ActualWidth / 2) + .5;
                for (var Y = 0; Y < Bitmap.PixelHeight; Y++)
                    for (var X = 0; X < Bitmap.PixelWidth; X++)
                        if (Pixels[(Y * Bitmap.PixelWidth + X) * 4 + 3] > 0)
                        {
                            if (Y + .5 < Guide.HeaderHeight / 2 * Scale - 1) return false;
                            var AtChild = Math.Abs(X + .5 - ChildX * Scale) <= 1.3;
                            var AtOuter = Math.Abs(X + .5 - OuterX * Scale) <= 1.3;
                            if (!AtChild && !AtOuter) return false;
                            FoundChild |= AtChild;
                            FoundOuter |= AtOuter;
                        }
                return FoundChild && FoundOuter;
            }
            foreach (var Scale in new[] { 1d, 1.25, 1.5 })
                Check(RootBridgeAligned(FirstRoot, Scale) && RootBridgeAligned(LastRoot, Scale),
                    "Bookmark roots retain outer and aligned child guides below the folder center, with no title-to-folder line at scale " + Scale);
            Check(EndsAtVisibleFolder(RootGuides(FirstRoot), BookmarkLeaf(FirstRoot))
                && HasClosingLine(RootGuides(FirstRoot), BookmarkLeaf(FirstRoot))
                && EndsAtVisibleFolder(RootGuides(LastRoot), BookmarkLeaf(LastRoot))
                && HasClosingLine(RootGuides(LastRoot), BookmarkLeaf(LastRoot)),
                "Every expanded bookmark root retains its full outer guide and closing connection at the last visible descendant");
            Screenshot("TreeGuidesBookmarkRoots");
            LastRoot.Node.IsExpanded = false;
            await Settle();
            Check(LastGuideInkRow(RootGuides(LastRoot)) == -1,
                "Collapsing a Quick Access root clears the subtree guide without leaving a title-to-folder stub");
            BookmarkRoots.Items.Remove(LastRoot);
            await Settle();
            Check(RootBridgeAligned(FirstRoot, 1.5)
                && EndsAtVisibleFolder(RootGuides(FirstRoot), BookmarkLeaf(FirstRoot), 1.5)
                && HasClosingLine(RootGuides(FirstRoot), BookmarkLeaf(FirstRoot), 1.5),
                "Removing a bookmark preserves the remaining root's complete subtree guide and clear title gap at 150-percent scale");
        }
        finally { Panel.Children.Remove(Host); }
    }
}
