using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VirtualTerminal;
using VirtualTerminal.Rendering;

namespace CDir.Controls;

internal sealed partial class EmbeddedTerminalControl
{
    internal TextBox InputEditor { get; private set; } = null!;
    internal bool IsComposing { get; private set; }
    private Size CellSize;
    private (string Font, double Size, double Height) Metrics;
    private bool InputCleanupQueued;
    private bool SuppressEditorInput;
    private bool InputFocusQueued;
    private readonly TranslateTransform InputPosition = new();
    internal static readonly string[] UnicodeFontFallback = ["Microsoft YaHei UI", "Yu Gothic UI", "Malgun Gothic", "Segoe UI", "Segoe UI Symbol", "Segoe UI Emoji"];

    internal static readonly string[] BoldUnicodeFontFallback = ["Microsoft YaHei UI Bold", "Yu Gothic UI Bold", "Malgun Gothic Bold", "Segoe UI Bold", "Segoe UI Symbol", "Segoe UI Emoji"];

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs E)
    {
        base.OnPropertyChanged(E);
        // The decoder and renderer share the control's options. Install real glyph
        // fallbacks before the host applies its terminal font and configures the renderer.
        if (E.Property == SessionProperty && this.Session?.Decoder is TerminalDecoder Decoder)
            Decoder.Options.FontFallback = UnicodeFontFallback;
    }

    internal Rect CaretBounds
    {
        get
        {
            var Current = (this.FontFamily.Source, this.FontSize, this.LineHeight);
            if (Current != this.Metrics || this.CellSize.Width == 0)
            {
                using var Glyphs = new GlyphCache();
                Glyphs.Configure(Current.Source, Current.FontSize, Current.LineHeight, null);
                this.CellSize = new Size(Glyphs.CellWidth, Glyphs.CellHeight);
                this.Metrics = Current;
            }
            var Cursor = this.Session?.Decoder is TerminalDecoder Decoder ? Decoder.CursorPosition : default;
            return new Rect(this.Padding.Left + Cursor.X * this.CellSize.Width,
                this.Padding.Top + Cursor.Y * this.CellSize.Height, this.CellSize.Width, this.CellSize.Height);
        }
    }

    private void InitializeInputEditor()
    {
        InputMethod.SetIsInputMethodEnabled(this, true);
        // A real text editor supplies WPF's native IME text store. Opacity only hides
        // committed input; the editor stays visible, enabled and positioned for TSF.
        var Root = new FrameworkElementFactory(typeof(Canvas));
        var Editor = new FrameworkElementFactory(typeof(TextBox), "TerminalInputEditor");
        Root.AppendChild(Editor);
        this.Template = new ControlTemplate(typeof(EmbeddedTerminalControl)) { VisualTree = Root };
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        this.InputEditor = (TextBox)this.GetTemplateChild("TerminalInputEditor");
        this.InputEditor.BorderThickness = new Thickness(0);
        this.InputEditor.Padding = new Thickness(0);
        this.InputEditor.CaretBrush = Brushes.Transparent;
        this.InputEditor.Background = Brushes.Transparent;
        this.InputEditor.Opacity = 0;
        this.InputEditor.IsHitTestVisible = false;
        this.InputEditor.IsTabStop = false;
        this.InputEditor.RenderTransform = this.InputPosition;
        this.InputEditor.ContextMenu = new ContextMenu();
        // Keep the IME host independent of the application's light focus fills.
        // Its text is drawn over the terminal's user-selected background.
        this.InputEditor.FocusVisualStyle = null;
        this.InputEditor.Template = new ControlTemplate(typeof(TextBox))
        {
            VisualTree = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost")
        };
        FocusManager.SetIsFocusScope(this.InputEditor, true);
        InputMethod.SetIsInputMethodEnabled(this.InputEditor, true);
        this.InputEditor.SetBinding(FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
        this.InputEditor.SetBinding(FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
        this.InputEditor.SetBinding(ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        this.InputEditor.AddHandler(TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler(this.CompositionStarted), handledEventsToo: true);
        this.InputEditor.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent,
            new TextCompositionEventHandler(this.CompositionStarted), handledEventsToo: true);
        this.PositionInputEditor();
    }

    private void CompositionStarted(object Sender, TextCompositionEventArgs E)
    {
        if (!this.AllowDirectInput || this.Session is null) return;
        this.IsComposing = true;
        this.InputEditor.Opacity = 1;
        this.ScrollToBottom();
        this.PositionInputEditor();
    }

    private void PositionInputEditor()
    {
        if (this.InputEditor is null) return;
        var Bounds = this.CaretBounds;
        var X = Math.Clamp(Bounds.X, 0, Math.Max(0, this.ActualWidth - Bounds.Width));
        var Y = Math.Clamp(Bounds.Y, 0, Math.Max(0, this.ActualHeight - Bounds.Height));
        // Moving the invisible TSF host should not remeasure/rearrange a TextBox
        // on every echoed Backspace. A render transform still positions IME UI.
        this.InputPosition.X = X;
        this.InputPosition.Y = Y;
        var Width = this.IsComposing ? Math.Max(Bounds.Width, this.ActualWidth - X) : Bounds.Width;
        var Height = Bounds.Height + 4;
        if (this.InputEditor.Width != Width) this.InputEditor.Width = Width;
        if (this.InputEditor.Height != Height) this.InputEditor.Height = Height;
    }

    private void FocusInputEditor()
    {
        if (this.InputEditor is null || !this.AllowDirectInput) return;
        if (this.IsKeyboardFocusWithin || this.IsKeyboardFocused) this.InputEditor.Focus();
        else FocusManager.SetFocusedElement(this.InputEditor, this.InputEditor);
    }

    private void QueueInputEditorFocus()
    {
        if (this.InputFocusQueued) return;
        this.InputFocusQueued = true;
        this.Dispatcher.BeginInvoke(() =>
        {
            this.InputFocusQueued = false;
            // Mouse selection focuses the outer terminal again after its focus event.
            // Redirect once that gesture settles; never take focus back from another control.
            if (this.IsLoaded && this.IsKeyboardFocusWithin && this.AllowDirectInput
                && this.InputEditor is { IsKeyboardFocused: false }) this.InputEditor.Focus();
        }, DispatcherPriority.Input);
    }

    protected override void OnMouseDown(MouseButtonEventArgs E)
    {
        base.OnMouseDown(E);
        this.QueueInputEditorFocus();
    }

    protected override void OnGotFocus(RoutedEventArgs E)
    {
        base.OnGotFocus(E);
        if (ReferenceEquals(E.OriginalSource, this)) this.FocusInputEditor();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs E)
    {
        if (ReferenceEquals(E.OldFocus, this) && ReferenceEquals(E.NewFocus, this.InputEditor)) return;
        base.OnGotKeyboardFocus(E);
        if (ReferenceEquals(E.NewFocus, this)) { this.FocusInputEditor(); this.QueueInputEditorFocus(); }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs E)
    {
        if (ReferenceEquals(E.OldFocus, this) && ReferenceEquals(E.NewFocus, this.InputEditor)) return;
        base.OnLostKeyboardFocus(E);
        if (ReferenceEquals(E.OldFocus, this.InputEditor))
            this.Dispatcher.BeginInvoke(() =>
            {
                if (this.InputEditor is null || this.InputEditor.IsKeyboardFocusWithin) return;
                this.IsComposing = false;
                this.InputEditor.Clear();
                this.InputEditor.Opacity = 0;
            }, DispatcherPriority.Background);
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs E)
    {
        if (this.SuppressEditorInput) { E.Handled = true; return; }
        var FromEditor = false;
        for (var Source = E.OriginalSource as DependencyObject; this.InputEditor is not null && Source is not null;)
        {
            if (ReferenceEquals(Source, this.InputEditor)) { FromEditor = true; break; }
            Source = Source is FrameworkContentElement Content ? Content.Parent : VisualTreeHelper.GetParent(Source);
        }
        if (!FromEditor || this.InputEditor is null)
        {
            base.OnPreviewTextInput(E);
            return;
        }
        // Start/update events never reach the shell. Only the final committed text
        // is forwarded; let TextBox finish its own native composition processing.
        this.IsComposing = false;
        // Render has higher dispatcher priority than the deferred editor cleanup.
        // Hide committed text now so it cannot overlay the shell's colored echo.
        this.InputEditor.Opacity = 0;
        if (this.AllowDirectInput && this.Session is { } Session && E.Text.Length > 0)
        {
            this.ScrollToBottom();
            Session.Write(System.Text.Encoding.UTF8.GetBytes(E.Text));
        }
        if (this.InputCleanupQueued) return;
        this.InputCleanupQueued = true;
        this.Dispatcher.BeginInvoke(() =>
        {
            this.InputCleanupQueued = false;
            if (this.IsComposing || this.InputEditor is null) return;
            this.InputEditor.Clear();
            this.InputEditor.Opacity = 0;
        }, DispatcherPriority.Input);
    }
}
