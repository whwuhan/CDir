using System.Diagnostics;
using System.Text;
using CDir.Models;
using CDir.Services;

internal sealed partial class RegressionRunner
{
    private void RunVersionControlChecks()
    {
        var Service = new VersionControlService(DiscoveryBoundary: this.Root);
        VersionSnapshot Read(string Path) => Service.ReadAsync([Path]).GetAwaiter().GetResult().Single();
        void State(VersionSnapshot Snapshot, string Relative, VersionState Expected)
        {
            var Status = Snapshot.GetStatus(System.IO.Path.Combine(Snapshot.Root, Relative));
            Check(Status.State == Expected, $"{Snapshot.Provider} {Relative}: expected {Expected}, got {Status.State}: {Status.Description}");
        }
        string Git(string Path, params string[] Arguments) => VcsCommand(VersionControlService.FindExecutable("git"), Path,
            ["-c", "user.name=CDirTests", "-c", "user.email=cdir@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + this.MakeDirectory("EmptyVcsHooks"), .. Arguments]);
        string NewGit(string Name)
        {
            var Path = this.MakeDirectory(Name);
            Git(Path, "-c", "init.templateDir=", "init", "-q");
            File.WriteAllText(System.IO.Path.Combine(Path, "Clean.txt"), "clean\n");
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "Child"));
            File.WriteAllText(System.IO.Path.Combine(Path, "Child", "Tracked.txt"), "initial\n");
            File.WriteAllText(System.IO.Path.Combine(Path, ".gitignore"), "Ignored/\n");
            Git(Path, "add", "."); Git(Path, "commit", "-qm", "Initial fixture");
            return System.IO.Path.GetFullPath(Path);
        }

        this.Test("Git snapshots reuse unchanged state and invalidate precisely on file changes or watcher removal", () =>
        {
            var Path = NewGit("GitCache");
            var Cached = new VersionControlService(DiscoveryBoundary: this.Root, CacheResults: true);
            VersionSnapshot Query() => Cached.ReadAsync([Path]).GetAwaiter().GetResult().Single();
            var First = Query();
            var Commands = Cached.NativeCommandCount;
            Check(ReferenceEquals(First, Query()) && Cached.NativeCommandCount == Commands, "Unchanged reads must not spawn Git.");
            var Changed = System.IO.Path.Combine(Path, "Child", "Tracked.txt");
            File.AppendAllText(Changed, "change\n");
            Cached.Invalidate(Changed);
            var Updated = Query();
            State(Updated, "Child/Tracked.txt", VersionState.Modified); State(Updated, "Clean.txt", VersionState.Clean);
            Check(Cached.NativeCommandCount == Commands + 1, "Content-only refresh must reuse the index inventory and query status only.");
            Git(Path, "restore", "--", "Child/Tracked.txt");
            Commands = Cached.NativeCommandCount;
            Cached.Invalidate(this.Root);
            State(Query(), "Child/Tracked.txt", VersionState.Clean);
            Check(Cached.NativeCommandCount > Commands, "Removing a parent watcher must invalidate cached descendants.");
        });
        this.Test("Git clean files and timestamp-only changes do not show dirty badges", () =>
        {
            var Path = NewGit("GitClean");
            var Snapshot = Read(Path);
            State(Snapshot, "Clean.txt", VersionState.Clean); State(Snapshot, "Child", VersionState.Clean); State(Snapshot, ".", VersionState.Clean);
            File.SetLastWriteTimeUtc(System.IO.Path.Combine(Path, "Clean.txt"), DateTime.UtcNow.AddMinutes(1));
            Snapshot = Read(Path); State(Snapshot, "Clean.txt", VersionState.Clean); State(Snapshot, ".", VersionState.Clean);
            State(Snapshot, ".git/index", VersionState.None);
            var TimedOut = new VersionControlService(Timeout: TimeSpan.Zero).ReadAsync([Path]).GetAwaiter().GetResult().Single();
            State(TimedOut, "Clean.txt", VersionState.Unknown);
        });
        this.Test("Git modifications affect only the changed file and its ancestor directories and revert clears them", () =>
        {
            var Path = NewGit("GitModify");
            File.AppendAllText(System.IO.Path.Combine(Path, "Child", "Tracked.txt"), "changed\n");
            var Snapshot = Read(Path);
            State(Snapshot, "Child/Tracked.txt", VersionState.Modified); State(Snapshot, "Child", VersionState.Modified);
            State(Snapshot, "Clean.txt", VersionState.Clean); State(Snapshot, ".", VersionState.Modified);
            Check(Snapshot.GetStatus(System.IO.Path.Combine(Path, "Child")).ContainsChanges, "Directory tooltips must distinguish descendant changes.");
            Git(Path, "restore", "--", "Child/Tracked.txt"); Snapshot = Read(Path);
            State(Snapshot, "Child/Tracked.txt", VersionState.Clean); State(Snapshot, ".", VersionState.Clean);
        });
        this.Test("Git staged modifications Unicode renames additions and deletions are parsed without leaking status to siblings", () =>
        {
            var Path = NewGit("GitStage");
            Git(Path, "mv", "Child/Tracked.txt", "重命名 文件.txt");
            Git(Path, "rm", "Clean.txt");
            File.WriteAllText(System.IO.Path.Combine(Path, "Added.txt"), "new\n"); Git(Path, "add", "Added.txt");
            var Snapshot = Read(Path);
            State(Snapshot, "重命名 文件.txt", VersionState.Renamed); State(Snapshot, "Child", VersionState.Deleted);
            State(Snapshot, "Added.txt", VersionState.Added); State(Snapshot, "Clean.txt", VersionState.Deleted);
            Git(Path, "commit", "-qm", "Commit changes"); Snapshot = Read(Path);
            State(Snapshot, "重命名 文件.txt", VersionState.Clean); State(Snapshot, "Added.txt", VersionState.Clean); State(Snapshot, ".", VersionState.Clean);
            File.AppendAllText(System.IO.Path.Combine(Path, "Added.txt"), "staged\n"); Git(Path, "add", "Added.txt");
            Snapshot = Read(Path); State(Snapshot, "Added.txt", VersionState.Modified);
        });
        this.Test("Git ignored and untracked files have distinct states and ignored files do not dirty the parent", () =>
        {
            var Path = NewGit("GitIgnore");
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "Ignored", "Nested"));
            File.WriteAllText(System.IO.Path.Combine(Path, "Ignored", "Nested", "Ignored.txt"), "ignored");
            var Snapshot = Read(Path);
            State(Snapshot, "Ignored", VersionState.Ignored); State(Snapshot, "Ignored/Nested/Ignored.txt", VersionState.Ignored); State(Snapshot, ".", VersionState.Clean);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "New Folder"));
            File.WriteAllText(System.IO.Path.Combine(Path, "New Folder", "未跟踪.txt"), "new");
            Snapshot = Read(Path); State(Snapshot, "New Folder/未跟踪.txt", VersionState.Untracked); State(Snapshot, "New Folder", VersionState.Untracked);
            State(Snapshot, "Clean.txt", VersionState.Clean);
        });
        this.Test("Git merge conflicts override ordinary changes while unrelated files remain clean", () =>
        {
            var Path = NewGit("GitConflict"); var Branch = Git(Path, "branch", "--show-current").Trim();
            Git(Path, "checkout", "-qb", "Other");
            File.WriteAllText(System.IO.Path.Combine(Path, "Child", "Tracked.txt"), "other\n"); Git(Path, "commit", "-qam", "Other change");
            Git(Path, "checkout", Branch);
            File.WriteAllText(System.IO.Path.Combine(Path, "Child", "Tracked.txt"), "main\n"); Git(Path, "commit", "-qam", "Main change");
            try { Git(Path, "merge", "Other"); } catch (InvalidOperationException) { }
            var Snapshot = Read(Path); State(Snapshot, "Child/Tracked.txt", VersionState.Conflict); State(Snapshot, "Child", VersionState.Conflict);
            State(Snapshot, ".", VersionState.Conflict); State(Snapshot, "Clean.txt", VersionState.Clean);
        });
        this.Test("Git worktrees use their own index and nested repositories keep independent states", () =>
        {
            var Path = NewGit("GitMain"); var Linked = System.IO.Path.Combine(this.Root, "GitLinked");
            Git(Path, "worktree", "add", "-qb", "Linked", Linked);
            File.AppendAllText(System.IO.Path.Combine(Linked, "Clean.txt"), "linked change\n");
            var Snapshot = Read(Linked); State(Snapshot, "Clean.txt", VersionState.Modified); State(Read(Path), "Clean.txt", VersionState.Clean);
            Check(Snapshot.WatchDirectories.Any(Directory => !Directory.StartsWith(Linked, StringComparison.OrdinalIgnoreCase)), "Linked-worktree metadata needs its own watcher.");
            var Nested = NewGit("GitMain/Nested");
            var Snapshots = Service.ReadAsync([Path, Nested]).GetAwaiter().GetResult();
            Check(Snapshots.Count == 2, "Nested working copies must not reuse their parent's snapshot.");
            State(Snapshots.Single(Item => Item.Root == Nested), ".", VersionState.Clean);
        });
        this.Test("Git submodule changes are detected even when configured to be ignored", () =>
        {
            var Path = NewGit("GitSuper"); var Source = NewGit("GitModuleSource");
            Git(Path, "-c", "protocol.file.allow=always", "submodule", "add", "-q", Source, "Module"); Git(Path, "commit", "-qm", "Add submodule");
            Git(Path, "config", "submodule.Module.ignore", "all");
            File.AppendAllText(System.IO.Path.Combine(Path, "Module", "Clean.txt"), "module change\n");
            State(Read(Path), "Module", VersionState.Modified);
            State(Read(System.IO.Path.Combine(Path, "Module")), "Clean.txt", VersionState.Modified);
        });
        this.Test("Version queries fail closed on missing tools and cancelled queries instead of reporting clean", () =>
        {
            var Path = NewGit("GitUnavailable");
            var Missing = new VersionControlService(GitExecutable: System.IO.Path.Combine(this.Root, "MissingGit.exe"));
            var Snapshot = Missing.ReadAsync([Path]).GetAwaiter().GetResult().Single();
            State(Snapshot, "Clean.txt", VersionState.Unknown); State(Snapshot, ".", VersionState.Unknown);
            State(Snapshot, ".git/index", VersionState.None);
            using var Cancellation = new CancellationTokenSource(); Cancellation.Cancel();
            Throws<OperationCanceledException>(() => Service.ReadAsync([Path], Cancellation.Token).GetAwaiter().GetResult());
            Check(Service.ReadAsync([this.MakeDirectory("NonRepository")]).GetAwaiter().GetResult().Count == 0, "Ordinary folders must not get repository badges.");
        });
        this.Test("Machine status parsing rejects truncation path escapes and XML entities", () =>
        {
            var Snapshot = new VersionSnapshot(this.MakeDirectory("Parser"), "Git", []);
            Throws<InvalidOperationException>(() => VersionControlService.ParseGitStatus(" M partial", Snapshot));
            Throws<InvalidOperationException>(() => VersionControlService.ParseGitStatus("R  dest\0", Snapshot));
            Throws<InvalidOperationException>(() => VersionControlService.ParseGitStatus(" M ../escape\0", Snapshot));
            Throws<System.Xml.XmlException>(() => VersionControlService.ParseSvnStatus("<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///c:/private'>]><status>&x;</status>", Snapshot));
        });

        string Svn(string Path, params string[] Arguments) => VcsCommand(VersionControlService.FindExecutable("svn"), Path,
            [.. Arguments, "--non-interactive", "--no-auth-cache", "--config-dir", this.MakeDirectory("SvnTestConfig")]);
        string NewSvn(string Name)
        {
            var Repository = this.MakeDirectory(Name + "Repository");
            VcsCommand(VersionControlService.FindExecutable("svnadmin"), this.Root, ["create", Repository]);
            var Path = System.IO.Path.Combine(this.Root, Name);
            Svn(this.Root, "checkout", "-q", new Uri(Repository + System.IO.Path.DirectorySeparatorChar).AbsoluteUri, Path);
            File.WriteAllText(System.IO.Path.Combine(Path, "Clean.txt"), "clean\n");
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "Child"));
            File.WriteAllText(System.IO.Path.Combine(Path, "Child", "Tracked.txt"), "initial\n");
            Svn(Path, "add", "Clean.txt", "Child"); Svn(Path, "propset", "svn:ignore", "Ignored", ".");
            Svn(Path, "commit", "-qm", "Initial fixture");
            return Path;
        }
        this.Test("SVN watch paths use Windows separators so cached states refresh after edits", () =>
        {
            var Path = NewSvn("SvnCache");
            var Cached = new VersionControlService(DiscoveryBoundary: this.Root, CacheResults: true);
            VersionSnapshot Query() => Cached.ReadAsync([Path]).GetAwaiter().GetResult().Single();
            var First = Query();
            Check(First.WatchDirectories.All(Directory => !Directory.Contains('/')), "SVN XML paths must be normalized before matching watcher events.");
            var Changed = System.IO.Path.Combine(Path, "Child", "Tracked.txt");
            File.AppendAllText(Changed, "change\n");
            Cached.Invalidate(Changed);
            State(Query(), "Child/Tracked.txt", VersionState.Modified);
            Svn(Path, "revert", "-R", ".");
            Cached.Invalidate(Changed);
            State(Query(), "Child/Tracked.txt", VersionState.Clean);
        });
        this.Test("SVN clean content and timestamp-only changes stay clean", () =>
        {
            var Path = NewSvn("SvnClean");
            File.SetLastWriteTimeUtc(System.IO.Path.Combine(Path, "Clean.txt"), DateTime.UtcNow.AddMinutes(1));
            var Snapshot = Read(Path); State(Snapshot, "Clean.txt", VersionState.Clean); State(Snapshot, "Child", VersionState.Clean); State(Snapshot, ".", VersionState.Clean);
        });
        this.Test("SVN visible-directory queries omit deep content and combine cached scopes without losing changes", () =>
        {
            var Path = NewSvn("SvnVisible");
            var Child = System.IO.Path.Combine(Path, "Child");
            var Changed = System.IO.Path.Combine(Child, "Tracked.txt");
            File.AppendAllText(Changed, "changed\n");
            var Cached = new VersionControlService(DiscoveryBoundary: this.Root, CacheResults: true);
            VersionSnapshot Query(params string[] Paths) => Cached.ReadAsync(Paths, ShallowSvn: true).GetAwaiter().GetResult().Single();
            var Snapshot = Query(Path);
            State(Snapshot, "Clean.txt", VersionState.Clean); State(Snapshot, "Child/Tracked.txt", VersionState.None);
            var Commands = Cached.NativeCommandCount;
            Snapshot = Query(Path, Child);
            State(Snapshot, "Child/Tracked.txt", VersionState.Modified); State(Snapshot, "Child", VersionState.Modified); State(Snapshot, ".", VersionState.Modified);
            Check(Cached.NativeCommandCount == Commands + 2, "Only the newly visible scope should require SVN info/status commands.");
            Snapshot = Query(Child, Path);
            State(Snapshot, "Child", VersionState.Modified); State(Snapshot, "Clean.txt", VersionState.Clean);
            Check(Cached.NativeCommandCount == Commands + 2, "Changing scope order must reuse raw snapshots without overwriting aggregates.");
            Svn(Path, "revert", "-R", ".");
            Cached.Invalidate(Changed.Replace('\\', '/'));
            Snapshot = Query(Path, Child);
            State(Snapshot, "Child/Tracked.txt", VersionState.Clean); State(Snapshot, ".", VersionState.Clean);
            State(Snapshot, ".svn/wc.db", VersionState.None);
        });
        this.Test("Automatic discovery separates nested Git SVN and ordinary directories", () =>
        {
            var SvnPath = NewSvn("MixedSvn");
            var GitPath = NewGit("MixedSvn/GitChild");
            File.AppendAllText(System.IO.Path.Combine(SvnPath, "Clean.txt"), "svn change\n");
            var Ordinary = this.MakeDirectory("MixedOrdinary");
            var Snapshots = Service.ReadAsync([SvnPath, System.IO.Path.Combine(SvnPath, "Child"), GitPath, Ordinary], ShallowSvn: true).GetAwaiter().GetResult();
            Check(Snapshots.Count == 2, "Only Git and SVN repositories should produce snapshots.");
            State(Snapshots.Single(Snapshot => Snapshot.Provider == "SVN"), "Clean.txt", VersionState.Modified);
            State(Snapshots.Single(Snapshot => Snapshot.Provider == "Git"), "Clean.txt", VersionState.Clean);
            Check(Snapshots.All(Snapshot => Snapshot.GetStatus(Ordinary) == VersionStatus.None), "Ordinary directories must never inherit a provider badge.");
            var OuterGit = NewGit("MixedGit");
            var InnerSvn = System.IO.Path.Combine(OuterGit, "SvnChild");
            Svn(this.Root, "checkout", "-q", Svn(SvnPath, "info", "--show-item", "url", ".").Trim(), InnerSvn);
            Snapshots = Service.ReadAsync([OuterGit, InnerSvn], ShallowSvn: true).GetAwaiter().GetResult();
            Check(Snapshots.Count == 2 && Snapshots.Single(Snapshot => Snapshot.Root == InnerSvn).Provider == "SVN", "Nested SVN must override its Git ancestor.");
        });
        this.Test("Tool discovery supports custom installation directories and matches the requested executable", () =>
        {
            var Bin = this.MakeDirectory("CustomSvn/bin");
            var SvnExe = System.IO.Path.Combine(Bin, "svn.exe");
            File.WriteAllText(SvnExe, "discovery fixture only");
            EqualPath(SvnExe, VersionControlService.FindExecutable("svn", SearchPath: "", InstallationDirectories: [Bin])!);
            Check(VersionControlService.FindExecutable("svnadmin", SearchPath: "", InstallationDirectories: [Bin]) is null, "svn.exe cannot stand in for svnadmin.exe.");
            var AdminExe = System.IO.Path.Combine(Bin, "svnadmin.exe");
            File.WriteAllText(AdminExe, "discovery fixture only");
            EqualPath(AdminExe, VersionControlService.FindExecutable("svnadmin", SearchPath: "", InstallationDirectories: [Bin])!);
        });
        this.Test("SVN content and property changes propagate to ancestors without marking clean siblings", () =>
        {
            var Path = NewSvn("SvnModify");
            File.AppendAllText(System.IO.Path.Combine(Path, "Child", "Tracked.txt"), "change\n");
            Svn(Path, "propset", "custom:property", "value", "Clean.txt");
            var Snapshot = Read(Path); State(Snapshot, "Child/Tracked.txt", VersionState.Modified); State(Snapshot, "Child", VersionState.Modified);
            State(Snapshot, "Clean.txt", VersionState.Modified); State(Snapshot, ".", VersionState.Modified);
            Svn(Path, "revert", "-R", "."); Snapshot = Read(Path);
            State(Snapshot, "Child/Tracked.txt", VersionState.Clean); State(Snapshot, "Clean.txt", VersionState.Clean); State(Snapshot, ".", VersionState.Clean);
        });
        this.Test("SVN distinguishes ignored untracked added missing and deleted entries and clears committed changes", () =>
        {
            var Path = NewSvn("SvnStates");
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "Ignored")); File.WriteAllText(System.IO.Path.Combine(Path, "Ignored", "File.txt"), "ignore");
            var Snapshot = Read(Path); State(Snapshot, "Ignored/File.txt", VersionState.Ignored); State(Snapshot, ".", VersionState.Clean);
            File.WriteAllText(System.IO.Path.Combine(Path, "Untracked.txt"), "untracked");
            File.WriteAllText(System.IO.Path.Combine(Path, "新增 文件.txt"), "added"); Svn(Path, "add", "新增 文件.txt");
            File.Delete(System.IO.Path.Combine(Path, "Clean.txt")); Svn(Path, "delete", "Child/Tracked.txt");
            Snapshot = Read(Path); State(Snapshot, "Untracked.txt", VersionState.Untracked); State(Snapshot, "新增 文件.txt", VersionState.Added);
            State(Snapshot, "Clean.txt", VersionState.Deleted); State(Snapshot, "Child/Tracked.txt", VersionState.Deleted);
            Svn(Path, "revert", "Clean.txt"); Svn(Path, "commit", "-qm", "Commit changes");
            Snapshot = Read(Path); State(Snapshot, "新增 文件.txt", VersionState.Clean); State(Snapshot, "Clean.txt", VersionState.Clean);
        });
        this.Test("SVN real update conflicts show conflict badges and preserve clean siblings", () =>
        {
            var Path = NewSvn("SvnConflict"); var Other = System.IO.Path.Combine(this.Root, "SvnOther");
            var Url = Svn(Path, "info", "--show-item", "url", ".").Trim(); Svn(this.Root, "checkout", "-q", Url, Other);
            File.WriteAllText(System.IO.Path.Combine(Other, "Child", "Tracked.txt"), "other\n"); Svn(Other, "commit", "-qm", "Other change");
            File.WriteAllText(System.IO.Path.Combine(Path, "Child", "Tracked.txt"), "local\n"); Svn(Path, "update", "--accept", "postpone");
            var Snapshot = Read(Path); State(Snapshot, "Child/Tracked.txt", VersionState.Conflict); State(Snapshot, "Child", VersionState.Conflict);
            State(Snapshot, ".", VersionState.Conflict); State(Snapshot, "Clean.txt", VersionState.Clean);
        });
        this.Test("SVN property tree conflicts and incomplete externals cannot be mistaken for clean content", () =>
        {
            var Path = this.MakeDirectory("SvnParser"); var Snapshot = new VersionSnapshot(Path, "SVN", []);
            VersionControlService.ParseSvnStatus("<status><target path='.'><entry path='Property.txt'><wc-status item='normal' props='conflicted'/></entry><entry path='Tree'><wc-status item='normal' props='none' tree-conflicted='true'/></entry><entry path='External'><wc-status item='external' props='none'/></entry></target></status>", Snapshot);
            Snapshot.Aggregate(); State(Snapshot, "Property.txt", VersionState.Conflict); State(Snapshot, "Tree", VersionState.Conflict); State(Snapshot, "External", VersionState.Unknown);
        });
    }

    private static string VcsCommand(string? Tool, string WorkingDirectory, string[] Arguments)
    {
        if (Tool is null) throw new SkipTestException("Required version-control command-line tool is not installed");
        var Info = new ProcessStartInfo(Tool) { WorkingDirectory = WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var Argument in Arguments) Info.ArgumentList.Add(Argument);
        foreach (var Key in Info.Environment.Keys.Where(Key => Key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) Info.Environment.Remove(Key);
        using var Process = System.Diagnostics.Process.Start(Info) ?? throw new InvalidOperationException("Cannot launch test command");
        var Output = Process.StandardOutput.ReadToEndAsync(); var Errors = Process.StandardError.ReadToEndAsync();
        if (!Process.WaitForExit(20000)) { Process.Kill(true); throw new TimeoutException("VCS test command timed out"); }
        Task.WhenAll(Output, Errors).GetAwaiter().GetResult();
        if (Process.ExitCode != 0) throw new InvalidOperationException(System.IO.Path.GetFileName(Tool) + " " + string.Join(" ", Arguments) + ": " + Errors.Result + Output.Result);
        return Output.Result;
    }
}
