using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CDir.Runtime;

namespace CDir;

internal static class Program
{
    [STAThread]
    private static void Main(string[] Args)
    {
        if (Args.FirstOrDefault() == "--runtime-probe")
        {
            var Root = Args.Length == 3 ? Path.GetFullPath(Args[1]) : throw new ArgumentException("Invalid runtime probe arguments.");
            if (Environment.GetEnvironmentVariable("CDIR_QUIET_RUNNER") != "1"
                || !Root.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                || File.ReadAllText(Path.Combine(Root, ".CDirRuntimeProbeOwner")) != Args[2])
                throw new InvalidOperationException("Runtime probes require an owned test scope.");
            var Ready = Path.Combine(Root, "Ready.json");
            File.WriteAllText(Ready + ".tmp", JsonSerializer.Serialize(new
            {
                ProcessId = Environment.ProcessId, Executable = Environment.ProcessPath,
                Assembly = typeof(Program).Assembly.Location, Source = RuntimeLocation.SourceExecutable,
                WorkingDirectory = Environment.CurrentDirectory
            }));
            File.Move(Ready + ".tmp", Ready);
            var Timer = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(Root, "Stop")) && Timer.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(30);
            return;
        }
        var App = new AppMain();
        App.InitializeComponent();
        App.Run();
    }
}
