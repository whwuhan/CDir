using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Threading;
using VirtualTerminal;

namespace CDir.Controls;

/// <summary>Keeps accessibility initialization from interrupting terminal focus and input.</summary>
internal sealed partial class EmbeddedTerminalControl : TerminalControl
{
    internal Func<int, ModifierKeys, bool>? FontWheel { get; set; }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs E)
    {
        if (!E.Handled && this.FontWheel?.Invoke(E.Delta, Keyboard.Modifiers) == true)
        {
            E.Handled = true;
            return;
        }
        base.OnPreviewMouseWheel(E);
    }

    internal EmbeddedTerminalControl()
    {
        this.InitializeInputEditor();
        this.InitializeClipboardRouting();
        this.CursorRenderer = new EmbeddedTerminalCursorRenderer(this);
        ScreenUpdated += (_, _) =>
        {
            this.PositionInputEditor();
            if (UIElementAutomationPeer.FromElement(this) is EmbeddedTerminalAutomationPeer Peer) Peer.UpdateText();
        };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new EmbeddedTerminalAutomationPeer(this);

    protected override void OnPreviewKeyDown(KeyEventArgs E)
    {
        // Leave all IME keys to the native text store, including the first processed key.
        if (this.IsComposing || E.Key == Key.ImeProcessed) return;
        var Modifiers = E.KeyboardDevice.Modifiers;
        if (!E.Handled && this.HandlePasteShortcut(E.Key, Modifiers))
        {
            E.Handled = true;
            return;
        }
        if (!E.Handled && this.AllowDirectInput && this.Session is { } SpaceSession
            && E.Key == Key.Space && (Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            // TextBox handles Space/Shift+Space as editing commands without TextInput.
            // Forward the key before the hidden IME editor consumes it locally.
            this.ScrollToBottom();
            SpaceSession.Write([0x20]);
            E.Handled = true;
        }
        if (!E.Handled && this.AllowDirectInput && this.Session is { } Session
            && E.Key == Key.Enter && Modifiers == ModifierKeys.None)
        {
            // ConPTY treats LF as a separate key. The generic encoder's CRLF in newline
            // mode accepts the command and then inserts a new line into the next prompt.
            this.ScrollToBottom();
            Session.Write([0x0D]);
            E.Handled = true;
        }
        base.OnPreviewKeyDown(E);
    }

    protected override void OnRender(System.Windows.Media.DrawingContext Context)
    {
        base.OnRender(Context);
        this.PositionInputEditor();
    }

    internal string AccessibleText()
    {
        if (this.Session is not { } Session) return string.Empty;
        var Buffer = Session.Buffer;
        lock (Buffer.SyncRoot) return Buffer.GetAllText();
    }
}

internal sealed class EmbeddedTerminalAutomationPeer : FrameworkElementAutomationPeer, ITextProvider
{
    private readonly DispatcherTimer TextChangedTimer;
    private EmbeddedTerminalControl Terminal => (EmbeddedTerminalControl)Owner;

    internal EmbeddedTerminalAutomationPeer(EmbeddedTerminalControl Owner) : base(Owner)
    {
        // A screen update already tells us text changed. Do not copy the entire
        // scrollback for every keystroke just to compare accessibility snapshots.
        // Clients obtain a fresh snapshot when they explicitly request a range.
        this.TextChangedTimer = new DispatcherTimer(DispatcherPriority.Background, Owner.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(50) };
        this.TextChangedTimer.Tick += (_, _) =>
        {
            this.TextChangedTimer.Stop();
            if (ListenerExists(AutomationEvents.TextPatternOnTextChanged))
                this.RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
        };
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override string GetClassNameCore() => nameof(TerminalControl);
    protected override string GetAutomationIdCore() => nameof(TerminalControl);
    protected override string GetNameCore() => AutomationProperties.GetName(this.Terminal);
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override bool IsKeyboardFocusableCore() => true;
    public override object? GetPattern(PatternInterface Pattern) => Pattern == PatternInterface.Text ? this : base.GetPattern(Pattern);

    public ITextRangeProvider DocumentRange
    {
        get
        {
            var Snapshot = this.Terminal.AccessibleText();
            return new TerminalTextRangeProvider(this, Snapshot, 0, Snapshot.Length);
        }
    }
    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.None;
    public ITextRangeProvider[] GetSelection() => [];
    public ITextRangeProvider[] GetVisibleRanges() => [this.DocumentRange];
    public ITextRangeProvider? RangeFromChild(IRawElementProviderSimple Child) => null;
    public ITextRangeProvider RangeFromPoint(Point Location) => this.DocumentRange;
    internal IRawElementProviderSimple GetRawProvider() => ProviderFromPeer(this) ?? throw new ElementNotAvailableException();

    internal void UpdateText()
    {
        // Coalesce notifications for screen readers without delaying shell input
        // or rendering. Never create text snapshots when nobody requested them.
        if (!this.TextChangedTimer.IsEnabled && ListenerExists(AutomationEvents.TextPatternOnTextChanged))
            this.TextChangedTimer.Start();
    }
}
