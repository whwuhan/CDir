using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CDir.Models;
using CDir.Services;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunSharedUIPreferencesSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Shared UI preferences check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Shared UI preferences")).FullName;
        var SettingsFile = Path.Combine(Root, "Settings.json");
        var State = new WorkspaceState { Width = 1500, Height = 850, ActiveTab = 0 };
        for (var Index = 0; Index < 3; Index++)
        {
            var Folder = Directory.CreateDirectory(Path.Combine(Root, "Folder " + Index)).FullName;
            File.WriteAllText(Path.Combine(Folder, "File.txt"), "Owned shared UI fixture");
            State.Tabs.Add(new WorkspaceTabState { Alias = "Shared " + Index, Settings = new AppSettings
            { LastPath = Folder, Language = LanguageService.CurrentLanguage, ExitOnClose = true, SidebarTreeLayouts = [] } });
        }
        State.Layout = new WorkspacePaneState { Split = "Horizontal", Ratio = .5,
            First = new WorkspacePaneState { Tabs = [0, 1], ActiveTab = 0 },
            Second = new WorkspacePaneState { Tabs = [2], ActiveTab = 2 } };
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(State.Tabs[0].Settings));
        var Probe = new MainWindow(SettingsFile, RestoreWorkspace: true, InitialWorkspace: State)
            { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            Probe.Show(); await Probe.WorkspaceRestored;
            var Source = Probe.ActiveTab!;
            var Paths = Probe.Tabs.Select(Tab => Tab.TabPath).ToArray();
            Check(Probe.Tabs.Count == 3 && Probe.Panes.Count == 2 && Probe.Tabs.Any(Tab => !Tab.IsVisible),
                "The UI sharing fixture includes a hidden tab and another visible pane");
            await Source.RunPreferencesSmokeChecksAsync(Passed, InspectShortcuts: async Dialog =>
            {
                Dialog.Categories.SelectedItem = Dialog.UICategory;
                Dialog.BoldAllFontsCheck.IsChecked = true;
                Dialog.GridLinesCheck.IsChecked = true;
                Dialog.ShowBookmarkPathsCheck.IsChecked = true;
                Dialog.ShowSidebarDriveUsageCheck.IsChecked = true;
                Dialog.AutoFitColumnsOnNavigationCheck.IsChecked = true;
                Dialog.SeparatePinnedTabsCheck.IsChecked = true;
                Dialog.FontSizePickers["FileList"].SelectedItem = 16.0;
                Dialog.ColumnChecks[nameof(FileEntry.Size)].IsChecked = false;
                Check(Dialog.IsVisible && Probe.Tabs.All(Tab => Tab.Settings.BoldAllFonts && Tab.FileGrid.FontWeight == FontWeights.Bold && Tab.Settings.ShowGridLines && Tab.Settings.ShowBookmarkPaths
                    && Tab.Settings.ShowSidebarDriveUsage && Tab.Settings.AutoFitColumnsOnNavigation && Tab.Settings.SeparatePinnedTabs
                    && Tab.ShowBookmarkPaths && Tab.ShowSidebarDriveUsage && Tab.FileGridLines.Visibility == Visibility.Visible
                    && Tab.FileGrid.FontSize == 16 && Tab.Settings.FontSizes!["FileList"] == 16
                    && Tab.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Size)).Visibility == Visibility.Collapsed),
                    "All existing tabs and panes update every UI option font and column visibility before Preferences closes");
                Check(Probe.Tabs.Select(Tab => Tab.TabPath).SequenceEqual(Paths)
                    && !ReferenceEquals(Source.Settings.FontSizes, Probe.Tabs[1].Settings.FontSizes),
                    "Sharing UI settings preserves each tab's folder and independent mutable font settings");
                var Late = new ExplorerTab(Probe, InitialPath: Root, InitialSettings: new AppSettings
                    { LastPath = Root, ExitOnClose = true, Language = LanguageService.CurrentLanguage });
                Probe.ApplyCurrentUIPreferences(Late);
                try
                {
                    Check(Late.Settings.BoldAllFonts && Late.Settings.ShowGridLines && Late.ShowBookmarkPaths && Late.ShowSidebarDriveUsage
                        && Late.Settings.FontSizes?.GetValueOrDefault("FileList") == 16
                        && Late.Settings.HiddenColumns!.Contains(nameof(FileEntry.Size)),
                        "A tab constructed later from older saved state adopts the window's current UI preferences");
                }
                finally { Late.DisposePage(); }
                var NewTab = Probe.AddTab(); await NewTab.WaitUntilReadyAsync();
                Check(NewTab.Settings.BoldAllFonts && NewTab.Settings.ShowGridLines && NewTab.ShowBookmarkPaths && NewTab.ShowSidebarDriveUsage
                    && NewTab.FileGrid.FontSize == 16
                    && NewTab.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Size)).Visibility == Visibility.Collapsed,
                    "A new tab immediately inherits the current shared UI preferences");
                Check(Probe.PersistWorkspaceNow() && DurableJsonStore.Read<WorkspaceState>(SettingsFile + ".workspace.json") is { Tabs.Count: 4 } Saved
                    && Saved.Tabs.All(Tab => Tab.Settings.BoldAllFonts && Tab.Settings.ShowGridLines && Tab.Settings.ShowBookmarkPaths
                        && Tab.Settings.FontSizes!["FileList"] == 16 && Tab.Settings.HiddenColumns!.Contains(nameof(FileEntry.Size))),
                    "The complete workspace persists the updated UI preferences for every tab while the dialog remains open");
                await this.RestorePreferencesSmokeAsync(Dialog, PreferenceResetScope.UI, Passed: Passed);
                var Deadline = Environment.TickCount64 + 30000;
                while (!Dialog.RestoreDefaultsButton.IsEnabled && Environment.TickCount64 < Deadline) await Task.Delay(20);
                Check(Dialog.BoldAllFontsCheck.IsChecked == false && Probe.Tabs.All(Tab => !Tab.Settings.BoldAllFonts && Tab.FileGrid.FontWeight == FontWeights.Normal
                    && Tab.Settings.ShowGridLines == DefaultSettings.Current.ShowGridLines
                    && Tab.Settings.ShowBookmarkPaths == DefaultSettings.Current.ShowBookmarkPaths
                    && Tab.Settings.ShowSidebarDriveUsage == DefaultSettings.Current.ShowSidebarDriveUsage
                    && Tab.Settings.AutoFitColumnsOnNavigation == DefaultSettings.Current.AutoFitColumnsOnNavigation
                    && Tab.Settings.SeparatePinnedTabs == DefaultSettings.Current.SeparatePinnedTabs
                    && Tab.FileGrid.FontSize == DefaultSettings.Current.FontSizes["FileList"]),
                    "Resetting UI preferences updates hidden visible and newly created tabs together");
            });
        }
        finally { Probe.RequestExit(); }
    }
}
