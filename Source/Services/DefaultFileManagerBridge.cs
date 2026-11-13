using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace CDir.Services;

internal sealed record IntegrationReply(bool Success, bool Enabled, string? Error = null);

internal static class DefaultFileManagerBridge
{
    internal static bool IsEnabled() => AppMain.SmokeTest ? DefaultFileManagerService.IsEnabled() : Request("status").Enabled;
    internal static void SetEnabled(bool Enabled)
    {
        if (AppMain.SmokeTest) { DefaultFileManagerService.SetEnabled(Enabled); return; }
        Request(Enabled ? "enable" : "disable");
        if (!Enabled && DefaultFileManagerService.OwnsCompleteRegistration()) DefaultFileManagerService.SetEnabled(false);
    }
    internal static void RefreshOwnedRegistration()
    {
        if (AppMain.SmokeTest || !DefaultFileManagerService.OwnsCompleteRegistration()) return;
        var State = Request("refresh");
        if (State.Enabled) return;
        // An earlier host-private registration represents the user's opt-in but
        // is invisible to Explorer. Restore that private overlay before enabling
        // the corresponding real per-user registration.
        DefaultFileManagerService.SetEnabled(false);
        Request("enable");
    }
    internal static IntegrationReply Request(string Action, string? OwnedRoot = null, Action? VerifyOwnedProbe = null)
    {
        try { return RequestAsync(Action, OwnedRoot, VerifyOwnedProbe).GetAwaiter().GetResult(); }
        catch (Exception Error) when (Error is OperationCanceledException or System.ComponentModel.Win32Exception)
        { throw new IOException("The Windows Shell integration helper could not complete the request.", Error); }
    }
    private static async Task<IntegrationReply> RequestAsync(string Action, string? OwnedRoot, Action? VerifyOwnedProbe)
    {
        if (AppMain.SmokeTest && (Action != "owned-probe" || OwnedRoot != AppMain.SmokeRoot))
            throw new InvalidOperationException("UI checks cannot access real Shell registrations.");
        using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var Name = "CDir.RegistryIntegration." + Guid.NewGuid().ToString("N");
        using var Pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var Lifetime=DesktopIntegrationProcess.Start(DefaultFileManagerService.Launcher, "--integration-bootstrap", Action, Name, OwnedRoot ?? "");
        await Pipe.WaitForConnectionAsync(Timeout.Token).ConfigureAwait(false);
        using var Reader = new StreamReader(Pipe, leaveOpen:true);
        using var Writer = new StreamWriter(Pipe, leaveOpen:true) { AutoFlush = true };
        var Line = await Reader.ReadLineAsync(Timeout.Token).ConfigureAwait(false);
        var Reply = Line is { Length: < 65536 } ? JsonSerializer.Deserialize<IntegrationReply>(Line) : null;
        try
        {
            if (Reply is not { Success: true }) throw new IOException(Reply?.Error ?? "The Windows Shell integration helper did not respond.");
            VerifyOwnedProbe?.Invoke();
            return Reply;
        }
        finally { await Writer.WriteLineAsync("done".AsMemory(), Timeout.Token).ConfigureAwait(false); Lifetime.Wait(); }
    }
}
