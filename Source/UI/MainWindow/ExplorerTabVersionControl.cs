using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using CDir.Controls;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private readonly VersionControlService VersionControl = new(DiscoveryBoundary: AppMain.SmokeTest ? AppMain.SmokeRoot : null, CacheResults: true);
    private readonly DispatcherTimer VersionDebounce = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<string, FileSystemWatcher> VersionWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> VersionMetadataStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> VersionPrimedRoots = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<VersionSnapshot> VersionSnapshots = [];
    private CancellationTokenSource? VersionCancellation;
    private long VersionEpoch;
    private int VersionInvalidationQueued;
    private bool VersionRefreshPending;
    private int VersionQueryNavigation;
    private long VersionMainEpoch;
    private bool VersionQueryIncludesSidebar;
    private string[] VersionReadDirectories = [];
    private long VersionReadEpoch = -1;
    private int VersionReadNavigation = -1;
    internal long VersionReadCount;

    private bool AffectsVersionPath(string Item, string? Changed)
    {
        if (Changed is null) return true;
        if (SamePath(Item, Changed) || Item.StartsWith(Changed.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            || Changed.StartsWith(Item.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        var Normalized = Changed.Replace('/', '\\');
        if (!Normalized.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase) && !Normalized.Contains("\\.svn\\", StringComparison.OrdinalIgnoreCase)) return false;
        return this.VersionSnapshots.Any(Snapshot => Snapshot.Contains(Item) && Snapshot.WatchDirectories.Any(Root =>
            SamePath(Root, Changed) || Changed.StartsWith(Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)));
    }

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
            // RepositoryLocated runs on the query worker. Never hash index/database
            // files on the UI dispatcher, including large working-copy metadata.
            if (this.VersionPrimedRoots.TryAdd(Path, 0)) this.PrimeVersionMetadata(Path);
            if (!this.WindowClosed && !Dispatcher.HasShutdownStarted)
                Dispatcher.Invoke(() => { if (!this.WindowClosed) this.EnsureVersionWatcher(Path); });
        };
    }

    private IEnumerable<FolderNode> AllVersionNodes(IEnumerable<FolderNode>? Roots = null)
    {
        IEnumerable<FolderNode> Visit(FolderNode Node)
        {
            if (!Node.IsPlaceholder) yield return Node;
            foreach (var Child in Node.Children)
                foreach (var Descendant in Visit(Child)) yield return Descendant;
        }
        return (Roots ?? this.Roots.Concat(this.SidebarBookmarkRoots())).SelectMany(Visit);
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
                    if (Node is { IsPlaceholder: false } && !ShellLocations.IsVirtual(Node.FullPath)
                        && Top + Icon.ActualHeight > 0 && Top < this.SidebarScroll.ViewportHeight)
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
        this.UpdateStickySidebar();
        if (Math.Abs(E.VerticalChange) > 0 || E.ExtentHeightChange != 0 || E.ViewportHeightChange != 0)
            this.ScheduleVersionRefresh();
    }

    private void InvalidateVersionStatuses(string? Path = null)
    {
        this.VersionControl.Invalidate(Path);
        Interlocked.Increment(ref this.VersionEpoch);
        var AffectsMain = this.CurrentPath.Length > 0 && this.AffectsVersionPath(this.CurrentPath, Path);
        if (AffectsMain) Interlocked.Increment(ref this.VersionMainEpoch);
        if (this.WindowClosed || Dispatcher.HasShutdownStarted) return;
        if (Interlocked.Exchange(ref this.VersionInvalidationQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref this.VersionInvalidationQueued, 0);
            if (this.WindowClosed) return;
            // Keep verified badges while checking notifications. Read-only VCS operations
            // and metadata maintenance must never flash every icon back to pending.
            if (AffectsMain && this.VersionQueryIncludesSidebar) this.VersionCancellation?.Cancel();
            this.ScheduleVersionRefresh();
        });
    }

    private async Task RefreshVersionStatusesAsync()
    {
        if (this.IsVirtualLocation) return;
        if (this.WindowClosed || this.CurrentPath.Length == 0) return;
        this.VersionDebounce.Stop();
        if (this.VersionCancellation is not null)
        {
            this.VersionRefreshPending = true;
            if (this.VersionQueryNavigation != this.NavigationVersion) this.VersionCancellation.Cancel();
            return;
        }
        this.VersionRefreshPending = false;
        using var Cancellation = new CancellationTokenSource();
        this.VersionCancellation = Cancellation;
        var Epoch = Interlocked.Read(ref this.VersionEpoch);
        var MainEpoch = Interlocked.Read(ref this.VersionMainEpoch);
        var Navigation = this.NavigationVersion;
        this.VersionQueryNavigation = Navigation;
        this.VersionQueryIncludesSidebar = false;
        var MainDirectories = new[] { this.CurrentPath }
            .Concat(this.Entries.Where(Entry => Entry.IsDirectory && Entry.Name is not ".git" and not ".svn").Select(Entry => Entry.FullPath)).ToArray();
        var Directories = MainDirectories.Concat(this.VisibleVersionNodes().Select(Node => Node.FullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (this.VersionReadEpoch == Epoch && this.VersionReadNavigation == Navigation
            && Directories.SequenceEqual(this.VersionReadDirectories, StringComparer.OrdinalIgnoreCase))
        {
            this.VersionCancellation = null;
            this.ApplyVersionSnapshots(); // Newly materialized controls still receive their cached badges.
            return;
        }
        this.VersionReadCount++;
        async Task PublishReadyAsync(VersionSnapshot Snapshot)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (Cancellation.IsCancellationRequested || this.WindowClosed || Navigation != this.NavigationVersion) return;
                if (!Snapshot.IsComplete && Snapshot.Error is null && this.VersionSnapshots.Any(Previous => SamePath(Previous.Root, Snapshot.Root)
                    && Previous.IsComplete && Previous.Error is null)) return;
                var ScopedMain = !this.VersionQueryIncludesSidebar && !SamePath(Snapshot.CoverageRoot, Snapshot.Root);
                if (ScopedMain ? MainEpoch != Interlocked.Read(ref this.VersionMainEpoch) : Epoch != Interlocked.Read(ref this.VersionEpoch)) return;
                var WholeRepository = Snapshot.IsComplete && Snapshot.Error is null && SamePath(Snapshot.CoverageRoot, Snapshot.Root);
                this.VersionSnapshots = this.VersionSnapshots.Where(Previous => !SamePath(Previous.Root, Snapshot.Root)
                    || (!WholeRepository && !SamePath(Previous.CoverageRoot, Snapshot.CoverageRoot)))
                    .Append(Snapshot).OrderByDescending(Item => Item.CoverageRoot.Length).ToArray();
                this.ApplyVersionSnapshots();
            });
        }
        try
        {
            // Sidebars can contain slow or offline working copies. Display the main
            // pane first; completed repositories publish independently afterwards.
            var MainSnapshots = await this.VersionControl.ReadAsync(MainDirectories, Cancellation.Token, ShallowSvn: true,
                SnapshotReady: PublishReadyAsync, PriorityDirectory: this.CurrentPath);
            var Snapshots = MainSnapshots;
            if (Directories.Length > MainDirectories.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            {
                this.VersionQueryIncludesSidebar = true;
                Snapshots = await this.VersionControl.ReadAsync(Directories, Cancellation.Token, ShallowSvn: true, SnapshotReady: PublishReadyAsync);
            }
            if (Cancellation.IsCancellationRequested || this.WindowClosed || Navigation != this.NavigationVersion || Epoch != Interlocked.Read(ref this.VersionEpoch)) return;
            this.VersionSnapshots = Snapshots.Concat(MainSnapshots.Where(Main => !SamePath(Main.CoverageRoot, Main.Root)
                && Snapshots.Any(Full => SamePath(Full.Root, Main.Root) && Full.Error is not null)))
                .Distinct().OrderByDescending(Snapshot => Snapshot.CoverageRoot.Length).ToArray();
            this.ApplyVersionSnapshots();
            this.ConfigureVersionWatchers();
            this.VersionReadDirectories = Directories;
            this.VersionReadEpoch = Epoch;
            this.VersionReadNavigation = Navigation;
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
            this.VersionQueryIncludesSidebar = false;
            if (this.VersionRefreshPending || Epoch != Interlocked.Read(ref this.VersionEpoch) || Navigation != this.NavigationVersion) this.ScheduleVersionRefresh();
        }
    }

    private void ApplyVersionSnapshots()
    {
        VersionStatus Status(string Path) => this.VersionSnapshots.FirstOrDefault(Snapshot => Snapshot.Covers(Path))?.GetStatus(Path) ?? VersionStatus.None;
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
            this.VersionPrimedRoots.TryRemove(Old, out _);
            foreach (var Metadata in this.VersionMetadataStamps.Keys.Where(Path => Path.StartsWith(Old.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                this.VersionMetadataStamps.TryRemove(Metadata, out _);
        }
        foreach (var Path in Wanted) this.EnsureVersionWatcher(Path);
    }

    private static bool IsVersionStatusChange(string Path)
    {
        var Normalized = Path.Replace('/', '\\');
        var SvnIndex = Normalized.IndexOf("\\.svn\\", StringComparison.OrdinalIgnoreCase);
        if (SvnIndex >= 0)
        {
            var SvnRelative = Normalized[(SvnIndex + 6)..];
            return SvnRelative.Equals("wc.db", StringComparison.OrdinalIgnoreCase)
                || SvnRelative.Equals("wc.db-wal", StringComparison.OrdinalIgnoreCase)
                || SvnRelative.Equals("entries", StringComparison.OrdinalIgnoreCase)
                || SvnRelative.Equals("format", StringComparison.OrdinalIgnoreCase);
        }
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

    private static string? ReadVersionMetadataStamp(string Path)
    {
        try
        {
            using var Stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(Stream));
        }
        catch (FileNotFoundException) { return "missing"; }
        catch (DirectoryNotFoundException) { return "missing"; }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException) { return null; }
    }

    private bool HasVersionStatusChange(string Path)
    {
        if (!IsVersionStatusChange(Path)) return false;
        var Normalized = Path.Replace('/', '\\');
        if (!Normalized.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase) && !Normalized.Contains("\\.svn\\", StringComparison.OrdinalIgnoreCase)) return true;
        var Stamp = ReadVersionMetadataStamp(Path);
        if (Stamp is null) return true;
        var Previous = this.VersionMetadataStamps.GetValueOrDefault(Path);
        this.VersionMetadataStamps[Path] = Stamp;
        return Previous != Stamp;
    }

    private void PrimeVersionMetadata(string Root)
    {
        var GitRoot = System.IO.Path.Combine(Root, ".git");
        if (!Directory.Exists(GitRoot)) GitRoot = File.Exists(System.IO.Path.Combine(Root, "HEAD"))
            && (File.Exists(System.IO.Path.Combine(Root, "index")) || Directory.Exists(System.IO.Path.Combine(Root, "objects"))
                || File.Exists(System.IO.Path.Combine(Root, "commondir"))) ? Root : null;
        foreach (var Relative in GitRoot is null ? Array.Empty<string>() : new[] { "index", "HEAD", "packed-refs", "config", "info/exclude", "info/attributes" })
        {
            var Metadata = System.IO.Path.GetFullPath(System.IO.Path.Combine(GitRoot!, Relative));
            if (ReadVersionMetadataStamp(Metadata) is { } Stamp) this.VersionMetadataStamps.TryAdd(Metadata, Stamp);
        }
        foreach (var Relative in new[] { "wc.db", "wc.db-wal", "entries", "format" })
        {
            var Metadata = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, ".svn", Relative));
            if (ReadVersionMetadataStamp(Metadata) is { } Stamp) this.VersionMetadataStamps.TryAdd(Metadata, Stamp);
        }
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
                    if (this.HasVersionStatusChange(E.FullPath)) this.InvalidateVersionStatuses(E.FullPath);
                };
                Watcher.Changed += Changed;
                Watcher.Created += Changed;
                Watcher.Deleted += Changed;
                Watcher.Renamed += (_, E) => { if (this.HasVersionStatusChange(E.FullPath) | this.HasVersionStatusChange(E.OldFullPath)) this.InvalidateVersionStatuses(); };
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
        this.VersionMetadataStamps.Clear();
        this.VersionPrimedRoots.Clear();
    }
}
