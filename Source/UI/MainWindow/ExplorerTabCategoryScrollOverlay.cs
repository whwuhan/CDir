using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private ScrollViewer? CategoryOverlayTarget;
    private bool SyncingCategoryOverlay;

    // A category's own horizontal scrollbar sits below its last row. When that
    // row is beyond the sidebar viewport, mirror the bar at the viewport bottom.
    private void UpdateCategoryOverlayScrollBar()
    {
        if (this.CategoryOverlayScrollBar is null || this.WindowClosed) return;
        var Target = this.SidebarSections.Select(this.SidebarSectionElement).OfType<Expander>()
            .Where(Section => Section.IsExpanded && Section.IsVisible)
            .Select(this.CategoryHorizontalScroller).OfType<ScrollViewer>()
            .FirstOrDefault(this.CategoryBarBelowViewport);
        var Bar = this.CategoryOverlayScrollBar;
        this.CategoryOverlayTarget = Target;
        if (Target is null)
        {
            Bar.Visibility = Visibility.Collapsed;
            return;
        }
        var Host = (FrameworkElement)Bar.Parent;
        var Left = Target.TranslatePoint(new Point(), Host).X;
        this.SyncingCategoryOverlay = true;
        try
        {
            Bar.Margin = new Thickness(Left, 0, 0, 0);
            Bar.Width = Math.Max(0, Math.Min(Target.ActualWidth, Host.ActualWidth - Left));
            Bar.Maximum = Target.ScrollableWidth;
            Bar.ViewportSize = Target.ViewportWidth;
            Bar.LargeChange = Math.Max(16, Target.ViewportWidth);
            Bar.Value = Target.HorizontalOffset;
            Bar.Visibility = Visibility.Visible;
        }
        finally { this.SyncingCategoryOverlay = false; }
    }

    private ScrollViewer? CategoryHorizontalScroller(Expander Section)
    {
        if (Section.Template?.FindName("CategoryScroll", Section) is ScrollViewer Category
            && Category.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled) return Category;
        // My Computer scrolls inside its TreeView rather than the category host.
        return ReferenceEquals(Section, this.DirectoryTreeSection) ? FindVisualChild<ScrollViewer>(this.FolderTree) : null;
    }

    private bool CategoryBarBelowViewport(ScrollViewer Viewer)
    {
        if (!Viewer.IsVisible || Viewer.ScrollableWidth <= 0 || Viewer.ComputedHorizontalScrollBarVisibility != Visibility.Visible) return false;
        var Top = Viewer.TranslatePoint(new Point(), this.SidebarScroll).Y;
        var Bottom = Viewer.TranslatePoint(new Point(0, Viewer.ActualHeight), this.SidebarScroll).Y;
        return Top < this.SidebarScroll.ViewportHeight && Bottom > this.SidebarScroll.ViewportHeight + .5;
    }

    private void CategoryOverlayScrollValueChanged(object Sender, RoutedPropertyChangedEventArgs<double> E)
    {
        if (this.SyncingCategoryOverlay || this.CategoryOverlayTarget is not { } Target) return;
        Target.ScrollToHorizontalOffset(E.NewValue);
    }

    private void RevealCategoryOverlayOnScroll(ScrollViewer Viewer)
    {
        if (ReferenceEquals(Viewer, this.CategoryOverlayTarget) && this.CategoryOverlayScrollBar.IsVisible)
            ScrollBarReveal.RevealScrollBar(this.CategoryOverlayScrollBar);
    }
}
