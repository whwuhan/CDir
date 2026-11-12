using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunCategoryHorizontalScrollSmokeChecksAsync(List<string> Passed)
    {
        var Id = Guid.NewGuid().ToString("N");
        var Fixture = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CDirCategoryScroll" + Id)).FullName;
        var Marker = Path.Combine(Fixture, ".CDirCategoryScrollOwner");
        File.WriteAllText(Marker, Id);
        var InitialPaths = this.Settings.ShowBookmarkPaths;
        var InitialWidth = this.SidebarColumn.Width;
        var InitialFavorites = this.FavoritesList.ItemsSource;
        var InitialOffset = this.SidebarScroll.VerticalOffset;
        var Categories = new List<SidebarCategory>();
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Category horizontal scroll check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            this.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            this.UpdateLayout();
        }
        IEnumerable<T> Children<T>(DependencyObject Parent) where T : DependencyObject
        {
            for (var Index = 0; Index < VisualTreeHelper.GetChildrenCount(Parent); Index++)
            {
                var Child = VisualTreeHelper.GetChild(Parent, Index);
                if (Child is T Match) yield return Match;
                foreach (var Descendant in Children<T>(Child)) yield return Descendant;
            }
        }
        FolderNode Prepare(FolderNode Root)
        {
            var ChildPath = Directory.CreateDirectory(Path.Combine(Root.FullPath, "Long child " + new string('W', 58))).FullName;
            Root.IsLoaded = true; Root.Children.Clear();
            var Child = new FolderNode(Path.GetFileName(ChildPath), ChildPath) { IsLoaded = true };
            Child.Children.Clear(); Root.Children.Add(Child); Root.IsExpanded = true;
            return Root;
        }
        try
        {
            this.SidebarColumn.Width = new GridLength(360);
            this.ChangeShowBookmarkPaths(true);
            var FavoritePath = Directory.CreateDirectory(Path.Combine(Fixture, "Quick " + new string('Q', 45))).FullName;
            var Favorite = new FavoriteLocation(Path.GetFileName(FavoritePath), FavoritePath);
            Prepare(Favorite.Node);
            this.FavoritesList.ItemsSource = new[] { Favorite };
            var Sections = new List<(Expander Section, FolderNode Root)> { (this.FavoritesSection, Favorite.Node) };
            foreach (var Name in new[] { "Category A", "Category B" })
            {
                var RootPath = Directory.CreateDirectory(Path.Combine(Fixture, Name + new string('R', 45))).FullName;
                var Bookmark = new CategoryFolder { FullPath = RootPath };
                Prepare(Bookmark.Node);
                var Category = new SidebarCategory { Name = Name, Folders = [Bookmark] };
                Categories.Add(Category); this.CustomCategories.Add(Category); this.SidebarSections.Add(Category);
                await Settle();
                Sections.Add((this.SidebarSectionElement(Category)!, Bookmark.Node));
            }
            await Settle();
            foreach (var (Section, Root) in Sections)
            {
                var Viewer = (ScrollViewer)Section.Template.FindName("CategoryScroll", Section);
                var Header = this.SidebarRootHeader(Section, Root)!;
                var Label = Children<TextBlock>(Header).Single(Text => Text.Name == "BookmarkPath");
                var ChildTree = Children<TreeView>(Header).Single();
                var ChildLabel = Children<TextBlock>(ChildTree).Single(Text => Text.Name == "TreeFolderPath");
                var NameLabel = Children<TextBlock>(ChildTree).Single(Text => Text.Text == Root.Children[0].Name);
                Header.BringIntoView(new Rect(0, 0, 100, 20)); await Settle();
                Viewer.ScrollToLeftEnd(); await Settle();
                Check(Viewer.ScrollableWidth > 100 && Viewer.ComputedHorizontalScrollBarVisibility == Visibility.Visible
                    && Viewer.ScrollableHeight == 0 && FindVisualChild<ScrollViewer>(ChildTree)!.ScrollableWidth == 0,
                    "The entire category has one horizontal scroll area, including its bookmark root and descendants: " + Section.Header);
                var ChildItem = (TreeViewItem)ChildTree.ItemContainerGenerator.ContainerFromIndex(0);
                var ChildHeader = (FrameworkElement)ChildItem.Template.FindName("PART_Header", ChildItem);
                foreach (var Offset in new[] { 0.0, 96.0 })
                {
                    Viewer.ScrollToHorizontalOffset(Offset); await Settle();
                    var Expected = Viewer.HorizontalOffset;
                    var Changes = 0;
                    void Changed(object Sender, ScrollChangedEventArgs E)
                    {
                        if (ReferenceEquals(E.OriginalSource, Viewer) && E.HorizontalChange != 0) Changes++;
                    }
                    Viewer.ScrollChanged += Changed;
                    try
                    {
                        foreach (var Target in new FrameworkElement[] { Header, Label, ChildItem, ChildHeader, ChildLabel })
                        {
                            Target.BringIntoView(); await Settle();
                            Check(Math.Abs(Viewer.HorizontalOffset - Expected) < 0.01 && Changes == 0,
                                $"Automatic root, child and path reveals preserve horizontal position without a transient jump: {Section.Header}, {Target.GetType().Name}, {Offset}");
                        }
                        ChildItem.BringIntoView(new Rect(0, 0, 180, TreeHeaderHeight(ChildItem))); await Settle();
                        Check(Math.Abs(Viewer.HorizontalOffset - Expected) < 0.01 && Changes == 0,
                            $"An explicit child header rectangle preserves the category horizontal position: {Section.Header}, {Offset}");
                    }
                    finally { Viewer.ScrollChanged -= Changed; }
                }
                Viewer.ScrollToLeftEnd(); await Settle();
                var NaturalName = new FormattedText(NameLabel.Text, System.Globalization.CultureInfo.CurrentUICulture, NameLabel.FlowDirection,
                    new Typeface(NameLabel.FontFamily, NameLabel.FontStyle, NameLabel.FontWeight, NameLabel.FontStretch),
                    NameLabel.FontSize, NameLabel.Foreground, VisualTreeHelper.GetDpi(NameLabel).PixelsPerDip).WidthIncludingTrailingWhitespace;
                Check(NameLabel.ActualWidth + 1 >= NaturalName
                    && Math.Abs(Label.TranslatePoint(new Point(), Viewer).X - ChildLabel.TranslatePoint(new Point(), Viewer).X) < 1,
                    "Long descendant names fit in full and paths retain aligned columns within their category: " + Section.Header);
                var InitialX = Label.TranslatePoint(new Point(), Viewer).X;
                var InitialChildX = ChildLabel.TranslatePoint(new Point(), Viewer).X;
                var Vertical = this.SidebarScroll.VerticalOffset;
                var Extent = Viewer.ExtentWidth;
                ScrollInteraction.HandleWheel(Label, -120, ModifierKeys.Shift); await Settle();
                Check(Viewer.HorizontalOffset > 0 && this.SidebarScroll.VerticalOffset == Vertical
                    && Math.Abs(Label.TranslatePoint(new Point(), Viewer).X - InitialX + Viewer.HorizontalOffset) < 1
                    && Math.Abs(ChildLabel.TranslatePoint(new Point(), Viewer).X - InitialChildX + Viewer.HorizontalOffset) < 1
                    && Math.Abs(Viewer.ExtentWidth - Extent) < 1,
                    "Shift wheel moves roots and descendants together with a stable range and vertical position: " + Section.Header);
                Check(Sections.Where(Other => Other.Section != Section).All(Other =>
                    ((ScrollViewer)Other.Section.Template.FindName("CategoryScroll", Other.Section)).HorizontalOffset == 0),
                    "Horizontal offsets are independent between categories: " + Section.Header);
                Viewer.ScrollToRightEnd(); await Settle();
                var Longest = Children<TextBlock>(Viewer).Where(Text => Text.Name is "BookmarkPath" or "TreeFolderPath")
                    .OrderByDescending(Text => Text.TranslatePoint(new Point(Text.ActualWidth, 0), Viewer).X).First();
                Check(Longest.TranslatePoint(new Point(Longest.ActualWidth, 0), Viewer).X <= Viewer.ViewportWidth + 1,
                    "Scrolling to the right exposes the complete longest path: " + Section.Header);
                Viewer.ScrollToLeftEnd(); await Settle();
                var Bar = (ScrollBar)Viewer.Template.FindName("PART_HorizontalScrollBar", Viewer);
                var Track = (Track)Bar.Template.FindName("PART_Track", Bar);
                Track.Thumb!.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                Track.Thumb.RaiseEvent(new DragDeltaEventArgs(8, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                Track.Thumb.RaiseEvent(new DragCompletedEventArgs(8, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                await Settle();
                Check(Viewer.HorizontalOffset > 0, "Dragging the category scrollbar moves the complete category: " + Section.Header);
                Viewer.ScrollToLeftEnd(); await Settle();
                var Screen = Header.PointToScreen(new Point(70, 10));
                var Packed = (IntPtr)unchecked((int)(((uint)(ushort)(short)Screen.Y << 16) | (ushort)(short)Screen.X));
                var Handled = false;
                this.HostWindow.BrowserAppCommand(IntPtr.Zero, 0x020E, (IntPtr)(60 << 16), Packed, ref Handled);
                await Settle();
                Check(Handled && Viewer.HorizontalOffset > 0 && Viewer.HorizontalOffset <= 24,
                    "Native horizontal wheel input over a category root routes to its own content area: " + Section.Header);
                Viewer.ScrollToLeftEnd(); await Settle();
                this.ChangeShowBookmarkPaths(false); await Settle();
                Check(Viewer.ScrollableWidth > 0 && !Label.IsVisible, "Categories still scroll full names with folder paths hidden: " + Section.Header);
                this.ChangeShowBookmarkPaths(true); await Settle();
            }
        }
        finally
        {
            this.FavoritesList.ItemsSource = InitialFavorites;
            foreach (var Category in Categories) { this.SidebarSections.Remove(Category); this.CustomCategories.Remove(Category); }
            this.ChangeShowBookmarkPaths(InitialPaths);
            this.SidebarColumn.Width = InitialWidth;
            this.SidebarScroll.ScrollToVerticalOffset(InitialOffset);
            await Settle();
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != Id
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unowned category scroll fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
