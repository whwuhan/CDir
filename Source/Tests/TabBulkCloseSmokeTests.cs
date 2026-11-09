using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunTabBulkCloseSmokeChecksAsync(List<string> Passed)
    {
        var SettingsFile = Path.Combine(AppMain.SmokeRoot!, "TabBulkClose.json");
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = this.CurrentPath, SeparatePinnedTabs = true }));
        MainWindow? Probe = null;
        ContextMenu? Menu = null;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Bulk tab close check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Open()
        {
            Probe = new MainWindow(SettingsFile, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show();
            await Probe.ActiveTab!.WaitUntilReadyAsync();
            await Probe.WorkspaceRestored;
        }
        async Task<ExplorerTab> Add(string Alias, bool Pinned = false)
        {
            var Page = Probe!.AddTab();
            await Page.WaitUntilReadyAsync();
            Page.SetTabAlias(Alias);
            if (Pinned) Probe.SetTabPinned(Page, true);
            return Page;
        }
        async Task<(double ElapsedMs, int Writes, int Captures)> CloseOthers(ExplorerTab Anchor)
        {
            Probe!.UpdateLayout();
            var Header = Probe.Headers.Single(Header => ReferenceEquals(Header.Page, Anchor));
            var Items = Probe.PinnedHeaders.Contains(Header) ? Probe.PinnedTabHeaders : Probe.TabHeaders;
            var Presenter = (ContentPresenter)Items.ItemContainerGenerator.ContainerFromItem(Header);
            var Surface = FindVisualChild<Border>(Presenter)!;
            Menu = Surface.ContextMenu;
            Menu.StaysOpen = true;
            Menu.PlacementTarget = Surface;
            Menu.IsOpen = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var Item = Menu.Items.OfType<MenuItem>().Single(Item => Item.Tag?.ToString() == "CloseOtherTabs");
            Check(Item.IsEnabled, "Bulk-close performance checks invoke the rendered Close Others menu");
            var Writes = Probe.WorkspaceWriteCount;
            var Captures = Probe.WorkspaceCaptureCount;
            var Timer = Stopwatch.StartNew();
            Menu.IsOpen = false;
            Item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Timer.Stop();
            return (Timer.Elapsed.TotalMilliseconds, Probe.WorkspaceWriteCount - Writes, Probe.WorkspaceCaptureCount - Captures);
        }
        try
        {
            await Open();
            var Anchor = Probe!.ActiveTab!;
            Anchor.SetTabAlias("Keep");
            var Pinned = new[] { await Add("Pinned 1", true), await Add("Pinned 2", true) };
            var Targets = new List<ExplorerTab>();
            for (var Index = 0; Index < 20; Index++) Targets.Add(await Add("Close " + Index));
            var Measurement = await CloseOthers(Anchor);
            if (AppMain.ScreenshotPath is { } Output)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!, "TabBulkClosePerformance.json"), JsonSerializer.Serialize(new
                {
                    ClosedTabs = Targets.Count, Measurement.ElapsedMs, Measurement.Writes, Measurement.Captures,
                    SurvivingTabs = Probe.Tabs.Count, ReleaseVersion = typeof(AppMain).Assembly.GetName().Version!.ToString(3)
                }, new JsonSerializerOptions { WriteIndented = true }));
            Check(Probe.Tabs.SequenceEqual(Pinned.Append(Anchor)) && ReferenceEquals(Probe.ActiveTab, Anchor)
                && Targets.All(Page => Page.WindowClosed && !Page.RefreshTimer.IsEnabled && !Page.DriveUsageTimer.IsEnabled),
                "Closing twenty other tabs preserves both pinned workspaces, selects the anchor once and releases every closed tab");
            Check(Measurement.Writes == 1 && Measurement.Captures == 1,
                $"Bulk close captures and writes only the final workspace (writes={Measurement.Writes}, captures={Measurement.Captures}, elapsed={Measurement.ElapsedMs:F1} ms)");
            var Saved = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(SettingsFile + ".workspace.json"))!;
            Check(Saved.Tabs.Select(Tab => Tab.Alias).SequenceEqual(new[] { "Pinned 1", "Pinned 2", "Keep" })
                && Saved.ActiveTab == 2, "The final bulk-close state is durably saved before the menu command returns");
            Probe.RequestExit(); Probe = null;
            await Open();
            Check(Probe!.Tabs.Select(Page => Page.TabAlias).SequenceEqual(new[] { "Pinned 1", "Pinned 2", "Keep" })
                && Probe.Tabs.Take(2).All(Page => Page.IsTabPinned) && Probe.ActiveTab!.TabAlias == "Keep",
                "Reopening after bulk close restores the exact surviving tabs, pin states and active workspace");
            Anchor = Probe.Tabs.Single(Page => Page.TabAlias == "Keep");
            var SelectedPinned = Probe.Tabs[0];
            await Add("Extra 1"); await Add("Extra 2");
            Probe.SelectTab(SelectedPinned);
            Measurement = await CloseOthers(Anchor);
            Check(ReferenceEquals(Probe.ActiveTab, SelectedPinned) && Probe.Tabs.Count == 3
                && Measurement.Writes == 1 && Measurement.Captures == 1,
                "Bulk close preserves a surviving active pinned tab and still saves only once");
        }
        finally
        {
            if (Menu is not null) Menu.IsOpen = false;
            Probe?.RequestExit();
        }
    }
}
