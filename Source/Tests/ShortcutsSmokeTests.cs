using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunShortcutsSmokeChecksAsync(List<string> Passed)
    {
        await this.RunSelectionEscapeSmokeChecksAsync(Passed);
        await this.RunMouseHistorySmokeChecksAsync(Passed);
        await this.RunRoutedShortcutSmokeChecksAsync(Passed);
        await this.RunFileClipboardSmokeChecksAsync(Passed);
        await this.RunNewWindowShortcutSmokeChecksAsync(Passed);
        await this.RunSidebarAndSearchShortcutSmokeChecksAsync(Passed);
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Shortcuts UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        Check(KeyboardShortcut.All.Count == 33 && KeyboardShortcut.All.All(Item => KeyboardShortcut.TryNormalize(Item.DefaultGesture, Item.TerminalOnly, out _))
            && KeyboardShortcut.All.Select(Item => Item.DefaultGesture).Distinct().Count() == 33,
            "All thirty-three bindings have valid unique defaults including fixed new-window copy and paste gestures");
        Check(KeyboardShortcut.Resolve(new Dictionary<string, string> { ["Search"] = "Alt+Q" }) is var Migrated
            && Migrated["Search"] == "Alt+Q" && Migrated["ToggleSidebar"] == "",
            "An existing personal Alt+Q binding is preserved when adding the new sidebar command");
        var LegacySidebar = new AppSettings { Shortcuts = new() { ["ToggleSidebar"] = "Alt+1", ["Search"] = "Ctrl+Shift+F" } };
        LegacySidebar.UpgradeShortcutDefaults();
        Check(LegacySidebar.Shortcuts!["ToggleSidebar"] == "Alt+Q" && LegacySidebar.Shortcuts["Search"] == "Ctrl+Shift+F",
            "The previous sidebar default upgrades once without changing other personal shortcuts");
        LegacySidebar.Shortcuts["ToggleSidebar"] = "Alt+1"; LegacySidebar.UpgradeShortcutDefaults();
        Check(LegacySidebar.Shortcuts["ToggleSidebar"] == "Alt+1", "A later personal Alt+1 sidebar binding survives reloads");
        var Conflict = new AppSettings { Shortcuts = new() { ["ToggleSidebar"] = "Alt+1", ["Search"] = "Alt+Q" } };
        Conflict.UpgradeShortcutDefaults();
        Check(Conflict.Shortcuts!["ToggleSidebar"] == "Alt+1" && Conflict.Shortcuts["Search"] == "Alt+Q",
            "Upgrading the sidebar default preserves an existing personal Alt+Q command");
        Check(KeyboardShortcut.Resolve(new Dictionary<string, string> { ["Search"] = "Alt+E" }) is var ExistingFit
            && ExistingFit["Search"] == "Alt+E" && ExistingFit["AutoFitColumns"] == "",
            "The new fit shortcut cannot take over an existing personal Alt+E binding");
        Check(KeyboardShortcut.Resolve(new Dictionary<string, string> { ["Search"] = "Alt+R" }) is var ExistingDistribution
            && ExistingDistribution["Search"] == "Alt+R" && ExistingDistribution["DistributePanesEvenly"] == "",
            "The distribution default cannot take over an existing personal Alt+R binding");
        Check(new[] { "Ctrl+", "Win+R", "Alt+F4", "Escape", "A", "Ctrl+Ctrl+A", "9999" }
            .All(Value => !KeyboardShortcut.TryParse(Value, out _, out _)), "Reserved, incomplete, and typing keys are rejected");
        Check(KeyboardShortcut.Resolve(new Dictionary<string, string> { ["Search"] = "invalid", ["Cut"] = "", ["Copy"] = "", ["Paste"] = "F8", ["NewWindow"] = "F9" }) is var Loaded
            && Loaded["Search"] == "Ctrl+F" && Loaded["Cut"] == "Ctrl+X" && Loaded["Copy"] == "Ctrl+C" && Loaded["Paste"] == "Ctrl+V" && Loaded["NewWindow"] == "Ctrl+N"
            && KeyboardShortcut.Resolve(new Dictionary<string, string> { ["NewWindow"] = "" })["NewWindow"] == "Ctrl+N",
            "Saved settings preserve customizable unassignment and cannot clear or rebind fixed new-window copy and paste shortcuts");
        Check(KeyboardShortcut.Resolve(new Dictionary<string, string> { ["Search"] = "Ctrl+C" })["Search"] == "Ctrl+F",
            "Duplicate bindings in a damaged settings file safely restore defaults");
        Check(KeyboardShortcut.Format(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift) == "Ctrl++"
            && KeyboardShortcut.Format(Key.Add, ModifierKeys.Control) == "Ctrl++"
            && KeyboardShortcut.Format(Key.Subtract, ModifierKeys.Control) == "Ctrl+-"
            && KeyboardShortcut.TryParse("Ctrl++", out _, out _),
            "Plus and minus gestures normalize physical keyboard aliases to readable shortcuts");
        Check(KeyboardShortcut.TryParseWheel("Alt+WheelDown", out var WheelDirection, out var WheelModifiers)
            && WheelDirection == -1 && WheelModifiers == ModifierKeys.Alt
            && !KeyboardShortcut.TryNormalize("WheelUp", true, out _) && !KeyboardShortcut.TryNormalize("Ctrl+WheelUp", false, out _),
            "Modified wheel gestures parse safely and cannot replace unrelated application shortcuts");
        Check(KeyboardShortcut.All.Where(Item => !Item.DefaultGesture.Contains("Wheel")).All(Item => KeyboardShortcut.TryParse(Item.DefaultGesture, out var Key, out var Modifiers)
            && KeyboardShortcut.Match(this.ShortcutBindings, Key, Modifiers)?.Id == Item.Id),
            "Every default key dispatches its command, including the Enter/Return alias");

        var InitialLanguage = LanguageService.CurrentLanguage;
        var InitialSettings = this.Settings.Shortcuts;
        var InitialBindings = new Dictionary<string, string>(this.ShortcutBindings);
        var SavedFile = Path.Combine(AppMain.SmokeRoot!, "ShortcutsSettings.json");
        try
        {
            await this.RunPreferencesSmokeChecksAsync(Passed, InspectShortcuts: async Dialog =>
            {
                Dialog.Categories.SelectedItem = Dialog.ShortcutsCategory;
                Dialog.UpdateLayout();
                Check(Dialog.ShortcutsPanel.IsVisible && !Dialog.GeneralPanel.IsVisible && !Dialog.UIPanel.IsVisible
                    && Dialog.ShortcutEditors.Count == 24 && Dialog.ShortcutRows.Values.All(Row => Row.IsVisible),
                    "The keyboard shortcuts page lists the twenty-four customizable bindings");
                foreach (var Language in LanguageService.SupportedLanguages)
                {
                    Dialog.LanguagePicker.SelectedValue = Language.Code;
                    Dialog.ShortcutSearch.Text = LanguageService.Text("NewFolder");
                    Check(Dialog.ShortcutsCategory.Content?.ToString() == LanguageService.Text("ShortcutsCategory")
                        && Dialog.ShortcutRows["NewFolder"].Visibility == Visibility.Visible
                        && Dialog.ShortcutRows["Refresh"].Visibility == Visibility.Collapsed,
                        Language.Code + " searches localized action names and updates the category caption");
                    var EnglishCaptions = ((Panel)Dialog.ShortcutsPanel).Children.OfType<Grid>()
                        .Select(Row => Dialog.ShortcutRows.Single(Pair => ReferenceEquals(Pair.Value, Row)).Key)
                        .Select(Id => LanguageService.EnglishText(KeyboardShortcut.All.Single(Item => Item.Id == Id).ResourceKey)).ToArray();
                    Check(EnglishCaptions.SequenceEqual(EnglishCaptions.OrderBy(Text => Text, StringComparer.OrdinalIgnoreCase))
                        && EnglishCaptions[0] == "Back" && EnglishCaptions[^1] == "Up" && LanguageService.CurrentLanguage == Language.Code,
                        Language.Code + " keeps shortcuts in ascending English action-name order without changing the interface language");
                }
                Dialog.LanguagePicker.SelectedValue = "en-US";
                Dialog.ShortcutSearch.Text = "ctrl + shift + n";
                Check(Dialog.ShortcutRows["NewFolder"].Visibility == Visibility.Visible && Dialog.ShortcutRows["Refresh"].Visibility == Visibility.Collapsed,
                    "Key search ignores case and supports spaces around plus signs");
                Dialog.ShortcutSearch.Text = "__no_matching_shortcut__";
                Check(Dialog.ShortcutRows.Values.All(Row => Row.Visibility == Visibility.Collapsed) && Dialog.ShortcutEmptyHint.IsVisible,
                    "An unmatched search shows the empty-state hint");
                Dialog.ShortcutSearch.Text = "";
                Dialog.UpdateLayout();
                Check(Dialog.ShortcutEditors.ContainsKey("DistributePanesEvenly") && Dialog.ShortcutEditors.ContainsKey("AutoFitColumns") && Dialog.ShortcutEditors.ContainsKey("ToggleSidebar") && Dialog.ShortcutEditors.ContainsKey("Search")
                    && Dialog.SetShortcut("ToggleSidebar", "Ctrl+Shift+B")
                    && this.ShortcutBindings["ToggleSidebar"] == "Ctrl+Shift+B"
                    && Application.Current.Resources["TextHideSidebarTooltip"].ToString()!.Contains("Ctrl+Shift+B"),
                    "Sidebar toggling is customizable and its tooltip reflects the changed key");
                Dialog.ShortcutClearButtons["ToggleSidebar"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(this.ShortcutBindings["ToggleSidebar"] == "", "The sidebar shortcut can be unassigned");
                Dialog.ShortcutResetButtons["ToggleSidebar"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(this.ShortcutBindings["ToggleSidebar"] == "Alt+Q", "Reset restores the sidebar shortcut to Alt+Q");
                foreach (var Id in new[] { "Copy", "Cut", "Paste", "NewWindow", "TerminalFontIncreaseWheel", "TerminalFontDecreaseWheel" })
                {
                    Check(!Dialog.ShortcutRows.ContainsKey(Id) && !Dialog.ShortcutEditors.ContainsKey(Id)
                        && !Dialog.ShortcutClearButtons.ContainsKey(Id) && !Dialog.ShortcutResetButtons.ContainsKey(Id)
                        && !Dialog.SetShortcut(Id, "F8") && !Dialog.SetShortcut(Id, "") && !this.ChangeShortcut(Id, "F8") && !this.ChangeShortcut(Id, "")
                        && this.ShortcutBindings[Id] == KeyboardShortcut.All.Single(Item => Item.Id == Id).DefaultGesture,
                        "The fixed shortcut is omitted from customization and still cannot be cleared or rebound: " + Id);
                }
                Dialog.ShortcutSearch.Text = "Ctrl+C";
                Check(Dialog.ShortcutRows.Values.All(Row => Row.Visibility == Visibility.Collapsed) && Dialog.ShortcutEmptyHint.IsVisible,
                    "Searching for a hidden fixed shortcut shows no customizable results");
                Dialog.ShortcutSearch.Text = "";
                var Editor = Dialog.ShortcutEditors["Search"];
                foreach (var (Id, Modifiers, Expected) in new[]
                {
                    ("NextTab", ModifierKeys.Control, "Ctrl+Tab"),
                    ("PreviousTab", ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+Tab")
                })
                {
                    Dialog.ShortcutClearButtons[Id].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var TabInput = new KeyEventArgs(new SmokeKeyboardDevice(Modifiers), PresentationSource.FromVisual(Dialog), 0, Key.Tab)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    Dialog.ShortcutEditors[Id].RaiseEvent(TabInput);
                    Check(TabInput.Handled && this.ShortcutBindings[Id] == Expected && Dialog.ShortcutEditors[Id].Text == Expected,
                        "The shortcut recorder accepts the standard tab-switching gesture: " + Expected);
                }
                Editor.RaiseEvent(new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(Dialog), 0, Key.F8)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Check(this.ShortcutBindings["Search"] == "F8" && Editor.Text == "F8" && this.Settings.Shortcuts!["Search"] == "F8",
                    "Recording a key applies it immediately and updates the saved settings model");
                Check(!Dialog.SetShortcut("Search", "Ctrl+C") && this.ShortcutBindings["Search"] == "F8"
                    && Dialog.ShortcutError.IsVisible && Dialog.ShortcutError.Text.Contains(LanguageService.Text("Copy")),
                    "Conflicting shortcuts are rejected with the conflicting action identified");
                Check(!Dialog.SetShortcut("Search", "A") && this.ShortcutBindings["Search"] == "F8",
                    "Invalid input preserves the existing binding");
                Dialog.ShortcutClearButtons["Search"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(this.ShortcutBindings["Search"] == "" && Editor.Text == LanguageService.Text("ShortcutUnassigned")
                    && KeyboardShortcut.Match(this.ShortcutBindings, Key.F8, ModifierKeys.None) is null,
                    "Clear unassigns a command and removes its old key");
                Dialog.ShortcutResetButtons["Search"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(this.ShortcutBindings["Search"] == "Ctrl+F" && Editor.Text == "Ctrl+F", "Reset restores an individual shortcut");
                Check(!Dialog.RecordShortcutWheel("TerminalFontIncreaseWheel", 120, ModifierKeys.Alt)
                    && !Dialog.SetShortcut("TerminalFontIncreaseWheel", "Alt+WheelUp")
                    && !Dialog.SetShortcut("TerminalFontDecreaseWheel", "")
                    && !Dialog.SetShortcut("TerminalFontIncrease", "Alt+WheelUp")
                    && Dialog.SetShortcut("TerminalFontIncrease", "F6"),
                    "Wheel zoom is fixed while keyboard font shortcuts remain customizable");
                Check(Dialog.ShortcutEditors["TerminalFontReset"].Text == "Ctrl+0"
                    && Dialog.SetShortcut("TerminalFontReset", "Ctrl+R")
                    && KeyboardShortcut.Match(this.ShortcutBindings, Key.R, ModifierKeys.Control)?.Id == "TerminalFontReset",
                    "The terminal font reset shortcut is listed and can be rebound");
                Dialog.ShortcutResetButtons["TerminalFontReset"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Dialog.SetShortcut("NewFolder", "Ctrl+Shift+K")
                    && Application.Current.Resources["TextNewFolderTooltip"].ToString()!.Contains("Ctrl+Shift+K")
                    && this.ShortcutHelpBody().Contains("Ctrl+Shift+K"), "Tooltips and help reflect changed shortcuts without changing Windows Shell menus");
                Check(!await this.HandleShortcutAsync(Key.K, ModifierKeys.Control | ModifierKeys.Shift, true, false),
                    "File-operation shortcuts preserve native editing while a text field has focus");
                foreach (var Language in LanguageService.SupportedLanguages)
                {
                    Dialog.LanguagePicker.SelectedValue = Language.Code;
                    var Tooltip = Application.Current.Resources["TextNewFolderTooltip"].ToString()!;
                    Check(Tooltip.Contains("Ctrl+Shift+K") && !Tooltip.Contains("Ctrl+Shift+N"),
                        Language.Code + " changed tooltips replace the old key, including full-width parentheses");
                }
                Dialog.LanguagePicker.SelectedValue = "en-US";
                Check(Dialog.SetShortcut("Open", "F10") && Dialog.SetShortcut("Search", "Enter")
                    && !await this.HandleShortcutAsync(Key.Enter, ModifierKeys.None, true, false),
                    "Rebinding a global command to Enter preserves path and text-field confirmation");
                Check(Dialog.SetShortcut("ToggleTerminal", "Ctrl+Shift+T")
                    && KeyboardShortcut.Match(this.ShortcutBindings, Key.T, ModifierKeys.Control | ModifierKeys.Shift)?.Id == "ToggleTerminal"
                    && KeyboardShortcut.Match(this.ShortcutBindings, Key.X, ModifierKeys.Alt) is null
                    && Application.Current.Resources["TextTerminalToggleTooltip"].ToString()!.Contains("Ctrl+Shift+T"),
                    "Terminal toggling participates in shortcut recording, matching, and tooltip updates");
                Check(!Dialog.SetShortcut("SelectAll", "F9") && !Dialog.SetShortcut("SelectAll", "")
                    && !Dialog.ShortcutEditors.ContainsKey("SelectAll") && !this.ChangeShortcut("SelectAll", "F9")
                    && KeyboardShortcut.Resolve(new Dictionary<string, string> { ["SelectAll"] = "F9" })["SelectAll"] == "Ctrl+A"
                    && CanHandleShortcut(KeyboardShortcut.All.Single(Item => Item.Id == "SelectAll"), Key.A, ModifierKeys.Control, true, false),
                    "Ctrl+A is fixed and routes main-window text fields to the file list");
                var BeforeModalSelection = this.SelectedEntries().Select(Entry => Entry.FullPath).ToArray();
                var ModalSelectAll = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control), PresentationSource.FromVisual(Dialog.ShortcutSearch), 0, Key.A)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                Dialog.ShortcutSearch.RaiseEvent(ModalSelectAll);
                Check(!ModalSelectAll.Handled && Dialog.IsVisible && ReferenceEquals(Dialog.Owner, this.HostWindow)
                    && this.SelectedEntries().Select(Entry => Entry.FullPath).SequenceEqual(BeforeModalSelection),
                    "Ctrl+A inside Preferences keeps the modal window open and leaves the main-panel selection unchanged");
                Check(Dialog.SetShortcut("Search", "F8") && this.SaveSettings(SavedFile), "Changed shortcuts serialize to an owned settings file");
                var Restored = KeyboardShortcut.Resolve(ReadSettings(SavedFile).Shortcuts);
                Check(Restored["Search"] == "F8" && Restored["Cut"] == "Ctrl+X" && Restored["NewFolder"] == "Ctrl+Shift+K" && Restored["SelectAll"] == "Ctrl+A"
                    && Restored["Copy"] == "Ctrl+C" && Restored["Paste"] == "Ctrl+V"
                    && Restored["TerminalFontIncrease"] == "F6" && Restored["TerminalFontIncreaseWheel"] == "Ctrl+WheelUp",
                    "Changed bindings survive saving and loading settings");
                Dialog.LanguagePicker.SelectedValue = "zh-CN";
                Dialog.ShortcutSearch.Text = "";
                Dialog.UpdateLayout();
                if (AppMain.ScreenshotPath is not null)
                {
                    var Bitmap = new RenderTargetBitmap((int)Dialog.ActualWidth, (int)Dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    Bitmap.Render(Dialog);
                    var Encoder = new PngBitmapEncoder();
                    Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                    using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(AppMain.ScreenshotPath)!, "PreferencesShortcuts-zh-CN.png"));
                    Encoder.Save(Stream);
                }
            });
            this.FileGrid.SelectedItems.Clear();
            Check(await this.HandleShortcutAsync(Key.A, ModifierKeys.Control, false, true)
                && !await this.HandleShortcutAsync(Key.F9, ModifierKeys.None, false, true)
                && this.FileGrid.SelectedItems.Count == this.Entries.Count && this.Entries.Count > 0,
                "Fixed Ctrl+A selects every displayed file and folder and the rejected replacement key does nothing");
            await this.RunPreferencesSmokeChecksAsync(Passed, InspectShortcuts: async Dialog =>
            {
                Dialog.Categories.SelectedItem = Dialog.ShortcutsCategory;
                Check(Dialog.ShortcutEditors["Search"].Text == "F8" && Dialog.ShortcutEditors["NewFolder"].Text == "Ctrl+Shift+K" && !Dialog.ShortcutEditors.ContainsKey("Cut")
                    && !Dialog.ShortcutEditors.ContainsKey("Copy") && !Dialog.ShortcutEditors.ContainsKey("Paste"),
                    "Reopening Preferences retains changed bindings");
                Dialog.CopyOnDropCheck.IsChecked = false;
                await this.RestorePreferencesSmokeAsync(Dialog);
                while (!Dialog.RestoreDefaultsButton.IsEnabled) await Task.Delay(20);
                Check(this.Settings.Shortcuts is null && KeyboardShortcut.All.All(Item => this.ShortcutBindings[Item.Id] == Item.DefaultGesture)
                    && KeyboardShortcut.All.Where(Item => !Item.IsFixed).All(Item => Dialog.ShortcutEditors[Item.Id].Text == Item.DefaultGesture)
                    && Dialog.ShortcutEditors.Count == 24, "Restore Default Settings resets every shortcut and only refreshes customizable rows");
                Check(Dialog.CopyOnDropCheck.IsChecked == true && this.Settings.CopyOnDrop,
                    "Restore Default Settings turns drag-copy mode on");
            });
        }
        finally
        {
            this.Settings.Shortcuts = InitialSettings;
            this.ShortcutBindings = InitialBindings;
            this.ChangeLanguage(InitialLanguage, Persist: false);
        }
    }
}
