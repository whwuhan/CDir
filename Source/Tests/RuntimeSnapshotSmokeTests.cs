using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CDir.Runtime;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunRuntimeSnapshotSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Runtime snapshot check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "R")).FullName;
        var Source = Directory.CreateDirectory(Path.Combine(Root, "Publish", "CDirWinX64")).FullName;
        var Repo = Path.GetFullPath(Path.Combine(Source, "..", ".."));
        var Control = Directory.CreateDirectory(Path.Combine(Root, "P")).FullName;
        var Owner = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(Control, ".CDirRuntimeProbeOwner"), Owner);
        var Manifest = JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, RuntimeSnapshot.ManifestFile)))!;
        foreach (var Entry in Manifest.Files)
        {
            var Destination = Path.Combine(Source, Entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, Entry.Path), Destination);
        }
        File.Copy(Path.Combine(AppContext.BaseDirectory, RuntimeSnapshot.ManifestFile), Path.Combine(Source, RuntimeSnapshot.ManifestFile));
        async Task Git(params string[] Arguments)
        {
            var Info = new ProcessStartInfo("git") { WorkingDirectory = Repo, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var Argument in new[] { "-c", "core.autocrlf=false", "-c", "core.hooksPath=" + Path.Combine(Root, "No hooks"),
                "-c", "user.name=CDir Tests", "-c", "user.email=cdir-tests@example.invalid" }.Concat(Arguments)) Info.ArgumentList.Add(Argument);
            using var Child = Process.Start(Info)!;
            var Output = Child.StandardOutput.ReadToEndAsync();
            var Error = Child.StandardError.ReadToEndAsync();
            await Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await Output;
            if (Child.ExitCode != 0) throw new IOException("Owned Git fixture failed: " + await Error);
            await Error;
        }
        Process? Probe = null;
        string? ProbeExecutable = null;
        try
        {
            await Git("init", "--initial-branch=before");
            var Tracked = new[] { "CDir.dll", "CDir.exe", "CDir.App.exe" };
            await Git(new[] { "add", "--" }.Concat(Tracked.Select(Name => "Publish/CDirWinX64/" + Name)).ToArray());
            await Git("commit", "-m", "Owned original release");
            await Git("checkout", "-b", "after");
            var Changed = new Dictionary<string, string>();
            foreach (var Name in Tracked)
            {
                var Path = System.IO.Path.Combine(Source, Name);
                using (var Stream = File.Open(Path, FileMode.Append)) Stream.WriteByte(0);
                Changed[Name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path)));
            }
            await Git("commit", "-am", "Owned other release");
            await Git("checkout", "before");
            var Info = new ProcessStartInfo(Path.Combine(Source, "CDir.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Repo };
            foreach (var Argument in new[] { "--runtime-probe", Control, Owner }) Info.ArgumentList.Add(Argument);
            using var Launcher = Process.Start(Info)!;
            await Launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check(Launcher.ExitCode == 0, "The repository launcher exits after starting its verified runtime snapshot");
            var Deadline = DateTime.UtcNow.AddSeconds(15);
            var ReadyPath = Path.Combine(Control, "Ready.json");
            while (!File.Exists(ReadyPath) && DateTime.UtcNow < Deadline) await Task.Delay(30);
            if (!File.Exists(ReadyPath)) throw new IOException("The isolated runtime probe did not become ready.");
            using var Ready = JsonDocument.Parse(File.ReadAllText(ReadyPath));
            var Data = Ready.RootElement;
            ProbeExecutable = Data.GetProperty("Executable").GetString()!;
            var Cache = Path.GetFullPath(Path.Combine(Control, "C")) + "\\";
            Check(ProbeExecutable.StartsWith(Cache, StringComparison.OrdinalIgnoreCase)
                && Data.GetProperty("Assembly").GetString()!.StartsWith(Cache, StringComparison.OrdinalIgnoreCase)
                && Data.GetProperty("WorkingDirectory").GetString()!.StartsWith(Cache, StringComparison.OrdinalIgnoreCase),
                "The running executable, managed assembly and working directory all leave the checkout");
            Probe = Process.GetProcessById(Data.GetProperty("ProcessId").GetInt32());
            Check(string.Equals(Probe.MainModule?.FileName, ProbeExecutable, StringComparison.OrdinalIgnoreCase)
                && Data.GetProperty("Source").GetString() == Path.Combine(Source, "CDir.exe")
                && !InstanceWorkspaceService.IsUserInstance(Probe.Id) && InstanceWorkspaceService.Peers().Length == 0,
                "Runtime probes preserve installation identity without registering, cloning or changing user instances");
            await Git("checkout", "after");
            Check(Tracked.All(Name => Changed[Name] == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Source, Name)))))
                && !Probe.HasExited, "Git checkout replaces the source EXE, apphost and CDir.dll while the application runtime stays alive");
            await Git("checkout", "before");
            Check(!Probe.HasExited && File.Exists(Path.Combine(Path.GetDirectoryName(ProbeExecutable)!, "Config", "TerminalStartup.ps1")),
                "Switching back keeps the existing runtime and its terminal resources available");
            File.WriteAllText(Path.Combine(Control, "Stop"), "Stop owned probe");
            await Probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var Snapshot = Path.GetDirectoryName(ProbeExecutable)!;
            var Reused = RuntimeSnapshot.Prepare(Source, Path.Combine(Control, "C"));
            Check(Reused == Snapshot && Directory.GetDirectories(Path.GetDirectoryName(Snapshot)!).Length == 1,
                "Repeated launches reuse the same complete verified snapshot");
            using (var Stream = File.Open(Path.Combine(Snapshot, "CDir.dll"), FileMode.Append)) Stream.WriteByte(0);
            var Rejected = false;
            try { RuntimeSnapshot.Prepare(Source, Path.Combine(Control, "C")); }
            catch (IOException) { Rejected = true; }
            Check(Rejected, "A corrupted cached runtime is rejected before any application is launched");
            var Invalid = Directory.CreateDirectory(Path.Combine(Root, "Invalid source")).FullName;
            File.WriteAllText(Path.Combine(Invalid, RuntimeSnapshot.ManifestFile), JsonSerializer.Serialize(new RuntimeManifest(1,
                [new("../Escaped.txt", 0, ""), new("CDir.App.exe", 0, ""), new("CDir.dll", 0, "")])));
            Rejected = false;
            try { RuntimeSnapshot.Prepare(Invalid, Path.Combine(Control, "Rejected snapshots")); }
            catch (IOException) { Rejected = true; }
            Check(Rejected && !File.Exists(Path.Combine(Root, "Escaped.txt")),
                "A manifest cannot read or write paths outside its owned installation and snapshot directories");
        }
        finally
        {
            File.WriteAllText(Path.Combine(Control, "Stop"), "Stop owned probe");
            if (Probe is not null)
            {
                try { await Probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException)
                {
                    if (string.Equals(Probe.MainModule?.FileName, ProbeExecutable, StringComparison.OrdinalIgnoreCase)
                        && ProbeExecutable!.StartsWith(Path.Combine(Control, "C") + "\\", StringComparison.OrdinalIgnoreCase))
                    { Probe.Kill(); await Probe.WaitForExitAsync(); }
                    else throw new IOException("The owned runtime probe identity could not be verified.");
                }
                Probe.Dispose();
            }
        }
    }
}
