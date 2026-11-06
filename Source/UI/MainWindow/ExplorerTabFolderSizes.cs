using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private CancellationTokenSource? FolderSizeCancellation;
    private Task FolderSizeTask = Task.CompletedTask;
    private Func<string, CancellationToken, Func<FolderSizeResult, Task>?, Task<FolderSizeResult?>> ReadFolderSize = FolderSizeService.MeasureAsync;

    private void CancelFolderSizes()
    {
        this.FolderSizeCancellation?.Cancel();
        this.FolderSizeCancellation = null;
    }

    private void QueueFolderSizes()
    {
        this.CancelFolderSizes();
        if (!this.Settings.CalculateFolderSizes)
        {
            foreach (var Entry in this.Entries) Entry.ResetFolderSize();
            if (this.SortProperty == nameof(FileEntry.Size)) this.View?.Refresh();
            return;
        }
        if (this.WindowClosed || !this.IsTabSelected || this.IsRecycleBin || this.Entries.Count == 0
            || this.Settings.HiddenColumns?.Contains(nameof(FileEntry.Size)) == true) return;
        var Cancellation = new CancellationTokenSource();
        this.FolderSizeCancellation = Cancellation;
        this.FolderSizeTask = this.UpdateFolderSizesAsync(this.Entries, this.NavigationVersion, Cancellation);
    }

    private async Task UpdateFolderSizesAsync(IReadOnlyList<FileEntry> Listed, int Version, CancellationTokenSource Cancellation)
    {
        var Token = Cancellation.Token;
        var Reader = this.ReadFolderSize;
        var Seen = new HashSet<FileEntry>();
        bool Current() => !Token.IsCancellationRequested && !this.WindowClosed && this.IsTabSelected
            && this.Settings.CalculateFolderSizes
            && Version == this.NavigationVersion && ReferenceEquals(Listed, this.Entries);
        try
        {
            await Task.Delay(350, Token);
            while (Current())
            {
                var Batch = this.View?.Cast<FileEntry>().Where(Entry => Entry.IsDirectory && !Entry.FolderSizeComplete && Seen.Add(Entry)).ToArray() ?? [];
                foreach (var Entry in Batch)
                {
                    if (!Current()) return;
                    async Task Apply(FolderSizeResult Result)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (Current()) Entry.UpdateFolderSize(Result.Bytes, Result.Complete);
                        }, DispatcherPriority.Background, Token);
                    }
                    var Result = await Task.Run(() => Reader(Entry.FullPath, Token, Apply), Token);
                    if (Result is { } Size) await Apply(Size);
                    await Task.Delay(15, Token);
                }
                if (!this.Searching) break;
                await Task.Delay(200, Token);
            }
            // Sorting on every progress notification would make rows jump continuously.
            if (Current() && this.SortProperty == nameof(FileEntry.Size))
                await Dispatcher.InvokeAsync(() => { if (Current()) this.View?.Refresh(); }, DispatcherPriority.Background, Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception Error) when (Error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        finally
        {
            if (ReferenceEquals(this.FolderSizeCancellation, Cancellation)) this.FolderSizeCancellation = null;
            Cancellation.Dispose();
        }
    }
}
