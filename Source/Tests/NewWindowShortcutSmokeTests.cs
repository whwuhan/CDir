using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CDir.Controls;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunNewWindowShortcutSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var Launcher = this.StartNewWindowProcess;
        var InitialTerminal = this.TerminalSurface;
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "New window 中文 " + Guid.NewGuid().ToString("N"))).FullName;
        var Launches = new List<ProcessStartInfo>();
        using var Surface = new EmbeddedTerminalControl { Width = 1, Height = 1, IsHitTestVisible = false };
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("New-window shortcut check failed: " + Name);
            Passed.Add(Name);
        }
        void Press(UIElement Element)
        {
            var Input = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control), PresentationSource.FromVisual(Element), 0, Key.N)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Element.RaiseEvent(Input);
            Check(Input.Handled, "Ctrl+N reaches the new-window command from " + Element.GetType().Name);
        }
        try
        {
            await this.NavigateAsync(Fixture, RecordHistory: false);
            this.StartNewWindowProcess = Launches.Add;
            this.TerminalSurface = Surface;
            this.MainPanelContent.Children.Add(Surface);
            var TabCount = this.HostWindow.Tabs.Count;
            foreach (var Element in new UIElement[] { this.FileGrid, this.SearchBox, this.FolderTree, Surface })
            {
                var Before = Launches.Count;
                Press(Element);
                Check(Launches.Count == Before + 1 && this.HostWindow.Tabs.Count == TabCount && this.CurrentPath == Fixture,
                    "Each Ctrl+N requests one independent instance and preserves the existing tabs and directory");
            }
            Check(Launches.All(Info => Info.FileName == Environment.ProcessPath && !Info.UseShellExecute
                && Info.WorkingDirectory == AppContext.BaseDirectory && Info.ArgumentList.SequenceEqual(new[] { "--clone-from", Environment.ProcessId.ToString() })),
                "The new instance requests the complete live workspace from its exact parent process");
            var Failure = new Win32Exception(5);
            this.StartNewWindowProcess = _ => throw Failure;
            Press(this.FileGrid);
            Check(this.StatusText.Text == LanguageService.Text("NewWindowError", Failure.Message) && !this.WindowClosed
                && this.HostWindow.Tabs.Count == TabCount && this.CurrentPath == Fixture,
                "A failed new-instance launch reports an error without closing or changing the existing workspace");
        }
        finally
        {
            this.StartNewWindowProcess = Launcher;
            this.MainPanelContent.Children.Remove(Surface);
            this.TerminalSurface = InitialTerminal;
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned new-window fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
