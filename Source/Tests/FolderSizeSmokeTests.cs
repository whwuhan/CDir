using System.IO;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunFolderSizeSmokeChecksAsync(List<string> Passed)
    {
        var Initial = this.CurrentPath;
        var Hidden = this.Settings.HiddenColumns?.ToArray() ?? [];
        var Reader = this.ReadFolderSize;
        var Folder = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "FolderSizes-" + Guid.NewGuid().ToString("N"))).FullName;
        var Nested = Directory.CreateDirectory(Path.Combine(Folder, "Nested")).FullName;
        var Empty = Directory.CreateDirectory(Path.Combine(Folder, "Empty")).FullName;
        Directory.CreateDirectory(Path.Combine(Nested, "Child"));
        File.WriteAllBytes(Path.Combine(Nested, "one.bin"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(Nested, "Child", "two.bin"), new byte[321]);
        TaskCompletionSource<FolderSizeResult?>? Release = null;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Folder size UI check failed: " + Name);
            Passed.Add(Name);
        }
        try
        {
            this.Settings.HiddenColumns = Hidden.Where(Key => Key != nameof(FileEntry.Size)).ToList();
            this.ApplyColumnVisibility();
            Check(await this.NavigateAsync(Folder), "The folder size fixture opens successfully");
            var NestedEntry = this.Entries.Single(Entry => SamePath(Entry.FullPath, Nested));
            this.SelectPath(Nested);
            AppMain.RecordSmokeProgress("Folder sizes: waiting for real recursive totals");
            await this.FolderSizeTask.WaitAsync(TimeSpan.FromSeconds(10));
            Check(NestedEntry.Size == 1345 && NestedEntry.FolderSizeComplete
                && this.Entries.Single(Entry => SamePath(Entry.FullPath, Empty)).Size == 0,
                "The Size column progressively receives recursive totals and completed zero-byte folders");
            Check(ReferenceEquals(this.FileGrid.SelectedItem, NestedEntry), "Background size updates preserve the file panel selection");
            this.CaptureAddressBreadcrumbPreview("FolderSizesPreview.png");

            // An uncooperative reader models slow filesystem metadata: navigation must
            // cancel and reject its eventual result without waiting for the device.
            var Started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var OffUi = false;
            this.ReadFolderSize = async (_, Token, Progress) =>
            {
                OffUi = !Dispatcher.CheckAccess(); Started.TrySetResult(Token);
                return await Release.Task;
            };
            await this.RefreshAsync(true);
            var Stale = this.Entries.First(Entry => Entry.IsDirectory);
            var Pending = this.FolderSizeTask;
            AppMain.RecordSmokeProgress($"Folder sizes: waiting for blocked reader (entries={this.Entries.Count}, active={this.IsTabSelected}, hidden={string.Join(',', this.Settings.HiddenColumns ?? [])}, task={Pending.Status})");
            var ScanToken = await Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            Check(OffUi && !Pending.IsCompleted, "Slow recursive filesystem work runs off the UI thread while input continues");
            await this.NavigateAsync(Empty);
            Check(ScanToken.IsCancellationRequested && !Pending.IsCompleted && SamePath(this.CurrentPath, Empty),
                "Navigation cancels a slow size scan and completes without waiting for it");
            Release.SetResult(new(999999, true));
            AppMain.RecordSmokeProgress("Folder sizes: waiting for stale reader cancellation");
            await Pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(Stale.Size is null && this.Entries.Count == 0, "A late size result cannot update an old row or a different directory");

            var Invocations = 0;
            this.ReadFolderSize = (_, _, _) => { Interlocked.Increment(ref Invocations); return Task.FromResult<FolderSizeResult?>(new(1, true)); };
            this.Settings.HiddenColumns = [.. Hidden, nameof(FileEntry.Size)];
            this.ApplyColumnVisibility();
            await this.NavigateAsync(Folder);
            await Task.Delay(500);
            Check(Invocations == 0, "Hiding the Size column avoids recursive filesystem scans");
            this.Settings.HiddenColumns = Hidden.Where(Key => Key != nameof(FileEntry.Size)).ToList();
            this.ApplyColumnVisibility();
            AppMain.RecordSmokeProgress($"Folder sizes: waiting for visible-column restart (path={this.CurrentPath}, entries={this.Entries.Count}, pending={this.Entries.Count(Entry => Entry.IsDirectory && !Entry.FolderSizeComplete)}, active={this.IsTabSelected}, hidden={string.Join(',', this.Settings.HiddenColumns ?? [])}, task={this.FolderSizeTask.Status})");
            await this.FolderSizeTask.WaitAsync(TimeSpan.FromSeconds(5));
            var RestartDeadline = Environment.TickCount64 + 3000;
            while (Invocations == 0 && Environment.TickCount64 < RestartDeadline) await Task.Delay(20);
            Check(Invocations > 0, $"Showing the Size column resumes deferred folder totals (entries={this.Entries.Count}, task={this.FolderSizeTask.Status}, path={this.CurrentPath})");
        }
        finally
        {
            this.CancelFolderSizes();
            Release?.TrySetResult(null);
            this.ReadFolderSize = Reader;
            this.Settings.HiddenColumns = Hidden.ToList();
            this.ApplyColumnVisibility();
            await this.NavigateAsync(Initial, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Folder) || (File.GetAttributes(Folder) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing folder size cleanup outside the owned fixture.");
            Directory.Delete(Folder, true);
        }
    }
}
