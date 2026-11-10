using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace CDir.Services;

public static class ScrollBarReveal
{
    internal static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(150);
    internal static readonly TimeSpan ScrollDisplayDuration = TimeSpan.FromSeconds(1);
    private static bool Initialized;
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(RevealState), typeof(ScrollBarReveal));
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ScrollBarReveal),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, (Element, Args) =>
        {
            if (!(bool)Args.NewValue && Element.GetValue(StateProperty) is RevealState State) State.Reset();
        }));
    public static readonly DependencyProperty RevealOnScrollProperty = DependencyProperty.RegisterAttached(
        "RevealOnScroll", typeof(bool), typeof(ScrollBarReveal),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    private static readonly DependencyPropertyKey IsRevealedKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsRevealed", typeof(bool), typeof(ScrollBarReveal), new PropertyMetadata(false));
    public static readonly DependencyProperty IsRevealedProperty = IsRevealedKey.DependencyProperty;

    public static bool GetIsEnabled(DependencyObject Element) => (bool)Element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject Element, bool Value) => Element.SetValue(IsEnabledProperty, Value);
    public static bool GetIsRevealed(DependencyObject Element) => (bool)Element.GetValue(IsRevealedProperty);
    public static bool GetRevealOnScroll(DependencyObject Element) => (bool)Element.GetValue(RevealOnScrollProperty);
    public static void SetRevealOnScroll(DependencyObject Element, bool Value) => Element.SetValue(RevealOnScrollProperty, Value);

    internal static void Initialize()
    {
        if (Initialized) return;
        Initialized = true;
        EventManager.RegisterClassHandler(typeof(ScrollViewer), ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((Sender, E) =>
            {
                if (ReferenceEquals(Sender, E.OriginalSource)) RevealScroll((ScrollViewer)Sender, E.HorizontalChange != 0, E.VerticalChange != 0);
            }), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), Mouse.MouseWheelEvent,
            new MouseWheelEventHandler((Sender, E) =>
            {
                if (E.Delta != 0) RevealScroll((ScrollViewer)Sender, Keyboard.Modifiers == ModifierKeys.Shift, Keyboard.Modifiers == ModifierKeys.None);
            }), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ScrollBar), Mouse.MouseEnterEvent,
            new MouseEventHandler((Sender, _) => State((ScrollBar)Sender)?.Enter()));
        EventManager.RegisterClassHandler(typeof(ScrollBar), Mouse.MouseLeaveEvent,
            new MouseEventHandler((Sender, _) => State((ScrollBar)Sender)?.Leave()));
        EventManager.RegisterClassHandler(typeof(ScrollBar), Mouse.GotMouseCaptureEvent,
            new MouseEventHandler((Sender, _) => State((ScrollBar)Sender)?.Show()), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ScrollBar), Mouse.LostMouseCaptureEvent,
            new MouseEventHandler((Sender, _) => State((ScrollBar)Sender)?.Release()), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ScrollBar), Thumb.DragStartedEvent,
            new DragStartedEventHandler((Sender, _) => State((ScrollBar)Sender)?.StartDrag()), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ScrollBar), Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((Sender, _) => State((ScrollBar)Sender)?.EndDrag()), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ScrollBar), FrameworkElement.UnloadedEvent,
            new RoutedEventHandler((Sender, _) =>
            {
                if (((ScrollBar)Sender).GetValue(StateProperty) is RevealState State) State.Reset();
            }));
    }

    private static void RevealScroll(ScrollViewer Viewer, bool Horizontal, bool Vertical)
    {
        if (!GetRevealOnScroll(Viewer) || !GetIsEnabled(Viewer)) return;
        if (Horizontal && Viewer.Template?.FindName("PART_HorizontalScrollBar", Viewer) is ScrollBar HorizontalBar) State(HorizontalBar)?.Scroll();
        if (Vertical && Viewer.Template?.FindName("PART_VerticalScrollBar", Viewer) is ScrollBar VerticalBar) State(VerticalBar)?.Scroll();
    }

    private static RevealState? State(ScrollBar Bar)
    {
        // Only explicitly enabled regions participate; ordinary scrollbars stay visible.
        if (!GetIsEnabled(Bar)) return null;
        if (Bar.GetValue(StateProperty) is RevealState Existing) return Existing;
        var Created = new RevealState(Bar);
        Bar.SetValue(StateProperty, Created);
        return Created;
    }

    private sealed class RevealState
    {
        private readonly ScrollBar Bar;
        private readonly DispatcherTimer Timer;
        private readonly DispatcherTimer ScrollTimer;
        private bool Hovering;
        private bool Dragging;
        private bool Scrolling;

        internal RevealState(ScrollBar Bar)
        {
            this.Bar = Bar;
            this.Timer = new DispatcherTimer(DispatcherPriority.Input, Bar.Dispatcher) { Interval = HoverDelay };
            this.Timer.Tick += (_, _) =>
            {
                this.Timer.Stop();
                if (this.Hovering && GetIsEnabled(this.Bar) && this.Bar.IsVisible && this.Bar.IsEnabled) this.Show();
            };
            this.ScrollTimer = new DispatcherTimer(DispatcherPriority.Input, Bar.Dispatcher) { Interval = ScrollDisplayDuration };
            this.ScrollTimer.Tick += (_, _) =>
            {
                this.ScrollTimer.Stop();
                this.Scrolling = false;
                this.Release();
            };
        }

        internal void Enter()
        {
            this.Hovering = true;
            if (this.Dragging || this.Bar.IsMouseCaptureWithin || GetIsRevealed(this.Bar)) { this.Show(); return; }
            this.Timer.Stop();
            this.Timer.Start();
        }

        internal void Leave()
        {
            this.Hovering = false;
            this.Timer.Stop();
            this.Release();
        }

        internal void Show()
        {
            this.Timer.Stop();
            this.Bar.SetValue(IsRevealedKey, true);
        }

        internal void StartDrag() { this.Dragging = true; this.Show(); }
        internal void EndDrag() { this.Dragging = false; this.Release(); }

        internal void Scroll()
        {
            if (!GetIsEnabled(this.Bar) || !GetRevealOnScroll(this.Bar) || !this.Bar.IsVisible || !this.Bar.IsEnabled) return;
            this.Scrolling = true;
            this.Show();
            this.ScrollTimer.Stop();
            this.ScrollTimer.Start();
        }

        internal void Release()
        {
            if (!this.Hovering && !this.Dragging && !this.Scrolling && !this.Bar.IsMouseCaptureWithin)
                this.Bar.SetValue(IsRevealedKey, false);
        }

        internal void Reset()
        {
            this.Timer.Stop();
            this.ScrollTimer.Stop();
            this.Hovering = this.Dragging = this.Scrolling = false;
            this.Bar.SetValue(IsRevealedKey, false);
        }
    }
}
