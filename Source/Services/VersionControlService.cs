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

    public async Task<IReadOnlyList<VersionSnapshot>> ReadAsync(IEnumerable<string> Directories, CancellationToken Cancellation = default, bool ShallowSvn = false)
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
        var Results = await Task.WhenAll(Repositories.Select(Repository => this.ReadRepositoryAsync(Repository, Cancellation))).ConfigureAwait(false);
        return ShallowSvn ? Results.GroupBy(Snapshot => Snapshot.Root, StringComparer.OrdinalIgnoreCase)
            .Select(Group => Group.First().Provider == "SVN" ? VersionSnapshot.Combine(Group) : Group.First()).ToArray() : Results;
    }

    private Repository? Discover(string DirectoryPath, Dictionary<string, Repository?> Cache, CancellationToken Cancellation)
    {
        var Visited = new List<string>();
        Repository? Found = null;
        for (var Current = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(DirectoryPath)); ;)
        {
            Cancellation.ThrowIfCancellationRequested();
            if (Cache.TryGetValue(Current, out Found)) break;
            Visited.Add(Current);
            var Git = System.IO.Path.Combine(Current, ".git");
            if (Directory.Exists(Git) || File.Exists(Git)) { Found = new(Current, "Git"); break; }
            if (Directory.Exists(System.IO.Path.Combine(Current, ".svn"))) { Found = new(Current, "SVN"); break; }
            if (this.DiscoveryBoundary is not null && SamePath(Current, this.DiscoveryBoundary)) break;
            if (Directory.GetParent(Current) is not { } Parent) break;
            Current = Parent.FullName;
        }
        foreach (var Path in Visited) Cache[Path] = Found;
        return Found;
    }

    private async Task<VersionSnapshot> ReadRepositoryAsync(Repository Repository, CancellationToken Cancellation)
    {
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
                : await this.ReadSvnAsync(Repository, Cancellation).ConfigureAwait(false);
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
            var Status = await Git("status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=matching", "--ignore-submodules=none").ConfigureAwait(false);
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

    private async Task<VersionSnapshot> ReadSvnAsync(Repository Repository, CancellationToken Cancellation)
    {
        var Tool = this.SvnExecutable ?? throw new InvalidOperationException("svn.exe was not found in PATH");
        var Info = ParseXml(await this.RunAsync(Tool, Repository.Root, ["info", "--xml", "--non-interactive", "--depth=empty", "--", "."], Cancellation).ConfigureAwait(false));
        var ReportedRoot = Info.Descendants("wcroot-abspath").SingleOrDefault()?.Value;
        if (ReportedRoot is null || !SamePath(ReportedRoot, Repository.Root)) throw new InvalidOperationException("SVN working-copy root could not be verified");
        var Root = System.IO.Path.GetFullPath(ReportedRoot);
        // Large working copies must not enumerate every clean descendant just to decorate visible rows.
        // Absolute targets keep XML entry paths relative to the working-copy root unambiguous.
        var Output = await this.RunAsync(Tool, Root, ["status", "--xml", "--verbose", "--no-ignore", "--ignore-externals",
            Repository.Scope is null ? "--depth=infinity" : "--depth=immediates", "--non-interactive", "--", Repository.Scope ?? "."], Cancellation).ConfigureAwait(false);
        var Snapshot = new VersionSnapshot(Root, "SVN", [Root]);
        ParseSvnStatus(Output, Snapshot);
        if (Repository.Scope is null) Snapshot.Aggregate();
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
    private sealed record Repository(string Root, string Provider, string? Scope = null);
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
    public string Provider { get; }
    public IReadOnlyList<string> WatchDirectories { get; }
    public string? Error { get; private init; }
    public VersionSnapshot(string Root, string Provider, IReadOnlyList<string> WatchDirectories)
    {
        this.Root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Root));
        this.Provider = Provider;
        this.WatchDirectories = WatchDirectories.Select(Path => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static VersionSnapshot Combine(IEnumerable<VersionSnapshot> Snapshots)
    {
        // Merge raw directory records before aggregating; a later clean parent must not erase a child's changes.
        var Parts = Snapshots.ToArray();
        var First = Parts[0];
        var Error = Parts.FirstOrDefault(Part => Part.Error is not null)?.Error;
        if (Error is not null) return Unavailable(First.Root, First.Provider, Error);
        var Combined = new VersionSnapshot(First.Root, First.Provider, Parts.SelectMany(Part => Part.WatchDirectories).ToArray());
        foreach (var Part in Parts)
            foreach (var Pair in Part.States) Combined.Set(Pair.Key, Pair.Value.State);
        Combined.Aggregate();
        return Combined;
    }

    public static VersionSnapshot Unavailable(string Root, string Provider, string Error) => new(Root, Provider, [Root]) { Error = Error };
    public bool Contains(string Path) => string.Equals(this.Root, System.IO.Path.TrimEndingDirectorySeparator(Path), StringComparison.OrdinalIgnoreCase)
        || Path.StartsWith(this.Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public VersionStatus GetStatus(string Path)
    {
        if (!this.Contains(Path)) return VersionStatus.None;
        var Relative = System.IO.Path.GetRelativePath(this.Root, Path).Replace('\\', '/');
        if (Relative.Split('/').Any(Part => Part.Equals(".git", StringComparison.OrdinalIgnoreCase) || Part.Equals(".svn", StringComparison.OrdinalIgnoreCase))) return VersionStatus.None;
        if (this.Error is not null) return new(VersionState.Unknown, this.Provider, Error: this.Error);
        if (this.States.TryGetValue(Relative, out var Status)) return Status;
        for (var Parent = Relative; Parent.Contains('/');)
        {
            Parent = Parent[..Parent.LastIndexOf('/')];
            if (this.Inherited.TryGetValue(Parent, out Status)) return Status;
        }
        return VersionStatus.None;
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
