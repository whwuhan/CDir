using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Controls;
using VirtualTerminal;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunTerminalRepaintSmokeChecksAsync(List<string> Passed)
    {
        var Surface = (EmbeddedTerminalControl)this.TerminalSurface!;
        var Editor = Surface.InputEditor;
        var Session = this.TerminalSession!;
        var Position = ((TerminalDecoder)Session.Decoder).CursorPosition;
        var Frames = new List<string>();
        var HiddenOnCommit = true;
        var HiddenAtRender = true;
        var SawUncoloredText = false;
        void Observe(object? Sender, EventArgs E)
        {
            lock (Session.Buffer.SyncRoot)
            {
                for (var Index = 0; Index < 4; Index++)
                {
                    var Cell = Session.Buffer.GetRow(Position.Y)[Position.X + Index];
                    if (Cell.Character.Value != 'd') continue;
                    Frames.Add($"{Index}: {Cell.Style.Foreground}, default={Cell.Style.ForegroundIsDefault}");
                    SawUncoloredText |= Cell.Style.ForegroundIsDefault
                        || (Cell.Style.Foreground.R == Cell.Style.Foreground.G && Cell.Style.Foreground.G == Cell.Style.Foreground.B);
                }
            }
        }
        void TextEvent(System.Windows.RoutedEvent Event, string Text) => Editor.RaiseEvent(
            new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, Editor, Text))
                { RoutedEvent = Event });
        Session.BufferUpdated += Observe;
        try
        {
            for (var Index = 0; Index < 4; Index++)
            {
                TextEvent(TextCompositionManager.PreviewTextInputStartEvent, "d");
                TextEvent(TextCompositionManager.PreviewTextInputEvent, "d");
                HiddenOnCommit &= Editor.Opacity == 0;
                // Simulate TextBox completing its native commit before its queued cleanup.
                Editor.Text = "d";
                await Dispatcher.InvokeAsync(() => HiddenAtRender &= Editor.Opacity == 0, DispatcherPriority.Render);
                await Task.Delay(80);
            }
        }
        finally { Session.BufferUpdated -= Observe; }
        if (AppMain.ScreenshotPath is { } Output)
            File.WriteAllLines(Output + ".repaint.txt", new[] { "Editor hidden synchronously: " + HiddenOnCommit }.Concat(Frames));
        Session.Write(Encoding.UTF8.GetBytes("\x7f\x7f\x7f\x7f"));
        await Task.Delay(150);
        if (!HiddenOnCommit || !HiddenAtRender) throw new InvalidOperationException("Terminal repaint UI smoke test failed: Committed input remains visible in the native editor until a later dispatcher turn.");
        Passed.Add("Committed input hides the native editor synchronously before any render can show uncolored text");
        if (Frames.Count == 0 || SawUncoloredText) throw new InvalidOperationException("Terminal repaint UI smoke test failed: Character-by-character input did not retain syntax coloring from its first buffer update.");
        Passed.Add("Character-by-character input retains PowerShell syntax coloring from its first buffer update without a white intermediate frame");
    }
}
