using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VirtualTerminal;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunTerminalPredictionSmokeChecksAsync(List<string> Passed)
    {
        var Session = this.TerminalSession!;
        var Surface = this.TerminalSurface!;
        var ReportPath = Path.Combine(AppMain.SmokeRoot!, "PredictionColors.json");
        var Ready = Path.Combine(AppMain.SmokeRoot!, "PredictionReady.txt");
        static string Quote(string Text) => "'" + Text.Replace("'", "''", StringComparison.Ordinal) + "'";
        void Send(string Command) => Session.Write(Encoding.UTF8.GetBytes(Command + "\r"));
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Terminal prediction check failed: " + Name);
            Passed.Add(Name);
        }
        async Task WaitUntil(Func<bool> Condition)
        {
            var Deadline = Environment.TickCount64 + 15000;
            while (Environment.TickCount64 < Deadline)
            {
                try { if (Condition()) { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); return; } }
                catch (IOException Error) when ((Error.HResult & 0xFFFF) is 32 or 33) { }
                await Task.Delay(30);
            }
            throw new TimeoutException("The owned terminal prediction check did not settle.");
        }
        bool HasMutedSuggestion()
        {
            lock (Session.Buffer.SyncRoot)
                for (var Y = 0; Y < Session.Buffer.Rows; Y++)
                    foreach (var Cell in Session.Buffer.GetRow(Y))
                        if (Cell.Character.Value != ' ' && !Cell.Style.ForegroundIsDefault
                            && Cell.Style.Foreground.R == 111 && Cell.Style.Foreground.G == 111 && Cell.Style.Foreground.B == 111) return true;
            return false;
        }
        Send("& {$o=Get-PSReadLineOption; $r=@{SupportsInline=($null -ne $o.PSObject.Properties['InlinePredictionColor']); InlineColor=$o.InlinePredictionColor; TooltipColor=$o.ListPredictionTooltipColor; SupportsTooltip=($null -ne $o.PSObject.Properties['ListPredictionTooltipColor']); Version=(Get-Module PSReadLine).Version.ToString()}; [IO.File]::WriteAllText(" + Quote(ReportPath) + ",($r|ConvertTo-Json -Compress))}");
        JsonDocument? Report = null;
        await WaitUntil(() =>
        {
            if (!File.Exists(ReportPath)) return false;
            try { Report = JsonDocument.Parse(File.ReadAllText(ReportPath)); return true; }
            catch (JsonException) { return false; }
        });
        using (Report)
        {
            var Options = Report!.RootElement;
            var Expected = "\x1b[38;2;111;111;111m";
            if (!Options.GetProperty("SupportsInline").GetBoolean())
            {
                Check(!Session.HasExited, "Older PSReadLine versions without prediction color options remain compatible with terminal startup");
                return;
            }
            Check(Options.GetProperty("InlineColor").GetString() == Expected
                && (!Options.GetProperty("SupportsTooltip").GetBoolean() || Options.GetProperty("TooltipColor").GetString() == Expected),
                "PSReadLine uses an explicit muted gray for completion predictions and their tooltips");
        }
        Send("$global:CDirPreviousPredictionSource=(Get-PSReadLineOption).PredictionSource; Set-PSReadLineOption -PredictionSource History; [Microsoft.PowerShell.PSConsoleReadLine]::AddToHistory(\"Write-Output 'CDir muted completion preview'\"); [IO.File]::WriteAllText(" + Quote(Ready) + ",'ready')");
        await WaitUntil(() => File.Exists(Ready) && File.ReadAllText(Ready) == "ready");
        await Task.Delay(120);
        try
        {
            var Input = new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                new TextComposition(InputManager.Current, Surface, "Write-Output 'CDir muted"))
                { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
            Surface.RaiseEvent(Input);
            await WaitUntil(HasMutedSuggestion);
            Check(HasMutedSuggestion(), "A real history suggestion reaches the terminal buffer as muted gray rather than bright foreground text");
            this.CaptureAddressBreadcrumbPreview("TerminalMutedPredictionPreview.png");
        }
        finally
        {
            Session.Write([0x03]);
            await Task.Delay(100);
            Send("Set-PSReadLineOption -PredictionSource $global:CDirPreviousPredictionSource; Remove-Variable CDirPreviousPredictionSource -Scope Global");
            await Task.Delay(100);
        }
    }
}
