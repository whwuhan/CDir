using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace CDir.Services;

internal sealed class ShellLaunchServer(string Name, Func<ShellLaunchRequest, Task<ShellLaunchReply>> Handle) : IDisposable
{
    private readonly CancellationTokenSource Stop = new();
    internal void Start() => _ = this.ServeAsync();
    private async Task ServeAsync()
    {
        while (!this.Stop.IsCancellationRequested)
        {
            try
            {
                using var Pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await Pipe.WaitForConnectionAsync(this.Stop.Token).ConfigureAwait(false);
                using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(this.Stop.Token);
                Timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var Reader = new StreamReader(Pipe, leaveOpen: true);
                using var Writer = new StreamWriter(Pipe, leaveOpen: true) { AutoFlush = true };
                var Line = await Reader.ReadLineAsync(Timeout.Token).ConfigureAwait(false);
                var Request = Line is { Length: < 65536 } ? JsonSerializer.Deserialize<ShellLaunchRequest>(Line) : null;
                var Reply = Request is null ? new ShellLaunchReply(false) : await Handle(Request).WaitAsync(Timeout.Token).ConfigureAwait(false);
                await Writer.WriteLineAsync(JsonSerializer.Serialize(Reply).AsMemory(), Timeout.Token).ConfigureAwait(false);
            }
            catch (Exception Error) when (Error is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            { if (!this.Stop.IsCancellationRequested) await Task.Delay(100).ConfigureAwait(false); }
        }
    }
    public void Dispose() => this.Stop.Cancel();
}
