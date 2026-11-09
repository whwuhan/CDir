using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using CDir.Models;

namespace CDir.Services;

// Live snapshots travel over a per-user pipe, independently of the shared settings files.
internal sealed class InstanceWorkspaceService(Func<Task<WorkspaceState?>> Capture) : IDisposable
{
    private readonly CancellationTokenSource Stop = new();
    private Mutex? UserRegistration;
    private static string PipeName(int Id) => "CDir.Workspace." + Id;
    private static string RegistrationName(int Id) => @"Local\CDir.UserWorkspace." + Id;

    internal void Start(bool RegisterUserInstance = false)
    {
        if (RegisterUserInstance)
        {
            if (AppMain.SmokeTest) throw new InvalidOperationException("UI checks cannot register as a user instance.");
            this.UserRegistration = new Mutex(false, RegistrationName(Environment.ProcessId));
        }
        _ = this.ServeAsync();
    }

    internal static bool IsUserInstance(int Id)
    {
        try
        {
            if (!Mutex.TryOpenExisting(RegistrationName(Id), out var Registration)) return false;
            using (Registration) return true;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static int[] Peers()
    {
        // UI checks must never discover, clone or suspend saving in user windows,
        // including when Test.ps1 -Published launches the exact same executable.
        if (AppMain.SmokeTest) return [];
        using var Current = Process.GetCurrentProcess();
        var Peers = new List<(int Id, DateTime Started)>();
        foreach (var Process in Process.GetProcessesByName(Current.ProcessName))
        {
            using (Process)
            {
                try
                {
                    if (Process.Id != Current.Id && Process.SessionId == Current.SessionId && IsUserInstance(Process.Id)
                        && Process.MainModule?.FileName is { } Executable && Runtime.RuntimeLocation.SameInstallation(Executable))
                        Peers.Add((Process.Id, Process.StartTime));
                }
                catch (Exception Error) when (Error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        return Peers.OrderByDescending(Peer => Peer.Started).Select(Peer => Peer.Id).ToArray();
    }

    internal static async Task<WorkspaceState?> ReadAsync(int Id)
    {
        try
        {
            using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var Pipe = new NamedPipeClientStream(".", PipeName(Id), PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await Pipe.ConnectAsync(Timeout.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<WorkspaceState>(Pipe, cancellationToken: Timeout.Token).ConfigureAwait(false);
        }
        catch (Exception Error) when (Error is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private async Task ServeAsync()
    {
        while (!this.Stop.IsCancellationRequested)
        {
            try
            {
                using var Pipe = new NamedPipeServerStream(PipeName(Environment.ProcessId), PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await Pipe.WaitForConnectionAsync(this.Stop.Token);
                using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(this.Stop.Token);
                Timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var State = await Capture().WaitAsync(Timeout.Token);
                await JsonSerializer.SerializeAsync(Pipe, State, cancellationToken: Timeout.Token);
            }
            catch (Exception Error) when (Error is IOException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A failed pipe creation must not spin on the UI thread.
                if (!this.Stop.IsCancellationRequested) await Task.Delay(100);
            }
        }
    }

    public void Dispose()
    {
        this.Stop.Cancel();
        this.UserRegistration?.Dispose();
        this.UserRegistration = null;
    }
}
