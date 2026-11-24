using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunSearchVersionBadgesSmokeAsync(List<string> Passed, string GitTool, string SvnTool, string SvnAdmin)
    {
        void Check(bool Value, string Message)
        {
            if (!Value) throw new InvalidOperationException("Search version badges: " + Message);
            Passed.Add(Message);
        }
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "VcsSearch")).FullName;
        var Scope = Directory.CreateDirectory(Path.Combine(Fixture, "Scope")).FullName;
        var GitRoot = Directory.CreateDirectory(Path.Combine(Scope, "A", "G")).FullName;
        var GitDeep = Directory.CreateDirectory(Path.Combine(GitRoot, "d", "e")).FullName;
        var SvnRoot = Path.Combine(Scope, "B", "S");
        var Repository = Path.Combine(Fixture, "Repo");
        var Hooks = Directory.CreateDirectory(Path.Combine(Fixture, "Hooks")).FullName;
        async Task Command(string Tool, string Directory, params string[] Arguments)
        {
            var Start = new ProcessStartInfo(Tool) { WorkingDirectory = Directory, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var Argument in Arguments) Start.ArgumentList.Add(Argument);
            using var Process = System.Diagnostics.Process.Start(Start)!;
            var Output = Process.StandardOutput.ReadToEndAsync();
            var Error = Process.StandardError.ReadToEndAsync();
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Task.WhenAll(Output, Error);
            if (Process.ExitCode != 0) throw new InvalidOperationException("Owned version fixture command failed: " + await Error + await Output);
        }
        Task Git(params string[] Arguments) => Command(GitTool, GitRoot,
            ["-c", "user.name=CDir Tests", "-c", "user.email=cdir-tests@example.invalid", "-c", "commit.gpgsign=false",
                "-c", "core.hooksPath=" + Hooks, "-c", "core.longpaths=true", .. Arguments]);
        Task Svn(string Directory, params string[] Arguments) => Command(SvnTool, Directory,
            [.. Arguments, "--non-interactive", "--no-auth-cache", "--config-dir", Path.Combine(Fixture, "SvnConfig")]);
        var GitClean = Path.Combine(GitDeep, "badge-clean.txt");
        var GitModified = Path.Combine(GitDeep, "badge-modified.txt");
        File.WriteAllText(GitClean, "clean\n"); File.WriteAllText(GitModified, "initial\n");
        await Git("-c", "init.templateDir=", "init", "-q"); await Git("add", "."); await Git("commit", "-qm", "Owned search fixture");
        File.AppendAllText(GitModified, "modified\n");
        await Command(SvnAdmin, Fixture, "create", Repository);
        Directory.CreateDirectory(Path.GetDirectoryName(SvnRoot)!);
        await Svn(Fixture, "checkout", new Uri(Repository).AbsoluteUri, SvnRoot);
        var SvnDeep = Directory.CreateDirectory(Path.Combine(SvnRoot, "d", "e")).FullName;
        var SvnClean = Path.Combine(SvnDeep, "badge-clean.txt");
        var SvnModified = Path.Combine(SvnDeep, "badge-modified.txt");
        File.WriteAllText(SvnClean, "clean\n"); File.WriteAllText(SvnModified, "initial\n");
        await Svn(SvnRoot, "add", "d"); await Svn(SvnRoot, "commit", "-m", "Owned search fixture");
        File.AppendAllText(SvnModified, "modified\n");
        var Plain = Path.Combine(Scope, "badge-plain.txt"); File.WriteAllText(Plain, "No repository");
        var SettingsFile = Path.Combine(Fixture, "Settings.json");
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppSettings
            { LastPath = Scope, ExitOnClose = true, AutoFitColumnsOnNavigation = true, FileView = FileViewMode.Details }));
        var Probe = new MainWindow(SettingsFile) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            Probe.Show(); await Probe.ActiveTab!.WaitUntilReadyAsync();
            var Page = Probe.ActiveTab!;
            Page.SearchBox.Text = "badge"; await Page.WaitForSearchSmokeAsync();
            FileEntry Entry(string Path) => Page.View!.Cast<FileEntry>().Single(Entry => Entry.FullPath == Path);
            async Task WaitUntil(Func<bool> Condition)
            {
                var Deadline = Environment.TickCount64 + 20000;
                while (!Condition())
                {
                    if (Environment.TickCount64 > Deadline) throw new TimeoutException("Search badges did not settle: "
                        + string.Join("; ", Page.View!.Cast<FileEntry>().Select(Entry => Entry.FullPath + "=" + Entry.VersionStatus.Description)));
                    await Task.Delay(40);
                }
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Page.UpdateLayout();
            }
            await WaitUntil(() => Entry(GitClean).VersionStatus is { Provider: "Git", State: VersionState.Clean }
                && Entry(GitModified).VersionStatus is { Provider: "Git", State: VersionState.Modified }
                && Entry(SvnClean).VersionStatus is { Provider: "SVN", State: VersionState.Clean }
                && Entry(SvnModified).VersionStatus is { Provider: "SVN", State: VersionState.Modified });
            Check(Page.View!.Cast<FileEntry>().Count() == 5 && Entry(Plain).VersionStatus.State == VersionState.None,
                "A search outside nested Git and SVN repositories resolves clean and modified deep matches and leaves ordinary files unbadged");
            foreach (var Mode in new[] { FileViewMode.Details, FileViewMode.LargeIcons, FileViewMode.List, FileViewMode.Tiles })
            {
                Page.ApplyRememberedFolderView(Mode, 48); Page.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                foreach (var Path in new[] { GitClean, GitModified, SvnClean, SvnModified })
                {
                    var Item = Entry(Path);
                    var Container = Mode == FileViewMode.Details
                        ? Page.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Name)).GetCellContent(Item)
                        : Page.FileTiles.ItemContainerGenerator.ContainerFromItem(Item) as FrameworkElement;
                    var Icon = Container is null ? null : FindVisualChild<ShellIcon>(Container);
                    Check(Icon is not null && Icon.VersionStatus == Item.VersionStatus && Icon.VersionStatus.State != VersionState.None,
                        Mode + ": a recursive " + Item.VersionStatus.Provider + " result binds its verified state to the rendered icon");
                }
            }
            Page.ApplyRememberedFolderView(FileViewMode.Details);
            Page.CaptureVersionControlPreview("SearchVersionBadges.png");
            await Git("checkout", "--", "d/e/badge-modified.txt");
            await Svn(SvnRoot, "revert", SvnModified);
            await WaitUntil(() => Entry(GitModified).VersionStatus.State == VersionState.Clean && Entry(SvnModified).VersionStatus.State == VersionState.Clean);
            Check(Page.SearchBox.Text == "badge" && Page.View!.Cast<FileEntry>().Count() == 5,
                "Git and SVN repository changes refresh search badges in place without replacing the query or result set");
            Page.SearchBox.Text = "badge-plain"; await Page.WaitForSearchSmokeAsync();
            await WaitUntil(() => Page.VersionCancellation is null && !Page.VersionDebounce.IsEnabled);
            Check(Page.View!.Cast<FileEntry>().Single().FullPath == Plain && Entry(Plain).VersionStatus.State == VersionState.None,
                "Replacing the search removes old repository scopes and never carries a badge onto an ordinary result");
        }
        finally { Probe.RequestExit(); }
    }
}
