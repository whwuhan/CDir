using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunStickyAncestorSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        var InitialPaths = this.Settings.ShowBookmarkPaths;
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Pinned ancestors 中文 " + Guid.NewGuid().ToString("N"))).FullName;
        var RootPath = Directory.CreateDirectory(Path.Combine(Fixture, "Root")).FullName;
        var Current = Directory.CreateDirectory(Path.Combine(RootPath, "Ancestor A", "Parent B", "Current")).FullName;
        var Other = Directory.CreateDirectory(Path.Combine(RootPath, "Other branch", "Other parent")).FullName;
        for (var Index = 0; Index < 80; Index++) Directory.CreateDirectory(Path.Combine(Current, "Child " + Index.ToString("D2")));
        for (var Index = 0; Index < 80; Index++) Directory.CreateDirectory(Path.Combine(Other, "Sibling " + Index.ToString("D2")));
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Pinned ancestors UI check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            this.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        try
        {
            this.TryAddSidebarCategory("Pinned Ancestors");
            var Category = this.CustomCategories.Last();
            this.AddFoldersToCategory(Category, [new FileEntry { FullPath = RootPath, IsDirectory = true }]);
            await Settle();
            var Section = this.SidebarSectionElement(Category)!;
            var BookmarkHeader = FindVisualChild<Button>(this.SidebarRootHeader(Section, Category.Folders.Single().Node)!)!;
            static TextBlock PathLabel(DependencyObject Element)
            {
                for (var Index = 0; Index < VisualTreeHelper.GetChildrenCount(Element); Index++)
                {
                    var Child = VisualTreeHelper.GetChild(Element, Index);
                    if (Child is TextBlock { Name: "BookmarkPath" or "TreeFolderPath" } Text) return Text;
                    if (PathLabel(Child) is { } Found) return Found;
                }
                return null!;
            }
            this.ChangeShowBookmarkPaths(false);
            await Settle();
            Check(PathLabel(BookmarkHeader).Visibility == Visibility.Collapsed,
                "Bookmark rows hide their absolute path when the preference is off");
            this.ChangeShowBookmarkPaths(true);
            await Settle();
            var PathText = PathLabel(BookmarkHeader);
            var NameText = FindVisualChild<TextBlock>(BookmarkHeader)!;
            Check(PathText.IsVisible && PathText.Text == RootPath && PathText.ActualWidth > 0
                && PathText.TextTrimming == TextTrimming.CharacterEllipsis
                && NameText.TranslatePoint(new Point(NameText.ActualWidth, 0), BookmarkHeader).X
                    <= PathText.TranslatePoint(new Point(), BookmarkHeader).X,
                "Enabled bookmark paths occupy a separate trimmed column to the right of the folder name");
            Check(BookmarkHeader.ToolTip?.ToString() == RootPath && ToolTipService.GetInitialShowDelay(BookmarkHeader) == 0
                && PathText.ToolTip?.ToString() == RootPath && ToolTipService.GetInitialShowDelay(PathText) == 0,
                "Bookmark names and path labels expose the complete absolute path without a hover delay");
            var FavoriteHeader = FindVisualChild<Button>(this.FavoritesList)!;
            Check(PathLabel(FavoriteHeader).IsVisible && PathLabel(FavoriteHeader).Text == ((FavoriteLocation)FavoriteHeader.DataContext).FullPath,
                "The same preference updates quick-access bookmark paths");
            var ComputerRoot = new FolderNode("Owned folder root", RootPath);
            await this.EnsureChildrenAsync(ComputerRoot);
            this.Roots.Add(ComputerRoot);
            var FavoriteSource = this.FavoritesList.ItemsSource;
            try
            {
                ComputerRoot.IsExpanded = true;
                var Favorite = new FavoriteLocation("Owned quick-access root", RootPath);
                this.FavoritesList.ItemsSource = new[] { Favorite };
                await this.EnsureChildrenAsync(Favorite.Node);
                Favorite.Node.IsExpanded = true;
                await Settle();
                var ComputerItem = (TreeViewItem)this.FolderTree.ItemContainerGenerator.ContainerFromItem(ComputerRoot);
                var ComputerChild = (TreeViewItem)ComputerItem.ItemContainerGenerator.ContainerFromItem(ComputerRoot.Children[0]);
                var FavoriteTree = FindVisualChild<TreeView>(this.SidebarRootHeader(this.FavoritesSection, Favorite.Node)!)!;
                var FavoriteChild = (TreeViewItem)FavoriteTree.ItemContainerGenerator.ContainerFromItem(Favorite.Node.Children[0]);
                foreach (var Row in new[] { ComputerItem, ComputerChild, FavoriteChild })
                {
                    var Label = PathLabel((FrameworkElement)Row.Template.FindName("PART_Header", Row));
                    Check(Label.IsVisible && Label.Text == ((FolderNode)Row.DataContext).FullPath && Label.ActualWidth > 0
                        && Math.Abs(Label.TranslatePoint(new Point(), this.SidebarScroll).X - PathLabel(BookmarkHeader).TranslatePoint(new Point(), this.SidebarScroll).X) < 1,
                        "Quick-access children and My Computer folders share the category path column: " + ((FolderNode)Row.DataContext).Name);
                }
            }
            finally
            {
                this.Roots.Remove(ComputerRoot);
                this.FavoritesList.ItemsSource = FavoriteSource;
                await Settle();
            }
            var Key = this.TreeKeyForSection(Section);
            await this.NavigateAsync(Current, TreeKey: Key);
            await Settle();
            this.SidebarScroll.ScrollToEnd();
            await Settle();
            var Root = Category.Folders.Single().Node;
            var First = Root.Children.Single(Node => Node.Name == "Ancestor A");
            var Parent = First.Children.Single();
            var CurrentNode = Parent.Children.Single();
            await this.NavigateAsync(RootPath, RevealTree: false, SyncNavigationTree: false, TreeKey: Key);
            this.SidebarScroll.ScrollToEnd();
            await Settle();
            Check(ReferenceEquals(this.StickySectionTarget, Section) && ReferenceEquals(this.StickySidebarRoot.DataContext, Root)
                && this.StickySidebarAncestors.Items.OfType<FolderNode>().SequenceEqual(new[] { First, Parent, CurrentNode }),
                "A panel left at the root still pins every parent of the deeply scrolled tree rows");
            Check(!this.StickySidebarAncestors.Items.OfType<FolderNode>().Any(Node => Node.Name.StartsWith("Child "))
                && this.StickyAncestorScroll.ActualHeight < this.SidebarScroll.ViewportHeight,
                "Visible leaf rows are excluded and the fixed ancestor area leaves room for browsing");
            Check(PathLabel(this.StickySidebarRoot).IsVisible && PathLabel(this.StickySidebarRoot).Text == RootPath,
                "A pinned bookmark root retains its absolute path column");
            Check(this.StickySidebar.TranslatePoint(new Point(this.StickySidebar.ActualWidth, 0), this.SidebarScroll).X
                >= this.SidebarScroll.ViewportWidth && this.SidebarScroll.ClipToBounds,
                "The opaque pinned surface covers the complete scrolling content width without a right-hand text leak");
            double PinnedIconX(FolderNode Node) => FindVisualChild<CDir.Controls.ShellIcon>(
                this.StickySidebarAncestors.ItemContainerGenerator.ContainerFromItem(Node))!.TranslatePoint(new Point(), this.StickySidebar).X;
            var SourceTree = FindVisualChild<TreeView>(this.SidebarRootHeader(Section, Root)!)!;
            var FirstItem = (TreeViewItem)SourceTree.ItemContainerGenerator.ContainerFromItem(First);
            var ParentItem = (TreeViewItem)FirstItem.ItemContainerGenerator.ContainerFromItem(Parent);
            var CurrentItem = (TreeViewItem)ParentItem.ItemContainerGenerator.ContainerFromItem(CurrentNode);
            var Leaf = CurrentNode.Children.First(Node => !Node.IsPlaceholder);
            var LeafItem = (TreeViewItem)CurrentItem.ItemContainerGenerator.ContainerFromItem(Leaf);
            var TreeRows = new[] { FirstItem, ParentItem, CurrentItem, LeafItem };
            foreach (var TreeRow in TreeRows)
            {
                var RowHeader = (FrameworkElement)TreeRow.Template.FindName("PART_Header", TreeRow);
                var Label = PathLabel(RowHeader);
                Check(Label.IsVisible && Label.Text == ((FolderNode)TreeRow.DataContext).FullPath && Label.ActualWidth > 0
                    && Label.TextTrimming == TextTrimming.CharacterEllipsis && Label.FontSize == (double)this.FindResource("UIFontSidebar")
                    && Label.ToolTip?.ToString() == Label.Text && ToolTipService.GetInitialShowDelay(Label) == 0,
                    "Every expanded category descendant, including leaf folders, displays its own trimmed path and immediate full-path tip: " + ((FolderNode)TreeRow.DataContext).Name);
                Check(Math.Abs(Label.TranslatePoint(new Point(), this.SidebarScroll).X - PathLabel(BookmarkHeader).TranslatePoint(new Point(), this.SidebarScroll).X) < 1,
                    "Indented descendants align their paths with the root folder column: " + ((FolderNode)TreeRow.DataContext).Name);
            }
            foreach (var Node in new[] { First, Parent, CurrentNode })
            {
                var Label = PathLabel(this.StickySidebarAncestors.ItemContainerGenerator.ContainerFromItem(Node));
                Check(Label.IsVisible && Label.Text == Node.FullPath && Label.ActualWidth > 0,
                    "Every pinned ancestor displays its own absolute path: " + Node.Name);
                Check(Math.Abs(Label.TranslatePoint(new Point(), this.SidebarScroll).X - PathLabel(this.StickySidebarRoot).TranslatePoint(new Point(), this.SidebarScroll).X) < 1,
                    "Pinned ancestor paths align with the fixed root path column: " + Node.Name);
            }
            this.ChangeShowBookmarkPaths(false);
            await Settle();
            Check(TreeRows.All(Row => PathLabel((FrameworkElement)Row.Template.FindName("PART_Header", Row)).Visibility == Visibility.Collapsed)
                && this.StickySidebarAncestors.Items.OfType<FolderNode>().All(Node => PathLabel(this.StickySidebarAncestors.ItemContainerGenerator.ContainerFromItem(Node)).Visibility == Visibility.Collapsed),
                "Disabling folder paths immediately hides ordinary descendants and pinned ancestors together");
            this.ChangeShowBookmarkPaths(true);
            await Settle();
            double HeaderX(TreeViewItem Item) => ((FrameworkElement)Item.Template.FindName("PART_Header", Item))
                .TranslatePoint(new Point(), this.SidebarScroll).X;
            var FirstX = PinnedIconX(First);
            var ParentX = PinnedIconX(Parent);
            var CurrentX = PinnedIconX(CurrentNode);
            Check(FirstX > FindVisualChild<CDir.Controls.ShellIcon>(this.StickySidebarRoot)!.TranslatePoint(new Point(), this.StickySidebar).X
                && ParentX > FirstX && CurrentX > ParentX
                && Math.Abs((ParentX - FirstX) - (HeaderX(ParentItem) - HeaderX(FirstItem))) < 1
                && Math.Abs((CurrentX - ParentX) - (HeaderX(CurrentItem) - HeaderX(ParentItem))) < 1,
                "Pinned folder icons preserve the original tree's increasing indentation at every ancestor level");
            CDir.Controls.TreeBranchLines PinnedGuides(FolderNode Node) => FindVisualChild<CDir.Controls.TreeBranchLines>(
                this.StickySidebarAncestors.ItemContainerGenerator.ContainerFromItem(Node))!;
            static IEnumerable<GeometryDrawing> Strokes(Drawing Drawing) => Drawing switch
            {
                GeometryDrawing Stroke => new[] { Stroke },
                DrawingGroup Group => Group.Children.SelectMany(Strokes),
                _ => []
            };
            var SourceRoot = this.SidebarRootHeader(Section, Root)!;
            var RootGuides = FindVisualChild<CDir.Controls.TreeBranchLines>(SourceRoot)!;
            double ColumnX(CDir.Controls.TreeBranchLines Guides) => Guides.TranslatePoint(
                new Point(Math.Floor(Guides.ActualWidth / 2) + .5, 0), this.SidebarScroll).X;
            var ExpectedColumns = new List<double> { ColumnX(RootGuides) };
            var PinnedRootGuides = FindVisualChild<CDir.Controls.TreeBranchLines>(this.StickyRootRow)!;
            LineGeometry[] Lines(CDir.Controls.TreeBranchLines Guide) => Strokes(VisualTreeHelper.GetDrawing(Guide))
                .Select(Stroke => Stroke.Geometry).OfType<LineGeometry>().ToArray();
            Check(!RootGuides.HasIncomingGuide
                && Lines(RootGuides).All(Line => Line.StartPoint.Y >= RootGuides.HeaderHeight / 2)
                && Lines(RootGuides).Any(Line => Line.StartPoint.X == Line.EndPoint.X && Line.EndPoint.Y > RootGuides.HeaderHeight
                    && Math.Abs(RootGuides.TranslatePoint(Line.StartPoint, this.SidebarScroll).X - ColumnX(RootGuides)) < .01)
                && Lines(PinnedRootGuides).All(Line => Line.StartPoint.Y >= PinnedRootGuides.ActualHeight / 2)
                && Lines(PinnedRootGuides).Any(Line => Line.StartPoint.X == Line.EndPoint.X
                    && Math.Abs(PinnedRootGuides.TranslatePoint(Line.StartPoint, this.SidebarScroll).X - ColumnX(RootGuides)) < .01),
                "Normal and fixed roots retain the folder's subtree guide below the header center but never connect upward to the category title");
            foreach (var (Original, Pinned, Child) in new[]
            {
                (RootGuides, PinnedRootGuides, FirstItem),
                ((CDir.Controls.TreeBranchLines)FirstItem.Template.FindName("BranchLines", FirstItem), PinnedGuides(First), ParentItem),
                ((CDir.Controls.TreeBranchLines)ParentItem.Template.FindName("BranchLines", ParentItem), PinnedGuides(Parent), CurrentItem)
            })
            {
                var ChildColumn = ColumnX((CDir.Controls.TreeBranchLines)Child.Template.FindName("BranchLines", Child));
                bool OutgoingAligned(CDir.Controls.TreeBranchLines Guide, double Height) => Lines(Guide).Any(Line =>
                    Line.StartPoint.Y > 0 && Line.StartPoint.X == Line.EndPoint.X && Math.Abs(Line.EndPoint.Y - Height) < .01
                    && Math.Abs(Guide.TranslatePoint(Line.StartPoint, this.SidebarScroll).X - ChildColumn) < .01);
                Check(OutgoingAligned(Original, Original.HeaderHeight) && OutgoingAligned(Pinned, Pinned.ActualHeight),
                    "Fixed and scrolling outgoing connectors meet the actual child guide column, including child-tree padding");
            }
            var RootArrow = FindVisualChild<ToggleButton>(this.StickyRootRow)!;
            Check(RootArrow.IsVisible && RootArrow.IsChecked == Root.IsExpanded
                && Math.Abs(RootArrow.TranslatePoint(new Point(), this.SidebarScroll).X
                    - FindVisualChild<ToggleButton>(SourceRoot)!.TranslatePoint(new Point(), this.SidebarScroll).X) < .01
                && Math.Abs(FindVisualChild<CDir.Controls.ShellIcon>(this.StickySidebarRoot)!.TranslatePoint(new Point(), this.SidebarScroll).X
                    - FindVisualChild<CDir.Controls.ShellIcon>(SourceRoot)!.TranslatePoint(new Point(), this.SidebarScroll).X) < .01,
                "The pinned root arrow and folder icon align exactly with the original root");
            foreach (var (Node, Depth, Original) in new[] { (First, 1, FirstItem), (Parent, 2, ParentItem), (CurrentNode, 3, CurrentItem) })
            {
                var Guides = PinnedGuides(Node);
                var Segments = Lines(Guides);
                var Icon = FindVisualChild<CDir.Controls.ShellIcon>(this.StickySidebarAncestors.ItemContainerGenerator.ContainerFromItem(Node))!;
                var OriginalGuides = (CDir.Controls.TreeBranchLines)Original.Template.FindName("BranchLines", Original);
                ExpectedColumns.Add(ColumnX(OriginalGuides));
                var Columns = Segments.Where(Line => Line.StartPoint.Y == 0 && Math.Abs(Line.EndPoint.Y - Guides.ActualHeight) < 1
                    && Line.StartPoint.X == Line.EndPoint.X).Select(Line => Guides.TranslatePoint(Line.StartPoint, this.SidebarScroll).X).ToArray();
                Check(Columns.Length == ExpectedColumns.Count && Columns.Zip(ExpectedColumns).All(Pair => Math.Abs(Pair.First - Pair.Second) < .01)
                    && Segments.Any(Line => Line.StartPoint.Y == Line.EndPoint.Y && Line.StartPoint.X < Line.EndPoint.X
                        && Math.Abs(Guides.TranslatePoint(Line.EndPoint, this.SidebarScroll).X
                            - OriginalGuides.TranslatePoint(new Point(OriginalGuides.ActualWidth, 0), this.SidebarScroll).X) < .01),
                    "Pinned depth " + Depth + " matches every original guide column and horizontal endpoint without a pixel offset");
                var Arrow = FindVisualChild<ToggleButton>(this.StickySidebarAncestors.ItemContainerGenerator.ContainerFromItem(Node))!;
                var OriginalArrow = (ToggleButton)Original.Template.FindName("Expander", Original);
                var OriginalHeader = (FrameworkElement)Original.Template.FindName("PART_Header", Original);
                Check(Arrow.IsVisible && Arrow.IsChecked == Node.IsExpanded
                    && Math.Abs(Arrow.TranslatePoint(new Point(), this.SidebarScroll).X - OriginalArrow.TranslatePoint(new Point(), this.SidebarScroll).X) < .01
                    && Math.Abs(Icon.TranslatePoint(new Point(), this.SidebarScroll).X
                        - FindVisualChild<CDir.Controls.ShellIcon>(OriginalHeader)!.TranslatePoint(new Point(), this.SidebarScroll).X) < .01,
                    "Pinned depth " + Depth + " retains the original arrow position expansion state and icon position");
            }
            var NativeGuides = (CDir.Controls.TreeBranchLines)FirstItem.Template.FindName("BranchLines", FirstItem);
            var NativePen = Strokes(VisualTreeHelper.GetDrawing(NativeGuides)).First(Stroke => Stroke.Pen is not null).Pen;
            Check(Strokes(VisualTreeHelper.GetDrawing(PinnedGuides(Parent))).All(Stroke => Stroke.Pen is { } Pen
                && Pen.Thickness == NativePen.Thickness && Pen.Brush.ToString() == NativePen.Brush.ToString()
                && Pen.DashStyle.Dashes.SequenceEqual(NativePen.DashStyle.Dashes)),
                "Pinned guides retain the original tree's line color thickness and dotted pattern");
            if (AppMain.ScreenshotPath is { } Screenshot)
            {
                var Bitmap = new RenderTargetBitmap((int)this.HostWindow.ActualWidth, (int)this.HostWindow.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                Bitmap.Render(this.HostWindow);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Screenshot)!, "StickyAncestors.png"));
                Encoder.Save(Stream);
            }
            var FirstButton = FindVisualChild<Button>(this.StickySidebarAncestors.ItemContainerGenerator.ContainerFromItem(First))!;
            this.StickySidebarRootClick(FirstButton, new RoutedEventArgs(Button.ClickEvent));
            await this.SidebarClickTask;
            // Navigation from a pinned row is asynchronous, just like the real tree.
            var Deadline = DateTime.UtcNow.AddSeconds(10);
            while (this.Loading && DateTime.UtcNow < Deadline) await Task.Delay(20);
            await Settle();
            Check(SamePath(this.CurrentPath, First.FullPath), "Clicking a fixed ancestor navigates to that folder");
            await this.NavigateAsync(Current, TreeKey: Key);
            this.SidebarScroll.ScrollToEnd();
            await Settle();
            var ParentButton = FindVisualChild<Button>(this.StickySidebarAncestors.ItemContainerGenerator.ContainerFromItem(Parent))!;
            var Menu = ParentButton.ContextMenu;
            var StayOpen = Menu.StaysOpen;
            try
            {
                Menu.StaysOpen = true;
                Menu.PlacementTarget = ParentButton;
                Menu.IsOpen = true;
                await Settle();
                var Action = Menu.Items.OfType<MenuItem>().Single(Item => Item.Tag?.ToString() == "CollapseAllSubfolders");
                var InvokedPath = (Action.DataContext as FolderNode)?.FullPath;
                Action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                var CollapsedImmediately = !Parent.IsExpanded;
                await Settle();
                Check(!Parent.IsExpanded && Parent.Children.All(Node => !Node.IsExpanded) && Root.IsExpanded && First.IsExpanded
                    && SamePath(this.CurrentPath, Current),
                    "An intermediate fixed ancestor collapses only its own branch without navigating or collapsing higher ancestors"
                    + $" (parent={Parent.IsExpanded}, immediate={CollapsedImmediately}, target={InvokedPath}, expectedTarget={Parent.FullPath}, path={this.CurrentPath})");
            }
            finally { Menu.IsOpen = false; Menu.StaysOpen = StayOpen; }
            await this.NavigateAsync(Other, TreeKey: Key);
            await this.NavigateAsync(RootPath, RevealTree: false, SyncNavigationTree: false, TreeKey: Key);
            this.SidebarScroll.ScrollToEnd();
            await Settle();
            Check(this.StickySidebarAncestors.Items.OfType<FolderNode>().Select(Node => Node.Name)
                .SequenceEqual(new[] { "Other branch", "Other parent" }),
                "Scrolling into a sibling branch replaces the entire pinned chain while the panel remains at the root");
            var Replacement = this.StickySidebarAncestors.Items.OfType<FolderNode>().ToArray();
            Check(Math.Abs(PinnedIconX(Replacement[0]) - FirstX) < 1 && PinnedIconX(Replacement[1]) > PinnedIconX(Replacement[0]),
                "Replacing the pinned branch restarts indentation at the root's child level");
            Check(PinnedGuides(Replacement[0]).PinnedDepth == 1 && PinnedGuides(Replacement[1]).PinnedDepth == 2,
                "Replacing the pinned branch resets its guide columns without retaining the previous branch's extra level");
            await this.NavigateAsync(InitialPath, RevealTree: false, SyncNavigationTree: false, TreeKey: Key);
            this.SidebarScroll.ScrollToEnd();
            await Settle();
            Check(this.StickySidebarAncestors.Items.OfType<FolderNode>().Select(Node => Node.Name)
                .SequenceEqual(new[] { "Other branch", "Other parent" }),
                "The scrolled tree's pinned parents remain independent of an unrelated main-panel directory");
            var BranchNode = this.StickySidebarAncestors.Items.OfType<FolderNode>().First();
            var Descendant = BranchNode.Children.Single();
            var BranchArrow = FindVisualChild<ToggleButton>(this.StickySidebarAncestors.ItemContainerGenerator.ContainerFromItem(BranchNode))!;
            BranchArrow.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            BranchArrow.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle();
            Check(!BranchNode.IsExpanded && Descendant.IsExpanded && SamePath(this.CurrentPath, InitialPath),
                "A pinned ancestor arrow collapses that folder without navigating or discarding descendant expansion state");
            BranchNode.IsExpanded = true;
            await Settle();
            this.SidebarScroll.ScrollToEnd();
            await Settle();
            Check(FindVisualChild<ToggleButton>(this.StickySidebarAncestors.ItemContainerGenerator.ContainerFromItem(BranchNode))!.IsChecked == true,
                "Pinned arrows synchronize when the original tree folder expands again");
            var ExtraRoot = new CategoryFolder { FullPath = Other };
            Category.Folders.Add(ExtraRoot);
            await this.EnsureChildrenAsync(ExtraRoot.Node);
            ExtraRoot.Node.IsExpanded = true;
            Root.IsExpanded = false;
            await Settle();
            var CategoryHeight = ((FrameworkElement)Section.Template.FindName("CategoryHeaderToggle", Section)).ActualHeight;
            this.SidebarScroll.ScrollToVerticalOffset(this.SidebarScroll.VerticalOffset + SourceRoot.TranslatePoint(new Point(), this.SidebarScroll).Y - CategoryHeight + 1);
            await Settle();
            Check(this.StickySidebarRoot.Visibility == Visibility.Collapsed && this.StickySidebarAncestors.Items.Count == 0,
                "Collapsed roots never create redundant pinned folder rows over unrelated content");
            Root.IsExpanded = true;
            this.SidebarScroll.ScrollToEnd();
            await Settle();
            Check(SamePath(this.CurrentPath, InitialPath), "Changing the visible branch never navigates the unrelated main panel");
            var NextCategory = new SidebarCategory { Name = "Following Category" };
            var NextRoot = new CategoryFolder { FullPath = Other };
            NextCategory.Folders.Add(NextRoot);
            await this.EnsureChildrenAsync(NextRoot.Node);
            NextRoot.Node.IsExpanded = true;
            this.CustomCategories.Add(NextCategory);
            this.SidebarSections.Add(NextCategory);
            await Settle();
            this.SidebarScroll.ScrollToVerticalOffset(this.SidebarScroll.VerticalOffset + this.SidebarRootBottom(Section) - CategoryHeight / 2);
            await Settle();
            var SectionBottom = this.SidebarRootBottom(Section);
            Check(ReferenceEquals(this.StickySectionTarget, Section) && SectionBottom > 0 && SectionBottom < CategoryHeight
                && this.StickySidebar.RenderTransform is TranslateTransform { Y: < 0 } Shift
                && Math.Abs(Shift.Y + CategoryHeight - SectionBottom) < 1,
                "The next category pushes the previous pinned header away at their boundary");
        }
        finally
        {
            this.ChangeShowBookmarkPaths(InitialPaths);
            this.RestoreSidebarCategories(InitialCategories, InitialOrder);
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || File.GetAttributes(Fixture).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Refusing cleanup outside the owned pinned-ancestor fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
