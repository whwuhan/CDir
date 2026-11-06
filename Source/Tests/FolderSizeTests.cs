using CDir.Services;
using CDir.Models;
using System.IO;

internal sealed partial class RegressionRunner
{
    private void RunFolderSizeChecks()
    {
        this.Test("Folder sizes include nested and hidden files without reading contents", () =>
        {
            var Folder = this.MakeDirectory("size-sum");
            var Child = this.MakeDirectory("size-sum/child");
            File.WriteAllBytes(Path.Combine(Folder, "one.bin"), new byte[1024]);
            var Hidden = Path.Combine(Child, "hidden.bin");
            File.WriteAllBytes(Hidden, new byte[123]);
            File.SetAttributes(Hidden, FileAttributes.Hidden);
            var Size = FolderSizeService.MeasureAsync(Folder, default).GetAwaiter().GetResult();
            Check(Size is { Bytes: 1147, Complete: true }, "The recursive total must include hidden descendants.");
        });
        this.Test("Empty folders are zero while unavailable folders retain an unknown size", () =>
        {
            var Empty = this.MakeDirectory("size-empty");
            Check(FolderSizeService.MeasureAsync(Empty, default).GetAwaiter().GetResult() is { Bytes: 0, Complete: true }, "An empty folder has a completed zero total.");
            Check(FolderSizeService.MeasureAsync(Path.Combine(Empty, "missing"), default).GetAwaiter().GetResult() is null, "A missing folder is not an empty folder.");
        });
        this.Test("Folder scans skip junction targets and loops and mark the result incomplete", () =>
        {
            var Folder = this.MakeDirectory("size-links");
            var Outside = this.MakeDirectory("size-outside");
            File.WriteAllBytes(Path.Combine(Folder, "own.bin"), new byte[77]);
            File.WriteAllBytes(Path.Combine(Outside, "outside.bin"), new byte[1234]);
            var Link = Path.Combine(Folder, "outside");
            var Loop = Path.Combine(Folder, "loop");
            try
            {
                try { Directory.CreateSymbolicLink(Link, Outside); } catch (Exception Error) { CreateJunction(Link, Outside, Error); }
                try { Directory.CreateSymbolicLink(Loop, Folder); } catch (Exception Error) { CreateJunction(Loop, Folder, Error); }
                Check(FolderSizeService.MeasureAsync(Folder, default).GetAwaiter().GetResult() is { Bytes: 77, Complete: false }, "Links must not duplicate content or escape the tree.");
                Check(FolderSizeService.MeasureAsync(Link, default).GetAwaiter().GetResult() is null, "A linked root must not be traversed.");
            }
            finally
            {
                if (Directory.Exists(Link)) Directory.Delete(Link);
                if (Directory.Exists(Loop)) Directory.Delete(Loop);
            }
        });
        this.Test("Large folder scans report bounded partial totals and cancel between chunks", () =>
        {
            var Folder = this.MakeDirectory("size-progress");
            for (var Index = 0; Index < 2048; Index++) File.WriteAllBytes(Path.Combine(Folder, Index + ".bin"), new byte[7]);
            var Reports = new List<FolderSizeResult>();
            var Result = FolderSizeService.MeasureAsync(Folder, default, Progress => { Reports.Add(Progress); return Task.CompletedTask; }).GetAwaiter().GetResult();
            // A scheduled partial report can coincide with the final chunk: all
            // bytes have been counted, but enumeration has not returned yet.
            Check(Result is { Bytes: 14336, Complete: true } && Reports.Count > 0 && Reports.All(Report => !Report.Complete && Report.Bytes <= 14336)
                && Reports.Any(Report => Report.Bytes < 14336), "Large trees must expose bounded incomplete progress followed by an exact total.");
            using var Cancel = new CancellationTokenSource();
            Throws<OperationCanceledException>(() => FolderSizeService.MeasureAsync(Folder, Cancel.Token, _ => { Cancel.Cancel(); return Task.CompletedTask; }).GetAwaiter().GetResult());
            Check(FolderSizeService.MeasureAsync(this.MakeDirectory("size-after-cancel"), default).GetAwaiter().GetResult() is { Bytes: 0 }, "Cancellation must release the global scan gate.");
        });
        this.Test("Pre-cancelled scans do no work and size updates notify the displayed column", () =>
        {
            using var Cancel = new CancellationTokenSource(); Cancel.Cancel();
            Throws<OperationCanceledException>(() => FolderSizeService.MeasureAsync(this.Root, Cancel.Token).GetAwaiter().GetResult());
            var Entry = new FileEntry { IsDirectory = true };
            var Changes = new List<string?>(); Entry.PropertyChanged += (_, E) => Changes.Add(E.PropertyName);
            Check(Entry.Size is null && Entry.DisplaySize == "—", "Pending size must not pretend to be zero.");
            Entry.UpdateFolderSize(512, false);
            Check(Entry.Size == 512 && Entry.DisplaySize.StartsWith("≥ ") && !Entry.FolderSizeComplete, "Partial totals must be identifiable.");
            Entry.UpdateFolderSize(1024, true);
            Check(Entry.Size == 1024 && !Entry.DisplaySize.StartsWith("≥ ") && Entry.FolderSizeComplete
                && Changes.Count(Name => Name == nameof(FileEntry.Size)) == 2 && Changes.Count(Name => Name == nameof(FileEntry.DisplaySize)) == 2,
                "Completing a total must update both numeric sorting and display bindings.");
        });
    }
}
