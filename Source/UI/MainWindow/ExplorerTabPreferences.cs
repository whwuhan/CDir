using System.Windows;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RestoreDefaultPreferencesAsync()
    {
        if (this.WindowClosed) return;
        var Defaults = DefaultSettings.Current;
        var RefreshHidden = this.Settings.ShowHidden != Defaults.ShowHidden;
        this.Settings.ShowHidden = Defaults.ShowHidden;
        this.Settings.ShowGridLines = Defaults.ShowGridLines;
        this.Settings.Use12HourClock = Defaults.Use12HourClock;
        this.Settings.ExitOnClose = Defaults.ExitOnClose;
        this.Settings.CopyOnDrop = Defaults.CopyOnDrop;
        this.Settings.FontSizes = null;
        this.Settings.Shortcuts = null;
        this.Settings.TerminalFontSize = Defaults.TerminalFontSize;
        this.Settings.TerminalBackgroundColor = Defaults.TerminalBackgroundColor;
        this.Settings.TerminalForegroundColor = Defaults.TerminalForegroundColor;
        this.ApplyTerminalTheme();
        this.ShortcutBindings = KeyboardShortcut.Resolve(null);
        this.ApplyUIFonts();
        this.ApplyGridLines();
        this.RestoreColumnLayout(Defaults.ColumnOrder, Defaults.HiddenColumns);
        foreach (var Entry in this.Entries) Entry.Use12HourClock = Defaults.Use12HourClock;
        this.ChangeLanguage(Defaults.Language, Persist: false);
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
        if (RefreshHidden) await this.RefreshAsync(true);
    }

    private void ApplyGridLines() => this.FileGridLines.Visibility = this.Settings.ShowGridLines ? Visibility.Visible : Visibility.Collapsed;

    private void ChangeGridLines(bool ShowGridLines)
    {
        this.Settings.ShowGridLines = ShowGridLines;
        this.ApplyGridLines();
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }

    private async Task ChangeShowHiddenAsync(bool ShowHidden)
    {
        if (this.WindowClosed || this.Settings.ShowHidden == ShowHidden) return;
        this.Settings.ShowHidden = ShowHidden;
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
        await this.RefreshAsync(true);
    }

    private void ChangeTimeFormat(bool Use12HourClock)
    {
        if (this.WindowClosed) return;
        this.Settings.Use12HourClock = Use12HourClock;
        foreach (var Entry in this.Entries) Entry.Use12HourClock = Use12HourClock;
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }

    private void ChangeExitOnClose(bool ExitOnClose)
    {
        if (this.WindowClosed) return;
        this.Settings.ExitOnClose = ExitOnClose;
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }

    private void ChangeCopyOnDrop(bool CopyOnDrop)
    {
        if (this.WindowClosed) return;
        this.Settings.CopyOnDrop = CopyOnDrop;
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }
}
