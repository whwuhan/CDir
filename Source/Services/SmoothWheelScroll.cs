using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CDir.Services;

// One short, retargetable motion per viewport. No timers remain subscribed
// after scrolling ends; keyboard, dragging and programmatic scrolling stay direct.
internal static class SmoothWheelScroll
{
    internal static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(110);
    private static readonly ConditionalWeakTable<ScrollViewer, Motion> Motions = new();
    internal static bool IsActive(ScrollViewer Viewer) => Motions.TryGetValue(Viewer, out var Motion) && Motion.Active;
    internal static void Cancel(ScrollViewer? Viewer) { if (Viewer is not null && Motions.TryGetValue(Viewer, out var Motion)) Motion.Stop(); }

    internal static bool Wheel(ScrollViewer Viewer, int Delta, double PixelsPerUnit = 1, bool Horizontal = false, bool LogicalRows = false)
    {
        if (Delta == 0 || !Viewer.IsVisible || !double.IsFinite(PixelsPerUnit) || PixelsPerUnit <= 0) return false;
        var Maximum = Horizontal ? Viewer.ScrollableWidth : Viewer.ScrollableHeight;
        if (Maximum <= 0) return false;
        var Lines = SystemParameters.WheelScrollLines;
        if (Lines == 0) return true;
        // Honor the system's line/page setting without making a single large
        // thumbnail row count as hundreds of pixels per line.
        var Distance = Lines < 0 ? (Horizontal ? Viewer.ViewportWidth : Viewer.ViewportHeight)
            : Lines * (PixelsPerUnit > 1 ? 1 : 16d);
        Motions.GetValue(Viewer, Target => new Motion(Target)).Add(-Delta / 120d * Distance, PixelsPerUnit, Horizontal, LogicalRows);
        return true;
    }

    private sealed class Motion
    {
        private readonly ScrollViewer Viewer;
        private readonly Stopwatch Clock = new();
        private double From, Target, Requested, Units;
        private bool Horizontal;
        private bool LogicalRows;
        private TimeSpan LastFrame = TimeSpan.MinValue;
        internal bool Active { get; private set; }
        internal Motion(ScrollViewer Viewer)
        {
            this.Viewer = Viewer;
            Viewer.Unloaded += (_, _) => this.Stop();
            Viewer.IsVisibleChanged += (_, _) => { if (!Viewer.IsVisible) this.Stop(); };
            Viewer.PreviewMouseDown += (_, _) => this.Stop();
            Viewer.PreviewKeyDown += (_, _) => this.Stop();
            Viewer.ScrollChanged += (_, E) =>
            {
                if (!this.Active || !ReferenceEquals(E.OriginalSource, Viewer)) return;
                if (!this.LogicalRows && (E.ExtentHeightChange != 0 || E.ExtentWidthChange != 0 || E.ViewportHeightChange != 0 || E.ViewportWidthChange != 0)
                    || Math.Abs(this.Offset - this.Requested) > (this.LogicalRows ? 1.01 : .02)) this.Stop();
            };
        }
        private double Offset => this.Horizontal ? this.Viewer.HorizontalOffset : this.Viewer.VerticalOffset;
        private double Maximum => this.Horizontal ? this.Viewer.ScrollableWidth : this.Viewer.ScrollableHeight;
        internal void Add(double Distance, double Units, bool Horizontal, bool LogicalRows)
        {
            if (this.Active && (this.Horizontal != Horizontal || this.Units != Units)) this.Stop();
            this.Horizontal = Horizontal; this.Units = Units; this.LogicalRows = LogicalRows;
            var Current = this.Offset;
            var Continuing = this.Active && Math.Sign(Distance) == Math.Sign(this.Target - Current);
            this.Target = Math.Clamp((Continuing ? this.Target : Current) + Distance, 0, this.Maximum);
            this.From = Current;
            if (Math.Abs(this.Target - Current) < .001)
            {
                // An opposite detent can arrive before WPF has applied the
                // previous command. Replace that queued command at the edge.
                this.Apply(1); this.Stop(); return;
            }
            this.Clock.Restart();
            if (!this.Active)
            {
                this.Active = true; this.LastFrame = TimeSpan.MinValue;
                CompositionTarget.Rendering += this.Render;
            }
            // Give the first wheel event immediate feedback, then finish over
            // a few frames. Reversing direction discards the old destination.
            this.Apply(.2);
        }
        private void Apply(double Progress)
        {
            this.Requested = Math.Clamp(this.From + (this.Target - this.From) * Progress, 0, this.Maximum);
            if (this.Horizontal) this.Viewer.ScrollToHorizontalOffset(this.Requested);
            else this.Viewer.ScrollToVerticalOffset(this.Requested);
        }
        private void Render(object? Sender, EventArgs E)
        {
            if (!this.Active) return;
            if (!this.Viewer.IsVisible || this.Viewer.Dispatcher.HasShutdownStarted) { this.Stop(); return; }
            if (E is RenderingEventArgs Frame)
            {
                if (Frame.RenderingTime == this.LastFrame) return;
                this.LastFrame = Frame.RenderingTime;
            }
            var Time = Math.Clamp(this.Clock.Elapsed.TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);
            // Begin at the immediate first increment; never move backwards on
            // the next frame or queue an animation for every physical detent.
            this.Apply(.2 + .8 * (1 - Math.Pow(1 - Time, 3)));
            if (Time >= 1) this.Stop();
        }
        internal void Stop()
        {
            if (!this.Active) return;
            this.Active = false; this.Clock.Stop();
            CompositionTarget.Rendering -= this.Render;
        }
    }
}
