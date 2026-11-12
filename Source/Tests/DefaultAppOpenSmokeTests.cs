using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunDefaultAppOpenSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialView = this.Settings.FileView;
        var InitialSettings = this.Settings.Shortcuts;
        var InitialBindings = new Dictionary<string, string>(this.ShortcutBindings);
        var Launcher = this.Files.StartFileProcess;
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Default app 中文 & spaces")).FullName;
        var TextPath = Path.Combine(Fixture, "Text 中文.txt");
        var UnknownPath = Path.Combine(Fixture, "Unknown & 'quoted'.cdirown");
        File.WriteAllText(TextPath, "Default app fixture");
        File.WriteAllBytes(UnknownPath, [0, 255, 1]);
        Directory.CreateDirectory(Path.Combine(Fixture, "Folder"));
        var Calls = new List<ProcessStartInfo>();
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Default app check failed: " + Name);
            Passed.Add(Name);
        }
        void Capture(ProcessStartInfo Info)
        {
            if (!AppMain.IsInsideSmokeScope(Info.FileName) || !SamePath(Info.WorkingDirectory, Fixture))
                throw new InvalidOperationException("Refusing a launch outside the default-app fixture.");
            Calls.Add(Info);
        }
        void Route(Key Key)
        {
            var Input = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(this.ActiveFileControl), 0, Key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            this.ActiveFileControl.RaiseEvent(Input);
            Check(Input.Handled, "The actual file-panel route handles " + Key);
        }
        try
        {
            this.Files.StartFileProcess = Capture;
            this.ShortcutBindings["OpenWithDefault"] = "Space";
            Check(KeyboardShortcut.All.Single(Item => Item.Id == "OpenWithDefault") is { IsFixed: false, FileListOnly: true }
                && KeyboardShortcut.Resolve(null)["OpenWithDefault"] == "Space",
                "Opening with the default app has a customizable file-list-only Space default");
            Check(KeyboardShortcut.Resolve(new Dictionary<string, string> { ["QuickEditText"] = "F9" })["OpenWithDefault"] == "F9"
                && KeyboardShortcut.Resolve(new Dictionary<string, string> { ["QuickEditText"] = "" })["OpenWithDefault"] == ""
                && KeyboardShortcut.Resolve(new Dictionary<string, string> { ["QuickEditText"] = "F9", ["OpenWithDefault"] = "F8" })["OpenWithDefault"] == "F8",
                "Legacy custom and cleared quick-editor bindings migrate without replacing an explicit new binding");
            var LegacySettings = new AppSettings { ShortcutDefaultsRevision = 1,
                Shortcuts = new() { ["QuickEditText"] = "F9", ["ToggleTextWrap"] = "Alt+Z", ["ToggleEditorTabPin"] = "Alt+W" } };
            LegacySettings.UpgradeShortcutDefaults();
            Check(LegacySettings.Shortcuts!.Count == 1 && LegacySettings.Shortcuts["OpenWithDefault"] == "F9",
                "Existing settings save the migrated open key and drop removed editor commands even after an earlier default upgrade");
            Check(typeof(AppMain).Assembly.GetType("CDir.UI.Dialogs.TextEditorWindow") is null
                && !KeyboardShortcut.Groups.Contains("TextEditor")
                && !KeyboardShortcut.All.Any(Item => Item.Id is "ToggleTextWrap" or "ToggleEditorTabPin" or "TextEditorSave"),
                "The built-in editor, its shortcut group and its dedicated commands are removed");
            foreach (var Mode in new[] { FileViewMode.Details, FileViewMode.LargeIcons, FileViewMode.List, FileViewMode.Tiles })
            {
                await this.NavigateAsync(Fixture, RecordHistory: false);
                this.ChangeFileView(Mode);
                this.SelectPath(TextPath);
                Calls.Clear();
                Route(Key.Space);
                Check(Calls is [{ UseShellExecute: true, Verb: "", Arguments: "" } Info] && SamePath(Info.FileName, TextPath)
                    && SamePath(this.CurrentPath, Fixture), Mode + " opens the selected file through its Windows association without reading or editing it");
            }
            Calls.Clear();
            Check(!await this.HandleShortcutAsync(Key.Space, ModifierKeys.None, InText: true, InFileList: true)
                && !await this.HandleShortcutAsync(Key.Space, ModifierKeys.None, InText: false, InFileList: false)
                && Calls.Count == 0, "Space leaves text input and non-file controls alone");
            this.FileGrid.SelectedItems.Clear();
            await this.HandleShortcutAsync(Key.Space, ModifierKeys.None, InText: false, InFileList: true);
            Check(Calls.Count == 0, "Space without selected files launches nothing");
            this.SelectPath(Path.Combine(Fixture, "Folder"));
            await this.HandleShortcutAsync(Key.Space, ModifierKeys.None, InText: false, InFileList: true);
            Check(Calls.Count == 0 && SamePath(this.CurrentPath, Fixture), "The default-app shortcut skips folder selections");
            this.SelectPath(TextPath);
            this.SelectPath(UnknownPath, Append: true);
            Calls.Clear();
            await this.HandleShortcutAsync(Key.Space, ModifierKeys.None, InText: false, InFileList: true);
            Check(Calls.Count == 2 && Calls.Select(Info => Info.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([TextPath, UnknownPath]),
                "The shortcut opens each selected file exactly once, including non-text files");
            Check(this.ChangeShortcut("OpenWithDefault", "F9"), "The default-app shortcut can be rebound");
            this.SelectPath(TextPath);
            Calls.Clear();
            Check(!await this.HandleShortcutAsync(Key.Space, ModifierKeys.None, InText: false, InFileList: true) && Calls.Count == 0,
                "Rebinding releases Space");
            Route(Key.F9);
            Check(Calls.Count == 1 && this.ChangeShortcut("OpenWithDefault", "")
                && !await this.HandleShortcutAsync(Key.F9, ModifierKeys.None, InText: false, InFileList: true)
                && this.ChangeShortcut("OpenWithDefault", "Space"), "The custom default-app key opens files and can be cleared and restored");
            this.SearchBox.Text = ".txt";
            await this.WaitForSearchSmokeAsync();
            this.SelectPath(TextPath);
            Calls.Clear();
            Route(Key.Space);
            Check(Calls.Count == 1 && SamePath(Calls[0].FileName, TextPath) && this.SearchBox.Text == ".txt",
                "Opening a filtered result preserves its search");
            this.SearchBox.Clear();
            await this.WaitForSearchSmokeAsync();
            this.SelectPath(UnknownPath);
            Calls.Clear();
            this.Files.StartFileProcess = Info =>
            {
                Capture(Info);
                if (Info.Verb.Length == 0) throw new Win32Exception(1155);
            };
            await this.HandleShortcutAsync(Key.Space, ModifierKeys.None, InText: false, InFileList: true);
            Check(Calls.Count == 2 && Calls[1] is { Verb: "openas", UseShellExecute: true, ErrorDialog: true, Arguments: "" }
                && Calls[1].FileName == UnknownPath && Calls[1].ErrorDialogParentHandle != 0,
                "A missing association invokes the owned Windows app picker with the literal Unicode path");
            this.Files.StartFileProcess = Info =>
            {
                Capture(Info);
                throw new Win32Exception(Info.Verb.Length == 0 ? 1155 : 1223);
            };
            this.Files.OpenPath(UnknownPath, Fixture);
            Check(SamePath(this.CurrentPath, Fixture), "Cancelling the Windows picker returns normally without changing location");
            foreach (var ErrorCode in new[] { 5, 31 })
            {
                Calls.Clear();
                this.Files.StartFileProcess = Info => { Capture(Info); throw new Win32Exception(ErrorCode, "Owned launch failure"); };
                await this.OpenSelectedAsync();
                Check(Calls.Count == 1 && this.StatusText.Text.Contains("Owned launch failure"),
                    "Launch errors are reported without opening the association picker: " + ErrorCode);
            }
            Check(File.ReadAllText(TextPath) == "Default app fixture" && File.ReadAllBytes(UnknownPath).SequenceEqual(new byte[] { 0, 255, 1 }),
                "Opening files never changes their bytes");
        }
        finally
        {
            this.Files.StartFileProcess = Launcher;
            this.Settings.Shortcuts = InitialSettings;
            this.ShortcutBindings = InitialBindings;
            this.ApplyShortcutHints();
            this.SearchBox.Clear();
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.ChangeFileView(InitialView);
            if (!AppMain.IsInsideSmokeScope(Fixture) || File.GetAttributes(Fixture).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Refusing cleanup outside the owned default-app fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
