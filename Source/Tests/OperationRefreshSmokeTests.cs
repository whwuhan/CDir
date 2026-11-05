using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunOperationRefreshSmokeChecksAsync(List<string> Passed)
    {
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Refresh-" + Guid.NewGuid().ToString("N"))).FullName;
        var Work = Directory.CreateDirectory(Path.Combine(Fixture, "Work")).FullName;
        var Destination = Directory.CreateDirectory(Path.Combine(Fixture, "Destination")).FullName;
        var Keep = Directory.CreateDirectory(Path.Combine(Work, "Keep", "Child")).Parent!.FullName;
        var Moving = Directory.CreateDirectory(Path.Combine(Work, "Moving", "Nested")).Parent!.FullName;
        var MovingFile = Path.Combine(Work, "Move.txt");
        File.WriteAllText(MovingFile, "Owned moving file");
        File.WriteAllText(Path.Combine(Moving, "Nested", "File.txt"), "Owned moving folder");
        for (var Index = 0; Index < 90; Index++) File.WriteAllText(Path.Combine(Work, $"Stable-{Index:00}.txt"), "Stable row");
        var InitialPath = this.CurrentPath;
        var InitialHistory = HistoryInstance;
        var InitialFileHistory = this.Files.OperationHistory;
        var History = new FileOperationHistory(Path.Combine(Fixture, "Journal"));
        var ResetCount = 0;
        var CollapseCount = 0;
        var Subscriptions = new List<INotifyCollectionChanged>();
        void Changed(object? Sender, NotifyCollectionChangedEventArgs E) { if (E.Action == NotifyCollectionChangedAction.Reset) ResetCount++; }
        void Watch(INotifyCollectionChanged Collection) { Collection.CollectionChanged += Changed; Subscriptions.Add(Collection); }
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Operation refresh check failed: " + Name);
            Passed.Add(Name);
        }
        try
        {
            HistoryInstance = History;
            this.Files.OperationHistory = History;
            await this.NavigateAsync(Work, RecordHistory: false);
            var Current = this.AllVersionNodes().Single(Node => SamePath(Node.FullPath, Work));
            var KeepNode = Current.Children.Single(Node => SamePath(Node.FullPath, Keep));
            await this.EnsureChildrenAsync(KeepNode);
            KeepNode.IsExpanded = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
            Current.PropertyChanged += (_, E) => { if (E.PropertyName == nameof(FolderNode.IsExpanded) && !Current.IsExpanded) CollapseCount++; };
            KeepNode.PropertyChanged += (_, E) => { if (E.PropertyName == nameof(FolderNode.IsExpanded) && !KeepNode.IsExpanded) CollapseCount++; };
            var KeepChild = KeepNode.Children.Single();
            var Source = this.FileGrid.ItemsSource;
            var View = this.View;
            var Address = this.AddressBreadcrumbs.ItemsSource;
            var Navigation = this.NavigationVersion;
            var Viewer = FindVisualChild<ScrollViewer>(this.FileGrid)!;
            Viewer.ScrollToVerticalOffset(20);
            this.UpdateLayout();
            var Stable = this.FileGrid.Items.Cast<FileEntry>().First(Entry => Entry.Name.StartsWith("Stable-", StringComparison.Ordinal)
                && this.FileGrid.ItemContainerGenerator.ContainerFromItem(Entry) is DataGridRow Row && Row.IsVisible
                && Row.TranslatePoint(new Point(), this.FileGrid).Y > this.FileGrid.ActualHeight / 3
                && Row.TranslatePoint(new Point(), this.FileGrid).Y < this.FileGrid.ActualHeight * 2 / 3);
            this.FileGrid.SelectedItem = Stable;
            this.UpdateLayout();
            var Vertical = Viewer.VerticalOffset;
            var StableRow = this.FileGrid.ItemContainerGenerator.ContainerFromItem(Stable);
            Watch((ObservableCollection<FileEntry>)this.Entries);
            foreach (var Node in this.AllVersionNodes().Where(Node => Node.IsLoaded)) Watch(Node.Children);

            async Task StableUI(string Operation)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                this.UpdateLayout();
                Check(ReferenceEquals(Source, this.FileGrid.ItemsSource) && ReferenceEquals(View, this.View)
                    && ReferenceEquals(Stable, this.Entries.Single(Entry => SamePath(Entry.FullPath, Stable.FullPath)))
                    && ReferenceEquals(StableRow, this.FileGrid.ItemContainerGenerator.ContainerFromItem(Stable)),
                    Operation + " preserves the bound view unchanged file objects and realized rows"
                    + $" (source={ReferenceEquals(Source, this.FileGrid.ItemsSource)}, view={ReferenceEquals(View, this.View)}, entry={this.Entries.Contains(Stable)}, row={ReferenceEquals(StableRow, this.FileGrid.ItemContainerGenerator.ContainerFromItem(Stable))})");
                Check(Current.IsExpanded && KeepNode.IsExpanded && CollapseCount == 0 && ResetCount == 0
                    && ReferenceEquals(KeepNode, Current.Children.Single(Node => SamePath(Node.FullPath, Keep)))
                    && ReferenceEquals(KeepChild, KeepNode.Children.Single()),
                    Operation + " updates the sidebar without clearing children collapsing branches or replacing stable nodes");
                Check(this.FileGrid.SelectedItems.Contains(Stable) && Math.Abs(Viewer.VerticalOffset - Vertical) < 0.5
                    && ReferenceEquals(Address, this.AddressBreadcrumbs.ItemsSource) && Navigation == this.NavigationVersion,
                    Operation + " retains selection scroll position address controls and the current navigation");
            }

            Check(await this.RunOperationAsync("Moving owned fixtures", () => this.Files.CopyItems([MovingFile, Moving], Destination, true), "Moved"), "The mixed file and folder move completes");
            Check(!this.Entries.Any(Entry => SamePath(Entry.FullPath, MovingFile) || SamePath(Entry.FullPath, Moving))
                && !Current.Children.Any(Node => SamePath(Node.FullPath, Moving)), "Move removes the real file and folder from both visible views");
            await StableUI("Move");
            await this.ReplayFileOperationAsync(false);
            Check(this.Entries.Any(Entry => SamePath(Entry.FullPath, MovingFile)) && Current.Children.Any(Node => SamePath(Node.FullPath, Moving)), "Undo restores moved items to the list and directory tree");
            await StableUI("Undo");
            await this.ReplayFileOperationAsync(true);
            await StableUI("Redo");
            this.ScheduleRefresh();
            await Task.Delay(800);
            await StableUI("A queued filesystem notification after redo");
            File.WriteAllText(Path.Combine(Work, "New.txt"), "External addition");
            await this.RefreshAsync();
            Check(this.Entries.Any(Entry => Entry.Name == "New.txt"), "Incremental background refresh includes newly created files");
            await StableUI("Background refresh");
        }
        finally
        {
            foreach (var Collection in Subscriptions) Collection.CollectionChanged -= Changed;
            this.Files.OperationHistory = InitialFileHistory;
            HistoryInstance = InitialHistory;
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unowned operation refresh fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
