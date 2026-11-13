using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CDir.Services;

internal sealed record ShellLaunchRequest(string? Location, bool Probe = false);
internal sealed record ShellLaunchReply(bool Accepted, long LastActivated = 0);

internal static class ShellLaunchClient
{
    internal static string PipeName(int Id) => "CDir.ShellLaunch." + Id;
    internal static string UserRegistrationName(int Id) => @"Local\CDir.UserWorkspace." + Id;

    internal static int[] UserInstances(string Executable)
    {
        using var Current = Process.GetCurrentProcess();
        var Result = new List<int>();
        foreach (var Peer in Process.GetProcessesByName("CDir"))
        using (Peer)
        {
            try
            {
                if (Peer.Id == Current.Id || Peer.SessionId != Current.SessionId
                    || !string.Equals(Peer.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Mutex.TryOpenExisting(UserRegistrationName(Peer.Id), out var Registration)) continue;
                using (Registration) Result.Add(Peer.Id);
            }
            catch (Exception Error) when (Error is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
        }
        return Result.ToArray();
    }

    internal static async Task<ShellLaunchReply?> SendAsync(string PipeName, ShellLaunchRequest Request)
    {
        try
        {
            using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var Pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await Pipe.ConnectAsync(Timeout.Token).ConfigureAwait(false);
            // Framing avoids waiting for an EOF on the bidirectional pipe.
            using var Writer = new StreamWriter(Pipe, leaveOpen: true) { AutoFlush = true };
            using var Reader = new StreamReader(Pipe, leaveOpen: true);
            await Writer.WriteLineAsync(JsonSerializer.Serialize(Request).AsMemory(), Timeout.Token).ConfigureAwait(false);
            var Line = await Reader.ReadLineAsync(Timeout.Token).ConfigureAwait(false);
            return Line is { Length: < 65536 } ? JsonSerializer.Deserialize<ShellLaunchReply>(Line) : null;
        }
        catch (Exception Error) when (Error is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal static async Task<bool> ForwardAsync(int[] Instances, string? Location)
    {
        var Candidates = new List<(int Id, long Active)>();
        foreach (var Id in Instances)
        {
            var Reply = await SendAsync(PipeName(Id), new(null, Probe: true)).ConfigureAwait(false);
            if (Reply is { Accepted: true }) Candidates.Add((Id, Reply.LastActivated));
        }
        foreach (var Peer in Candidates.OrderByDescending(Item => Item.Active))
        {
            AllowSetForegroundWindow((uint)Peer.Id);
            if (await SendAsync(PipeName(Peer.Id), new(Location)).ConfigureAwait(false) is { Accepted: true }) return true;
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint ProcessId);
}
