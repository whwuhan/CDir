using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Controls;
using VirtualTerminal;
using VirtualTerminal.Session;

namespace CDir.UI;

public partial class ExplorerTab
{
    private sealed class ClipboardTestSession : TerminalSession
    {
        internal List<byte> Sent { get; } = [];
        public override void Write(ReadOnlySpan<byte> Bytes) => this.Sent.AddRange(Bytes.ToArray());
        protected override void Dispose(bool Disposing) { }
    }

    private async Task RunTerminalClipboardSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Terminal clipboard UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        using (var Capture = new ClipboardTestSession())
        using (var Control = new EmbeddedTerminalControl { Session = Capture })
        {
            try
            {
                Control.Measure(new Size(800, 300)); Control.Arrange(new Rect(0, 0, 800, 300)); Control.UpdateLayout();
                Capture.Decoder.Write(Encoding.UTF8.GetBytes("COPY 中文 text"));
                var SelectedText = typeof(TerminalControl).GetMethod("BuildSelectedText",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                string? Selection() => SelectedText.Invoke(Control, null) as string;
                KeyEventArgs Press(Key Key, ModifierKeys Modifiers, UIElement? Source = null, bool Up = false)
                {
                    var Input = new KeyEventArgs(new SmokeKeyboardDevice(Modifiers), PresentationSource.FromVisual(this.HostWindow), 0, Key)
                        { RoutedEvent = Up ? Keyboard.KeyUpEvent : Keyboard.PreviewKeyDownEvent };
                    (Source ?? Control.InputEditor).RaiseEvent(Input);
                    return Input;
                }
                var Copies = new List<string?>();
                // Observe the actual terminal copy command without touching the user's clipboard.
                CommandManager.AddPreviewExecutedHandler(Control, (_, E) =>
                {
                    if (E.Command != TerminalControl.CopyCommand) return;
                    Copies.Add(Selection());
                    E.Handled = true;
                });
                foreach (var Source in new UIElement[] { Control, Control.InputEditor })
                {
                    Control.SelectAll();
                    var Selected = Selection();
                    Check(Selected?.Contains("COPY 中文 text", StringComparison.Ordinal) == true,
                        "The owned terminal fixture has a real Unicode text selection");
                    foreach (var Modifier in new[] { Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt, Key.LWin, Key.RWin })
                    {
                        Capture.Sent.Clear();
                        Press(Modifier, ModifierKeys.Control, Source);
                        Press(Modifier, ModifierKeys.None, Source, Up: true);
                        Check(Selection() == Selected && Capture.Sent.Count == 0,
                            "Modifier down and up preserve terminal selection without sending input: " + Modifier + " from " + Source.GetType().Name);
                    }
                    foreach (var Modifiers in new[] { ModifierKeys.Control, ModifierKeys.Control | ModifierKeys.Shift })
                    {
                        var Before = Copies.Count;
                        Capture.Sent.Clear();
                        var Copy = Press(Key.C, Modifiers, Source);
                        Check(Copy.Handled && Copies.Count == Before + 1 && Copies[^1] == Selected
                            && Capture.Sent.Count == 0 && Selection() == Selected && Control.InputEditor.Text.Length == 0,
                            "The routed copy shortcut copies selected terminal text exactly once without interrupting the shell: " + Modifiers + " from " + Source.GetType().Name);
                    }
                }
                Control.AllowDirectInput = false;
                var BeforeExitedCopy = Copies.Count;
                Press(Key.LeftCtrl, ModifierKeys.Control);
                Press(Key.C, ModifierKeys.Control);
                Check(Copies.Count == BeforeExitedCopy + 1 && Selection() is not null,
                    "Selected terminal output remains copyable after direct input is disabled");
                Control.AllowDirectInput = true;
                Press(Key.Right, ModifierKeys.None);
                Check(Selection() is null, "An ordinary terminal key still clears the text selection");
                Capture.Sent.Clear();
                var Interrupt = Press(Key.C, ModifierKeys.Control);
                Check(Interrupt.Handled && Capture.Sent.SequenceEqual(new byte[] { 0x03 }),
                    "Ctrl+C without a selection still sends exactly one shell interrupt");
                Capture.Sent.Clear();
                var EmptyCopy = Press(Key.C, ModifierKeys.Control | ModifierKeys.Shift);
                Check(EmptyCopy.Handled && Capture.Sent.Count == 0,
                    "Ctrl+Shift+C without a selection does not interrupt the shell");
                const string Payload = "echo '粘贴文本'";
                Control.ReadClipboardText = () => Payload;
                string Sent() => Encoding.UTF8.GetString(Capture.Sent.ToArray());
                foreach (var Gesture in new[] { (Key.V, ModifierKeys.Control), (Key.V, ModifierKeys.Control | ModifierKeys.Shift), (Key.Insert, ModifierKeys.Shift) })
                {
                    Capture.Sent.Clear();
                    Check(Control.HandlePasteShortcut(Gesture.Item1, Gesture.Item2) && Sent() == Payload && Control.InputEditor.Text.Length == 0,
                        "Paste shortcut " + Gesture + " sends text exactly once to the session instead of the invisible editor");
                }
                Capture.Sent.Clear();
                ApplicationCommands.Paste.Execute(null, Control.InputEditor);
                Check(Sent() == Payload && Control.InputEditor.Text == "", "Native text-editor paste commands route to the terminal exactly once");
                Capture.Sent.Clear();
                TerminalControl.PasteCommand.Execute(null, Control);
                Check(Sent() == Payload, "The terminal context-menu paste command uses the same session input route");
                Capture.Sent.Clear();
                var NativePaste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, Payload), false, DataFormats.UnicodeText);
                Control.InputEditor.RaiseEvent(NativePaste);
                Check(NativePaste.CommandCancelled && Sent() == Payload && Control.InputEditor.Text.Length == 0,
                    "Native paste data is redirected before insertion into the transparent IME editor");
                Capture.Sent.Clear();
                Control.PasteText("a\r\nb\nc");
                Check(Sent() == "a\rb\rc", "Plain multiline paste normalizes line endings to single terminal Enter keys");
                Capture.Decoder.Write(Encoding.UTF8.GetBytes("\x1b[?2004h"));
                Capture.Sent.Clear();
                Control.PasteText("中文\r\ntext");
                Check(Sent() == "\x1b[200~中文\rtext\x1b[201~", "Bracketed paste preserves the requested envelope around Unicode multiline text");
                Capture.Sent.Clear();
                Control.AllowDirectInput = false;
                Check(!Control.PasteClipboard() && Capture.Sent.Count == 0, "An exited or disabled terminal rejects pasted input");
                Control.AllowDirectInput = true;
                var Failed = false;
                Control.ClipboardFailed = _ => Failed = true;
                Control.ReadClipboardText = () => throw new System.Runtime.InteropServices.ExternalException("Owned clipboard test");
                Check(!Control.PasteClipboard() && Failed, "Clipboard access failures surface without crashing or losing the terminal session");
            }
            finally { Control.Session = null; }
        }
        var Surface = (EmbeddedTerminalControl)this.TerminalSurface!;
        var Session = this.TerminalSession!;
        var ClipboardReader = Surface.ReadClipboardText;
        var FileClipboardWriter = this.WriteClipboardFiles;
        var FileCopies = 0;
        var LiveCopies = 0;
        ExecutedRoutedEventHandler ObserveCopy = (_, E) =>
        {
            if (E.Command != TerminalControl.CopyCommand) return;
            LiveCopies++;
            E.Handled = true;
        };
        try
        {
            this.WriteClipboardFiles = _ => FileCopies++;
            CommandManager.AddPreviewExecutedHandler(Surface, ObserveCopy);
            Surface.SelectAll();
            foreach (var Source in new UIElement[] { Surface, Surface.InputEditor })
            {
                foreach (var Key in new[] { Key.LeftCtrl, Key.C })
                {
                    var Input = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control), PresentationSource.FromVisual(Source), 0, Key)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    Source.RaiseEvent(Input);
                    if (Key == Key.C) Check(Input.Handled, "The live terminal handles Ctrl+C through the full window route");
                }
            }
            Check(LiveCopies == 2 && FileCopies == 0 && !Session.HasExited,
                "Ctrl then C in the live terminal copies terminal text without invoking file copying or exiting the session");
            CommandManager.RemovePreviewExecutedHandler(Surface, ObserveCopy);
            this.WriteClipboardFiles = FileClipboardWriter;
            FocusManager.SetFocusedElement(this, Surface);
            Session.Write(Encoding.UTF8.GetBytes("$global:CDirPasteCount=0\r"));
            await Task.Delay(100);
            const string PasteCommand = "$global:CDirPasteCount++";
            Surface.ReadClipboardText = () => PasteCommand;
            async Task Wait(Func<bool> Condition, string Name)
            {
                var Deadline = Environment.TickCount64 + 10000;
                while (!Condition() && Environment.TickCount64 < Deadline) await Task.Delay(40);
                Check(Condition(), Name);
            }
            string CursorLine()
            {
                var Result = new StringBuilder();
                lock (Session.Buffer.SyncRoot)
                    // The prompt plus pasted command can wrap when the scrollbar reserves a column.
                    for (var Y = 0; Y <= ((TerminalDecoder)Session.Decoder).CursorPosition.Y; Y++)
                    {
                        var Row = new StringBuilder();
                        foreach (var Cell in Session.Buffer.GetRow(Y))
                            if (!Cell.Style.Continuation) Row.Append(Cell.Character.ToString());
                        Result.Append(Row.ToString().TrimEnd());
                    }
                return Result.ToString().TrimEnd();
            }
            foreach (var Route in new[] { "keyboard", "native editor", "terminal menu" })
            {
                if (Route == "keyboard") Surface.HandlePasteShortcut(Key.V, ModifierKeys.Control);
                else if (Route == "native editor") ApplicationCommands.Paste.Execute(null, Surface.InputEditor);
                else TerminalControl.PasteCommand.Execute(null, Surface);
                await Wait(() => CursorLine().EndsWith(PasteCommand, StringComparison.Ordinal),
                    "Pasted text through " + Route + " is visibly echoed at the real PowerShell prompt before Enter");
                Surface.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Surface), 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                await Wait(() => CursorLine().EndsWith('>'), "The pasted command returns to the PowerShell prompt through " + Route);
            }
            var CountFile = Path.Combine(AppMain.SmokeRoot!, "TerminalPasteCount.txt");
            Session.Write(Encoding.UTF8.GetBytes("[IO.File]::WriteAllText('" + CountFile.Replace("'", "''", StringComparison.Ordinal) + "',[string]$global:CDirPasteCount)\r"));
            await Wait(() => File.Exists(CountFile), "All paste routes execute successfully in the real shell");
            Check(File.ReadAllText(CountFile) == "3", "Three pasted commands execute exactly once each without hidden duplicates");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        finally
        {
            Surface.ReadClipboardText = ClipboardReader;
            this.WriteClipboardFiles = FileClipboardWriter;
            CommandManager.RemovePreviewExecutedHandler(Surface, ObserveCopy);
        }
    }
}
