using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    internal MainWindow HostWindow { get; }
    private readonly string? InitialPath;
    private readonly bool RunStartupChecks;
    private readonly AppSettings? InheritedSettings;
    private readonly FolderNode[]? InitialRoots;
    private readonly bool RefreshInheritedRoots;
    private bool PageLoaded;
    private IInputElement? SavedFocus;
    private bool RestoringTabFocus;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string TabPath => this.CurrentPath.Length > 0 ? this.CurrentPath : this.InitialPath ?? "";
    public string TabAlias { get; private set; } = "";
    public string TabTitle => this.TabAlias.Length > 0 ? this.TabAlias : this.IsComputer ? LanguageService.Text("DirectoryTree") : this.IsRecycleBin ? LanguageService.Text("RecycleBin") : this.TabPath.Length == 0 ? LanguageService.Text("NewTab")
        : Path.GetFileName(Path.TrimEndingDirectorySeparator(this.TabPath)) is { Length: > 0 } Name ? Name : this.TabPath;
    public bool IsTabSelected { get; private set; }
    public bool IsTabPinned { get; private set; }

    internal void SetTabPinned(bool Pinned)
    {
        this.IsTabPinned = Pinned;
        this.PropertyChanged?.Invoke(this, new(nameof(IsTabPinned)));
    }
    internal bool IsOperationBusy => this.OperationBusy;
    internal AppSettings PageSettings => this.Settings;
    internal bool IsInitialLayoutReady => this.InitialLayoutReady.Task.IsCompletedSuccessfully;
    internal bool IsInitialContentReady { get; private set; }
    private int InitialNavigationVersion;
    private bool StartupInteraction;
    internal FolderNode[] CloneRootNodes() => this.Roots.Select(Node => Node.CloneLoadedTree()).ToArray();
    internal FolderNode[] CloneRootEntries() => this.Roots.Select(Node => Node.CloneRootEntry()).ToArray();

    internal void SetTabAlias(string Alias)
    {
        this.TabAlias = Alias.Trim();
        this.NotifyTabHeader();
    }

    private void NotifyTabHeader()
    {
        this.PropertyChanged?.Invoke(this, new(nameof(TabPath)));
        this.PropertyChanged?.Invoke(this, new(nameof(TabTitle)));
    }

    internal void SetTabSelected(bool Selected)
    {
        if (this.IsTabSelected == Selected) return;
        if (!Selected) this.CancelInlineRename();
        if (!Selected) this.EndMarqueeSelection();
        if (!Selected) this.CancelSidebarFolderClick();
        if (!Selected) this.ClearFileTransferFeedback();
        if (!Selected && this.IsKeyboardFocusWithin) this.SavedFocus = Keyboard.FocusedElement;
        this.IsTabSelected = Selected;
        if (Selected) this.QueueFolderSizes();
        else this.CancelFolderSizes();
        if (Selected) this.UpdateActions();
        this.PropertyChanged?.Invoke(this, new(nameof(IsTabSelected)));
    }

    internal void RestoreTabFocus()
    {
        if (!this.IsInitialContentReady) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!this.IsTabSelected || this.WindowClosed || !ReferenceEquals(this.HostWindow.ActiveTab, this)) return;
            this.RestoringTabFocus = true;
            try
            {
                if (this.SavedFocus is UIElement { IsVisible: true, IsEnabled: true } Element) Element.Focus();
                else this.ActiveFileControl.Focus();
            }
            finally { this.RestoringTabFocus = false; }
        });
    }

    internal Rect SplitDropBounds(UIElement RelativeTo)
        => new(this.ContentPanels.TranslatePoint(new Point(), RelativeTo), this.ContentPanels.RenderSize);
    internal void RefreshActiveActions() => this.UpdateActions();

    internal void DismissSearchTips() => this.SearchTips.Visibility = Visibility.Collapsed;
    internal void HandleWindowMouseDown(object Sender, MouseButtonEventArgs E) => this.WindowInputPreviewMouseDown(Sender, E);
    internal void ShowBusyCloseNotice() => this.ShowNotice(LanguageService.Text("BusyCloseNotice"), true);
    internal void ShowTrayUnavailableNotice() => this.ShowNotice(LanguageService.Text("TrayUnavailable"), true);
    internal void PersistPageSettings() => this.SaveSettings();
    internal AppSettings ClonePageSettings() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this.Settings))!;
    internal Task WaitUntilReadyAsync() => this.InitialLayoutReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
    internal void RefreshShellResources() { this.ApplyLanguageResources(); this.ApplyUIFonts(); }

    internal bool TryGetTabCommand(Key Key, ModifierKeys Modifiers, out string Command)
    {
        Command = KeyboardShortcut.Match(this.ShortcutBindings, Key, Modifiers)?.Id ?? "";
        return Command is "NewTab" or "CloseTab" or "NextTab" or "PreviousTab" or "ToggleTabPin";
    }

    internal AppSettings CaptureSharedUIPreferences() => new()
    {
        ShowGridLines = this.Settings.ShowGridLines,
        ShowBookmarkPaths = this.Settings.ShowBookmarkPaths,
        ShowSidebarDriveUsage = this.Settings.ShowSidebarDriveUsage,
        AutoFitColumnsOnNavigation = this.Settings.AutoFitColumnsOnNavigation,
        SeparatePinnedTabs = this.Settings.SeparatePinnedTabs,
        FontSizes = this.Settings.FontSizes is null ? null : new(this.Settings.FontSizes, StringComparer.Ordinal),
        ColumnOrder = this.Settings.ColumnOrder?.ToList(), HiddenColumns = this.Settings.HiddenColumns?.ToList()
    };

    internal void ApplySharedUIPreferences(AppSettings Source)
    {
        if (this.WindowClosed) return;
        this.Settings.ShowGridLines = Source.ShowGridLines;
        this.Settings.ShowBookmarkPaths = Source.ShowBookmarkPaths;
        this.ShowBookmarkPaths = Source.ShowBookmarkPaths;
        this.Settings.ShowSidebarDriveUsage = Source.ShowSidebarDriveUsage;
        this.ShowSidebarDriveUsage = Source.ShowSidebarDriveUsage;
        this.Settings.AutoFitColumnsOnNavigation = Source.AutoFitColumnsOnNavigation;
        this.Settings.SeparatePinnedTabs = Source.SeparatePinnedTabs;
        this.Settings.FontSizes = Source.FontSizes is null ? null : new(Source.FontSizes, StringComparer.Ordinal);
        this.ApplyUIFonts();
        this.ApplyGridLines();
        this.RestoreColumnLayout(Source.ColumnOrder?.ToArray(), Source.HiddenColumns?.ToArray());
        if (Source.ShowSidebarDriveUsage) this.QueueSidebarDriveUsageRefresh();
    }

    internal async Task ApplySharedPreferencesAsync(AppSettings Source)
    {
        if (this.WindowClosed) return;
        var RefreshHidden = this.Settings.ShowHidden != Source.ShowHidden;
        var Shared = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Source))!;
        this.Settings.ShowHidden = Shared.ShowHidden;
        this.Settings.Use12HourClock = Shared.Use12HourClock;
        this.Settings.ExitOnClose = Shared.ExitOnClose;
        this.Settings.CopyOnDrop = Shared.CopyOnDrop;
        this.Settings.CalculateFolderSizes = Shared.CalculateFolderSizes;
        this.ApplySharedUIPreferences(Shared);
        this.HostWindow.UpdatePinnedTabLayout(Shared.SeparatePinnedTabs);
        this.UpdateHiddenItemsMenu();
        this.Settings.Shortcuts = Shared.Shortcuts;
        this.Settings.TerminalFontSize = Shared.TerminalFontSize;
        this.Settings.TerminalBackgroundColor = Shared.TerminalBackgroundColor;
        this.Settings.TerminalForegroundColor = Shared.TerminalForegroundColor;
        this.Settings.ShortcutDefaultsRevision = Source.ShortcutDefaultsRevision;
        this.ShortcutBindings = KeyboardShortcut.Resolve(this.Settings.Shortcuts);
        this.ApplyTerminalTheme();
        foreach (var Entry in this.Entries) Entry.Use12HourClock = Shared.Use12HourClock;
        this.ChangeLanguage(Shared.Language ?? LanguageService.CurrentLanguage, Persist: false);
        if (RefreshHidden && this.CurrentPath.Length > 0) await this.RefreshAsync(true);
    }
}
