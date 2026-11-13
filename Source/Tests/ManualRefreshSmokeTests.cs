using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunManualRefreshSmokeChecksAsync(List<string> Passed)
    {
        var Initial = this.CurrentPath;
        var InitialView = this.Settings.FileView;
        var InitialSize = this.Settings.FileViewIconSize;
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "ManualRefresh-" + Guid.NewGuid().ToString("N"))).FullName;
        for (var Index = 0; Index < 120; Index++) File.WriteAllText(Path.Combine(Fixture, $"File {Index:000}.txt"), "Owned refresh fixture");
        var Target = Path.Combine(Fixture, "File 030.txt");
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Manual refresh check failed: " + Name);
            Passed.Add(Name);
            AppMain.RecordSmokeProgress("Manual refresh: " + Name);
        }
        try
        {
            await this.NavigateAsync(Fixture, RecordHistory: false);
            this.Watcher!.EnableRaisingEvents = false;
            this.RefreshTimer.Stop();
            var PassiveView = this.View;
            var PassiveEntry = this.Entries.Single(Entry => SamePath(Entry.FullPath, Target));
            File.WriteAllText(Target, "Changed before a passive refresh");
            await this.RefreshCurrentFolderAsync(RefreshTree: false);
            Check(ReferenceEquals(PassiveView, this.View) && ReferenceEquals(PassiveEntry, this.Entries.Single(Entry => SamePath(Entry.FullPath, Target)))
                && PassiveEntry.Size == new FileInfo(Target).Length,
                "Background refresh updates metadata while retaining the current view and file objects");
            var RemovedIndex = 119;
            foreach (var Mode in Enum.GetValues<FileViewMode>())
            {
                this.ChangeFileView(Mode, FocusPanel: false, SaveImmediately: false);
                this.SelectPath(Target);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                this.UpdateLayout();
                var Viewer = FindVisualChild<ScrollViewer>(this.ActiveFileControl)!;
                Viewer.ScrollToVerticalOffset(2);
                this.UpdateLayout();
                var Vertical = Viewer.VerticalOffset;
                var Horizontal = Viewer.HorizontalOffset;
                var Previous = this.View;
                var PreviousEntry = this.Entries.Single(Entry => SamePath(Entry.FullPath, Target));
                var Navigation = this.NavigationVersion;
                var History = this.History.ToArray();
                var Added = Path.Combine(Fixture, "Added-" + Mode + ".txt");
                var Removed = Path.Combine(Fixture, $"File {RemovedIndex--:000}.txt");
                File.WriteAllText(Added, "Only manual refresh can discover this file");
                File.Delete(Removed);
                File.WriteAllText(Target, "Updated for " + Mode);
                var PaintedClearedView = false;
                void Changed(object Sender, SelectionChangedEventArgs E)
                {
                    if (this.FileGrid.ItemsSource is not null) return;
                    Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        if (this.FileGrid.ItemsSource is not null || this.FileTiles.ItemsSource is not null) return;
                        PaintedClearedView = true;
                        if (Mode == FileViewMode.LargeIcons) this.CaptureAddressBreadcrumbPreview("ManualRefresh-Cleared.png");
                    });
                }
                this.FileGrid.SelectionChanged += Changed;
                try
                {
                    Check(await this.HandleShortcutAsync(Key.F5, ModifierKeys.None, InText: false, InFileList: true), Mode + ": F5 dispatches manual refresh");
                    await this.SearchTask;
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    this.UpdateLayout();
                    Check(PaintedClearedView && !ReferenceEquals(Previous, this.View)
                        && !ReferenceEquals(PreviousEntry, this.Entries.Single(Entry => SamePath(Entry.FullPath, Target))),
                        Mode + ": F5 paints a cleared frame and rebuilds the view and file objects");
                    Check(this.Entries.Any(Entry => SamePath(Entry.FullPath, Added)) && !this.Entries.Any(Entry => SamePath(Entry.FullPath, Removed))
                        && this.Entries.Single(Entry => SamePath(Entry.FullPath, Target)).Size == new FileInfo(Target).Length,
                        Mode + ": F5 rereads additions deletions and metadata with the filesystem watcher disabled");
                    Check(this.SelectedEntries().Select(Entry => Entry.FullPath).SequenceEqual([Target])
                        && Math.Abs(Viewer.VerticalOffset - Vertical) < 0.5 && Math.Abs(Viewer.HorizontalOffset - Horizontal) < 0.5
                        && Navigation == this.NavigationVersion && this.History.SequenceEqual(History),
                        Mode + ": F5 preserves selection scroll position current location and navigation history");
                    if (Mode == FileViewMode.LargeIcons)
                    {
                        var Panel = FindVisualChild<CDir.Controls.FileTilePanel>(this.FileTiles)!;
                        var Viewport = new Rect(Panel.HorizontalOffset, Panel.VerticalOffset, Panel.ViewportWidth, Panel.ViewportHeight);
                        Check(Enumerable.Range(0, this.FileTiles.Items.Count).Where(Index => Panel.ItemBounds(Index).IntersectsWith(Viewport))
                            .All(Index => this.FileTiles.ItemContainerGenerator.ContainerFromIndex(Index) is ListBoxItem
                                { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } Container && this.FileTiles.IsAncestorOf(Container)),
                            "All icons inside the actual viewport remain realized and visible after manual reload");
                        this.CaptureAddressBreadcrumbPreview("ManualRefresh-Reloaded.png");
                    }
                }
                finally { this.FileGrid.SelectionChanged -= Changed; }
            }
            this.SearchBox.Text = "File 030";
            await this.SearchTask;
            var FilteredView = this.View;
            var AddedMatch = Path.Combine(Fixture, "File 030 Added.txt");
            File.WriteAllText(AddedMatch, "Filtered refresh fixture");
            await this.HandleShortcutAsync(Key.F5, ModifierKeys.None, InText: true, InFileList: false);
            await this.SearchTask;
            Check(this.SearchBox.Text == "File 030" && !ReferenceEquals(FilteredView, this.View)
                && this.View!.Cast<FileEntry>().Any(Entry => SamePath(Entry.FullPath, AddedMatch))
                && this.SelectedEntries().Any(Entry => SamePath(Entry.FullPath, Target)),
                "F5 from a search field rebuilds filtered results and preserves the query and selection");
            this.SearchBox.Clear();
            await this.SearchTask;
            var MenuView = this.View;
            Check(await this.ExecuteBackgroundCommandAsync("CDir.Refresh") && !ReferenceEquals(MenuView, this.View),
                "The folder background Refresh command uses the same full reload as F5");
        }
        finally
        {
            this.SearchBox.Clear();
            await this.NavigateAsync(Initial, RecordHistory: false);
            this.ChangeFileView(InitialView, InitialSize, FocusPanel: false, SaveImmediately: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unowned manual refresh fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
