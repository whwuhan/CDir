using System.IO;
using System.Text.Json;
using CDir.Models;

namespace CDir.Services;

// Each process owns a file: shutdown never waits for another process or a save decision.
internal sealed class SessionWorkspaceStore(string WorkspacePath)
{
    private readonly string DirectoryPath = WorkspacePath + ".sessions";
    private readonly string Id = Guid.NewGuid().ToString("N");
    private string? LastJson;
    private string SnapshotPath => Path.Combine(this.DirectoryPath, this.Id + ".json");

    internal bool Save(WorkspaceState State)
    {
        try
        {
            var Json = JsonSerializer.Serialize(State);
            if (Json != this.LastJson || File.GetLastWriteTimeUtc(this.SnapshotPath) <= File.GetLastWriteTimeUtc(WorkspacePath))
            {
                DurableJsonStore.Write(this.SnapshotPath, Json);
                this.LastJson = Json;
            }
            return true;
        }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    internal WorkspaceState? ReadLatest()
    {
        try
        {
            DurableJsonStore.CheckTestPath(this.DirectoryPath);
            if (!Directory.Exists(this.DirectoryPath)) return null;
            var Cutoff = File.GetLastWriteTimeUtc(WorkspacePath);
            WorkspaceState? Latest = null;
            DateTime LatestWrite = default;
            foreach (var FilePath in Directory.EnumerateFiles(this.DirectoryPath, "*.json"))
            {
                DurableJsonStore.CheckTestPath(FilePath);
                var Written = File.GetLastWriteTimeUtc(FilePath);
                if (Written <= Cutoff) continue;
                var State = DurableJsonStore.Read<WorkspaceState>(FilePath);
                if (State is not { Version: 1, Tabs.Count: > 0 } || State.Tabs.Any(Tab => Tab is null || Tab.Settings is null)) continue;
                if (Latest is null || State.LastActivatedUtc > Latest.LastActivatedUtc
                    || State.LastActivatedUtc == Latest.LastActivatedUtc && Written > LatestWrite)
                { Latest = State; LatestWrite = Written; }
            }
            return Latest;
        }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    internal void Discard()
    {
        try
        {
            foreach (var FilePath in new[] { this.SnapshotPath, this.SnapshotPath + ".bak" })
            {
                DurableJsonStore.CheckTestPath(FilePath);
                File.Delete(FilePath);
            }
        }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
}
