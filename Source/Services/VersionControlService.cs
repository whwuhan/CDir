using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CDir.Models;
using Microsoft.Win32;

namespace CDir.Services;

/// <summary>Reads local working-copy state from the owning VCS. No Shell overlays or persistent status cache.</summary>
public sealed class VersionControlService
{
    private readonly string? GitExecutable;
    private readonly string? SvnExecutable;
    private readonly TimeSpan Timeout;
    private readonly string? DiscoveryBoundary;
    private readonly SemaphoreSlim Slots = new(2);
    private readonly bool CacheResults;
    private readonly object CacheSync = new();
    private readonly Dictionary<Repository, VersionSnapshot> Snapshots = new();
    private readonly Dictionary<string, Repository> Discoveries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VersionSnapshot> SvnViews = new(StringComparer.OrdinalIgnoreCase);
    private long DiscoveryProbes;
    internal long DiscoveryProbeCount => Interlocked.Read(ref this.DiscoveryProbes);
    private readonly HashSet<string> VerifiedSvnRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Stamp, string[] Metadata, VersionSnapshot Clean)> Inventories = new(StringComparer.OrdinalIgnoreCase);
    private long CacheGeneration;
    private long CommandCount;
    public event Action<string>? RepositoryLocated;
    internal long NativeCommandCount => Interlocked.Read(ref this.CommandCount);

    public void Invalidate(string? Path = null)
    {
        Path = Path is null ? null : System.IO.Path.GetFullPath(Path);
        lock (this.CacheSync)
        {
            this.CacheGeneration++;
            foreach (var Key in this.SvnViews.Keys.Where(Key => Path is null || this.SvnViews[Key].WatchDirectories.Any(Root => SamePath(Path, Root)
                || Path.StartsWith(Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                || Root.StartsWith(Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToArray()) this.SvnViews.Remove(Key);
            var DiscoveryPath = Path is not null && System.IO.Path.GetFileName(Path) is ".git" or ".svn" ? System.IO.Path.GetDirectoryName(Path) : Path;
            foreach (var Key in this.Discoveries.Keys.Where(Key => DiscoveryPath is null || SamePath(Key, DiscoveryPath)
                || Key.StartsWith(DiscoveryPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)).ToArray()) this.Discoveries.Remove(Key);
            foreach (var Key in this.Snapshots.Keys.Where(Key => Path is null || this.Snapshots[Key].WatchDirectories.Any(Root => SamePath(Path, Root)
                || Path.StartsWith(Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                || Root.StartsWith(Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToArray()) this.Snapshots.Remove(Key);
            if (Path is null || System.IO.Path.GetFileName(Path).Equals(".git", StringComparison.OrdinalIgnoreCase)
                || (Path.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase) && System.IO.Path.GetFileName(Path) is "config" or "config.worktree" or "commondir")) this.Inventories.Clear();
        }
    }

    public VersionControlService(string? GitExecutable = null, string? SvnExecutable = null, TimeSpan? Timeout = null, string? DiscoveryBoundary = null, bool CacheResults = false)
    {
        this.GitExecutable = GitExecutable ?? FindExecutable("git");
        this.SvnExecutable = SvnExecutable ?? FindExecutable("svn");
        this.Timeout = Timeout ?? TimeSpan.FromSeconds(20);
        this.DiscoveryBoundary = DiscoveryBoundary is null ? null : System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(DiscoveryBoundary));
        this.CacheResults = CacheResults;
    }

    public async Task<IReadOnlyList<VersionSnapshot>> ReadAsync(IEnumerable<string> Directories, CancellationToken Cancellation = default, bool ShallowSvn = false,
        Func<VersionSnapshot, Task>? SnapshotReady = null, string? PriorityDirectory = null)
    {
        var Inputs = Directories.Select(Path => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Path)))
            .Where(Path => !Path.Split(System.IO.Path.DirectorySeparatorChar).Any(Part => Part.Equals(".git", StringComparison.OrdinalIgnoreCase) || Part.Equals(".svn", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (Inputs.Any(Path => !this.IsInsideBoundary(Path))) throw new ArgumentException("Version-control query is outside the test scope");
        var Repositories = await Task.Run(() =>
        {
            var Cache = new Dictionary<string, Repository?>(StringComparer.OrdinalIgnoreCase);
            return Inputs.Select(Path =>
            {
                var Repository = this.Discover(Path, Cache, Cancellation);
                return ShallowSvn && Repository?.Provider == "SVN" ? Repository with { Scope = Path } : Repository;
            }).OfType<Repository>().Distinct().ToArray();
        }, Cancellation).ConfigureAwait(false);
        return await Task.WhenAll(Repositories.GroupBy(Item => Item.Root, StringComparer.OrdinalIgnoreCase).Select(Group => Task.Run(async () =>
        {
            var Repository = Group.First();
            bool HaveCache;
            lock (this.CacheSync) HaveCache = this.CacheResults && this.Snapshots.Keys.Any(Key => SamePath(Key.Root, Repository.Root));
            if (!HaveCache && SnapshotReady is not null) await SnapshotReady(VersionSnapshot.Checking(Repository.Root, Repository.Provider)).ConfigureAwait(false);
            VersionSnapshot Result;
            if (ShallowSvn && Repository.Provider == "SVN")
            {
                var Priority = PriorityDirectory is not null && (SamePath(PriorityDirectory, Repository.Root)
                    || PriorityDirectory.StartsWith(Repository.Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) ? PriorityDirectory : null;
                var VisibleScopes = Priority is null ? Group.ToArray() : Group.Where(Item => SamePath(Item.Scope!, Priority)).ToArray();
                if (VisibleScopes.Length == 0) VisibleScopes = Group.ToArray();
                var ViewKey = Repository.Root + "\0" + Priority + "\0" + string.Join('\0', VisibleScopes.Select(Item => Item.Scope!).Order(StringComparer.OrdinalIgnoreCase));
                VersionSnapshot? View;
                long ViewGeneration;
                lock (this.CacheSync)
                {
                    ViewGeneration = this.CacheGeneration;
                    View = this.CacheResults ? this.SvnViews.GetValueOrDefault(ViewKey) : null;
                }
                if (View is not null)
                {
                    if (SnapshotReady is not null) await SnapshotReady(View).ConfigureAwait(false);
                    return View;
                }
                var Inventory = await this.ReadSvnInventoriesAsync(VisibleScopes, Cancellation).ConfigureAwait(false);
                var ChangesKey = new Repository(Repository.Root, "SVN", Priority is not null && !SamePath(Priority, Repository.Root) ? Priority : null, ChangesOnly: true);
                VersionSnapshot? CachedChanges;
                lock (this.CacheSync)
                {
                    var WholeCopy = new Repository(Repository.Root, "SVN", ChangesOnly: true);
                    if (this.CacheResults && this.Snapshots.ContainsKey(WholeCopy)) ChangesKey = WholeCopy;
                    this.Snapshots.TryGetValue(ChangesKey, out CachedChanges);
                }
                if (CachedChanges is null && SnapshotReady is not null)
                    await SnapshotReady(Inventory.WithPendingDirectories()).ConfigureAwait(false);
                var Changes = await this.ReadRepositoryAsync(ChangesKey, Cancellation).ConfigureAwait(false);
                Result = VersionSnapshot.Combine([Inventory, Changes]);
                if (ChangesKey.Scope is not null) Result = Result.WithCoverage(ChangesKey.Scope);
                lock (this.CacheSync)
                    if (this.CacheResults && ViewGeneration == this.CacheGeneration)
                    {
                        if (this.SvnViews.Count >= 256) this.SvnViews.Clear();
                        this.SvnViews[ViewKey] = Result;
                    }
            }
            else Result = await this.ReadRepositoryAsync(Repository, Cancellation).ConfigureAwait(false);
            if (SnapshotReady is not null) await SnapshotReady(Result).ConfigureAwait(false);
            return Result;
        }, Cancellation))).ConfigureAwait(false);
    }

    private async Task<VersionSnapshot> ReadSvnInventoriesAsync(Repository[] Scopes, CancellationToken Cancellation)
    {
        var Parts = new List<VersionSnapshot>();
        var Missing = new List<Repository>();
        lock (this.CacheSync)
            foreach (var Scope in Scopes)
                if (this.CacheResults && this.Snapshots.TryGetValue(Scope, out var Cached)) Parts.Add(Cached);
                else Missing.Add(Scope);
        // Keep each native command comfortably below Windows' command-line limit.
        for (var Offset = 0; Offset < Missing.Count;)
        {
            var Batch = new List<Repository>();
            var Length = 0;
            while (Offset < Missing.Count && Batch.Count < 64 && (Length < 16000 || Batch.Count == 0))
            {
                var Scope = Missing[Offset++];
                Batch.Add(Scope);
                Length += Scope.Scope!.Length + 3;
            }
            var Targets = Batch.Select(Item => Item.Scope!).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            long Generation;
            lock (this.CacheSync) Generation = this.CacheGeneration;
            var Snapshot = await this.ReadRepositoryAsync(new(Scopes[0].Root, "SVN", "batch:" + string.Join('\0', Targets)), Cancellation, Targets).ConfigureAwait(false);
            if (Snapshot.Error is not null) return Snapshot;
            lock (this.CacheSync)
                if (this.CacheResults && Generation == this.CacheGeneration)
                    foreach (var Scope in Batch) this.Snapshots[Scope] = Snapshot;
            Parts.Add(Snapshot);
        }
        return VersionSnapshot.CombineRaw(Parts.Distinct());
    }

    private Repository? Discover(string DirectoryPath, Dictionary<string, Repository?> Cache, CancellationToken Cancellation)
    {
        long Generation;
        lock (this.CacheSync) Generation = this.CacheGeneration;
        var Visited = new List<string>();
        Repository? Found = null;
        for (var Current = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(DirectoryPath)); ;)
        {
            Cancellation.ThrowIfCancellationRequested();
            if (Cache.TryGetValue(Current, out Found)) break;
            lock (this.CacheSync)
                if (this.CacheResults && this.Discoveries.TryGetValue(Current, out Found)) break;
            Visited.Add(Current);
            Interlocked.Increment(ref this.DiscoveryProbes);
            var Git = System.IO.Path.Combine(Current, ".git");
            if (Directory.Exists(Git) || File.Exists(Git)) { Found = new(Current, "Git"); break; }
            if (Directory.Exists(System.IO.Path.Combine(Current, ".svn"))) { Found = new(Current, "SVN"); break; }
            if (this.DiscoveryBoundary is not null && SamePath(Current, this.DiscoveryBoundary)) break;
            if (Directory.GetParent(Current) is not { } Parent) break;
            Current = Parent.FullName;
        }
        foreach (var Path in Visited) Cache[Path] = Found;
        // Only positive discoveries are retained: ordinary directories have no repository watcher.
        lock (this.CacheSync)
            if (this.CacheResults && Found is not null && Generation == this.CacheGeneration)
            {
                if (this.Discoveries.Count + Visited.Count > 4096) this.Discoveries.Clear();
                foreach (var Path in Visited) this.Discoveries[Path] = Found;
            }
        return Found;
    }

    private async Task<VersionSnapshot> ReadRepositoryAsync(Repository Repository, CancellationToken Cancellation, string[]? SvnScopes = null)
    {
        lock (this.CacheSync)
            if (this.CacheResults && this.Snapshots.TryGetValue(Repository, out var Ready)) return Ready;
        await this.Slots.WaitAsync(Cancellation).ConfigureAwait(false);
        long Generation;
        lock (this.CacheSync) Generation = this.CacheGeneration;
        try
        {
            lock (this.CacheSync)
            {
                if (this.CacheResults && this.Snapshots.TryGetValue(Repository, out var Cached)) return Cached;
            }
            RepositoryLocated?.Invoke(Repository.Root);
            var Snapshot = Repository.Provider == "Git"
                ? await this.ReadGitAsync(Repository, Cancellation).ConfigureAwait(false)
                : await this.ReadSvnAsync(Repository, Cancellation, SvnScopes).ConfigureAwait(false);
            lock (this.CacheSync)
                if (this.CacheResults && Generation == this.CacheGeneration) this.Snapshots[Repository] = Snapshot;
            return Snapshot;
        }
        catch (OperationCanceledException) when (Cancellation.IsCancellationRequested) { throw; }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or XmlException or ArgumentException or OperationCanceledException or System.Security.SecurityException)
        {
            var Failed = VersionSnapshot.Unavailable(Repository.Root, Repository.Provider, Error is OperationCanceledException ? "Query timed out" : Error.Message);
            lock (this.CacheSync) if (this.CacheResults && Generation == this.CacheGeneration) this.Snapshots[Repository] = Failed;
            return Failed;
        }
        finally { this.Slots.Release(); }
    }

    private async Task<VersionSnapshot> ReadGitAsync(Repository Repository, CancellationToken Cancellation)
    {
        var Tool = this.GitExecutable ?? throw new InvalidOperationException("git.exe was not found in PATH");
        string[] Prefix = ["--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "-c", "status.submoduleSummary=false"];
        Task<string> Git(params string[] Arguments) => this.RunAsync(Tool, Repository.Root, [.. Prefix, .. Arguments], Cancellation);
        (string Stamp, string[] Metadata, VersionSnapshot Clean) Inventory = default;
        lock (this.CacheSync) if (this.CacheResults) this.Inventories.TryGetValue(Repository.Root, out Inventory);
        var Metadata = Inventory.Metadata;
        if (Metadata is null)
        {
            var Locations = (await Git("rev-parse", "--path-format=absolute", "--show-toplevel", "--absolute-git-dir", "--git-common-dir").ConfigureAwait(false))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (Locations.Length != 3 || !SamePath(Locations[0], Repository.Root)) throw new InvalidOperationException("Git working-copy root could not be verified");
            Metadata = Locations.Skip(1).Select(Path => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Path)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        if (Metadata.Any(Path => !this.IsInsideBoundary(Path))) throw new InvalidOperationException("Git metadata is outside the test scope");
        foreach (var Path in Metadata) RepositoryLocated?.Invoke(Path);
        // Retry if the index/HEAD changes between the tracked-file and status queries (commit, stage, checkout).
        for (var Attempt = 0; Attempt < 2; Attempt++)
        {
            var Before = ReadGitStamp(Metadata);
            var Clean = Inventory.Stamp == Before && Inventory.Clean is not null ? Inventory.Clean : null;
            if (Clean is null)
            {
                var Tracked = await Git("ls-files", "--cached", "-z").ConfigureAwait(false);
                Clean = new VersionSnapshot(Repository.Root, "Git", [Repository.Root, .. Metadata]);
                foreach (var Relative in Tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries)) Clean.Set(Relative, VersionState.Clean);
                Clean.Aggregate();
            }
            var Status = await Git("status", "--porcelain=v1", "-z", "--untracked-files=normal", "--ignored=matching", "--ignore-submodules=none").ConfigureAwait(false);
            if (Before != ReadGitStamp(Metadata)) continue;
            lock (this.CacheSync) if (this.CacheResults) this.Inventories[Repository.Root] = (Before, Metadata, Clean);
            var Snapshot = Clean.Clone();
            ParseGitStatus(Status, Snapshot);
            Snapshot.Aggregate();
            return Snapshot;
        }
        throw new InvalidOperationException("Git index or HEAD changed during the query; retrying on the next refresh");
    }

    internal static void ParseGitStatus(string Output, VersionSnapshot Snapshot)
    {
        if (Output.Length > 0 && Output[^1] != '\0') throw new InvalidOperationException("Incomplete Git status output");
        var Records = Output.Split('\0');
        for (var Index = 0; Index < Records.Length - 1; Index++)
        {
            var Record = Records[Index];
            if (Record.Length < 4 || Record[2] != ' ') throw new InvalidOperationException("Invalid Git status record");
            var Code = Record[..2];
            var State = Code switch
            {
                "??" => VersionState.Untracked,
                "!!" => VersionState.Ignored,
                "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU" => VersionState.Conflict,
                _ when Code.Contains('U') => VersionState.Conflict,
                _ when Code.Contains('D') => VersionState.Deleted,
                _ when Code.Contains('R') => VersionState.Renamed,
                _ when Code.Contains('A') || Code.Contains('C') => VersionState.Added,
                _ when Code.Any(Char => Char is 'M' or 'T' or 'm') => VersionState.Modified,
                _ => throw new InvalidOperationException("Unknown Git status code: " + Code)
            };
            Snapshot.Set(Record[3..], State);
            if (Code.Contains('R') || Code.Contains('C'))
            {
                if (++Index >= Records.Length - 1 || Records[Index].Length == 0) throw new InvalidOperationException("Incomplete Git rename record");
                // Renaming out of a folder makes the old parent dirty too. Copying leaves the source clean.
                if (Code.Contains('R')) Snapshot.Set(Records[Index], VersionState.Deleted);
            }
        }
    }

    private async Task<VersionSnapshot> ReadSvnAsync(Repository Repository, CancellationToken Cancellation, string[]? Scopes = null)
    {
        var Tool = this.SvnExecutable ?? throw new InvalidOperationException("svn.exe was not found in PATH");
        bool Verified;
        lock (this.CacheSync) Verified = this.CacheResults && this.VerifiedSvnRoots.Contains(Repository.Root);
        if (!Verified)
        {
            var Info = ParseXml(await this.RunAsync(Tool, Repository.Root, ["info", "--xml", "--non-interactive", "--depth=empty", "--", "."], Cancellation).ConfigureAwait(false));
            var ReportedRoot = Info.Descendants("wcroot-abspath").SingleOrDefault()?.Value;
            if (ReportedRoot is null || !SamePath(ReportedRoot, Repository.Root)) throw new InvalidOperationException("SVN working-copy root could not be verified");
            lock (this.CacheSync) if (this.CacheResults) this.VerifiedSvnRoots.Add(Repository.Root);
        }
        var Root = Repository.Root;
        // Large working copies must not enumerate every clean descendant just to decorate visible rows.
        // Absolute targets keep XML entry paths relative to the working-copy root unambiguous.
        var Output = await this.RunAsync(Tool, Root, ["status", "--xml", .. (Repository.ChangesOnly ? Array.Empty<string>() : new[] { "--verbose" }), "--no-ignore", "--ignore-externals",
            Repository.ChangesOnly || Repository.Scope is null ? "--depth=infinity" : "--depth=immediates", "--non-interactive", "--", .. (Scopes ?? [Repository.Scope ?? "."])], Cancellation).ConfigureAwait(false);
        var Snapshot = new VersionSnapshot(Root, "SVN", [Root]);
        ParseSvnStatus(Output, Snapshot);
        if (Repository.Scope is null && !Repository.ChangesOnly) Snapshot.Aggregate();
        return Snapshot;
    }

    internal static void ParseSvnStatus(string Output, VersionSnapshot Snapshot)
    {
        var Document = ParseXml(Output);
        if (Document.Root?.Name != "status" || !Document.Descendants("target").Any()) throw new InvalidOperationException("Incomplete SVN status output");
        foreach (var Entry in Document.Descendants("entry"))
        {
            var Relative = Entry.Attribute("path")?.Value ?? throw new InvalidOperationException("Missing SVN entry path");
            var Status = Entry.Element("wc-status") ?? throw new InvalidOperationException("Missing SVN working-copy status");
            var Item = Status.Attribute("item")?.Value;
            var Properties = Status.Attribute("props")?.Value;
            var State = Status.Attribute("tree-conflicted")?.Value == "true" || Item == "conflicted" || Properties == "conflicted" ? VersionState.Conflict
                : Item is "incomplete" or "external" || Status.Attribute("file-external")?.Value == "true" ? VersionState.Unknown
                : Item switch
                {
                    "normal" or "none" when Properties == "modified" => VersionState.Modified,
                    "normal" => VersionState.Clean,
                    "none" => VersionState.None,
                    "modified" or "replaced" => VersionState.Modified,
                    "added" => VersionState.Added,
                    "deleted" or "missing" => VersionState.Deleted,
                    "obstructed" => VersionState.Conflict,
                    "unversioned" => VersionState.Untracked,
                    "ignored" => VersionState.Ignored,
                    _ => throw new InvalidOperationException("Unknown SVN status: " + Item)
                };
            Snapshot.Set(Relative, State);
        }
    }

    private async Task<string> RunAsync(string Tool, string DirectoryPath, string[] Arguments, CancellationToken Cancellation)
    {
        Interlocked.Increment(ref this.CommandCount);
        using var Deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        Deadline.CancelAfter(this.Timeout);
        var Info = new ProcessStartInfo(Tool)
        {
            WorkingDirectory = DirectoryPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var Argument in Arguments) Info.ArgumentList.Add(Argument);
        // Inherited Git overrides must not redirect a query to another repository or index.
        foreach (var Key in Info.Environment.Keys.Where(Key => Key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) Info.Environment.Remove(Key);
        Info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        Info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var Process = new Process { StartInfo = Info };
        if (!Process.Start()) throw new InvalidOperationException("Cannot start " + Tool);
        using var Registration = Deadline.Token.Register(() => { try { if (!Process.HasExited) Process.Kill(true); } catch (Exception Error) when (Error is InvalidOperationException or Win32Exception) { } });
        var Output = ReadLimitedAsync(Process.StandardOutput, 32 * 1024 * 1024, Deadline);
        var Errors = ReadLimitedAsync(Process.StandardError, 1024 * 1024, Deadline);
        await Task.WhenAll(Output, Errors, Process.WaitForExitAsync(Deadline.Token)).ConfigureAwait(false);
        Deadline.Token.ThrowIfCancellationRequested();
        if (Process.ExitCode != 0) throw new InvalidOperationException(System.IO.Path.GetFileName(Tool) + ": " + (await Errors.ConfigureAwait(false)).Trim());
        return await Output.ConfigureAwait(false);
    }

    private static async Task<string> ReadLimitedAsync(StreamReader Reader, int Limit, CancellationTokenSource Cancellation)
    {
        var Output = new StringBuilder();
        var Buffer = new char[4096];
        try
        {
            int Count;
            while ((Count = await Reader.ReadAsync(Buffer.AsMemory(), Cancellation.Token).ConfigureAwait(false)) > 0)
            {
                if (Output.Length + Count > Limit) throw new InvalidOperationException("Version-control output exceeds the supported size");
                Output.Append(Buffer, 0, Count);
            }
            return Output.ToString();
        }
        catch { Cancellation.Cancel(); throw; }
    }

    private static XDocument ParseXml(string Output)
    {
        using var Reader = XmlReader.Create(new StringReader(Output), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(Reader);
    }

    private static string ReadGitStamp(IEnumerable<string> Metadata) => string.Join("|", Metadata.SelectMany(Path => new[] { "index", "HEAD", "packed-refs" }
        .Select(Name => { var File = new FileInfo(System.IO.Path.Combine(Path, Name)); return File.Exists ? File.Length + ":" + File.LastWriteTimeUtc.Ticks : "missing"; })));

    internal static string? FindExecutable(string Name, string? SearchPath = null, IEnumerable<string>? InstallationDirectories = null)
    {
        foreach (var DirectoryPath in (SearchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Concat(InstallationDirectories ?? InstalledToolDirectories(Name)))
        {
            var Expanded = Environment.ExpandEnvironmentVariables(DirectoryPath.Trim().Trim('"'));
            if (!System.IO.Path.IsPathFullyQualified(Expanded)) continue;
            var Candidate = System.IO.Path.Combine(Expanded, Name + ".exe");
            if (File.Exists(Candidate)) return Candidate;
        }
        return null;
    }

    private static IEnumerable<string> InstalledToolDirectories(string Name)
    {
        foreach (var View in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var Hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            var Location = ReadInstallDirectory(Hive, View, Name == "git" ? "GitForWindows" : "TortoiseSVN", Name == "git" ? "InstallPath" : "Directory");
            if (Location is not null)
                foreach (var Relative in Name == "git" ? new[] { "cmd", "bin" } : new[] { "bin" }) yield return System.IO.Path.Combine(Location, Relative);
        }
        foreach (var Base in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
        foreach (var Relative in Name == "git" ? new[] { "Git/cmd", "Git/bin", "Programs/Git/cmd" } : new[] { "TortoiseSVN/bin", "SlikSvn/bin" })
            yield return System.IO.Path.Combine(Environment.GetFolderPath(Base), Relative);
    }

    private static string? ReadInstallDirectory(RegistryHive Hive, RegistryView View, string Product, string Value)
    {
        try
        {
            using var Registry = RegistryKey.OpenBaseKey(Hive, View);
            using var Key = Registry.OpenSubKey("SOFTWARE\\" + Product);
            return Key?.GetValue(Value) is string Location && System.IO.Path.IsPathFullyQualified(Location) ? Location : null;
        }
        catch (Exception Error) when (Error is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return null; }
    }

    private static bool SamePath(string Left, string Right) => string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Left)),
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Right)), StringComparison.OrdinalIgnoreCase);
    private bool IsInsideBoundary(string Path)
    {
        if (this.DiscoveryBoundary is null) return true;
        var FullPath = System.IO.Path.GetFullPath(Path);
        return SamePath(FullPath, this.DiscoveryBoundary) || FullPath.StartsWith(this.DiscoveryBoundary.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }
    private sealed record Repository(string Root, string Provider, string? Scope = null, bool ChangesOnly = false);
}

public sealed class VersionSnapshot
{
    private readonly Dictionary<string, VersionStatus> States = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VersionStatus> Inherited = new(StringComparer.Ordinal);
    private readonly HashSet<string> PendingAggregation = new(StringComparer.Ordinal);

    internal VersionSnapshot Clone()
    {
        var Copy = new VersionSnapshot(this.Root, this.Provider, this.WatchDirectories);
        foreach (var Pair in this.States) Copy.States.Add(Pair.Key, Pair.Value);
        foreach (var Pair in this.Inherited) Copy.Inherited.Add(Pair.Key, Pair.Value);
        return Copy;
    }
    public string Root { get; }
    private readonly string RootPrefix;
    private string CoveragePrefix;
    public string Provider { get; }
    public IReadOnlyList<string> WatchDirectories { get; }
    public string? Error { get; private init; }
    public string CoverageRoot { get; private set; } = "";
    public bool IsComplete { get; private set; } = true;
    public bool Covers(string Path)
    {
        Path = System.IO.Path.GetFullPath(Path);
        return string.Equals(this.CoverageRoot, System.IO.Path.TrimEndingDirectorySeparator(Path), StringComparison.OrdinalIgnoreCase)
            || Path.StartsWith(this.CoveragePrefix, StringComparison.OrdinalIgnoreCase);
    }
    internal VersionSnapshot WithCoverage(string Path)
    {
        this.CoverageRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Path));
        this.CoveragePrefix = this.CoverageRoot.TrimEnd('\\') + "\\";
        return this;
    }
    public VersionSnapshot(string Root, string Provider, IReadOnlyList<string> WatchDirectories)
    {
        this.Root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Root));
        this.RootPrefix = this.Root.TrimEnd('\\') + "\\";
        this.CoveragePrefix = this.RootPrefix;
        this.CoverageRoot = this.Root;
        this.Provider = Provider;
        this.WatchDirectories = WatchDirectories.Select(Path => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static VersionSnapshot Combine(IEnumerable<VersionSnapshot> Snapshots)
    {
        var Combined = CombineRaw(Snapshots);
        if (Combined.Error is null) Combined.Aggregate();
        return Combined;
    }

    internal VersionSnapshot WithPendingDirectories()
    {
        if (this.Error is not null) return this;
        var Copy = CombineRaw([this]);
        Copy.IsComplete = false;
        foreach (var Pair in this.States)
            if (Pair.Value.State == VersionState.Clean && (Pair.Key == "." || Directory.Exists(System.IO.Path.Combine(this.Root, Pair.Key.Replace('/', '\\')))))
                Copy.Set(Pair.Key, VersionState.Unknown);
        Copy.Aggregate();
        return Copy;
    }

    internal static VersionSnapshot CombineRaw(IEnumerable<VersionSnapshot> Snapshots)
    {
        // Merge raw directory records before aggregating; a later clean parent must not erase a child's changes.
        var Parts = Snapshots.ToArray();
        var First = Parts[0];
        var Error = Parts.FirstOrDefault(Part => Part.Error is not null)?.Error;
        if (Error is not null) return Unavailable(First.Root, First.Provider, Error);
        var Combined = new VersionSnapshot(First.Root, First.Provider, Parts.SelectMany(Part => Part.WatchDirectories).ToArray());
        foreach (var Part in Parts)
            foreach (var Pair in Part.States) Combined.Set(Pair.Key, Pair.Value.State);
        return Combined;
    }

    public static VersionSnapshot Unavailable(string Root, string Provider, string Error) => new(Root, Provider, [Root]) { Error = Error };
    internal static VersionSnapshot Checking(string Root, string Provider) => new(Root, Provider, [Root]) { IsComplete = false };
    public bool Contains(string Path) => string.Equals(this.Root, System.IO.Path.TrimEndingDirectorySeparator(Path), StringComparison.OrdinalIgnoreCase)
        || Path.StartsWith(this.RootPrefix, StringComparison.OrdinalIgnoreCase);

    public VersionStatus GetStatus(string Path)
    {
        Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Path));
        if (!this.Contains(Path)) return VersionStatus.None;
        // All paths are now canonical. Avoid a second full normalization and
        // allocating a string array for every badge in large working copies.
        var Relative = Path.Length == this.Root.Length ? "." : Path[this.RootPrefix.Length..].Replace('\\', '/');
        var Remaining = Relative.AsSpan();
        while (!Remaining.IsEmpty)
        {
            var Slash = Remaining.IndexOf('/');
            var Part = Slash < 0 ? Remaining : Remaining[..Slash];
            if (Part.Equals(".git", StringComparison.OrdinalIgnoreCase) || Part.Equals(".svn", StringComparison.OrdinalIgnoreCase)) return VersionStatus.None;
            if (Slash < 0) break;
            Remaining = Remaining[(Slash + 1)..];
        }
        if (!string.Equals(this.CoverageRoot, Path, StringComparison.OrdinalIgnoreCase)
            && !Path.StartsWith(this.CoveragePrefix, StringComparison.OrdinalIgnoreCase)) return new(VersionState.Unknown, this.Provider);
        if (this.Error is not null) return new(VersionState.Unknown, this.Provider, Error: this.Error);
        if (this.States.TryGetValue(Relative, out var Status)) return Status;
        for (var Parent = Relative; Parent.Contains('/');)
        {
            Parent = Parent[..Parent.LastIndexOf('/')];
            if (this.Inherited.TryGetValue(Parent, out Status)) return Status;
        }
        return this.IsComplete ? VersionStatus.None : new(VersionState.Unknown, this.Provider);
    }

    internal void Set(string Relative, VersionState State)
    {
        var Path = System.IO.Path.IsPathFullyQualified(Relative) ? System.IO.Path.GetFullPath(Relative) : System.IO.Path.GetFullPath(Relative.Replace('/', '\\'), this.Root);
        if (!this.Contains(Path)) throw new InvalidOperationException("Version-control path is outside the working copy");
        var Key = System.IO.Path.GetRelativePath(this.Root, Path).Replace('\\', '/').TrimEnd('/');
        var Status = new VersionStatus(State, this.Provider);
        this.States[Key] = Status;
        this.PendingAggregation.Add(Key);
        if (State is VersionState.Ignored or VersionState.Untracked or VersionState.Unknown) this.Inherited[Key] = Status;
    }

    internal void Aggregate()
    {
        this.States.TryAdd(".", new(VersionState.Clean, this.Provider));
        foreach (var Pair in this.PendingAggregation.Select(Key => new KeyValuePair<string, VersionStatus>(Key, this.States[Key])).ToArray())
        {
            if (Pair.Key == "." || Pair.Value.State is VersionState.Ignored or VersionState.None) continue;
            var Parent = Pair.Key;
            while (true)
            {
                Parent = Parent.Contains('/') ? Parent[..Parent.LastIndexOf('/')] : ".";
                if (!this.States.TryGetValue(Parent, out var Previous) || Rank(Pair.Value.State) > Rank(Previous.State))
                    this.States[Parent] = Pair.Value with { ContainsChanges = Pair.Value.State != VersionState.Clean };
                if (Parent == ".") break;
            }
        }
        this.PendingAggregation.Clear();
    }

    private static int Rank(VersionState State) => State switch
    {
        VersionState.Unknown => 10, VersionState.Conflict => 9, VersionState.Modified => 8, VersionState.Deleted => 7,
        VersionState.Renamed => 6, VersionState.Added => 5, VersionState.Untracked => 4, VersionState.Clean => 2, _ => 0
    };
}
