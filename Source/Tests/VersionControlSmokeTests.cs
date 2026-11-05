using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunVersionControlSmokeChecksAsync(List<string> Passed)
    {
        var GitTool = VersionControlService.FindExecutable("git");
        var SvnTool = VersionControlService.FindExecutable("svn");
        var SvnAdmin = VersionControlService.FindExecutable("svnadmin");
        if (GitTool is null || SvnTool is null || SvnAdmin is null) return;
        var InitialPath = this.CurrentPath;
        var InitialSearch = this.SearchBox.Text;
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        var InitialFavoritesExpanded = this.FavoritesSection.IsExpanded;
        var InitialTreeExpanded = this.DirectoryTreeSection.IsExpanded;
        var InitialScroll = this.SidebarScroll.VerticalOffset;
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CDirVcsUI" + FixtureId);
        Directory.CreateDirectory(Fixture);
        var Marker = System.IO.Path.Combine(Fixture, ".CDirVcsOwner");
        File.WriteAllText(Marker, FixtureId);
        var Hooks = Directory.CreateDirectory(System.IO.Path.Combine(Fixture, "EmptyHooks")).FullName;

        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Version-control UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Command(string Tool, string Path, params string[] Arguments)
        {
            var Info = new ProcessStartInfo(Tool) { WorkingDirectory = Path, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var Key in Info.Environment.Keys.Where(Key => Key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) Info.Environment.Remove(Key);
            foreach (var Argument in Arguments) Info.ArgumentList.Add(Argument);
            using var Process = System.Diagnostics.Process.Start(Info) ?? throw new InvalidOperationException("Cannot launch VCS fixture command");
            var Output = Process.StandardOutput.ReadToEndAsync(); var Errors = Process.StandardError.ReadToEndAsync();
            using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await Process.WaitForExitAsync(Timeout.Token); }
            catch (OperationCanceledException) { Process.Kill(true); await Process.WaitForExitAsync(); throw; }
            await Task.WhenAll(Output, Errors);
            if (Process.ExitCode != 0) throw new InvalidOperationException("VCS fixture command failed: " + Errors.Result + Output.Result);
        }
        Task Git(string Path, params string[] Arguments) => Command(GitTool, Path,
            ["-c", "user.name=CDirTests", "-c", "user.email=cdir@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + Hooks, .. Arguments]);
        Task Svn(string Path, params string[] Arguments) => Command(SvnTool, Path,
            [.. Arguments, "--non-interactive", "--no-auth-cache", "--config-dir", System.IO.Path.Combine(Fixture, "SvnConfig")]);
        FileEntry Entry(string Name) => this.Entries.Single(Entry => Entry.Name == Name);
        FolderNode TreeNode(string Path) => this.AllVersionNodes().First(Node => SamePath(Node.FullPath, Path));
        async Task WaitUntil(Func<bool> Condition)
        {
            var End = Environment.TickCount64 + 20000;
            while (!Condition())
            {
                if (Environment.TickCount64 > End) throw new TimeoutException("VCS UI refresh timed out: " + string.Join("; ", this.Entries.Select(Entry => Entry.Name + "=" + Entry.VersionStatus.Description)));
                await Task.Delay(50);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            }
        }
        try
        {
            var GitPath = Directory.CreateDirectory(System.IO.Path.Combine(Fixture, "GitWorkingCopy")).FullName;
            var ChildPath = Directory.CreateDirectory(System.IO.Path.Combine(GitPath, "Child")).FullName;
            File.WriteAllText(System.IO.Path.Combine(GitPath, "Clean.txt"), "clean\n");
            File.WriteAllText(System.IO.Path.Combine(GitPath, "Modified.txt"), "initial\n");
            File.WriteAllText(System.IO.Path.Combine(ChildPath, "Deep.txt"), "initial\n");
            await Git(GitPath, "-c", "init.templateDir=", "init", "-q"); await Git(GitPath, "add", "."); await Git(GitPath, "commit", "-qm", "Initial fixture");
            File.AppendAllText(System.IO.Path.Combine(GitPath, "Modified.txt"), "change\n");
            if (!await this.NavigateAsync(GitPath)) throw new InvalidOperationException("Cannot open Git UI fixture");
            await WaitUntil(() => Entry("Clean.txt").VersionStatus.State == VersionState.Clean && Entry("Modified.txt").VersionStatus.State == VersionState.Modified);
            Check(TreeNode(GitPath).VersionStatus is { State: VersionState.Modified, ContainsChanges: true }
                && Entry("Child").VersionStatus.State == VersionState.Clean,
                "Git list and directory tree distinguish changed files clean siblings and aggregate directory changes");

            // Start measuring once pending fixture-creation refreshes have completed.
            await WaitUntil(() => this.VersionCancellation is null && !this.VersionDebounce.IsEnabled && !this.VersionRefreshPending
                && Volatile.Read(ref this.VersionInvalidationQueued) == 0);
            var IdleCommands = this.VersionControl.NativeCommandCount;
            var LockNoise = System.IO.Path.Combine(GitPath, ".git", "index.lock");
            File.WriteAllText(LockNoise, "owned transient lock noise");
            File.Delete(LockNoise);
            await Task.Delay(3300);
            await this.RefreshVersionStatusesAsync();
            Check(this.VersionControl.NativeCommandCount == IdleCommands,
                $"unchanged repositories and transient Git locks do not poll and display refreshes reuse the verified snapshot (commands: {IdleCommands} -> {this.VersionControl.NativeCommandCount})");
            await this.NavigateAsync(ChildPath);
            await this.RefreshVersionStatusesAsync();
            await this.NavigateAsync(GitPath);
            await this.RefreshVersionStatusesAsync();
            await WaitUntil(() => this.VersionCancellation is null && Entry("Clean.txt").VersionStatus.State == VersionState.Clean);
            Check(this.VersionControl.NativeCommandCount == IdleCommands,
                "navigation inside the same repository does not start additional Git commands");

            var Modified = Entry("Modified.txt");
            this.FileGrid.ScrollIntoView(Modified); this.FileGrid.UpdateLayout();
            var Row = this.FileGrid.ItemContainerGenerator.ContainerFromItem(Modified) as DataGridRow;
            var Icon = Row is null ? null : FindVisualChild<ShellIcon>(Row);
            Check(Icon?.VersionStatus.State == VersionState.Modified && Icon.ToolTip?.ToString()?.Contains(Modified.VersionStatus.Description, StringComparison.Ordinal) == true,
                "file icon badges bind to verified status and expose a localized status tooltip");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var Decorated = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32); Decorated.Render(Icon!);
            var Status = Icon!.VersionStatus;
            Icon.SetCurrentValue(ShellIcon.VersionStatusProperty, VersionStatus.None);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var Plain = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32); Plain.Render(Icon);
            Icon.SetCurrentValue(ShellIcon.VersionStatusProperty, Status);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(!IconPixelsEqual(Plain, Decorated), "verified status badges render separately from the base Shell bitmap");

            File.AppendAllText(System.IO.Path.Combine(ChildPath, "Deep.txt"), "deep change\n");
            await WaitUntil(() => Entry("Child").VersionStatus.State == VersionState.Modified);
            Check(Entry("Clean.txt").VersionStatus.State == VersionState.Clean, "repository watcher refreshes deep changes without marking clean siblings dirty");
            this.CaptureVersionControlPreview("VersionControlGit.png");
            await Git(GitPath, "restore", "--", ".");
            await WaitUntil(() => Entry("Modified.txt").VersionStatus.State == VersionState.Clean && Entry("Child").VersionStatus.State == VersionState.Clean);
            Check(TreeNode(GitPath).VersionStatus.State == VersionState.Clean, "Git restore automatically clears list and directory tree dirty badges");
            File.AppendAllText(System.IO.Path.Combine(GitPath, "Modified.txt"), "to commit\n");
            await WaitUntil(() => Entry("Modified.txt").VersionStatus.State == VersionState.Modified);
            await Git(GitPath, "add", "."); await Git(GitPath, "commit", "-qm", "Commit fixture change");
            await WaitUntil(() => Entry("Modified.txt").VersionStatus.State == VersionState.Clean && TreeNode(GitPath).VersionStatus.State == VersionState.Clean);
            Check(true, "Git metadata changes after staging and commit trigger a fresh clean snapshot");

            var SvnRepository = Directory.CreateDirectory(System.IO.Path.Combine(Fixture, "SvnRepository")).FullName;
            await Command(SvnAdmin, Fixture, "create", SvnRepository);
            var SvnPath = System.IO.Path.Combine(Fixture, "SvnWorkingCopy");
            await Svn(Fixture, "checkout", "-q", new Uri(SvnRepository + System.IO.Path.DirectorySeparatorChar).AbsoluteUri, SvnPath);
            File.WriteAllText(System.IO.Path.Combine(SvnPath, "Clean.txt"), "clean\n");
            File.WriteAllText(System.IO.Path.Combine(SvnPath, "Property.txt"), "unchanged content\n");
            var SvnChild = Directory.CreateDirectory(System.IO.Path.Combine(SvnPath, "Child")).FullName;
            File.WriteAllText(System.IO.Path.Combine(SvnChild, "深层 文件.txt"), "initial\n");
            await Svn(SvnPath, "add", "Clean.txt", "Property.txt", "Child"); await Svn(SvnPath, "commit", "-qm", "Initial fixture");
            await Svn(SvnPath, "propset", "custom:property", "value", "Property.txt");
            if (!await this.NavigateAsync(SvnPath)) throw new InvalidOperationException("Cannot open SVN UI fixture");
            await WaitUntil(() => Entry("Property.txt").VersionStatus.State == VersionState.Modified && Entry("Clean.txt").VersionStatus.State == VersionState.Clean);
            Check(TreeNode(SvnPath).VersionStatus.State == VersionState.Modified, "SVN property-only changes get verified badges while unchanged files remain clean");
            File.AppendAllText(System.IO.Path.Combine(SvnPath, "Clean.txt"), "content change\n");
            await WaitUntil(() => Entry("Clean.txt").VersionStatus.State == VersionState.Modified);
            Check(true, "SVN watcher events invalidate cached XML paths after content changes");
            File.AppendAllText(System.IO.Path.Combine(SvnChild, "深层 文件.txt"), "deep change\n");
            await WaitUntil(() => Entry("Child").VersionStatus.State == VersionState.Modified);
            if (!await this.NavigateAsync(SvnChild)) throw new InvalidOperationException("Cannot open SVN child fixture");
            await WaitUntil(() => Entry("深层 文件.txt").VersionStatus is { State: VersionState.Modified, Provider: "SVN" }
                && this.AddressIcon.VersionStatus.Provider == "SVN");
            Check(true, "entering SVN subdirectories automatically reads visible Unicode files from the owning working copy");
            await this.NavigateAsync(SvnPath);
            await WaitUntil(() => Entry("Child").VersionStatus.State == VersionState.Modified);
            this.CaptureVersionControlPreview("VersionControlSvn.png");
            await Svn(SvnPath, "revert", "-R", ".");
            await WaitUntil(() => Entry("Property.txt").VersionStatus.State == VersionState.Clean && TreeNode(SvnPath).VersionStatus.State == VersionState.Clean);
            Check(true, "SVN revert refreshes working-copy metadata and clears obsolete dirty badges");

            var OldQuery = this.RefreshVersionStatusesAsync();
            if (!await this.NavigateAsync(GitPath)) throw new InvalidOperationException("Cannot switch back to Git fixture");
            await OldQuery;
            await WaitUntil(() => Entry("Clean.txt").VersionStatus is { State: VersionState.Clean, Provider: "Git" } && this.VersionCancellation is null);
            Check(this.Entries.All(Entry => Entry.VersionStatus.Provider == "Git"), "cancelled queries cannot overwrite the status of a newly navigated working copy");

            var OrdinaryPath = Directory.CreateDirectory(System.IO.Path.Combine(Fixture, "Ordinary")).FullName;
            Directory.CreateDirectory(System.IO.Path.Combine(OrdinaryPath, "Child"));
            File.WriteAllText(System.IO.Path.Combine(OrdinaryPath, "Plain.txt"), "no repository\n");
            if (!await this.NavigateAsync(OrdinaryPath)) throw new InvalidOperationException("Cannot open ordinary fixture");
            await this.RefreshVersionStatusesAsync();
            await WaitUntil(() => this.VersionCancellation is null && this.Entries.All(Entry => Entry.VersionStatus == VersionStatus.None)
                && this.AddressIcon.VersionStatus == VersionStatus.None && TreeNode(OrdinaryPath).VersionStatus == VersionStatus.None);
            Check(this.AddressIcon.ToolTip is null, "ordinary directories show no extra badges or version-control tooltips after leaving Git and SVN");

            // Keep the main pane outside either repository and hide the drive tree. Status must be
            // discovered from category headers and their independent descendants, including duplicates.
            this.RestoreSidebarCategories([]);
            this.TryAddSidebarCategory("VCS fixture");
            var Category = this.CustomCategories.Single();
            Category.Folders.Add(new CategoryFolder { FullPath = GitPath });
            Category.Folders.Add(new CategoryFolder { FullPath = SvnPath });
            this.TryAddSidebarCategory("Duplicate fixture");
            var DuplicateCategory = this.CustomCategories[^1];
            DuplicateCategory.Folders.Add(new CategoryFolder { FullPath = GitPath });
            this.FavoritesSection.IsExpanded = false;
            this.DirectoryTreeSection.IsExpanded = false;
            var GitBookmark = Category.Folders[0].Node;
            var SvnBookmark = Category.Folders[1].Node;
            var DuplicateBookmark = DuplicateCategory.Folders[0].Node;
            foreach (var Node in new[] { GitBookmark, SvnBookmark })
            {
                Node.IsExpanded = true;
                await this.EnsureChildrenAsync(Node);
            }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
            this.SidebarScroll.ScrollToTop();
            this.UpdateLayout();
            var GitChild = GitBookmark.Children.Single(Node => SamePath(Node.FullPath, ChildPath));
            var SvnChildNode = SvnBookmark.Children.Single(Node => SamePath(Node.FullPath, SvnChild));
            var BookmarkNodes = new[] { GitBookmark, SvnBookmark, DuplicateBookmark, GitChild, SvnChildNode };
            await WaitUntil(() => BookmarkNodes.All(Node => Node.VersionStatus.State == VersionState.Clean));
            Check(BookmarkNodes.All(Node => this.VisibleVersionNodes().Contains(Node))
                && GitBookmark.VersionStatus.Provider == "Git" && SvnBookmark.VersionStatus.Provider == "SVN",
                "visible custom category roots and children discover Git and SVN while the main pane is outside repositories");

            IEnumerable<ShellIcon> CategoryIcons(DependencyObject Parent)
            {
                for (var Index = 0; Index < VisualTreeHelper.GetChildrenCount(Parent); Index++)
                {
                    var Child = VisualTreeHelper.GetChild(Parent, Index);
                    if (Child is ShellIcon Icon) yield return Icon;
                    else foreach (var Descendant in CategoryIcons(Child)) yield return Descendant;
                }
            }
            var Icons = CategoryIcons(this.SidebarSectionElement(Category)!).ToArray();
            Check(BookmarkNodes.Where(Node => !ReferenceEquals(Node, DuplicateBookmark)).All(Node => Icons.Any(Icon => SamePath(Icon.Path ?? "", Node.FullPath)
                    && Icon.VersionStatus == Node.VersionStatus && Icon.ToolTip?.ToString()?.Contains(Node.VersionStatus.Description, StringComparison.Ordinal) == true)),
                "custom category root and child icons bind verified status and localized tooltips");

            File.AppendAllText(System.IO.Path.Combine(ChildPath, "Deep.txt"), "category Git change\n");
            File.AppendAllText(System.IO.Path.Combine(SvnChild, "深层 文件.txt"), "category SVN change\n");
            await WaitUntil(() => BookmarkNodes.All(Node => Node.VersionStatus.State == VersionState.Modified));
            Check(Icons.Where(Icon => Icon.IsVisible).All(Icon => Icon.VersionStatus.State == VersionState.Modified)
                && this.AddressIcon.VersionStatus == VersionStatus.None,
                "Git and SVN watcher changes update category root and child badges including duplicate bookmarks");
            this.CaptureVersionControlPreview("VersionControlCategories.png");

            await Git(GitPath, "restore", "--", ".");
            await Svn(SvnPath, "revert", "-R", ".");
            await WaitUntil(() => BookmarkNodes.All(Node => Node.VersionStatus.State == VersionState.Clean));
            Check(Icons.Where(Icon => Icon.IsVisible).All(Icon => Icon.VersionStatus.State == VersionState.Clean),
                "restoring Git and SVN files clears category dirty badges without navigating into the repositories");

            this.MarkVersionStatusesPending();
            Check(BookmarkNodes.All(Node => Node.VersionStatus.State == VersionState.Unknown),
                "pending status invalidation includes all independent bookmark trees");
            this.ScheduleVersionRefresh();
            await WaitUntil(() => BookmarkNodes.All(Node => Node.VersionStatus.State == VersionState.Clean));
            var Section = (Expander)this.SidebarSectionElement(Category)!;
            var DuplicateSection = (Expander)this.SidebarSectionElement(DuplicateCategory)!;
            Section.IsExpanded = false;
            DuplicateSection.IsExpanded = false;
            await WaitUntil(() => BookmarkNodes.All(Node => Node.VersionStatus == VersionStatus.None));
            Check(!this.VisibleVersionNodes().Any(BookmarkNodes.Contains)
                && !this.VersionWatchers.Keys.Any(Path => SamePath(Path, GitPath) || SamePath(Path, SvnPath)),
                "collapsed custom categories stop querying and watching repositories outside the current folder");
            Section.IsExpanded = true;
            DuplicateSection.IsExpanded = true;
            await WaitUntil(() => BookmarkNodes.All(Node => Node.VersionStatus.State == VersionState.Clean));
            Check(true, "expanding custom categories refreshes previously loaded roots and children without navigation");
        }
        finally
        {
            this.RestoreSidebarCategories(InitialCategories, InitialOrder);
            this.FavoritesSection.IsExpanded = InitialFavoritesExpanded;
            this.DirectoryTreeSection.IsExpanded = InitialTreeExpanded;
            this.SidebarScroll.ScrollToVerticalOffset(InitialScroll);
            if (!this.WindowClosed) await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.SearchBox.Text = InitialSearch;
            // Leave this fixture before disposing its recursive watcher and deleting readonly VCS objects.
            foreach (var Path in this.VersionWatchers.Keys.Where(Path => Path.StartsWith(Fixture, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                this.VersionWatchers[Path].Dispose(); this.VersionWatchers.Remove(Path);
            }
            var TempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd('\\') + "\\";
            if (!System.IO.Path.GetFullPath(Fixture).StartsWith(TempRoot, StringComparison.OrdinalIgnoreCase)
                || System.IO.Path.GetFileName(Fixture) != "CDirVcsUI" + FixtureId || File.ReadAllText(Marker) != FixtureId
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unsafe VCS test cleanup path");
            foreach (var Path in Directory.EnumerateFiles(Fixture, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                File.SetAttributes(Path, File.GetAttributes(Path) & ~FileAttributes.ReadOnly);
            Directory.Delete(Fixture, true);
        }
    }

    private void CaptureVersionControlPreview(string Name)
    {
        if (AppMain.ScreenshotPath is null) return;
        UpdateLayout();
        var Bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        Bitmap.Render(this);
        var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
        using var Stream = File.Create(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AppMain.ScreenshotPath)!, Name));
        Encoder.Save(Stream);
    }
}
