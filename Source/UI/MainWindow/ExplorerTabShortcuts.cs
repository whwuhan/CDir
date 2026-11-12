using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CDir.Localization;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private Dictionary<string, string> ShortcutBindings = KeyboardShortcut.Resolve(null);
    internal string PreferencesGesture => this.ShortcutBindings["Preferences"];

    private bool ChangeShortcut(string Id, string Gesture)
    {
        var Definition = KeyboardShortcut.All.FirstOrDefault(Item => Item.Id == Id);
        if (Definition is null || Definition.IsFixed) return false;
        if (Gesture.Length > 0 && !KeyboardShortcut.TryNormalize(Gesture, Definition.DefaultGesture.Contains("Wheel", StringComparison.Ordinal), out Gesture)) return false;
        if (Gesture.Length > 0 && this.ShortcutBindings.Any(Item => Item.Key != Id && Item.Value == Gesture)) return false;
        var Previous = this.Settings.Shortcuts;
        this.Settings.Shortcuts = new(this.ShortcutBindings, StringComparer.Ordinal) { [Id] = Gesture };
        this.Settings.ShortcutDefaultsRevision = 1;
        if (!this.SaveSettings())
        {
            this.Settings.Shortcuts = Previous;
            this.ShowNotice(LanguageService.Text("ShortcutSaveError"), true);
            return false;
        }
        this.ShortcutBindings[Id] = Gesture;
        this.ApplyShortcutHints();
        this.UpdateTerminalStatus();
        return true;
    }

    private void ApplyShortcutHints()
    {
        if (this.HostWindow.PreferencesMenuItem is { } Preferences
            && ReferenceEquals(this.HostWindow.ActiveTab, this)) Preferences.InputGestureText = this.PreferencesGesture;
        var Tooltips = new Dictionary<string, string>
        {
            ["BackTooltip"] = "Back", ["ForwardTooltip"] = "Forward", ["UpTooltip"] = "Up", ["RefreshTooltip"] = "Refresh",
            ["CopyCurrentPathTooltip"] = "CopyCurrentFolderPath", ["NewFolderTooltip"] = "NewFolder", ["CopyTooltip"] = "Copy",
            ["CutTooltip"] = "Cut", ["PasteTooltip"] = "Paste", ["RenameTooltip"] = "Rename", ["RecycleTooltip"] = "Delete",
            ["TerminalToggleTooltip"] = "ToggleTerminal",
            ["HideSidebarTooltip"] = "ToggleSidebar", ["ShowSidebarTooltip"] = "ToggleSidebar"
        };
        foreach (var Pair in Tooltips)
        {
            var Text = LanguageService.Text(Pair.Key);
            if (this.ShortcutBindings[Pair.Value] == KeyboardShortcut.All.Single(Item => Item.Id == Pair.Value).DefaultGesture)
            {
                Application.Current.Resources["Text" + Pair.Key] = Text;
                continue;
            }
            var Bracket = Math.Max(Text.LastIndexOf('('), Text.LastIndexOf('（'));
            if (Bracket >= 0) Text = Text[..Bracket].TrimEnd();
            var Gesture = this.ShortcutBindings[Pair.Value];
            Application.Current.Resources["Text" + Pair.Key] = Text + (Gesture.Length > 0 ? " (" + Gesture + ")" : "");
        }
        var AddressKeys = string.Join(" / ", new[] { this.ShortcutBindings["EditPath"], this.ShortcutBindings["EditPathAlternate"] }.Where(Value => Value.Length > 0));
        Application.Current.Resources["TextAddressEdit"] = this.ShortcutBindings["EditPath"] == KeyboardShortcut.All.Single(Item => Item.Id == "EditPath").DefaultGesture
            && this.ShortcutBindings["EditPathAlternate"] == KeyboardShortcut.All.Single(Item => Item.Id == "EditPathAlternate").DefaultGesture ? LanguageService.Text("AddressEdit")
            : LanguageService.Text("AddressEditCustom", AddressKeys.Length > 0 ? AddressKeys : LanguageService.Text("ShortcutUnassigned"));
    }

    private string ShortcutHelpBody() => LanguageService.Text("HelpOverview") + "\n\n"
        + string.Join("\n", KeyboardShortcut.All.Select(Item => LanguageService.Text(Item.ResourceKey) + ": "
            + (this.ShortcutBindings[Item.Id] is { Length: > 0 } Gesture ? Gesture : LanguageService.Text("ShortcutUnassigned"))));

    private async Task<bool> HandleShortcutAsync(Key Key, ModifierKeys Modifiers, bool InText, bool InFileList)
    {
        var Shortcut = KeyboardShortcut.Match(this.ShortcutBindings, Key, Modifiers);
        if (Shortcut?.TerminalOnly == true) return this.HandleTerminalFontGesture(KeyboardShortcut.Format(Key, Modifiers));
        if (!CanHandleShortcut(Shortcut, Key, Modifiers, InText, InFileList)) return false;
        switch (Shortcut!.Id)
        {
            case "NewWindow": this.OpenNewWindow(); break;
            case "Preferences": await this.OpenPreferencesAsync(); break;
            case "NewTab": case "CloseTab": case "NextTab": case "PreviousTab": await this.HostWindow.ExecuteTabCommandAsync(Shortcut.Id); break;
            case "ToggleTerminal": await this.ToggleTerminalAsync(); break;
            case "DistributePanesEvenly": this.HostWindow.DistributePanesEvenly(); break;
            case "AutoFitColumns": this.AutoFitColumns(); break;
            case "ToggleSidebar": this.ToggleSidebarClick(this.SidebarToggleButton, new RoutedEventArgs()); break;
            case "EditPath": case "EditPathAlternate": this.BeginAddressEdit(); break;
            case "Search": this.SearchBox.Focus(); this.SearchBox.SelectAll(); break;
            case "CopyCurrentFolderPath": this.CopyText(this.CurrentPath, LanguageService.Text("CurrentPathCopied")); break;
            case "Back": await this.BackAsync(); break;
            case "Forward": await this.ForwardAsync(); break;
            case "Up": await this.UpAsync(); break;
            case "Refresh": this.InvalidateVersionStatuses(); ShellIconService.InvalidateCache(); await this.RefreshRootsAsync(); await this.RefreshAndFitColumnsAsync(); break;
            case "NewFolder": await this.NewFolderAsync(); break;
            case "Copy": this.CopySelection(false); break;
            case "Cut": this.CopySelection(true); break;
            case "Paste": await this.PasteAsync(); break;
            case "Undo": await this.ReplayFileOperationAsync(false); break;
            case "Redo": await this.ReplayFileOperationAsync(true); break;
            case "SelectAll":
                this.SelectAllVisibleFiles();
                break;
            case "Rename": await this.RenameAsync(); break;
            case "Delete": await this.DeleteAsync(); break;
            case "Open": await this.OpenSelectedAsync(); break;
        }
        return true;
    }

    private static bool CanHandleShortcut(KeyboardShortcut? Shortcut, Key Key, ModifierKeys Modifiers, bool InText, bool InFileList)
    {
        if (Shortcut is null || Shortcut.TerminalOnly || (Shortcut.FileListOnly && !InFileList)) return false;
        if (Shortcut.Id == "SelectAll") return true;
        if (!InText) return true;
        if (!Shortcut.WorksInText) return false;
        // Keep confirmation and native editing available even when global actions are rebound.
        if ((Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 && Key is not (>= Key.F1 and <= Key.F24)) return false;
        return !(Modifiers == ModifierKeys.Control && Key is Key.A or Key.C or Key.X or Key.V or Key.Z or Key.Y);
    }
}
