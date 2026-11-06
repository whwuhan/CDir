using System.Runtime.CompilerServices;
using VirtualTerminal;

namespace CDir.Controls;

internal sealed partial class EmbeddedTerminalControl
{
    // VirtualTerminal.WPF 1.10 exposes relative scrolling but no viewport offset.
    // Read its actual offset so wheel, keyboard, clear and resize stay synchronized.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_scrollOffset")]
    private static extern ref int ReadScrollOffset(TerminalControl Control);

    internal event EventHandler? ScrollStateChanged;
    private (int History, int Rows, int Offset) LastScrollState;

    internal int HistoryLines
    {
        get
        {
            if (this.Session is not { } Session) return 0;
            lock (Session.Buffer.SyncRoot)
                return Session.Buffer.IsAlternate ? 0 : Session.Buffer.ScrollbackCount;
        }
    }

    internal int HistoryOffset => Math.Clamp(ReadScrollOffset(this), 0, this.HistoryLines);

    internal void SetHistoryPosition(double Position)
    {
        var Offset = this.HistoryLines - (int)Math.Round(Math.Clamp(Position, 0, this.HistoryLines));
        base.ScrollToBottom();
        base.ScrollBy(Offset);
        this.UpdateScrollState();
    }

    private void UpdateScrollState()
    {
        var History = this.HistoryLines;
        var Offset = Math.Clamp(ReadScrollOffset(this), 0, History);
        if (Offset != ReadScrollOffset(this))
        {
            base.ScrollToBottom();
            base.ScrollBy(Offset);
        }
        var State = (History, this.Session?.Buffer.Rows ?? 1, Offset);
        if (State == this.LastScrollState) return;
        this.LastScrollState = State;
        this.ScrollStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
