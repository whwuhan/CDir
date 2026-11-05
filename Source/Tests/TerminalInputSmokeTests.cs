using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using CDir.Controls;
using VirtualTerminal;
using VirtualTerminal.Rendering;
using VirtualTerminal.Session;

namespace CDir.UI;

public partial class ExplorerTab
{
    private sealed class CursorTestSession : TerminalSession
    {
        // A fixed grid isolates pixel geometry from backend reflow and preserves
        // the exact row/column pair that the old renderer incorrectly cached.
        public override void Resize(ushort Columns, ushort Rows, bool PushScrollback = true) { }
        protected override void Dispose(bool Disposing) { }
    }

    private async Task RunTerminalInputSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Terminal input UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task<bool> WaitForContent(string Path, string Expected)
        {
            var Deadline = Environment.TickCount64 + 10000;
            while (Environment.TickCount64 < Deadline)
            {
                try { if (File.Exists(Path) && File.ReadAllText(Path) == Expected) return true; }
                catch (IOException Error) when ((Error.HResult & 0xFFFF) is 32 or 33) { }
                await Task.Delay(40);
            }
            return false;
        }
        // Keep the cursor in exactly the same grid cell across font changes: merely
        // checking that the font property changed would miss the original regression.
        using (var Dummy = new CursorTestSession())
        using (var Preview = new EmbeddedTerminalControl { Session = Dummy, FontFamily = new FontFamily("Consolas"), FontSize = 15 })
        {
            try
            {
            Preview.Measure(new Size(900, 300));
            Preview.Arrange(new Rect(0, 0, 900, 300));
            Preview.UpdateLayout();
            Dummy.Write(Encoding.UTF8.GetBytes("\x1b[4;12H"));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Preview.InvalidateVisual();
            Preview.UpdateLayout();
            var Position = ((TerminalDecoder)Dummy.Decoder).CursorPosition;
            Rect Render()
            {
                var Bitmap = new RenderTargetBitmap(900, 300, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(Preview);
                return ((EmbeddedTerminalCursorRenderer)Preview.CursorRenderer).CellBounds;
            }
            var Original = Render();
            foreach (var Size in new[] { 30.0, 11.0, 24.0, 15.0 })
            {
                Preview.FontSize = Size;
                Preview.UpdateLayout();
                var Bounds = Render();
                using var Glyphs = new GlyphCache();
                Glyphs.Configure("Consolas", Size, Preview.LineHeight, null);
                Check(((TerminalDecoder)Dummy.Decoder).CursorPosition == Position
                    && Math.Abs(Bounds.X - Position.X * Glyphs.CellWidth) < 0.01
                    && Math.Abs(Bounds.Y - Position.Y * Glyphs.CellHeight) < 0.01
                    && Math.Abs(Bounds.Width - Glyphs.CellWidth) < 0.01 && Math.Abs(Bounds.Height - Glyphs.CellHeight) < 0.01,
                    "Font size " + Size + " recomputes cursor pixel bounds while its row and column stay unchanged"
                    + " (cursor=" + ((TerminalDecoder)Dummy.Decoder).CursorPosition + ", expected=" + Position
                    + ", bounds=" + Bounds + ", metrics=" + Glyphs.CellWidth + "x" + Glyphs.CellHeight + ")");
                var EditorPosition = Preview.InputEditor.TranslatePoint(new Point(), Preview);
                Check(Math.Abs(EditorPosition.X - Preview.CaretBounds.Left) < 0.01
                    && Math.Abs(EditorPosition.Y - Preview.CaretBounds.Top) < 0.01,
                    "Font size " + Size + " keeps the native IME editor anchored at the new cursor position");
            }
            Check(Render() == Original, "Zooming back restores the original cursor position and size without typing a character");
            }
            finally { Preview.Session = null; }
        }
        var Surface = (EmbeddedTerminalControl)this.TerminalSurface!;
        var Editor = Surface.InputEditor;
        var Session = this.TerminalSession!;
        FocusManager.SetFocusedElement(this, Surface);
        Check(Editor is { IsVisible: true, IsReadOnly: false, Focusable: true }
            && InputMethod.GetIsInputMethodEnabled(Editor) && FocusManager.GetFocusedElement(Editor) == Editor
            && Surface.IsFocused, "The terminal has a native IME text store while retaining terminal focus and shortcut scope");
        string Screen() { lock (Session.Buffer.SyncRoot) return Session.Buffer.GetAllText(); }
        void TextEvent(RoutedEvent Event, string Text)
        {
            Editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                new TextComposition(InputManager.Current, Editor, Text)) { RoutedEvent = Event });
        }
        var Result = Path.Combine(AppMain.SmokeRoot!, "Terminal-中文-输入.txt");
        var Quoted = Result.Replace("'", "''", StringComparison.Ordinal);
        var Command = "[IO.File]::WriteAllText('" + Quoted + "','中文输入确认一次')";
        var Before = Screen();
        TextEvent(TextCompositionManager.PreviewTextInputStartEvent, "zhongwen");
        TextEvent(TextCompositionManager.PreviewTextInputUpdateEvent, "中文");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(Surface.IsComposing && Editor.Opacity == 1 && Screen() == Before && !File.Exists(Result),
            "IME start and update remain local composition without sending pinyin or partial Chinese to the shell");
        var Enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Surface), 0, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Editor.RaiseEvent(Enter);
        Check(!Enter.Handled && Screen() == Before && !File.Exists(Result),
            "Enter during IME composition is left to the input method instead of executing a shell command");
        var CompositionSpace = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Surface), 0, Key.Space)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Editor.RaiseEvent(CompositionSpace);
        Check(!CompositionSpace.Handled && Surface.IsComposing && Screen() == Before,
            "Space during IME composition stays with the input method without reaching the shell");
        TextEvent(TextCompositionManager.PreviewTextInputEvent, Command);
        Check(!Surface.IsComposing && Editor.Opacity == 0,
            "IME commit immediately hides the local text before PowerShell paints its colored echo");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(!Surface.IsComposing && Editor.Text == "" && Editor.Opacity == 0,
            "Committed text clears the native editor after its composition has completed");
        Surface.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Surface), 0, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Check(await WaitForContent(Result, "中文输入确认一次"),
            "A complete IME commit reaches the real PowerShell session once with Chinese filenames and text intact");
        await Task.Delay(100);
        Before = Screen();
        TextEvent(TextCompositionManager.PreviewTextInputStartEvent, "cancel");
        TextEvent(TextCompositionManager.PreviewTextInputUpdateEvent, "取消");
        TextEvent(TextCompositionManager.PreviewTextInputEvent, "");
        Check(!Surface.IsComposing && Editor.Opacity == 0,
            "Canceling composition immediately hides the native editor before another render");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(!Surface.IsComposing && Screen() == Before && Editor.Text.Length == 0,
            "Canceling IME composition leaves the shell input and output unchanged");

        var SpaceResult = Path.Combine(AppMain.SmokeRoot!, "Terminal spaces 中文.txt");
        const string SpacedText = "  left  中文 right  ";
        var SpaceCommand = "[IO.File]::WriteAllText('" + SpaceResult.Replace("'", "''", StringComparison.Ordinal)
            + "','" + SpacedText + "')";
        foreach (var Character in SpaceCommand)
        {
            if (Character != ' ') TextEvent(TextCompositionManager.PreviewTextInputEvent, Character.ToString());
            else
            {
                // Real Space arrives as a key, not a synthetic TextInput event.
                var Space = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Surface), 0, Key.Space)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                Editor.RaiseEvent(Space);
                Check(Space.Handled, "A physical Space key is forwarded from the terminal's native input editor");
            }
        }
        Editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Surface), 0, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Check(await WaitForContent(SpaceResult, SpacedText),
            "Physical Space keys preserve consecutive leading and trailing spaces and Unicode paths in the real shell");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(Editor.Text.Length == 0 && Editor.Opacity == 0,
            "Space input leaves no hidden editor text to leak into the next command");
        using (var Glyphs = new GlyphCache())
        {
            var Fallbacks = ((TerminalDecoder)Session.Decoder).Options.FontFallback;
            Glyphs.Configure(Surface.FontFamily.Source, Surface.FontSize, Surface.LineHeight, Fallbacks);
            Check(Fallbacks is { Length: > 0 } && "中文日本語한글éЖ😀".EnumerateRunes().All(Rune => Glyphs.GetGlyph(Rune).Glyph != 0),
                "The terminal renderer resolves real glyphs for Chinese, Japanese, Korean, accented Latin, Cyrillic and emoji");
        }
        const string UnicodeText = "中文 日本語 한글 café Ж";
        var UnicodeResult = Path.Combine(AppMain.SmokeRoot!, "IME multilingual.txt");
        var UnicodeCommand = "[IO.File]::WriteAllText('" + UnicodeResult.Replace("'", "''", StringComparison.Ordinal) + "','" + UnicodeText + "')";
        TextEvent(TextCompositionManager.PreviewTextInputStartEvent, "input");
        TextEvent(TextCompositionManager.PreviewTextInputUpdateEvent, UnicodeText);
        foreach (var CompositionKey in new[] { Key.ImeProcessed, Key.Back, Key.Left, Key.Right, Key.Enter })
        {
            var Input = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(Editor), 0, CompositionKey)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Editor.RaiseEvent(Input);
            Check(!Input.Handled && Surface.IsComposing && !File.Exists(UnicodeResult),
                "The native IME retains composition keys without executing or editing shell input: " + CompositionKey);
        }
        TextEvent(TextCompositionManager.PreviewTextInputEvent, UnicodeCommand);
        Editor.RaiseEvent(new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(Editor), 0, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Check(await WaitForContent(UnicodeResult, UnicodeText),
            "A multilingual IME commit reaches the real PowerShell once with Chinese, Japanese, Korean and accented text intact");
        this.CaptureAddressBreadcrumbPreview("TerminalMultilingualPreview.png");
    }
}
