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
        for (var Element = Source; Element is not null; Element = Parent(Element))
        {
            if (Element is ScrollViewer Viewer && Viewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled
                && Viewer.ScrollableWidth > 0)
            {
                Viewer.ScrollToHorizontalOffset(Viewer.HorizontalOffset - Delta / 120.0 * 48);
                break;
            }
        }
        // At an edge (or without horizontal overflow), Shift must not fall back to vertical scrolling.
        return true;
    }

    private static DependencyObject? Parent(DependencyObject Element) => Element is Visual or Visual3D
        ? VisualTreeHelper.GetParent(Element) : Element is FrameworkContentElement Content ? Content.Parent : LogicalTreeHelper.GetParent(Element);
}
