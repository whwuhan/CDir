using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunSidebarLayoutSmokeChecksAsync(List<string> Passed)
    {
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.StartupPath!, "SidebarLayout-" + FixtureId)).FullName;
        var Parent = Directory.CreateDirectory(Path.Combine(Fixture, "Parent")).FullName;
        var ChildPath = Directory.CreateDirectory(Path.Combine(Parent, "Child")).FullName;
        var Deep = Directory.CreateDirectory(Path.Combine(ChildPath, "Deep")).FullName;
        var Marker = Path.Combine(Fixture, ".CDirSidebarLayoutOwner");
        File.WriteAllText(Marker, FixtureId);
        var SettingsFile = Path.Combine(Fixture, "Settings.json");
        ExplorerTab? Probe = null;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Sidebar layout UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Probe?.UpdateLayout();
        }
        async Task Open(string FileName)
        {
            var Shell = new MainWindow(FileName) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe = Shell.ActiveTab!;
            Shell.Show();
            await Probe.InitialLayoutReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Settle();
        }
        static string[] LayoutState(Dictionary<string, SidebarTreeLayout> Layouts) => Layouts
            .SelectMany(Pair => (Pair.Value.ExpandedPaths ?? []).Select(Path => Pair.Key + ":expanded:" + Path)
                .Concat((Pair.Value.SelectedPaths ?? []).Select(Path => Pair.Key + ":selected:" + Path)))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppSettings
            {
                LastPath = Parent, SidebarWidth = 365, FavoritesExpanded = false, DirectoryTreeExpanded = false,
                CustomCategories =
                [
                    new SidebarCategory { Id = "layout-work", Name = "Work", Folders = [new CategoryFolder { FullPath = Parent }] },
                    new SidebarCategory { Id = "layout-other", Name = "Other", IsExpanded = false, Folders = [new CategoryFolder { FullPath = Parent }] }
                ]
            }));
            await Open(SettingsFile);
            Check(Math.Abs(Probe!.SidebarColumn.ActualWidth - 365) < 1 && !Probe.FavoritesSection.IsExpanded && !Probe.DirectoryTreeSection.IsExpanded,
                "Startup restores the saved sidebar width and leaves collapsed built-in sections closed");
            var Splitter = (ITransformProvider)new GridSplitterAutomationPeer(Probe.SidebarSplitter).GetPattern(PatternInterface.Transform)!;
            Splitter.Move(55, 0);
            await Settle();
            var SavedWidth = Probe.SidebarColumn.ActualWidth;
            Probe.ChangeShowBookmarkPaths(true);
            Probe.ChangeShowSidebarDriveUsage(true);
            var Work = Probe.CustomCategories.Single(Category => Category.Id == "layout-work");
            var WorkSection = Probe.SidebarSectionElement(Work)!;
            await Probe.NavigateAsync(Deep, TreeKey: Probe.TreeKeyForSection(WorkSection));
            var Root = Work.Folders[0].Node;
            var Child = Root.Children.Single(Node => SamePath(Node.FullPath, ChildPath));
            Probe.SyncingTree = true;
            try
            {
                // Keep a remembered expanded child inside a collapsed parent.
                Root.IsExpanded = false;
                Probe.CollapseTreeBranches(Probe.Roots);
                WorkSection.IsExpanded = false;
                Probe.FavoritesSection.IsExpanded = false;
                Probe.DirectoryTreeSection.IsExpanded = false;
            }
            finally { Probe.SyncingTree = false; }
            Check(Child.IsExpanded, "The closing fixture remembers a child expansion hidden inside a collapsed parent");
            var QuickRoot = Probe.FavoritesList.Items.OfType<FavoriteLocation>().First().Node;
            await Probe.EnsureChildrenAsync(QuickRoot);
            QuickRoot.IsExpanded = true;
            await Settle();
            var ExpectedTrees = LayoutState(Probe.CaptureSidebarTreeLayouts());
            Probe.HostWindow.RequestExit();
            Probe = null;
            var Saved = ReadSettings(SettingsFile);
            Check(Math.Abs(Saved.SidebarWidth - SavedWidth) < 1 && Saved.NavigationTreeKey == "custom:layout-work"
                && LayoutState(Saved.SidebarTreeLayouts!).SequenceEqual(ExpectedTrees),
                "The real Closing handler writes sidebar width, navigation source and independent tree states before teardown");

            // Old releases remembered a separate selected node in every category.
            Saved.SidebarTreeLayouts!["folder-tree"].SelectedPaths = [Parent];
            Saved.SidebarTreeLayouts["custom:layout-other"].SelectedPaths = [Parent];
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Saved));
            await Open(SettingsFile);
            Check(Probe!.CaptureSidebarTreeLayouts().Values.Sum(Layout => Layout.SelectedPaths!.Count) == 1
                && Probe.CaptureSidebarTreeLayouts()["folder-tree"].SelectedPaths!.Count == 0
                && Probe.CaptureSidebarTreeLayouts()["custom:layout-other"].SelectedPaths!.Count == 0,
                "Upgrading saved per-category selections restores only one highlight in the active category");
            Check(Math.Abs(Probe!.SidebarColumn.ActualWidth - SavedWidth) < 1
                && Probe.Settings.ShowBookmarkPaths && Probe.ShowBookmarkPaths
                && Probe.Settings.ShowSidebarDriveUsage && Probe.ShowSidebarDriveUsage
                && SamePath(Probe.CurrentPath, Deep) && Probe.NavigationTreeKey == "custom:layout-work"
                && !Probe.DirectoryTreeSection.IsExpanded && !Probe.FavoritesSection.IsExpanded
                && Probe.CustomCategories.All(Category => !Category.IsExpanded)
                && LayoutState(Probe.CaptureSidebarTreeLayouts()).SequenceEqual(ExpectedTrees),
                "Reopening preserves each section and hidden child expansion without opening My Computer for the previous category path");
            Probe.ToggleSidebarClick(Probe.SidebarToggleButton, new RoutedEventArgs());
            Probe.HostWindow.RequestExit();
            Probe = null;
            await Open(SettingsFile);
            Check(!Probe!.Settings.ShowSidebar && Probe.SidebarColumn.Width.Value == 0
                && Math.Abs(Probe.ExpandedSidebarWidth.Value - SavedWidth) < 1
                && LayoutState(Probe.CaptureSidebarTreeLayouts()).SequenceEqual(ExpectedTrees),
                "A hidden sidebar retains its expanded width and category tree states across a real close and reopen");
            Probe.ToggleSidebarClick(Probe.SidebarToggleButton, new RoutedEventArgs());
            await Settle();
            Check(Math.Abs(Probe.SidebarColumn.ActualWidth - SavedWidth) < 1,
                "Showing the reopened sidebar restores the previous splitter width");
            Probe.HostWindow.RequestExit();
            Probe = null;

            var LegacyFile = Path.Combine(Fixture, "Legacy.json");
            File.WriteAllText(LegacyFile, JsonSerializer.Serialize(new
            {
                LastPath = Parent, SidebarWidth = -20, FavoritesExpanded = false, DirectoryTreeExpanded = false
            }));
            await Open(LegacyFile);
            Check(Math.Abs(Probe!.SidebarColumn.ActualWidth - DefaultSettings.Current.SidebarWidth) < 1 && !Probe.FavoritesSection.IsExpanded && !Probe.DirectoryTreeSection.IsExpanded,
                "Older settings and invalid widths fall back safely without reopening collapsed sections");
            Check(!Probe.Settings.ShowSidebarDriveUsage && !Probe.ShowSidebarDriveUsage,
                "Legacy settings leave drive usage disabled by default");
            Check(!Probe.Settings.ShowBookmarkPaths && !Probe.ShowBookmarkPaths,
                "Legacy settings leave bookmark paths disabled by default");
            Probe.HostWindow.RequestExit();
            Probe = null;
        }
        finally
        {
            Probe?.HostWindow.RequestExit();
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != FixtureId
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned sidebar layout fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
