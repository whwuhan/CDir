using System.Diagnostics;
using System.IO;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunFileLaunchSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialSearch = this.SearchBox.Text;
        var Launcher = this.Files.StartFileProcess;
        var ProcessDirectory = Environment.CurrentDirectory;
        // SmokeRoot already has a random owner ID. Keep Shell-launched paths below
        // MAX_PATH while retaining Unicode, spaces and a command metacharacter.
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Run 中文 & spaces")).FullName;
        var Nested = Directory.CreateDirectory(Path.Combine(Fixture, "Nested scripts")).FullName;
        File.WriteAllText(Path.Combine(Fixture, "panel-token.txt"), "panel-root");
        File.WriteAllText(Path.Combine(Nested, "panel-token.txt"), "script-parent");
        const string Script = "@echo off\r\ntype panel-token.txt > \"%~dpn0.observed.txt\"\r\nexit /b %errorlevel%\r\n";
        var Batch = Path.Combine(Fixture, "Probe launch.bat");
        var Command = Path.Combine(Nested, "Probe search.cmd");
        File.WriteAllText(Batch, Script, System.Text.Encoding.ASCII);
        File.WriteAllText(Command, Script, System.Text.Encoding.ASCII);
        var Children = new List<Process>();
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("File launch UI check failed: " + Name);
            Passed.Add(Name);
        }
        async Task OpenAndVerify(string PathValue)
        {
            this.FileGrid.SelectedItem = this.Entries.Single(Entry => SamePath(Entry.FullPath, PathValue));
            var Before = Children.Count;
            await this.OpenSelectedAsync();
            Check(Children.Count == Before + 1, "Opening an owned batch file starts exactly one process: " + this.StatusText.Text);
            var Child = Children[^1];
            using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await Child.WaitForExitAsync(Timeout.Token); }
            catch (OperationCanceledException) { if (!Child.HasExited) Child.Kill(true); await Child.WaitForExitAsync(); throw; }
            Check(Child.ExitCode == 0 && File.ReadAllText(Path.ChangeExtension(PathValue, ".observed.txt")).Trim() == "panel-root",
                "The actual " + Path.GetExtension(PathValue) + " reads relative files from the displayed panel directory");
        }
        try
        {
            this.Files.StartFileProcess = Info =>
            {
                if (!AppMain.IsInsideSmokeScope(Info.FileName) || !Info.UseShellExecute
                    || !SamePath(Info.WorkingDirectory, this.CurrentPath)
                    || Info.FileName != Batch && Info.FileName != Command)
                    throw new InvalidOperationException("Refusing to launch a file outside this owned batch fixture.");
                // Exercise the real Shell association without activating a console on the user's desktop.
                Info.WindowStyle = ProcessWindowStyle.Hidden;
                Children.Add(Process.Start(Info) ?? throw new InvalidOperationException("No owned batch process was returned."));
            };
            Check(await this.NavigateAsync(Fixture, RecordHistory: false), "Batch launch opens the owned panel directory");
            await OpenAndVerify(Batch);
            this.SearchBox.Text = "Probe search.cmd";
            await this.WaitForSearchSmokeAsync();
            await OpenAndVerify(Command);
            Check(SamePath(this.CurrentPath, Fixture) && Environment.CurrentDirectory == ProcessDirectory,
                "Opening a search-result script preserves the panel path and the CDir process working directory");
            this.Files.StartFileProcess = _ => throw new IOException("Owned launch failure");
            await this.OpenSelectedAsync();
            Check(this.StatusText.Text.Contains("Owned launch failure") && SamePath(this.CurrentPath, Fixture),
                "A failed batch launch reports its error and preserves the panel directory");
        }
        finally
        {
            this.Files.StartFileProcess = Launcher;
            foreach (var Child in Children) { if (!Child.HasExited) { Child.Kill(true); await Child.WaitForExitAsync(); } Child.Dispose(); }
            this.SearchBox.Text = "";
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.SearchBox.Text = InitialSearch;
            if (!AppMain.IsInsideSmokeScope(Fixture) || File.GetAttributes(Fixture).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Refusing cleanup outside the owned batch fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
