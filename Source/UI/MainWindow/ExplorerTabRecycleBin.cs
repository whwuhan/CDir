using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Data;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private string RecycleBinViewTarget = ShellLocations.RecycleBin;
    private Func<string, CancellationToken, Action<IReadOnlyList<FileEntry>>?, IReadOnlyList<FileEntry>> ReadRecycleEntries = RecycleBinService.ReadEntries;
    private Action<FileEntry[], string>? RecycleCommandOverride;
    private ShellFolderWatcher? RecycleWatcher;
    private CancellationTokenSource? RecycleReadCancellation;
    private long RecycleReadVersion;
    private bool RecycleBinLoaded;
    private bool IsRecycleBin => ShellLocations.IsRecycleBin(this.CurrentPath);
    private bool IsComputer => ShellLocations.IsComputer(this.CurrentPath);
    private bool IsVirtualLocation => ShellLocations.IsVirtual(this.CurrentPath);
    private string FileSystemWorkingDirectory => this.IsVirtualLocation
        ? this.History.Take(Math.Max(0, this.HistoryIndex + 1)).LastOrDefault(Path.IsPathFullyQualified)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : this.CurrentPath;

    private Task<IReadOnlyList<FileEntry>> ReadRecycleEntriesAsync(Action<IReadOnlyList<FileEntry>>? BatchReady = null)
    {
        this.RecycleReadCancellation?.Cancel();
        this.RecycleReadCancellation?.Dispose();
        var Cancellation = new CancellationTokenSource();
        this.RecycleReadCancellation = Cancellation;
        var Token = Cancellation.Token;
        var Reader = this.ReadRecycleEntries;
        var Location = this.RecycleBinViewTarget;
        if (AppMain.SmokeTest && !AppMain.IsInsideSmokeScope(Location))
            throw new UnauthorizedAccessException("Recycle Bin checks require an owned folder.");
        var Completion = new TaskCompletionSource<IReadOnlyList<FileEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var Worker = new Thread(() =>
        {
            try { Completion.SetResult(Reader(Location, Token, BatchReady)); }
            catch (Exception Error) { Completion.SetException(Error); }
        }) { IsBackground = true };
        Worker.SetApartmentState(ApartmentState.STA);
        Worker.Start();
        return Completion.Task;
    }

    private async Task<bool> NavigateRecycleBinAsync(int? HistoryIndex, bool RecordHistory)
    {
        if (this.WindowClosed || this.OperationBusy) return false;
        if (this.IsRecycleBin && this.Loading) return false;
        if (this.IsRecycleBin && this.RecycleBinLoaded) { await this.RefreshRecycleBinAsync(); return true; }
        var Version = ++this.NavigationVersion;
        this.CancelFolderSizes();
        this.CancelAddressNavigation();
        this.CancelSidebarFolderClick();
        this.CancelSearch();
        this.EndMarqueeSelection();
        this.Loading = true;
        this.UpdateActions();
        string? FailureNotice = null;
        try
        {
            this.Watcher?.Dispose();
            this.Watcher = null;
            this.RefreshTimer.Stop();
            this.VersionCancellation?.Cancel();
            this.CurrentPath = ShellLocations.RecycleBin;
            this.RecycleBinLoaded = false;
            if (HistoryIndex.HasValue) this.HistoryIndex = HistoryIndex.Value;
            else if (RecordHistory)
            {
                this.History.RemoveRange(this.HistoryIndex + 1, this.History.Count - this.HistoryIndex - 1);
                this.HistoryTreeKeys.RemoveRange(this.HistoryIndex + 1, this.HistoryTreeKeys.Count - this.HistoryIndex - 1);
                this.History.Add(this.CurrentPath);
                this.HistoryTreeKeys.Add("quick-access");
                this.HistoryIndex = this.History.Count - 1;
            }
            this.UseNavigationTree("quick-access");
            this.SearchBox.Clear();
            this.SearchContainer.IsEnabled = true;
            this.CurrentSearch = SearchQuery.Parse("");
            this.ApplyRecycleBinColumns();
            var Rows = new ObservableCollection<FileEntry>();
            this.FolderEntries = Rows;
            this.FolderRefreshPending = false;
            this.ShowEntries(Rows);
            this.AddressIcon.Path = ShellLocations.RecycleBin;
            this.AddressIcon.IsDrive = false;
            this.AddressIcon.VersionStatus = VersionStatus.None;
            this.UpdateAddressPath();
            this.NotifyTabHeader();
            this.QueueDriveUsageRefresh(this.CurrentPath);
            this.RecycleWatcher?.Dispose();
            try { this.RecycleWatcher = new ShellFolderWatcher(this.HostWindow, this.RecycleBinViewTarget, this.ScheduleRefresh); }
            catch (System.ComponentModel.Win32Exception) { this.RecycleWatcher = null; }
            this.UpdateActions();
            var Entries = await this.ReadRecycleEntriesAsync(Batch =>
            {
                // Keep only one batch in flight and yield to input/render work.
                // A completed navigation must never receive an old batch.
                this.Dispatcher.Invoke(() =>
                {
                    if (this.WindowClosed || Version != this.NavigationVersion || !this.IsRecycleBin) return;
                    foreach (var Entry in Batch)
                    {
                        Entry.Use12HourClock = this.Settings.Use12HourClock;
                        Rows.Add(Entry);
                    }
                    this.UpdateCounts();
                }, DispatcherPriority.Background);
            });
            if (this.WindowClosed || Version != this.NavigationVersion) return false;
            this.FolderEntries = Entries;
            this.RefreshFileRows(Entries);
            this.RecycleBinLoaded = true;
            this.UpdateCounts();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception Error)
        {
            if (Version == this.NavigationVersion && !this.WindowClosed)
            {
                FailureNotice = LanguageService.Text("OpenLocationError", FriendlyError(Error));
                if (AppMain.SmokeTest) AppMain.RecordSmokeProgress("Recycle Bin read failed: " + Error);
            }
            return false;
        }
        finally
        {
            if (Version == this.NavigationVersion)
            {
                this.Loading = false;
                this.View?.Refresh();
                this.UpdateActions();
                this.UpdateCounts();
                if (FailureNotice is not null) this.ShowNotice(FailureNotice, true);
            }
        }
    }

    private async Task RefreshRecycleBinAsync()
    {
        if (!this.IsRecycleBin || this.WindowClosed) return;
        if (this.Loading) { this.FolderRefreshPending = true; this.ScheduleRefresh(); return; }
        var Navigation = this.NavigationVersion;
        var Request = ++this.RecycleReadVersion;
        this.RefreshTimer.Stop();
        try
        {
            var Entries = await this.ReadRecycleEntriesAsync();
            if (this.WindowClosed || !this.IsRecycleBin || Navigation != this.NavigationVersion || Request != this.RecycleReadVersion) return;
            this.FolderEntries = Entries;
            this.FolderRefreshPending = false;
            this.RefreshFileRows(Entries);
        }
        catch (OperationCanceledException) { }
        catch (Exception Error) when (Error is COMException or IOException or UnauthorizedAccessException)
        { if (!this.WindowClosed && Navigation == this.NavigationVersion) this.ShowNotice(LanguageService.Text("OpenLocationError", FriendlyError(Error)), true); }
    }

    private void CloseRecycleBinView()
    {
        this.RecycleReadCancellation?.Cancel();
        this.RecycleReadCancellation?.Dispose();
        this.RecycleReadCancellation = null;
        ++this.RecycleReadVersion;
        this.RecycleWatcher?.Dispose();
        this.RecycleWatcher = null;
        this.RecycleBinLoaded = false;
        this.ApplyRecycleBinColumns();
    }

    private void ApplyRecycleBinColumns()
    {
        foreach (var Column in this.FileGrid.Columns.Where(Column => IsRecycleColumn(Column.SortMemberPath)).ToArray())
            this.FileGrid.Columns.Remove(Column);
        if (this.IsRecycleBin)
        {
            var PathStyle = new Style(typeof(TextBlock), (Style)this.FindResource("FileMetadataText"));
            PathStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            PathStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(FileEntry.OriginalLocation))));
            var Original = new DataGridTextColumn { Header = LanguageService.Text("OriginalLocationColumn"), SortMemberPath = nameof(FileEntry.OriginalLocation),
                Binding = new Binding(nameof(FileEntry.OriginalLocation)), Width = 300, MinWidth = 160,
                ElementStyle = PathStyle };
            var Deleted = new DataGridTextColumn { Header = LanguageService.Text("DateDeletedColumn"), SortMemberPath = nameof(FileEntry.DateDeleted),
                Binding = new Binding(nameof(FileEntry.DisplayDateDeleted)), Width = 200,
                ElementStyle = (Style)this.FindResource("FileMetadataText") };
            this.FileGrid.Columns.Add(Original);
            this.FileGrid.Columns.Add(Deleted);
        }
        else if (IsRecycleColumn(this.SortProperty))
        {
            this.SortProperty = nameof(FileEntry.Name);
            this.SortDirection = System.ComponentModel.ListSortDirection.Ascending;
        }
        this.ApplyColumnVisibility();
        this.ApplyGridLines();
    }

    private static bool IsRecycleColumn(string Key) => Key is nameof(FileEntry.OriginalLocation) or nameof(FileEntry.DateDeleted);

    private async Task ExecuteRecycleSelectionAsync(string Verb)
    {
        if (!this.IsRecycleBin || this.OperationBusy || this.Loading) return;
        var Entries = this.SelectedEntries();
        if (Entries.Length == 0) return;
        var Location = this.RecycleBinViewTarget;
        var Owner = new WindowInteropHelper(this.HostWindow).Handle;
        var Override = this.RecycleCommandOverride;
        await this.RunOperationAsync(LanguageService.Text(Verb == "undelete" ? "RestoringFiles" : Verb == "delete" ? "DeletingFiles" : "Opening"), () =>
        {
            if (Override is not null) { Override(Entries, Verb); return; }
            using var Menu = new ShellContextMenu(Owner, Location, Entries.Select(Entry => Entry.FullPath).ToArray(),
                ShellItemIds: Entries.Select(Entry => Entry.ShellId ?? throw new InvalidOperationException("Missing Shell item identity.")).ToArray());
            var Id = Menu.ReadEntries().FirstOrDefault(Item => string.Equals(Menu.DescribeCommand(Item.Id), Verb, StringComparison.OrdinalIgnoreCase))?.Id ?? 0;
            SharedFileHistory.Audit(Verb, Entries.Select(Entry => Entry.FullPath).ToArray(), "Invoked");
            if (Id != 0) Menu.Invoke(Id);
        }, LanguageService.Text(Verb == "undelete" ? "RestoreComplete" : Verb == "delete" ? "DeleteComplete" : "RecycleBin"), RequiresSta: true);
    }
}
