using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunCategoryOverlayScrollSmokeChecksAsync(List<string> Passed)
    {
        var Id = Guid.NewGuid().ToString("N");
        var Fixture = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CDirCategoryOverlay" + Id)).FullName;
        var Marker = Path.Combine(Fixture, ".CDirCategoryOverlayOwner");
        File.WriteAllText(Marker, Id);
        var InitialPaths = this.Settings.ShowBookmarkPaths;
        var InitialWidth = this.SidebarColumn.Width;
        var InitialOffset = this.SidebarScroll.VerticalOffset;
        SidebarCategory? Category = null;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Category overlay scrollbar check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            this.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            this.UpdateLayout();
        }
        var Bar = this.CategoryOverlayScrollBar;
        Track? BarTrack = null;
        void Hover(bool Inside) => Bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Inside ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent });
        try
        {
            this.SidebarColumn.Width = new GridLength(320);
            this.ChangeShowBookmarkPaths(true);
            var RootPath = Directory.CreateDirectory(Path.Combine(Fixture, "Overlay root " + new string('R', 40))).FullName;
            for (var Index = 0; Index < 90; Index++)
                Directory.CreateDirectory(Path.Combine(RootPath, $"Child {Index:00} " + new string('W', 40)));
            var Bookmark = new CategoryFolder { FullPath = RootPath };
            await this.EnsureChildrenAsync(Bookmark.Node);
            Bookmark.Node.IsExpanded = true;
            Category = new SidebarCategory { Name = "Overlay Category", Folders = [Bookmark] };
            this.CustomCategories.Add(Category); this.SidebarSections.Add(Category);
            await Settle();
            var Section = this.SidebarSectionElement(Category)!;
            var Viewer = (ScrollViewer)Section.Template.FindName("CategoryScroll", Section);
            Check(Viewer.ActualHeight > this.SidebarScroll.ViewportHeight && Viewer.ScrollableWidth > 0,
                "The fixture category is taller than the sidebar and horizontally scrollable");

            this.SidebarScroll.ScrollToVerticalOffset(this.SidebarScroll.VerticalOffset + Section.TranslatePoint(new Point(), this.SidebarScroll).Y);
            await Settle();
            Bar.ApplyTemplate();
            BarTrack = (Track)Bar.Template.FindName("PART_Track", Bar);
            Check(Bar.IsVisible && Bar.ActualHeight > 0 && Math.Abs(Bar.Maximum - Viewer.ScrollableWidth) < 1
                && Math.Abs(Bar.ViewportSize - Viewer.ViewportWidth) < 1 && Math.Abs(Bar.ActualWidth - Viewer.ActualWidth) < 1
                && Math.Abs(Bar.TranslatePoint(new Point(), Viewer).X) < 1,
                "A long category exposes a horizontal scrollbar at the bottom of the sidebar viewport");
            var BarTop = Bar.TranslatePoint(new Point(), this.SidebarPanel).Y;
            var ViewportBottom = this.SidebarScroll.TranslatePoint(new Point(0, this.SidebarScroll.ActualHeight), this.SidebarPanel).Y;
            Check(BarTop >= ViewportBottom - 1 && BarTop + Bar.ActualHeight <= this.SidebarPanel.ActualHeight + 1,
                "The bottom category scrollbar occupies the sidebar margin without covering tree rows");
            var Deadline = Environment.TickCount64 + 5000;
            while (ScrollBarReveal.GetIsRevealed(Bar) && Environment.TickCount64 < Deadline) await Task.Delay(20);
            Check(BarTrack!.Opacity == 0 && !ScrollBarReveal.GetIsRevealed(Bar),
                "The bottom category scrollbar is hidden while idle");

            Hover(true);
            Deadline = Environment.TickCount64 + 5000;
            while (BarTrack!.Opacity == 0 && Environment.TickCount64 < Deadline) await Task.Delay(20);
            Check(BarTrack!.Opacity == 1 && ScrollBarReveal.GetIsRevealed(Bar),
                "Hovering the bottom of a long category reveals its horizontal scrollbar");
            if (AppMain.ScreenshotPath is { } Output)
            {
                var Bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)this.HostWindow.ActualWidth, (int)this.HostWindow.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                Bitmap.Render(this.HostWindow);
                var Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, "CategoryOverlayScrollBar.png"));
                Encoder.Save(Stream);
            }
            Hover(false);
            await Task.Delay(ScrollBarReveal.HoverDelay + TimeSpan.FromMilliseconds(50));
            Check(BarTrack!.Opacity == 0 && !ScrollBarReveal.GetIsRevealed(Bar),
                "Leaving the bottom category scrollbar hides it again");

            var Vertical = this.SidebarScroll.VerticalOffset;
            Bar.Value = Math.Min(60, Bar.Maximum);
            await Settle();
            Check(Math.Abs(Viewer.HorizontalOffset - Bar.Value) < 1 && this.SidebarScroll.VerticalOffset == Vertical,
                "Moving the bottom scrollbar scrolls the whole category horizontally without moving vertically");
            Viewer.ScrollToHorizontalOffset(0);
            await Settle();
            Check(Math.Abs(Bar.Value) < 1, "Category horizontal scrolling updates the bottom scrollbar position");
            ScrollInteraction.HandleWheel(FindVisualChild<Button>(this.SidebarRootHeader(Section, Bookmark.Node)!)!, -120, ModifierKeys.Shift);
            await Settle();
            Check(Viewer.HorizontalOffset > 0 && Math.Abs(Bar.Value - Viewer.HorizontalOffset) < 1 && ScrollBarReveal.GetIsRevealed(Bar),
                "Shift wheel over the long category briefly reveals and synchronizes the bottom scrollbar");

            this.SidebarScroll.ScrollToVerticalOffset(this.SidebarScroll.VerticalOffset
                + Viewer.TranslatePoint(new Point(0, Viewer.ActualHeight), this.SidebarScroll).Y - this.SidebarScroll.ViewportHeight + 8);
            await Settle();
            Check(!Bar.IsVisible, "The mirrored scrollbar yields once the category's own scrollbar is inside the viewport");
        }
        finally
        {
            if (Category is not null) { this.SidebarSections.Remove(Category); this.CustomCategories.Remove(Category); }
            this.ChangeShowBookmarkPaths(InitialPaths);
            this.SidebarColumn.Width = InitialWidth;
            this.SidebarScroll.ScrollToVerticalOffset(InitialOffset);
            await Settle();
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != Id
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unowned category overlay fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
