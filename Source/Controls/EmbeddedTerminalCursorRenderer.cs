using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VirtualTerminal;
using VirtualTerminal.Rendering;

namespace CDir.Controls;

/// <summary>Recomputes pixel bounds even when a font change leaves the cursor in the same cell.</summary>
internal sealed class EmbeddedTerminalCursorRenderer(EmbeddedTerminalControl Owner) : TerminalCursorRenderer
{
    private readonly DispatcherTimer BlinkTimer = new() { Interval = TimeSpan.FromMilliseconds(530) };
    private bool BlinkOn = true;
    internal Rect CellBounds { get; private set; }

    public override void Attach(TerminalControl Control)
    {
        base.Attach(Control);
        this.BlinkTimer.Tick += this.Blink;
        this.BlinkTimer.Start();
    }

    public override void Detach()
    {
        this.BlinkTimer.Stop();
        this.BlinkTimer.Tick -= this.Blink;
        base.Detach();
    }

    private void Blink(object? Sender, EventArgs E)
    {
        this.BlinkOn = !this.BlinkOn;
        if (this.IsCursorVisible) Owner.InvalidateVisual();
    }

    public override void MoveTo(Rect CellRect) { this.BlinkOn = true; }

    public override void Render(DrawingContext Context)
    {
        var Bounds = Owner.CaretBounds;
        Bounds.Offset(-Owner.Padding.Left, -Owner.Padding.Top);
        this.CellBounds = Bounds;
        if (!this.IsCursorVisible || !this.BlinkOn) return;
        Context.DrawRectangle(new SolidColorBrush(Owner.ScreenForeground), null,
            new Rect(Bounds.X, Bounds.Y, Math.Max(2, Bounds.Width * 0.15), Bounds.Height));
    }
}
