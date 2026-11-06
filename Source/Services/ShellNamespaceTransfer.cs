using System.IO;
using System.Runtime.InteropServices;
using CDir.Localization;

namespace CDir.Services;

internal sealed partial class ShellContextMenu
{
    // A scoped check may additionally own a recycled item captured directly by
    // PostDeleteItem; it must never discover or enumerate the user's bin.
    internal static Func<byte[], string?, bool>? ValidateSmokeShellSource;
    private static void TransferShellSelection(IntPtr Owner, byte[][] Ids, string Destination, bool Move,
        FileOperationHistory? History, bool Quiet, bool ReplaceConflicts, CancellationToken Cancellation, Action<string, uint, uint>? Progress)
    {
        var Target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Destination));
        if (!Directory.Exists(Target)) throw new DirectoryNotFoundException(LanguageService.Text("ServiceTargetMissing"));
        if (Ids.Length == 0) throw new InvalidDataException("Empty Shell drag selection.");
        IFileOperation? Operation = null;
        IShellItem? TargetFolder = null;
        var Items = new List<(IShellItem Item, string? Path, string Name)>();
        uint Cookie = 0;
        var Advised = false;
        var Sink = new ShellTransferProgressSink(Cancellation, (_, _, _) => { }, (Total, Completed) => Progress?.Invoke("TransferringFiles", Total, Completed));
        void Audit(string Status, string? Error = null)
        {
            try { History?.Audit(Move ? "Move" : "Copy", Items.Select(Item => Item.Path ?? Item.Name).ToArray(), Status, Error); }
            catch (Exception Failure) when (Failure is IOException or UnauthorizedAccessException) { }
        }
        try
        {
            var InterfaceId = typeof(IShellItem).GUID;
            foreach (var Bytes in Ids)
            {
                Cancellation.ThrowIfCancellationRequested();
                if (Bytes.Length < 3 || ShellDropData.IdLength(Bytes) != Bytes.Length) throw new InvalidDataException("Invalid Shell drag identity.");
                var Memory = Marshal.AllocCoTaskMem(Bytes.Length);
                IShellItem Item;
                try
                {
                    Marshal.Copy(Bytes, 0, Memory, Bytes.Length);
                    Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(Memory, ref InterfaceId, out Item));
                }
                finally { Marshal.FreeCoTaskMem(Memory); }
                Items.Add((Item, null, "")); // Own the COM reference even if a later property read fails.
                string? ReadName(uint Kind)
                {
                    if (Item.GetDisplayName(Kind, out var Name) < 0) return null;
                    try { return Marshal.PtrToStringUni(Name); }
                    finally { Marshal.FreeCoTaskMem(Name); }
                }
                var FilePath = ReadName(0x80058000); // SIGDN_FILESYSPATH is optional for namespace items.
                var DisplayName = ReadName(0) ?? "";
                Items[^1] = (Item, FilePath, DisplayName);
                if (AppMain.SmokeTest && (FilePath is null || !AppMain.IsInsideSmokeScope(FilePath))
                    && ValidateSmokeShellSource?.Invoke(Bytes, FilePath) != true)
                    throw new UnauthorizedAccessException("Shell transfer checks require owned filesystem items.");
                if (FilePath is not null)
                {
                    FilePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(FilePath));
                    if (Directory.GetParent(FilePath) is null) throw new IOException(LanguageService.Text("ServiceCopyRootDenied"));
                    if (string.Equals(FilePath, Target, StringComparison.OrdinalIgnoreCase)
                        || Target.StartsWith(FilePath + "\\", StringComparison.OrdinalIgnoreCase))
                        throw new IOException(LanguageService.Text("ServiceRecursiveCopyDenied"));
                    if (Move && string.Equals(Path.GetDirectoryName(FilePath), Target, StringComparison.OrdinalIgnoreCase))
                        throw new IOException(LanguageService.Text("ServiceSameLocation"));
                    if (Quiet && !ReplaceConflicts && !string.Equals(FilePath, Path.Combine(Target, Path.GetFileName(FilePath)), StringComparison.OrdinalIgnoreCase)
                        && (File.Exists(Path.Combine(Target, DisplayName)) || Directory.Exists(Path.Combine(Target, DisplayName))))
                        throw new IOException(LanguageService.Text("ServiceTargetConflict", DisplayName));
                }
            }
            Audit("Started");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Target, IntPtr.Zero, ref InterfaceId, out TargetFolder));
            Operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            Marshal.ThrowExceptionForHR(Operation.SetOwnerWindow(Owner));
            var SameFolderCopy = !Move && Items.Any(Item => string.Equals(Path.GetDirectoryName(Item.Path), Target, StringComparison.OrdinalIgnoreCase));
            Marshal.ThrowExceptionForHR(Operation.SetOperationFlags(TransferOperationFlags(Quiet, SameFolderCopy, !AppMain.SmokeTest)));
            Marshal.ThrowExceptionForHR(Operation.Advise(Sink, out Cookie));
            Advised = true;
            foreach (var Item in Items)
                Marshal.ThrowExceptionForHR(Move ? Operation.MoveItem(Item.Item, TargetFolder, null, null) : Operation.CopyItem(Item.Item, TargetFolder, null, null));
            var Result = Operation.PerformOperations();
            Marshal.ThrowExceptionForHR(Operation.GetAnyOperationsAborted(out var Aborted));
            CompleteShellOperation(Result, Aborted, Cancellation);
            CompleteShellOperation(Sink.FinishResult, false, Cancellation);
            Audit("Completed");
        }
        catch (Exception Error)
        {
            Audit(Error is OperationCanceledException ? "Cancelled" : "Failed", Error.Message);
            throw;
        }
        finally
        {
            if (Operation is not null)
            {
                if (Advised) Operation.Unadvise(Cookie);
                Marshal.ReleaseComObject(Operation);
            }
            foreach (var Item in Items) Marshal.ReleaseComObject(Item.Item);
            if (TargetFolder is not null) Marshal.ReleaseComObject(TargetFolder);
        }
    }
}
