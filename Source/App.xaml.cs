using CDir.UI;
using CDir.Services;
using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CDir.Localization;

namespace CDir;

public partial class AppMain : Application
{
    internal static CDir.Models.WorkspaceState? StartupWorkspace { get; private set; }
    public static string? StartupPath { get; private set; }
    public static string? ScreenshotPath { get; private set; }
    public static bool SmokeTest { get; private set; }
    public static string SmokeGroup { get; private set; } = "navigation";
    public static string? SmokeRoot { get; private set; }

    protected override void OnStartup(StartupEventArgs E)
    {
        try
        {
            // Apply test-only scheduling before constructing controls or starting background workers.
            if (E.Args.Contains("--smoke-test", StringComparer.Ordinal))
            {
                if (Environment.GetEnvironmentVariable("CDIR_QUIET_RUNNER") != "1")
                    throw new InvalidOperationException("Run UI checks through Source/Scripts/Test.ps1 -UIGroup navigation.");
                EnterSmokeBackgroundMode();
            }
            TaskbarService.InitializeIdentity();
            base.OnStartup(E);
            var GroupSpecified = false;
            int? CloneFrom = null;
            for (var I = 0; I < E.Args.Length; I++)
            {
                if (E.Args[I] == "--screenshot" && I + 1 < E.Args.Length) ScreenshotPath = System.IO.Path.GetFullPath(E.Args[++I]);
                else if (E.Args[I] == "--clone-from" && I + 1 < E.Args.Length) CloneFrom = int.Parse(E.Args[++I]);
                else if (E.Args[I] == "--smoke-test") SmokeTest = true;
                else if (E.Args[I] == "--smoke-group")
                {
                    if (++I >= E.Args.Length) throw new ArgumentException("--smoke-group requires a group name");
                    SmokeGroup = E.Args[I];
                    GroupSpecified = true;
                }
                else if (!E.Args[I].StartsWith("--")) StartupPath = E.Args[I];
            }
            if (GroupSpecified && !SmokeTest) throw new ArgumentException("--smoke-group requires --smoke-test");
            if (SmokeGroup is not ("transfer" or "selection" or "lifecycle" or "tabs" or "navigation" or "sidebar" or "address" or "operations" or "columns" or "fonts" or "search" or "language" or "shortcuts" or "shell-icons" or "version-control" or "terminal" or "all"))
                throw new ArgumentException("Unknown smoke group: " + SmokeGroup);
            if (!SmokeTest && ScreenshotPath is null && StartupPath is null)
            {
                var Peers = InstanceWorkspaceService.Peers();
                foreach (var Peer in CloneFrom is { } Id && Peers.Contains(Id) ? new[] { Id } : Peers)
                {
                    var Live = InstanceWorkspaceService.ReadAsync(Peer).GetAwaiter().GetResult();
                    if (Live is { Version: 1, Tabs.Count: > 0 } && (StartupWorkspace is null || Live.LastActivatedUtc > StartupWorkspace.LastActivatedUtc))
                        StartupWorkspace = Live;
                }
            }
            if (SmokeTest)
            {
                RecordSmokeProgress("Preparing owned test scope");
                PrepareSmokeScope();
                Exit += (_, _) => StartupService.CleanupSmokeRegistration();
            }
        }
        catch (Exception Error) when (Error is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine("Cannot start UI checks: " + Error.Message);
            Shutdown(2);
            return;
        }
        DispatcherUnhandledException += (_, Args) =>
        {
            if (SmokeTest)
            {
                if (ScreenshotPath is not null) System.IO.File.WriteAllText(ScreenshotPath + ".error.txt", Args.Exception.ToString());
                Shutdown(1);
            }
            else
            {
                foreach (var Window in this.Windows.OfType<MainWindow>().ToArray()) Window.PersistWorkspaceNow();
                MessageBox.Show(Args.Exception.Message, LanguageService.Text("AppErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Args.Handled = true;
        };
        RecordSmokeProgress("Constructing main window");
        SessionEnding += (_, _) =>
        {
            foreach (var Window in this.Windows.OfType<MainWindow>().ToArray()) Window.PersistWorkspaceNow();
        };
        WindowAppearance.Initialize();
        ScrollInteraction.Initialize();
        this.MainWindow = new MainWindow();
        if (SmokeTest)
        {
            this.MainWindow.ShowActivated = false;
            this.MainWindow.ShowInTaskbar = false;
        }
        RecordSmokeProgress("Showing main window");
        this.MainWindow.Show();
    }

    internal static void RecordSmokeProgress(string Stage)
    {
        if (!SmokeTest || ScreenshotPath is null) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ScreenshotPath)!);
        File.AppendAllText(ScreenshotPath + ".progress.log", $"{DateTime.UtcNow:O} {Stage}{Environment.NewLine}");
    }

    private static void EnterSmokeBackgroundMode()
    {
        // Keep current-desktop software captures working when its session disconnects.
        AppContext.SetSwitch("Switch.System.Windows.Media.ShouldRenderEvenWhenNoDisplayDevicesAreAvailable", true);
        // QuietRunner already enforces very-low I/O and idle CPU priority. Entering
        // PROCESS_MODE_BACKGROUND_BEGIN again aggressively trims WPF's working set.
        using var Process = System.Diagnostics.Process.GetCurrentProcess();
        Process.PriorityClass = ProcessPriorityClass.Idle;
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }

    private static void PrepareSmokeScope()
    {
        if (StartupPath is not null)
            throw new ArgumentException("UI checks create their own test folders; do not pass a browsing path. Use Source/Scripts/Test.ps1.");
        var Id = Guid.NewGuid().ToString("N");
        SmokeRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CDirSmoke" + Id));
        Directory.CreateDirectory(SmokeRoot);
        File.WriteAllText(System.IO.Path.Combine(SmokeRoot, ".CDirSmokeOwner"), Id);
        Environment.SetEnvironmentVariable("TEMP", SmokeRoot);
        Environment.SetEnvironmentVariable("TMP", SmokeRoot);
        StartupPath = Directory.CreateDirectory(System.IO.Path.Combine(SmokeRoot, "Initial")).FullName;
        Directory.CreateDirectory(System.IO.Path.Combine(StartupPath, "child"));
        File.WriteAllText(System.IO.Path.Combine(StartupPath, "Example.txt"), "Owned navigation fixture");
    }

    internal static bool IsInsideSmokeScope(string Path)
    {
        if (SmokeRoot is null) return false;
        var FullPath = System.IO.Path.GetFullPath(Path);
        return string.Equals(System.IO.Path.TrimEndingDirectorySeparator(FullPath), SmokeRoot, StringComparison.OrdinalIgnoreCase)
            || FullPath.StartsWith(SmokeRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

}
