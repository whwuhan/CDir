using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;
using CDir.UI.Dialogs;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RestorePreferencesSmokeAsync(PreferencesWindow Preferences, PreferenceResetScope? Scope = PreferenceResetScope.All,
        bool Escape = false, List<string>? Passed = null)
    {
        Exception? Failure = null;
        var Inspected = false;
        ResetPreferencesDialog? FindChoice() => Preferences.OwnedWindows.OfType<ResetPreferencesDialog>().SingleOrDefault();
        var Timeout = new DispatcherTimer(DispatcherPriority.Send, Dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        Timeout.Tick += (_, _) => { Timeout.Stop(); Failure = new TimeoutException("Reset scope chooser did not complete."); FindChoice()?.Close(); };
        var Pending = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            var Choice = FindChoice();
            try
            {
                if (Choice is not { IsVisible: true } || !System.Windows.Interop.ComponentDispatcher.IsThreadModal
                    || !ReferenceEquals(Choice.Owner, Preferences) || Choice.CurrentCategoryOption.IsChecked != true
                    || Choice.AllPreferencesOption.IsChecked == true || Choice.Title != LanguageService.Text("RestoreDefaults")
                    || ((TextBlock)Choice.CurrentCategoryOption.Content).Text != LanguageService.Text("ResetScopeCurrent", ((ListBoxItem)Preferences.Categories.SelectedItem).Content)
                    || ((TextBlock)Choice.AllPreferencesOption.Content).Text != LanguageService.Text("ResetScopeAll"))
                    throw new InvalidOperationException("The reset chooser must be owned, localized and default to the current category.");
                CheckSquareWindowCorners(Choice);
                Passed?.Add("Reset opens a localized modal chooser defaulting to the current category");
                if (Scope == PreferenceResetScope.All) Choice.AllPreferencesOption.IsChecked = true;
                else if (Scope is not null && Choice.SelectedScope != Scope) throw new InvalidOperationException("Incorrect current reset category.");
                Choice.UpdateLayout();
                if (AppMain.ScreenshotPath is { } Output)
                {
                    var Bitmap = new RenderTargetBitmap((int)Math.Ceiling(Choice.ActualWidth), (int)Math.Ceiling(Choice.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    Bitmap.Render(Choice);
                    var Encoder = new PngBitmapEncoder();
                    Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                    using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, $"ResetPreferences-{LanguageService.CurrentLanguage}-{Choice.SelectedScope}.png"));
                    Encoder.Save(Stream);
                }
                Inspected = true;
                if (Scope is not null) Choice.RestoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else if (Escape) Choice.RaiseEvent(new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(Choice), 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                else Choice.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception Error) { Failure = Error; }
            finally { Timeout.Stop(); if (Choice?.IsVisible == true) Choice.Close(); }
        }));
        Timeout.Start();
        try { Preferences.RestoreDefaultsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        finally { Timeout.Stop(); if (Pending.Status == DispatcherOperationStatus.Pending) Pending.Abort(); }
        if (Failure is not null) throw new InvalidOperationException("Could not inspect reset scope chooser.", Failure);
        if (!Inspected) throw new InvalidOperationException("The reset scope chooser was not inspected.");
        var Deadline = Environment.TickCount64 + 10000;
        while (!Preferences.RestoreDefaultsButton.IsEnabled && Environment.TickCount64 < Deadline) await Task.Delay(20);
        if (!Preferences.RestoreDefaultsButton.IsEnabled) throw new TimeoutException("Restoring preferences did not finish.");
    }

    private async Task RunScopedPreferencesResetSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Scoped preferences reset failed: " + Name);
            Passed.Add(Name);
        }
        foreach (var Scope in new[] { PreferenceResetScope.General, PreferenceResetScope.UI, PreferenceResetScope.TerminalTheme, PreferenceResetScope.Shortcuts })
        {
            await this.RunPreferencesSmokeChecksAsync(Passed, InspectShortcuts: async Dialog =>
            {
                Dialog.LanguagePicker.SelectedValue = "zh-CN";
                Dialog.TimeFormatPicker.SelectedIndex = 1;
                Dialog.ExitOnCloseCheck.IsChecked = true;
                Dialog.CopyOnDropCheck.IsChecked = false;
                Dialog.StartOnLoginCheck.IsChecked = true;
                Dialog.DefaultFileManagerCheck.IsChecked = true;
                Dialog.GridLinesCheck.IsChecked = true;
                Dialog.ShowBookmarkPathsCheck.IsChecked = true;
                Dialog.ShowSidebarDriveUsageCheck.IsChecked = true;
                Dialog.SeparatePinnedTabsCheck.IsChecked = true;
                Dialog.FontSizePickers["Tabs"].SelectedItem = 17d;
                Dialog.ColumnChecks[nameof(FileEntry.Size)].IsChecked = false;
                Dialog.TerminalFontSizePicker.SelectedItem = 21d;
                Dialog.TerminalBackgroundEditor.Text = "#FFFFFF";
                Dialog.TerminalAutomaticForeground.IsChecked = false;
                Dialog.TerminalForegroundEditor.Text = "#66D9EF";
                Check(Dialog.SetShortcut("Search", "F8"), "Scoped reset fixture assigns a custom search shortcut");
                Dialog.Categories.SelectedItem = Scope switch
                {
                    PreferenceResetScope.General => Dialog.GeneralCategory,
                    PreferenceResetScope.UI => Dialog.UICategory,
                    PreferenceResetScope.TerminalTheme => Dialog.TerminalThemeCategory,
                    _ => Dialog.ShortcutsCategory
                };
                var SelectedCategory = Dialog.Categories.SelectedItem;
                var Before = JsonSerializer.Serialize(this.CaptureSaveSettings());
                await this.RestorePreferencesSmokeAsync(Dialog, null, Passed: Passed);
                Check(Before == JsonSerializer.Serialize(this.CaptureSaveSettings()) && this.ReadStartOnLogin() && this.ReadDefaultFileManager(),
                    "Cancel leaves all preferences and startup registration unchanged: " + Scope);
                await this.RestorePreferencesSmokeAsync(Dialog, null, Escape: true, Passed: Passed);
                Check(Before == JsonSerializer.Serialize(this.CaptureSaveSettings()) && this.ReadStartOnLogin() && this.ReadDefaultFileManager(),
                    "Escape leaves all preferences unchanged: " + Scope);
                await this.RestorePreferencesSmokeAsync(Dialog, Scope, Passed: Passed);
                Check(ReferenceEquals(SelectedCategory, Dialog.Categories.SelectedItem), "Scoped reset keeps the selected category: " + Scope);
                var GeneralReset = Scope == PreferenceResetScope.General;
                Check(LanguageService.CurrentLanguage == (GeneralReset ? "en-US" : "zh-CN")
                    && this.Settings.Use12HourClock == !GeneralReset && this.Settings.ExitOnClose == !GeneralReset
                    && this.Settings.CopyOnDrop == GeneralReset && this.ReadStartOnLogin() == !GeneralReset && this.ReadDefaultFileManager() == !GeneralReset,
                    "Only General reset changes language, clock, close behavior, copy-on-drop and startup: " + Scope);
                var UIReset = Scope == PreferenceResetScope.UI;
                Check(this.Settings.ShowGridLines == !UIReset && this.Settings.ShowBookmarkPaths == !UIReset
                    && this.Settings.ShowSidebarDriveUsage == !UIReset && this.Settings.SeparatePinnedTabs == !UIReset
                    && this.Settings.FontSizes!["Tabs"] == (UIReset ? 13 : 17)
                    && (this.Settings.HiddenColumns?.Contains(nameof(FileEntry.Size)) == true) == !UIReset,
                    "Only UI reset changes panel fonts, column visibility and display options: " + Scope);
                var TerminalReset = Scope == PreferenceResetScope.TerminalTheme;
                Check(this.Settings.TerminalFontSize == (TerminalReset ? 15 : 21)
                    && this.Settings.TerminalBackgroundColor == (TerminalReset ? "#1E1E1E" : "#FFFFFF")
                    && this.Settings.TerminalForegroundColor == (TerminalReset ? null : "#66D9EF"),
                    "Only Terminal Theme reset changes terminal fonts and colors: " + Scope);
                Check(this.ShortcutBindings["Search"] == (Scope == PreferenceResetScope.Shortcuts ? "Ctrl+F" : "F8")
                    && Dialog.ShortcutEditors["Search"].Text == this.ShortcutBindings["Search"],
                    "Only Keyboard Shortcuts reset changes custom bindings and refreshes the editor: " + Scope);
                var SavedFile = Path.Combine(AppMain.SmokeRoot!, "ScopedReset-" + Scope + ".json");
                Check(this.SaveSettings(SavedFile)
                    && JsonSerializer.Serialize(ReadSettings(SavedFile)) == JsonSerializer.Serialize(this.CaptureSaveSettings()),
                    "Scoped reset persists and reloads the same preferences: " + Scope);
            });
        }
        foreach (var Language in LanguageService.SupportedLanguages)
            await this.RunPreferencesSmokeChecksAsync(Passed, Language.Code, InspectShortcuts: async Dialog =>
            {
                Dialog.Categories.SelectedItem = Dialog.UICategory;
                await this.RestorePreferencesSmokeAsync(Dialog, null, Passed: Passed);
            });
    }
}
