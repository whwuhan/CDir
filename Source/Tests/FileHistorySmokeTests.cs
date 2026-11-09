using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDir.Localization;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunFileHistorySmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialReplay = this.ReplayShellOperation;
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "NativeHistory-" + Guid.NewGuid().ToString("N"))).FullName;
        var Work = Directory.CreateDirectory(Path.Combine(Fixture, "Work")).FullName;
        var Holding = Directory.CreateDirectory(Path.Combine(Fixture, "Holding")).FullName;
        var FilePath = Path.Combine(Work, "Windows operation.txt");
        var Held = Path.Combine(Holding, "Windows operation.txt");
        File.WriteAllText(FilePath, "Owned native history route");
        var Calls = new List<bool>();
        var Sta = true;
        var Owner = new System.Windows.Interop.WindowInteropHelper(this.HostWindow).Handle;
        void Check(bool Value, string Name)
        {
            if (!Value) throw new InvalidOperationException("Native history check failed: " + Name);
            Passed.Add(Name);
        }
        try
        {
            await this.NavigateAsync(Work, RecordHistory: false);
            string[]? NativeVerbs = null;
            Check(await this.RunOperationAsync("Reading owned Shell view commands", () =>
                CDir.Controls.NativeShellView.WithHistoryMenu(Owner, Work, Menu => NativeVerbs = ShellContextMenu.InspectHistoryMenu(Menu)),
                "Owned Shell view commands read", RequiresSta: true) && NativeVerbs is not null
                && NativeVerbs.Contains("view") && NativeVerbs.Contains("refresh"),
                "The undo adapter obtains commands from an initialized native Shell view without showing or activating it");
            if (AppMain.ScreenshotPath is { } Output)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!, "NativeHistoryMenu.json"), System.Text.Json.JsonSerializer.Serialize(NativeVerbs));
            Check(await this.TransferFilesAsync([FilePath], Work, false), "The Shell completes a real same-folder file copy before history replay");
            var SameFolderCopy = Directory.EnumerateFiles(Work).Single(Path => !SamePath(Path, FilePath));
            Passed.AddRange(ShellContextMenu.CheckOwnedHistoryMenu(Owner, Work, SameFolderCopy));
            Check(File.ReadAllText(FilePath) == "Owned native history route" && File.ReadAllText(SameFolderCopy) == "Owned native history route",
                "Repeated copy undo and redo preserve the original file and copied contents");
            this.ReplayShellOperation = (Window, Folder, Redo) =>
            {
                Sta &= Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
                if (Window != Owner || !SamePath(Folder, Work)) throw new InvalidOperationException("Unexpected native undo target.");
                Calls.Add(Redo);
                // An owned adapter verifies key routing and refresh without touching
                // Explorer's global undo stack, which belongs to the real user.
                if (Redo) File.Move(Held, FilePath); else File.Move(FilePath, Held);
            };
            var Undo = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control), PresentationSource.FromVisual(this.FileGrid), 0, Key.Z)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            this.FileGrid.RaiseEvent(Undo);
            var Deadline = Environment.TickCount64 + 10000;
            while ((File.Exists(FilePath) || this.OperationBusy) && Environment.TickCount64 < Deadline) await Task.Delay(20);
            Check(Undo.Handled && Calls.SequenceEqual(new[] { false }) && !File.Exists(FilePath),
                "Ctrl+Z invokes the Windows undo adapter once rather than replaying CDir backups");
            await this.HandleShortcutAsync(Key.Y, ModifierKeys.Control, false, true);
            Check(Calls.SequenceEqual(new[] { false, true }) && Sta && File.ReadAllText(FilePath) == "Owned native history route"
                && this.Entries.Any(Entry => SamePath(Entry.FullPath, FilePath)),
                "Ctrl+Y invokes Windows redo on an STA worker and refreshes the main panel");
            Check(ShellContextMenu.HistoryVerb(false) == "undo" && ShellContextMenu.HistoryVerb(true) == "redo",
                "Undo and redo use canonical Windows verbs independently of display language");
            var Blocked = false;
            try { ShellContextMenu.Replay(Owner, Work, false); }
            catch (InvalidOperationException) { Blocked = true; }
            Check(Blocked, "UI checks cannot execute real user-session undo or redo commands");
            this.ReplayShellOperation = (_, _, _) => throw new OperationCanceledException();
            await this.ReplayFileOperationAsync(false);
            Check(!this.OperationBusy && this.StatusText.Text == LanguageService.Text("OperationCancelled") && File.Exists(FilePath),
                "Cancelling Windows undo clears busy status and retains the current folder contents");
            foreach (var Surface in new UIElement[] { this.FileGrid, this.SearchBox, this.AddressBox, this.FolderTree })
            {
                var Escape = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(Surface), 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                Surface.RaiseEvent(Escape);
                Check(Escape.Handled && ReferenceEquals(FocusManager.GetFocusedElement(this), this.FileGrid),
                    "Escape returns logical focus to the file panel from " + Surface.GetType().Name);
            }
            var Version = typeof(ExplorerTab).Assembly.GetName().Version!;
            Check(this.HostWindow.TitleVersion.Text == $"v{Version.Major}.{Version.Minor}.{Version.Build}",
                "The title-bar release version matches the tested application assembly");
        }
        finally
        {
            this.ReplayShellOperation = InitialReplay;
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unowned native history fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
