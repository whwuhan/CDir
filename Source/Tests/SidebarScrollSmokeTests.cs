using CDir.Models;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunSidebarScrollSmokeChecksAsync(List<string> Passed)
    {
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirSidebarScroll" + FixtureId);
        Directory.CreateDirectory(Fixture);
        var Marker = Path.Combine(Fixture, ".CDirSidebarScrollOwner");
        File.WriteAllText(Marker, FixtureId);
        var Root = new FolderNode("Long Sidebar " + new string('W', 100), Fixture) { IsLoaded = true };
        Root.Children.Clear();
        var InitialOffset = this.SidebarScroll.VerticalOffset;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Sidebar scrolling UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            this.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            this.UpdateLayout();
        }
        IEnumerable<ScrollBar> Bars(DependencyObject Parent)
        {
            for (var Index = 0; Index < VisualTreeHelper.GetChildrenCount(Parent); Index++)
            {
                var Child = VisualTreeHelper.GetChild(Parent, Index);
                if (Child is ScrollBar Bar) yield return Bar;
                foreach (var Descendant in Bars(Child)) yield return Descendant;
            }
        }
        void CheckScrollBarStyle(ScrollBar Bar, Track Track)
        {
            var Grip = (Border)Track.Thumb!.Template.FindName("Grip", Track.Thumb);
            Check(Grip.CornerRadius == new CornerRadius(0),
                "The scrollbar thumb has square corners in both orientations: " + Bar.Orientation);
            Check(Grip.Background is SolidColorBrush Brush && Brush.Color.A == 255
                && Brush.Color.R == Brush.Color.G && Brush.Color.G == Brush.Color.B && Brush.Color.R >= 0x99,
                "The rendered scrollbar thumb uses a light neutral gray: " + Bar.Orientation);
        }
        void CheckPersistentScrollBar(ScrollBar Bar, Track Track)
        {
            CheckScrollBarStyle(Bar, Track);
            Check(!ScrollBarReveal.GetIsEnabled(Bar) && !ScrollBarReveal.GetRevealOnScroll(Bar) && Track.Opacity == 1,
                "The main panel scrollbar remains visible and does not inherit the sidebar hover policy: " + Bar.Orientation);
            foreach (var Event in new[] { Mouse.MouseEnterEvent, Mouse.MouseLeaveEvent })
            {
                Bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Event });
                Check(Track.Opacity == 1,
                    "Crossing a scrollbar boundary never changes its visibility: " + Bar.Orientation + " " + Event.Name);
            }
        }
        async Task CheckSidebarHoverReveal(ScrollBar Bar, Track Track)
        {
            var HitTestVisible = Bar.IsHitTestVisible;
            void Hover(bool Inside) => Bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = Inside ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent });
            void StartDrag() => Track.Thumb!.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            void EndDrag() => Track.Thumb!.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            try
            {
                // Keep these routed input checks independent of the user's desktop pointer.
                Bar.IsHitTestVisible = false;
                await Settle();
                Bar.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Hover(false);
                Check(ScrollBarReveal.GetIsEnabled(Bar) && Track.Opacity == 0,
                    "The sidebar scrollbar is hidden before deliberate hover: " + Bar.Orientation);
                CheckScrollBarStyle(Bar, Track);
                foreach (var Direction in new[] { "Main panel to sidebar", "Sidebar to main panel" })
                {
                    Hover(true);
                    Check(Track.Opacity == 0, Direction + " does not reveal a sidebar scrollbar on entry: " + Bar.Orientation);
                    await Task.Delay(35);
                    Hover(false);
                    Check(Track.Opacity == 0, Direction + " stays hidden when the pointer quickly exits: " + Bar.Orientation);
                }
                await Task.Delay(ScrollBarReveal.HoverDelay + TimeSpan.FromMilliseconds(50));
                Check(Track.Opacity == 0 && !ScrollBarReveal.GetIsRevealed(Bar),
                    "A cancelled hover cannot reveal the sidebar scrollbar after a boundary crossing: " + Bar.Orientation);
                Hover(true);
                Check(Track.Opacity == 0, "An intentional sidebar hover starts with a hidden track: " + Bar.Orientation);
                var Deadline = Environment.TickCount64 + 5000;
                while (Track.Opacity == 0 && Environment.TickCount64 < Deadline) await Task.Delay(20);
                Check(Track.Opacity == 1 && ScrollBarReveal.GetIsRevealed(Bar),
                    "Remaining over a sidebar scrollbar reveals its track after the hover delay: " + Bar.Orientation);
                Hover(false);
                Check(Track.Opacity == 0, "Leaving an idle sidebar scrollbar hides it immediately: " + Bar.Orientation);
                StartDrag();
                Check(Track.Opacity == 1, "Dragging a hidden sidebar scrollbar reveals it immediately: " + Bar.Orientation);
                Hover(false);
                await Task.Delay(ScrollBarReveal.HoverDelay + TimeSpan.FromMilliseconds(50));
                Check(Track.Opacity == 1, "A sidebar scrollbar remains visible while dragged outside its bounds: " + Bar.Orientation);
                EndDrag();
                Check(Track.Opacity == 0, "Releasing a sidebar scrollbar outside its bounds hides it: " + Bar.Orientation);
                Hover(true);
                StartDrag();
                EndDrag();
                Check(Track.Opacity == 1, "Releasing inside the sidebar scrollbar does not blink or restart the hover delay: " + Bar.Orientation);
                Hover(false);
                Hover(true);
                Bar.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                await Task.Delay(ScrollBarReveal.HoverDelay + TimeSpan.FromMilliseconds(50));
                Check(Track.Opacity == 0 && !ScrollBarReveal.GetIsRevealed(Bar),
                    "Unloading a sidebar scrollbar cancels its pending hover reveal: " + Bar.Orientation);
            }
            finally
            {
                Hover(false);
                Bar.IsHitTestVisible = HitTestVisible;
                await Settle();
            }
        }
        async Task CheckScrollReveal(ScrollViewer Viewer, ScrollBar Bar, Track Track)
        {
            var HitTestVisible = Bar.IsHitTestVisible;
            var Horizontal = Bar.Orientation == Orientation.Horizontal;
            var Original = Horizontal ? Viewer.HorizontalOffset : Viewer.VerticalOffset;
            bool RevealedDuringScroll = false;
            void Changed(object Sender, ScrollChangedEventArgs E)
            {
                if (ReferenceEquals(E.OriginalSource, Viewer) && (Horizontal ? E.HorizontalChange : E.VerticalChange) != 0)
                    RevealedDuringScroll |= ScrollBarReveal.GetIsRevealed(Bar) && Track.Opacity == 1;
            }
            void Move()
            {
                var Current = Horizontal ? Viewer.HorizontalOffset : Viewer.VerticalOffset;
                var Maximum = Horizontal ? Viewer.ScrollableWidth : Viewer.ScrollableHeight;
                var Target = Current < Maximum / 2 ? Math.Min(Maximum, Current + 24) : Math.Max(0, Current - 24);
                if (Horizontal) Viewer.ScrollToHorizontalOffset(Target); else Viewer.ScrollToVerticalOffset(Target);
            }
            try
            {
                Bar.IsHitTestVisible = false;
                Bar.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Viewer.ScrollChanged += Changed;
                Move(); await Settle();
                Check(ScrollBarReveal.GetRevealOnScroll(Viewer) && RevealedDuringScroll,
                    "Scrolling the tree reveals its scrollbar without hovering over the track: " + Bar.Orientation);
                Move(); Viewer.UpdateLayout();
                await Task.Delay(TimeSpan.FromTicks(ScrollBarReveal.ScrollDisplayDuration.Ticks * 2 / 3));
                Move(); Viewer.UpdateLayout();
                await Task.Delay(TimeSpan.FromTicks(ScrollBarReveal.ScrollDisplayDuration.Ticks * 2 / 3));
                Check(Track.Opacity == 1 && ScrollBarReveal.GetIsRevealed(Bar),
                    "Continued tree scrolling renews the visibility interval: " + Bar.Orientation);
                var Deadline = Environment.TickCount64 + 5000;
                while (ScrollBarReveal.GetIsRevealed(Bar) && Environment.TickCount64 < Deadline) await Task.Delay(20);
                Check(!ScrollBarReveal.GetIsRevealed(Bar) && Track.Opacity == 0,
                    "The scrollbar hides after scrolling stops outside the track: " + Bar.Orientation);
                Move(); Viewer.UpdateLayout();
                Track.Thumb!.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                await Task.Delay(ScrollBarReveal.ScrollDisplayDuration + TimeSpan.FromMilliseconds(50));
                Check(Track.Opacity == 1, "The scroll timeout cannot hide a scrollbar while it is dragged: " + Bar.Orientation);
                Track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Check(Track.Opacity == 0, "A completed drag respects the expired scroll interval: " + Bar.Orientation);
                Move(); Viewer.UpdateLayout();
                Bar.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Check(!ScrollBarReveal.GetIsRevealed(Bar) && Track.Opacity == 0,
                    "Unloading clears active scroll visibility and its timer: " + Bar.Orientation);
            }
            finally
            {
                Viewer.ScrollChanged -= Changed;
                if (Horizontal) Viewer.ScrollToHorizontalOffset(Original); else Viewer.ScrollToVerticalOffset(Original);
                await Settle();
                Bar.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Bar.IsHitTestVisible = HitTestVisible;
            }
        }
        try
        {
            for (var Index = 0; Index < 80; Index++)
            {
                var Path = Directory.CreateDirectory(System.IO.Path.Combine(Fixture, $"Folder-{Index:00}")).FullName;
                var Child = new FolderNode($"Folder {Index:00}", Path) { IsLoaded = true };
                Child.Children.Clear();
                Root.Children.Add(Child);
            }
            this.Roots.Add(Root);
            Root.IsExpanded = true;
            await Settle();
            this.SidebarScroll.ScrollToTop();
            await Settle();
            var Inner = FindVisualChild<ScrollViewer>(this.FolderTree)!;
            var VerticalBars = Bars(this.SidebarPanel).Where(Bar => Bar.Orientation == Orientation.Vertical && Bar.IsVisible).ToArray();
            Check(this.FolderTree.ActualHeight > this.SidebarScroll.ViewportHeight && this.SidebarScroll.ScrollableHeight > 0
                && VerticalBars.Length == 1 && Inner.ScrollableHeight == 0
                && Inner.ComputedVerticalScrollBarVisibility != Visibility.Visible,
                "A long folder tree has exactly one visible vertical scrollbar owned by the outer sidebar");
            var HorizontalBar = Bars(this.FolderTree).Single(Bar => Bar.Orientation == Orientation.Horizontal && Bar.IsVisible);
            var VerticalBar = VerticalBars.Single();
            Check(VerticalBar.ActualWidth == 12 && HorizontalBar.ActualHeight == 12,
                $"Both sidebar scrollbar orientations still reserve twelve pixels (vertical={VerticalBar.ActualWidth}, horizontal={HorizontalBar.ActualHeight})");
            foreach (var Bar in new[] { VerticalBar, HorizontalBar })
            {
                // The tree's own ScrollViewer handles BringIntoView for its scrollbar,
                // so position the outer viewport explicitly before testing horizontal input.
                if (Bar.Orientation == Orientation.Horizontal)
                    this.SidebarScroll.ScrollToVerticalOffset(this.SidebarScroll.VerticalOffset
                        + Bar.TranslatePoint(new Point(), this.SidebarScroll).Y - this.SidebarScroll.ViewportHeight / 2);
                await Settle();
                var Track = (Track)Bar.Template.FindName("PART_Track", Bar);
                var Thumb = Track.Thumb!;
                var Grip = (Border)Thumb.Template.FindName("Grip", Thumb);
                var Vertical = Bar.Orientation == Orientation.Vertical;
                Check((Vertical ? Track.ActualWidth : Track.ActualHeight) == 24
                    && (Vertical ? Thumb.ActualWidth : Thumb.ActualHeight) == 24
                    && (Vertical ? Grip.ActualWidth : Grip.ActualHeight) == 10,
                    "Scrollbar hit area doubles while the visible grip keeps its original thickness: " + Bar.Orientation);
                foreach (var Edge in new[] { 2.0, 22.0 })
                {
                    var Point = Vertical ? new Point(Edge, Thumb.ActualHeight / 2) : new Point(Thumb.ActualWidth / 2, Edge);
                    var PanelPoint = Thumb.TranslatePoint(Point, this.SidebarPanel);
                    var Hit = this.SidebarPanel.InputHitTest(PanelPoint) as DependencyObject;
                    Check(Hit is not null && (ReferenceEquals(Hit, Thumb) || Thumb.IsAncestorOf(Hit)),
                        $"Transparent thumb edges receive input, including the expanded area: {Bar.Orientation} {Edge}; point={PanelPoint}; hit={Hit}; sidebar={this.SidebarPanel.RenderSize}; offset={this.SidebarScroll.VerticalOffset}; bar={Bar.TranslatePoint(new Point(), this.SidebarPanel)}");
                }
                await CheckSidebarHoverReveal(Bar, Track);
                var InitialValue = Bar.Value;
                Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                Check(Track.Opacity == 1, "The sidebar scrollbar is visible while its thumb is dragged: " + Bar.Orientation);
                Thumb.RaiseEvent(new DragDeltaEventArgs(Vertical ? 0 : 8, Vertical ? 8 : 0) { RoutedEvent = Thumb.DragDeltaEvent });
                Thumb.RaiseEvent(new DragCompletedEventArgs(Vertical ? 0 : 8, Vertical ? 8 : 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                await Settle();
                Check(Bar.Value > InitialValue, "Dragging the expanded thumb scrolls the sidebar: " + Bar.Orientation);
                await CheckScrollReveal(Vertical ? this.SidebarScroll : Inner, Bar, Track);
            }
            this.SidebarScroll.ScrollToTop();
            Inner.ScrollToLeftEnd();
            await Settle();
            var InitialVertical = this.SidebarScroll.VerticalOffset;
            Check(ScrollInteraction.HandleWheel(FindVisualChild<ScrollContentPresenter>(Inner), -120, ModifierKeys.Shift),
                "The fixed Shift wheel gesture is recognized over the tree's own scroll content");
            await Settle();
            Check(Inner.HorizontalOffset > 0 && this.SidebarScroll.VerticalOffset == InitialVertical,
                "Shift wheel down moves the hovered tree right without scrolling the outer sidebar vertically");
            ScrollInteraction.HandleWheel(FindVisualChild<ScrollContentPresenter>(Inner), 120, ModifierKeys.Shift);
            await Settle();
            Check(Inner.HorizontalOffset == 0, "Shift wheel up moves the hovered tree left");
            // Exercise the actual native-message route at a tree row without moving
            // the user's pointer or injecting input into any desktop window.
            var NativeRow = (TreeViewItem)this.FolderTree.ItemContainerGenerator.ContainerFromItem(Root);
            NativeRow.BringIntoView(new Rect(0, 0, 100, 24));
            await Settle();
            var Screen = NativeRow.PointToScreen(new Point(70, 10));
            var PackedPoint = (IntPtr)unchecked((int)(((uint)(ushort)(short)Screen.Y << 16) | (ushort)(short)Screen.X));
            bool NativeWheel(int Delta)
            {
                var Handled = false;
                this.HostWindow.BrowserAppCommand(IntPtr.Zero, 0x020E,
                    (IntPtr)unchecked((int)((uint)(ushort)(short)Delta << 16)), PackedPoint, ref Handled);
                return Handled;
            }
            var NativeVertical = this.SidebarScroll.VerticalOffset;
            Check(NativeWheel(60), "The native horizontal wheel message routes to the tree under its screen coordinates");
            await Settle();
            Check(Inner.HorizontalOffset > 0 && Inner.HorizontalOffset <= 24 && this.SidebarScroll.VerticalOffset == NativeVertical,
                "A partial native wheel delta moves the hovered tree right without vertical scrolling");
            Check(NativeWheel(-60), "Native horizontal wheel input also routes left");
            await Settle();
            Check(Inner.HorizontalOffset == 0 && !NativeWheel(0), "Native horizontal wheel input returns to the left edge and ignores zero deltas");
            this.SidebarScroll.ScrollToTop();
            await Settle();
            ScrollInteraction.HandleWheel(Inner, 120, ModifierKeys.Shift);
            await Settle();
            Check(Inner.HorizontalOffset == 0 && this.SidebarScroll.VerticalOffset == InitialVertical,
                "Shift wheel clamps at the horizontal edge without a vertical fallback");
            Check(!ScrollInteraction.HandleWheel(Inner, -120, ModifierKeys.None)
                && !ScrollInteraction.HandleWheel(Inner, -120, ModifierKeys.Control)
                && !ScrollInteraction.HandleWheel(Inner, -120, ModifierKeys.Control | ModifierKeys.Shift)
                && !ScrollInteraction.HandleWheel(Inner, 0, ModifierKeys.Shift)
                && !KeyboardShortcut.TryNormalize("Shift+WheelUp", true, out _)
                && !KeyboardShortcut.TryNormalize("Shift+WheelDown", true, out _),
                "Ordinary wheel and control gestures remain unchanged and Shift wheel cannot be rebound as a shortcut");
            var FileViewer = FindVisualChild<ScrollViewer>(this.FileGrid)!;
            var NameColumn = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Name));
            var InitialWidth = NameColumn.Width;
            try
            {
                NameColumn.Width = new DataGridLength(2000);
                await Settle();
                FileViewer.ScrollToLeftEnd();
                await Settle();
                var MainBar = Bars(this.FileGrid).Single(Bar => Bar.Orientation == Orientation.Horizontal && Bar.IsVisible);
                CheckPersistentScrollBar(MainBar, (Track)MainBar.Template.FindName("PART_Track", MainBar));
                ScrollInteraction.HandleWheel(FindVisualChild<ScrollContentPresenter>(FileViewer), -60, ModifierKeys.Shift);
                await Settle();
                Check(FileViewer.HorizontalOffset > 0 && FileViewer.HorizontalOffset <= 24,
                    "Shift wheel also scrolls the main file list and supports partial wheel deltas");
                FileViewer.ScrollToRightEnd();
                await Settle();
                ScrollInteraction.HandleWheel(FileViewer, -120, ModifierKeys.Shift);
                await Settle();
                Check(FileViewer.HorizontalOffset == FileViewer.ScrollableWidth, "Shift wheel clamps to the right edge of the file list");
            }
            finally { NameColumn.Width = InitialWidth; FileViewer.ScrollToLeftEnd(); await Settle(); }
            var Container = (TreeViewItem)this.FolderTree.ItemContainerGenerator.ContainerFromItem(Root);
            var First = (TreeViewItem)Container.ItemContainerGenerator.ContainerFromItem(Root.Children[0]);
            var Last = (TreeViewItem)Container.ItemContainerGenerator.ContainerFromItem(Root.Children[^1]);
            if (SystemParameters.WheelScrollLines != 0)
            {
                First.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                await Settle();
                var Scrolled = this.SidebarScroll.VerticalOffset;
                First.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                await Settle();
                Check(Scrolled > 0 && this.SidebarScroll.VerticalOffset < Scrolled && Inner.VerticalOffset == 0,
                    "Wheel input over tree nodes scrolls the outer sidebar in both directions");
            }
            this.SidebarScroll.ScrollToEnd();
            await Settle();
            var BottomOffset = this.SidebarScroll.VerticalOffset;
            Check(this.StickySidebar.IsVisible && ReferenceEquals(this.StickySectionTarget, this.DirectoryTreeSection)
                && ReferenceEquals(this.StickySidebarRoot.DataContext, Root) && this.StickySidebarRoot.IsVisible,
                "Deep scrolling pins the owning category and outermost folder above the visible descendants");
            Check(this.StickySidebarSection.TranslatePoint(new Point(), this.SidebarScroll).Y == 0
                && this.StickySidebarRoot.TranslatePoint(new Point(), this.SidebarScroll).Y >= this.StickySidebarSection.ActualHeight,
                "Pinned category and root remain ordered at the top of the sidebar viewport");
            var LastTop = Last.TranslatePoint(new Point(), this.SidebarScroll).Y;
            Check(LastTop >= 0 && LastTop + 20 <= this.SidebarScroll.ViewportHeight + 1,
                "The outer sidebar scrollbar reaches the final folder without clipping it");
            var Visible = this.VisibleVersionNodes().ToArray();
            Check(Visible.Contains(Root.Children[^1]) && !Visible.Contains(Root.Children[0]) && Visible.Length < Root.Children.Count,
                "Version status refreshes use the outer viewport and exclude offscreen tree nodes");
            if (AppMain.ScreenshotPath is { } Screenshot)
            {
                var Bitmap = new RenderTargetBitmap((int)this.SidebarPanel.ActualWidth, (int)this.SidebarPanel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(this.SidebarPanel);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Screenshot)!, "SidebarSingleScroll.png"));
                Encoder.Save(Stream);
            }
            First.BringIntoView(new Rect(0, 0, 100, 24));
            await Settle();
            var FirstTop = First.TranslatePoint(new Point(), this.SidebarScroll).Y;
            Check(this.SidebarScroll.VerticalOffset < BottomOffset && FirstTop >= -1 && FirstTop + 20 <= this.SidebarScroll.ViewportHeight + 1,
                "Bringing a folder into view scrolls the outer sidebar to its header");
            this.SidebarScroll.ScrollToEnd();
            await Settle();
            var OriginalPath = this.CurrentPath;
            var Menu = this.StickySidebarRoot.ContextMenu;
            Menu.PlacementTarget = this.StickySidebarRoot;
            Menu.IsOpen = true;
            await Settle();
            Menu.Items.OfType<MenuItem>().Single(Item => Item.Tag?.ToString() == "CollapseAllSubfolders")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Settle();
            Check(!Root.IsExpanded && Root.Children.All(Child => !Child.IsExpanded) && this.CurrentPath == OriginalPath,
                "The pinned root context menu collapses its entire loaded branch without navigating");
            this.SidebarScroll.ScrollToTop();
            await Settle();
            Check(!this.StickySidebar.IsVisible, "Pinned headers disappear when the original category header is visible");
            var Category = new SidebarCategory { Name = "Pinned Category 中文" };
            var Bookmark = new CategoryFolder { FullPath = Fixture };
            Bookmark.Node.Children.Clear();
            foreach (var Child in Root.Children) Bookmark.Node.Children.Add(Child);
            Bookmark.Node.IsLoaded = true;
            Bookmark.Node.IsExpanded = true;
            Category.Folders.Add(Bookmark);
            this.CustomCategories.Add(Category);
            this.SidebarSections.Add(Category);
            try
            {
                await Settle();
                this.SidebarScroll.ScrollToEnd();
                await Settle();
                Check(this.StickySidebar.IsVisible && this.StickySidebarSection.Header?.ToString() == Category.Name
                    && ReferenceEquals(this.StickySidebarRoot.DataContext, Bookmark.Node),
                    "Custom categories pin their own header and independent bookmark root");
                var CategoryMenu = this.StickySidebarSection.ContextMenu;
                var StayOpen = CategoryMenu.StaysOpen;
                try
                {
                    CategoryMenu.StaysOpen = true;
                    CategoryMenu.PlacementTarget = this.StickySidebarSection;
                    CategoryMenu.IsOpen = true;
                    await Settle();
                    Check(CategoryMenu.Items.OfType<MenuItem>().Single(Item => Item.Tag?.ToString() == "CollapseCategoryFolders").IsEnabled,
                        "Pinned category menus resolve the real category and enable collapse");
                    CategoryMenu.Items.OfType<MenuItem>().Single(Item => Item.Tag?.ToString() == "CollapseCategoryFolders")
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    await Settle();
                    Check(!Bookmark.Node.IsExpanded && this.CurrentPath == OriginalPath,
                        "Pinned category collapse acts on its real roots without changing the current folder");
                }
                finally { CategoryMenu.IsOpen = false; CategoryMenu.StaysOpen = StayOpen; }
            }
            finally
            {
                this.SidebarSections.Remove(Category);
                this.CustomCategories.Remove(Category);
                await Settle();
            }
        }
        finally
        {
            this.Roots.Remove(Root);
            this.SidebarScroll.ScrollToVerticalOffset(InitialOffset);
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != FixtureId
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned sidebar scrolling fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
