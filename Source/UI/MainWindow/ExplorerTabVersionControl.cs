using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private readonly VersionControlService VersionControl = new(DiscoveryBoundary: AppMain.SmokeTest ? AppMain.SmokeRoot : null, CacheResults: true);
    private readonly DispatcherTimer VersionDebounce = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<string, FileSystemWatcher> VersionWatchers = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<VersionSnapshot> VersionSnapshots = [];
    private CancellationTokenSource? VersionCancellation;
    private long VersionEpoch;
    private int VersionInvalidationQueued;
    private bool VersionRefreshPending;

    private void InitializeVersionControl()
    {
        this.VersionDebounce.Tick += async (_, _) =>
        {
            this.VersionDebounce.Stop();
            if (this.Loading) { this.ScheduleVersionRefresh(); return; }
            await this.RefreshVersionStatusesAsync();
        };
        this.VersionControl.RepositoryLocated += Path =>
        {
            if (!this.WindowClosed && !Dispatcher.HasShutdownStarted)
                Dispatcher.Invoke(() => { if (!this.WindowClosed) this.EnsureVersionWatcher(Path); });
        };
    }

    private IEnumerable<FolderNode> AllVersionNodes()
    {
        IEnumerable<FolderNode> Visit(FolderNode Node)
        {
            if (!Node.IsPlaceholder) yield return Node;
            foreach (var Child in Node.Children)
                foreach (var Descendant in Visit(Child)) yield return Descendant;
        }
        return this.Roots.Concat(this.SidebarBookmarkRoots()).SelectMany(Visit);
    }

    private IEnumerable<FolderNode> VisibleVersionNodes()
    {
        // Bookmark headers and their independent child trees participate just like drive trees.
        // Use icon bounds: an expanded TreeViewItem includes its entire subtree in its height.
        IEnumerable<FolderNode> Visit(DependencyObject Parent)
        {
            for (var Index = 0; Index < VisualTreeHelper.GetChildrenCount(Parent); Index++)
            {
                var Child = VisualTreeHelper.GetChild(Parent, Index);
                if (Child is UIElement { IsVisible: false }) continue;
                if (Child is ShellIcon Icon)
                {
                    var Node = Icon.DataContext switch
                    {
                        FolderNode Folder => Folder,
                        CategoryFolder Folder => Folder.Node,
                        FavoriteLocation Folder => Folder.Node,
                        _ => null
                    };
                    var Top = Icon.TranslatePoint(new Point(), this.SidebarScroll).Y;
                    if (Node is { IsPlaceholder: false } && Top + Icon.ActualHeight > 0 && Top < this.SidebarScroll.ViewportHeight)
                        yield return Node;
                }
                else foreach (var Node in Visit(Child)) yield return Node;
            }
        }
        return Visit(this.SidebarSectionsList).Distinct();
    }

    private void ScheduleVersionRefresh()
    {
        if (this.WindowClosed) return;
        if (!this.VersionDebounce.IsEnabled) this.VersionDebounce.Start();
    }

    private void SidebarScrollChanged(object Sender, ScrollChangedEventArgs E)
    {
        if (Math.Abs(E.VerticalChange) > 0 || E.ExtentHeightChange != 0 || E.ViewportHeightChange != 0)
            this.ScheduleVersionRefresh();
    }

    private void InvalidateVersionStatuses(string? Path = null)
    {
        this.VersionControl.Invalidate(Path);
        Interlocked.Increment(ref this.VersionEpoch);
        if (this.WindowClosed || Dispatcher.HasShutdownStarted) return;
        if (Interlocked.Exchange(ref this.VersionInvalidationQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref this.VersionInvalidationQueued, 0);
            if (this.WindowClosed) return;
            this.MarkVersionStatusesPending();
            this.ScheduleVersionRefresh();
        });
    }

    private void MarkVersionStatusesPending()
    {
        VersionStatus Pending(VersionStatus Status) => Status.State == VersionState.None ? Status : new(VersionState.Unknown, Status.Provider);
        foreach (var Entry in this.Entries) Entry.VersionStatus = Pending(Entry.VersionStatus);
        foreach (var Node in this.AllVersionNodes()) Node.VersionStatus = Pending(Node.VersionStatus);
        this.AddressIcon.VersionStatus = Pending(this.AddressIcon.VersionStatus);
    }

    private async Task RefreshVersionStatusesAsync()
    {
        if (this.IsRecycleBin) return;
        if (this.WindowClosed || this.CurrentPath.Length == 0) return;
        this.VersionDebounce.Stop();
        if (this.VersionCancellation is not null) { this.VersionRefreshPending = true; return; }
        this.VersionRefreshPending = false;
        using var Cancellation = new CancellationTokenSource();
        this.VersionCancellation = Cancellation;
        var Epoch = Interlocked.Read(ref this.VersionEpoch);
        var Navigation = this.NavigationVersion;
        var Directories = this.VisibleVersionNodes().Select(Node => Node.FullPath)
            .Concat(this.Entries.Where(Entry => Entry.IsDirectory && Entry.Name is not ".git" and not ".svn").Select(Entry => Entry.FullPath))
            .Append(this.CurrentPath).ToArray();
        try
        {
            var Snapshots = await this.VersionControl.ReadAsync(Directories, Cancellation.Token, ShallowSvn: true);
            if (Cancellation.IsCancellationRequested || this.WindowClosed || Navigation != this.NavigationVersion || Epoch != Interlocked.Read(ref this.VersionEpoch)) return;
            this.VersionSnapshots = Snapshots.OrderByDescending(Snapshot => Snapshot.Root.Length).ToArray();
            this.ApplyVersionSnapshots();
            this.ConfigureVersionWatchers();
        }
        catch (OperationCanceledException) { }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (!Cancellation.IsCancellationRequested && !this.WindowClosed && Navigation == this.NavigationVersion)
            {
                this.VersionSnapshots = this.VersionSnapshots.Select(Snapshot => VersionSnapshot.Unavailable(Snapshot.Root, Snapshot.Provider, Error.Message)).ToArray();
                this.ApplyVersionSnapshots();
            }
        }
        finally
        {
            if (ReferenceEquals(this.VersionCancellation, Cancellation)) this.VersionCancellation = null;
            if (this.VersionRefreshPending || Epoch != Interlocked.Read(ref this.VersionEpoch) || Navigation != this.NavigationVersion) this.ScheduleVersionRefresh();
        }
    }

    private void ApplyVersionSnapshots()
    {
        VersionStatus Status(string Path) => this.VersionSnapshots.FirstOrDefault(Snapshot => Snapshot.Contains(Path))?.GetStatus(Path) ?? VersionStatus.None;
        foreach (var Entry in this.Entries) Entry.VersionStatus = Status(Entry.FullPath);
        foreach (var Node in this.AllVersionNodes()) Node.VersionStatus = Status(Node.FullPath);
        this.AddressIcon.VersionStatus = Status(this.CurrentPath);
    }

    private void ConfigureVersionWatchers()
    {
        var Paths = this.VersionSnapshots.SelectMany(Snapshot => Snapshot.WatchDirectories).Select(Path => System.IO.Path.TrimEndingDirectorySeparator(Path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var Wanted = Paths.Where(Path => !Paths.Any(Other => !string.Equals(Path, Other, StringComparison.OrdinalIgnoreCase)
            && Path.StartsWith(Other.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var Old in this.VersionWatchers.Keys.Where(Path => !Wanted.Contains(Path)).ToArray())
        {
            this.VersionControl.Invalidate(Old); // Unwatched repositories must be queried again when revisited.
            this.VersionWatchers[Old].Dispose();
            this.VersionWatchers.Remove(Old);
        }
        foreach (var Path in Wanted) this.EnsureVersionWatcher(Path);
    }

    private static bool IsVersionStatusChange(string Path)
    {
        var Normalized = Path.Replace('/', '\\');
        var Index = Normalized.IndexOf("\\.git\\", StringComparison.OrdinalIgnoreCase);
        if (Index < 0) return true;
        var Relative = Normalized[(Index + 6)..];
        if (Relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;
        return Relative.Equals("index", StringComparison.OrdinalIgnoreCase) || Relative.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            || Relative.Equals("packed-refs", StringComparison.OrdinalIgnoreCase) || Relative.Equals("refs", StringComparison.OrdinalIgnoreCase) || Relative.StartsWith("refs\\", StringComparison.OrdinalIgnoreCase)
            || Relative.Equals("config", StringComparison.OrdinalIgnoreCase) || Relative.Equals("config.worktree", StringComparison.OrdinalIgnoreCase)
            || Relative.Equals("commondir", StringComparison.OrdinalIgnoreCase) || Relative.Equals("info", StringComparison.OrdinalIgnoreCase)
            || Relative.Equals("info\\attributes", StringComparison.OrdinalIgnoreCase) || Relative.Equals("info\\exclude", StringComparison.OrdinalIgnoreCase) || Relative.StartsWith("worktrees\\", StringComparison.OrdinalIgnoreCase)
            || Relative.StartsWith("modules\\", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureVersionWatcher(string Path)
    {
        Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Path));
        if (this.VersionWatchers.Keys.Any(Root => Path.StartsWith(Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return;
        if (!this.VersionWatchers.ContainsKey(Path)) {
            FileSystemWatcher? Watcher = null;
            try
            {
                Watcher = new FileSystemWatcher(Path)
                {
                    IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes
                };
                FileSystemEventHandler Changed = (_, E) =>
                {
                    if (E.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(E.FullPath)) return;
                    if (IsVersionStatusChange(E.FullPath)) this.InvalidateVersionStatuses(E.FullPath);
                };
                Watcher.Changed += Changed;
                Watcher.Created += Changed;
                Watcher.Deleted += Changed;
                Watcher.Renamed += (_, E) => { if (IsVersionStatusChange(E.FullPath) || IsVersionStatusChange(E.OldFullPath)) this.InvalidateVersionStatuses(); };
                Watcher.Error += (_, _) => this.InvalidateVersionStatuses();
                Watcher.EnableRaisingEvents = true;
                this.VersionWatchers[Path] = Watcher;
            }
            catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Watcher?.Dispose(); // F5 remains available when a watcher cannot be installed.
            }
        }
    }

    private void StopVersionControl()
    {
        this.VersionDebounce.Stop();
        this.VersionCancellation?.Cancel();
        foreach (var Watcher in this.VersionWatchers.Values) Watcher.Dispose();
        this.VersionWatchers.Clear();
    }
}
