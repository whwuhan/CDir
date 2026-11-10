using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace CDir.Services;

internal static class ScrollInteraction
{
    internal static void Initialize()
    {
        ScrollBarReveal.Initialize();
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseWheelEvent,
        new MouseWheelEventHandler((_, E) =>
        {
            if (!E.Handled && HandleWheel(E.OriginalSource as DependencyObject, E.Delta, Keyboard.Modifiers)) E.Handled = true;
        }));
    }

    // This is a fixed mouse gesture, independent of user-configurable command bindings.
    internal static bool HandleWheel(DependencyObject? Source, int Delta, ModifierKeys Modifiers)
    {
        if (Modifiers != ModifierKeys.Shift || Delta == 0) return false;
        HandleHorizontalWheel(Source, -Delta);
        // At an edge (or without horizontal overflow), Shift must not fall back to vertical scrolling.
        return true;
    }

    // Native horizontal wheel deltas are positive to the right, opposite to Shift+wheel.
    internal static bool HandleHorizontalWheel(DependencyObject? Source, int Delta)
    {
        if (Delta == 0) return false;
        for (var Element = Source; Element is not null; Element = Parent(Element))
        {
            if (Element is ScrollViewer Viewer && Viewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled
                && Viewer.ScrollableWidth > 0)
            {
                Viewer.ScrollToHorizontalOffset(Viewer.HorizontalOffset + Delta / 120.0 * 48);
                return true;
            }
        }
        if (Source is not null && CDir.Controls.SidebarPathColumns.GetReferenceScroll(Source) is { ScrollableWidth: > 0 } Reference)
        {
            Reference.ScrollToHorizontalOffset(Reference.HorizontalOffset + Delta / 120.0 * 48);
            return true;
        }
        return false;
    }

    private static DependencyObject? Parent(DependencyObject Element) => Element is Visual or Visual3D
        ? VisualTreeHelper.GetParent(Element) : Element is FrameworkContentElement Content ? Content.Parent : LogicalTreeHelper.GetParent(Element);
}
