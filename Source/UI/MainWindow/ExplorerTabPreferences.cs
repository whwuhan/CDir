using System.Windows;
using CDir.Localization;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private void ChangeSharedUI(Action Change)
    {
        Change();
        this.HostWindow.SynchronizeUIPreferences(this);
    }

    private bool TryChangeSharedUI(Func<bool> Change)
    {
        if (!Change()) return false;
        this.HostWindow.SynchronizeUIPreferences(this);
        return true;
    }

    public static readonly DependencyProperty ShowBookmarkPathsProperty = DependencyProperty.Register(
        nameof(ShowBookmarkPaths), typeof(bool), typeof(ExplorerTab), new PropertyMetadata(false));
    public bool ShowBookmarkPaths { get => (bool)GetValue(ShowBookmarkPathsProperty); private set => SetValue(ShowBookmarkPathsProperty, value); }

    public static readonly DependencyProperty ShowSidebarDriveUsageProperty = DependencyProperty.Register(
        nameof(ShowSidebarDriveUsage), typeof(bool), typeof(ExplorerTab), new PropertyMetadata(false));
    public bool ShowSidebarDriveUsage { get => (bool)GetValue(ShowSidebarDriveUsageProperty); private set => SetValue(ShowSidebarDriveUsageProperty, value); }

    private void ChangeShowSidebarDriveUsage(bool Show)
    {
        if (this.WindowClosed) return;
        this.Settings.ShowSidebarDriveUsage = Show;
        this.ShowSidebarDriveUsage = Show;
        if (Show) this.QueueSidebarDriveUsageRefresh();
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }

    private void ChangeAutoFitColumnsOnNavigation(bool Fit)
    {
        if (this.WindowClosed) return;
        this.Settings.AutoFitColumnsOnNavigation = Fit;
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }

    private void ChangeSeparatePinnedTabs(bool Separate)
    {
        this.Settings.SeparatePinnedTabs = Separate;
        this.HostWindow.UpdatePinnedTabLayout(Separate);
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }

    private void ChangeShowBookmarkPaths(bool Show)
    {
        if (this.WindowClosed) return;
        this.Settings.ShowBookmarkPaths = Show;
        this.ShowBookmarkPaths = Show;
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }

    private async Task RestoreDefaultPreferencesAsync(PreferenceResetScope Scope)
    {
        if (this.WindowClosed) return;
        var Defaults = DefaultSettings.Current;
        var ResetGeneral = Scope is PreferenceResetScope.All or PreferenceResetScope.General;
        var RefreshHidden = ResetGeneral && this.Settings.ShowHidden != Defaults.ShowHidden;
        if (ResetGeneral)
        {
            this.Settings.ShowHidden = Defaults.ShowHidden;
            this.Settings.Use12HourClock = Defaults.Use12HourClock;
            this.Settings.ExitOnClose = Defaults.ExitOnClose;
            this.Settings.CopyOnDrop = Defaults.CopyOnDrop;
            this.Settings.CalculateFolderSizes = Defaults.CalculateFolderSizes;
            this.QueueFolderSizes();
            this.UpdateHiddenItemsMenu();
            foreach (var Entry in this.Entries) Entry.Use12HourClock = Defaults.Use12HourClock;
            this.ChangeLanguage(Defaults.Language, Persist: false);
        }
        if (Scope is PreferenceResetScope.All or PreferenceResetScope.UI)
        {
            this.Settings.AutoFitColumnsOnNavigation = Defaults.AutoFitColumnsOnNavigation;
            this.Settings.ShowGridLines = Defaults.ShowGridLines;
            this.Settings.SeparatePinnedTabs = Defaults.SeparatePinnedTabs;
            this.HostWindow.UpdatePinnedTabLayout(Defaults.SeparatePinnedTabs);
            this.Settings.ShowBookmarkPaths = Defaults.ShowBookmarkPaths;
            this.ShowBookmarkPaths = Defaults.ShowBookmarkPaths;
            this.Settings.ShowSidebarDriveUsage = Defaults.ShowSidebarDriveUsage;
            this.ShowSidebarDriveUsage = Defaults.ShowSidebarDriveUsage;
            this.Settings.FontSizes = null;
            this.ApplyUIFonts();
            this.ApplyGridLines();
            this.RestoreColumnLayout(Defaults.ColumnOrder, Defaults.HiddenColumns);
        }
        if (Scope is PreferenceResetScope.All or PreferenceResetScope.TerminalTheme)
        {
            this.Settings.TerminalFontSize = Defaults.TerminalFontSize;
            this.Settings.TerminalBackgroundColor = Defaults.TerminalBackgroundColor;
            this.Settings.TerminalForegroundColor = Defaults.TerminalForegroundColor;
            this.ApplyTerminalTheme();
        }
        if (Scope is PreferenceResetScope.All or PreferenceResetScope.Shortcuts)
        {
            this.Settings.Shortcuts = null;
            this.ShortcutBindings = KeyboardShortcut.Resolve(null);
        }
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
        if (Scope is PreferenceResetScope.All or PreferenceResetScope.UI) this.HostWindow.SynchronizeUIPreferences(this);
        if (RefreshHidden) await this.RefreshAsync(true);
        await this.HostWindow.SynchronizePreferencesAsync(this);
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
        this.UpdateHiddenItemsMenu();
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

    private void ChangeCalculateFolderSizes(bool Calculate)
    {
        if (this.WindowClosed) return;
        this.Settings.CalculateFolderSizes = Calculate;
        this.QueueFolderSizes();
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }

    private bool ReadStartOnLogin()
    {
        try { return StartupService.IsEnabled(); }
        catch (Exception Error) when (Error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            this.ShowNotice(LanguageService.Text("StartOnLoginError", FriendlyError(Error)), true);
            return false;
        }
    }

    private bool ChangeStartOnLogin(bool Enabled)
    {
        try { StartupService.SetEnabled(Enabled); return true; }
        catch (Exception Error) when (Error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            this.ShowNotice(LanguageService.Text("StartOnLoginError", FriendlyError(Error)), true);
            return false;
        }
    }
}
