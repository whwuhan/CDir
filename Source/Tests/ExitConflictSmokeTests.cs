using System.IO;
using System.Text.Json;
using System.Windows;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunExitConflictSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Exit conflict check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "ExitConflicts-" + Guid.NewGuid().ToString("N"))).FullName;
        MainWindow? Window = null, Peer = null;
        string SettingsFile = "", WorkspaceFile = "";
        var Questions = 0;
        async Task Open(string Name)
        {
            SettingsFile = Path.Combine(Root, Name + ".json");
            WorkspaceFile = SettingsFile + ".workspace.json";
            DurableJsonStore.Write(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = Root }));
            Window = new MainWindow(SettingsFile, RestoreWorkspace: true)
                { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Window.Show(); await Window.ActiveTab!.WaitUntilReadyAsync(); await Window.WorkspaceRestored;
            Questions = 0;
            Window.ConfirmWorkspaceSave = () => { Questions++; return MessageBoxResult.Cancel; };
        }
        void ExitWithoutQuestion(string Name)
        {
            Window!.RequestExit();
            Check(!Window.IsVisible && Questions == 0, Name);
            Window = null;
        }
        try
        {
            await Open("NoPeerWrite");
            Window!.MultiInstanceDetected = true;
            var GridLines = Window.ActiveTab!.Settings.ShowGridLines;
            Window.ActiveTab.ChangeGridLines(!GridLines);
            ExitWithoutQuestion("A last instance with only local edits exits without a prompt when no peer saved");
            Check(ReadSettings(SettingsFile).ShowGridLines == !GridLines,
                "Local edits are saved automatically when the shared baseline has not changed");

            await Open("OwnWrite");
            Window!.ActiveTab!.ChangeGridLines(!Window.ActiveTab.Settings.ShowGridLines);
            Window.PersistWorkspaceNow();
            Window.MultiInstanceDetected = true;
            Window.ActiveTab.SetTabAlias("After my own save");
            ExitWithoutQuestion("Successful writes by the current instance advance its baseline and do not cause a later conflict");

            await Open("SamePending");
            Window!.MultiInstanceDetected = true;
            Window.ActiveTab!.ChangeGridLines(!Window.ActiveTab.Settings.ShowGridLines);
            Window.ActiveTab.SetTabAlias("Same peer layout");
            DurableJsonStore.Write(SettingsFile, JsonSerializer.Serialize(Window.ActiveTab.CaptureSaveSettings()));
            var Same = Window.CaptureWorkspace()!;
            Same.LastActivatedUtc = Same.LastActivatedUtc.AddMinutes(5);
            DurableJsonStore.Write(WorkspaceFile, JsonSerializer.Serialize(Same, new JsonSerializerOptions { WriteIndented = true }));
            ExitWithoutQuestion("A peer save matching the pending configuration needs no confirmation despite activation metadata or formatting");

            await Open("MetadataOnly");
            Window!.MultiInstanceDetected = true;
            var MetadataOnly = DurableJsonStore.Read<WorkspaceState>(WorkspaceFile)!;
            MetadataOnly.LastActivatedUtc = MetadataOnly.LastActivatedUtc.AddHours(1);
            DurableJsonStore.Write(WorkspaceFile, JsonSerializer.Serialize(MetadataOnly, new JsonSerializerOptions { WriteIndented = true }));
            using (var Document = JsonDocument.Parse(File.ReadAllText(SettingsFile)))
            using (var Buffer = new MemoryStream())
            {
                using (var Writer = new Utf8JsonWriter(Buffer, new JsonWriterOptions { Indented = true }))
                {
                    Writer.WriteStartObject();
                    foreach (var Property in Document.RootElement.EnumerateObject().Reverse()) Property.WriteTo(Writer);
                    Writer.WriteEndObject();
                }
                DurableJsonStore.Write(SettingsFile, System.Text.Encoding.UTF8.GetString(Buffer.ToArray()));
            }
            Window.ActiveTab!.SetTabAlias("A new local edit");
            ExitWithoutQuestion("Timestamp, whitespace and object property order alone are not peer layout changes");

            await Open("SettingsConflict");
            Window!.MultiInstanceDetected = true;
            var External = Window.ActiveTab!.CaptureSaveSettings();
            External.ShowGridLines = !Window.ActiveTab!.Settings.ShowGridLines;
            DurableJsonStore.Write(SettingsFile, JsonSerializer.Serialize(External));
            var Disk = File.ReadAllText(SettingsFile);
            Window.RequestExit();
            Check(Window.IsVisible && Questions == 1 && File.ReadAllText(SettingsFile) == Disk,
                "A different peer settings save asks once and cancel leaves the instance and saved file intact");
            Window.ActiveTab.ChangeGridLines(External.ShowGridLines);
            Window.RequestExit();
            Check(!Window.IsVisible && Questions == 1,
                "After cancel, matching the peer configuration removes the conflict without another prompt");
            Window = null;

            await Open("WorkspaceConflict");
            Window!.ActiveTab!.SetTabAlias("My saved layout");
            Window.PersistWorkspaceNow();
            Window.MultiInstanceDetected = true;
            var Other = DurableJsonStore.Read<WorkspaceState>(WorkspaceFile)!;
            Other.Tabs[Other.ActiveTab].Alias = "Different peer layout";
            DurableJsonStore.Write(WorkspaceFile, JsonSerializer.Serialize(Other));
            Disk = File.ReadAllText(WorkspaceFile);
            Window.RequestExit();
            Check(Window.IsVisible && Questions == 1 && File.ReadAllText(WorkspaceFile) == Disk,
                "A workspace-only peer change is detected even when the current layout has not changed since its own save");
            Window.ConfirmWorkspaceSave = () => { Questions++; return MessageBoxResult.Yes; };
            Window.RequestExit(); Window = null;
            Check(Questions == 2 && DurableJsonStore.Read<WorkspaceState>(WorkspaceFile)!.Tabs[0].Alias == "My saved layout",
                "Approving a real conflict overwrites the peer workspace even when the local serialization cache is unchanged");

            await Open("PriorCloser");
            Window!.MultiInstanceDetected = true;
            Peer = new MainWindow(SettingsFile, RestoreWorkspace: true, InitialWorkspace: Window.CaptureWorkspace())
                { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Peer.MultiInstanceDetected = true;
            Peer.Show(); await Peer.ActiveTab!.WaitUntilReadyAsync(); await Peer.WorkspaceRestored;
            Peer.ActiveTab!.SetTabAlias("Saved by the earlier closer");
            var PeerQuestions = 0;
            Peer.ConfirmWorkspaceSave = () => { PeerQuestions++; return MessageBoxResult.Cancel; };
            Peer.RequestExit();
            Check(!Peer.IsVisible && PeerQuestions == 0
                && DurableJsonStore.Read<WorkspaceState>(WorkspaceFile)!.Tabs[0].Alias == "Saved by the earlier closer",
                "The first overlapping window closes and saves without asking when no preceding peer has saved");
            Peer = null;
            Window.RequestExit();
            Check(Window.IsVisible && Questions == 1,
                "The remaining window asks only after the earlier closer saved a layout different from its own");
            Window.ConfirmWorkspaceSave = () => MessageBoxResult.No;
            Window.RequestExit(); Window = null;
        }
        finally
        {
            foreach (var Remaining in new[] { Peer, Window }.OfType<MainWindow>())
            { Remaining.ConfirmWorkspaceSave = () => MessageBoxResult.No; Remaining.RequestExit(); }
        }
    }
}
