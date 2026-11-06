using System.IO;
using System.Runtime.InteropServices;
using CDir.Localization;

namespace CDir.Services;

internal sealed partial class ShellContextMenu
{
    // Let the Shell own preflight enumeration, conflict decisions, progress and undo.
    // Quiet checks never add records to the user's global Explorer undo stack.
    internal static uint TransferOperationFlags(bool SuppressPrompts, bool RenameOnCollision = false, bool RecordUndo = true) =>
        (SuppressPrompts ? 0x10u | 0x200u | 0x400u | 0x00100000u | 0x4u : 0x200u | (RecordUndo ? 0x20000000u : 0u))
        | (RenameOnCollision ? 0x8u : 0u);

    internal static bool IsShellCancellation(int Result) => Result is unchecked((int)0x80004004)
        or unchecked((int)0x800704C7) or unchecked((int)0x80270000);

    internal static void CompleteShellOperation(int Result, bool Aborted, CancellationToken Cancellation = default)
    {
        if (Aborted || Cancellation.IsCancellationRequested || IsShellCancellation(Result))
            throw new OperationCanceledException(Cancellation);
        Marshal.ThrowExceptionForHR(Result);
    }

    internal static void Transfer(IntPtr Owner, string[] Paths, string Destination, bool Move, FileOperationHistory? History,
        bool SuppressPrompts = false, bool ReplaceConflicts = false, CancellationToken Cancellation = default, Action<string, uint, uint>? Progress = null,
        byte[][]? ShellItemIds = null)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Shell operations require an STA thread.");
        Cancellation.ThrowIfCancellationRequested();
        if (ShellItemIds is not null)
        {
            TransferShellSelection(Owner, ShellItemIds, Destination, Move, History, SuppressPrompts, ReplaceConflicts, Cancellation, Progress);
            return;
        }
        var TargetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Destination));
        if (!Directory.Exists(TargetRoot)) throw new DirectoryNotFoundException(LanguageService.Text("ServiceTargetMissing"));
        var All = Paths.Select(Value => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Value))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var Sources = All.Where(Source => !All.Any(Other => !string.Equals(Source, Other, StringComparison.OrdinalIgnoreCase)
            && Source.StartsWith(Other + "\\", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (Sources.Length == 0) return;
        foreach (var Source in Sources)
        {
            if (Directory.GetParent(Source) is null) throw new IOException(LanguageService.Text("ServiceCopyRootDenied"));
            if (string.Equals(Source, TargetRoot, StringComparison.OrdinalIgnoreCase) || TargetRoot.StartsWith(Source + "\\", StringComparison.OrdinalIgnoreCase))
                throw new IOException(LanguageService.Text("ServiceRecursiveCopyDenied"));
            if (Move && string.Equals(Path.GetDirectoryName(Source), TargetRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException(LanguageService.Text("ServiceSameLocation"));
            // This fixture-only guard avoids unattended replacement prompts.
            // Production always delegates collisions and merges to Windows.
            var Target = Path.Combine(TargetRoot, Path.GetFileName(Source));
            if (SuppressPrompts && !ReplaceConflicts && !string.Equals(Source, Target, StringComparison.OrdinalIgnoreCase)
                && (File.Exists(Target) || Directory.Exists(Target)))
                throw new IOException(LanguageService.Text("ServiceTargetConflict", Path.GetFileName(Target)));
        }
        void Audit(string Status, string? Error = null)
        {
            try { History?.Audit(Move ? "Move" : "Copy", Sources, Status, Error); }
            catch (Exception Failure) when (Failure is IOException or UnauthorizedAccessException) { }
        }
        IFileOperation? Operation = null;
        IShellItem? TargetFolder = null;
        var Items = new List<IShellItem>();
        var Sink = new ShellTransferProgressSink(Cancellation, (_, _, _) => { }, (Total, Completed) => Progress?.Invoke("TransferringFiles", Total, Completed));
        uint Cookie = 0;
        var Advised = false;
        Audit("Started");
        try
        {
            Cancellation.ThrowIfCancellationRequested();
            var Id = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(TargetRoot, IntPtr.Zero, ref Id, out TargetFolder));
            Operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            Marshal.ThrowExceptionForHR(Operation.SetOwnerWindow(Owner));
            var SameFolderCopy = !Move && Sources.Any(Source => string.Equals(Path.GetDirectoryName(Source), TargetRoot, StringComparison.OrdinalIgnoreCase));
            Marshal.ThrowExceptionForHR(Operation.SetOperationFlags(TransferOperationFlags(SuppressPrompts, SameFolderCopy, !AppMain.SmokeTest)));
            Marshal.ThrowExceptionForHR(Operation.Advise(Sink, out Cookie));
            Advised = true;
            foreach (var Source in Sources)
            {
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Source, IntPtr.Zero, ref Id, out var Item));
                Items.Add(Item);
                Marshal.ThrowExceptionForHR(Move ? Operation.MoveItem(Item, TargetFolder, null, null) : Operation.CopyItem(Item, TargetFolder, null, null));
            }
            // No recursive walks, content hashes or backup copies precede this call.
            var Result = Operation.PerformOperations();
            Marshal.ThrowExceptionForHR(Operation.GetAnyOperationsAborted(out var Aborted));
            CompleteShellOperation(Result, Aborted || IsShellCancellation(Sink.FinishResult), Cancellation);
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
            foreach (var Item in Items) Marshal.ReleaseComObject(Item);
            if (TargetFolder is not null) Marshal.ReleaseComObject(TargetFolder);
        }
    }
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ShellTransferProgressSink(CancellationToken Cancellation, Action<IntPtr, IntPtr, int> Receive, Action<uint, uint>? Progress = null) : IShellFileOperationProgressSink
{
    internal int FinishResult { get; private set; }
    private int Continue() => Cancellation.IsCancellationRequested ? unchecked((int)0x80004004) : 0;
    private int Completed(IntPtr Source, IntPtr Target, int Result)
    {
        try
        {
            Receive(Source, Target, Result);
            return 0;
        }
        catch (Exception) { return unchecked((int)0x80004005); }
    }
    public int StartOperations() => this.Continue();
    public int FinishOperations(int Result) { this.FinishResult = Result; return 0; }
    public int PreRenameItem(uint Flags, IntPtr Item, string Name) => this.Continue();
    public int PostRenameItem(uint Flags, IntPtr Item, string Name, int Result, IntPtr Created) => 0;
    public int PreMoveItem(uint Flags, IntPtr Item, IntPtr Destination, string? Name) => this.Continue();
    public int PostMoveItem(uint Flags, IntPtr Item, IntPtr Destination, string? Name, int Result, IntPtr Created) => this.Completed(Item, Created, Result);
    public int PreCopyItem(uint Flags, IntPtr Item, IntPtr Destination, string? Name) => this.Continue();
    public int PostCopyItem(uint Flags, IntPtr Item, IntPtr Destination, string? Name, int Result, IntPtr Created) => this.Completed(Item, Created, Result);
    public int PreDeleteItem(uint Flags, IntPtr Item) => this.Continue();
    public int PostDeleteItem(uint Flags, IntPtr Item, int Result, IntPtr Created) => this.Completed(Item, Created, Result);
    public int PreNewItem(uint Flags, IntPtr Destination, string Name) => this.Continue();
    public int PostNewItem(uint Flags, IntPtr Destination, string Name, string? Template, uint Attributes, int Result, IntPtr Created) => 0;
    public int UpdateProgress(uint Total, uint Completed) { Progress?.Invoke(Total, Completed); return this.Continue(); }
    public int ResetTimer() => 0;
    public int PauseTimer() => 0;
    public int ResumeTimer() => 0;
}

[ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int Result);
    [PreserveSig] int PreRenameItem(uint Flags, IntPtr Item, [MarshalAs(UnmanagedType.LPWStr)] string Name);
    [PreserveSig] int PostRenameItem(uint Flags, IntPtr Item, [MarshalAs(UnmanagedType.LPWStr)] string Name, int Result, IntPtr Created);
    [PreserveSig] int PreMoveItem(uint Flags, IntPtr Item, IntPtr Destination, [MarshalAs(UnmanagedType.LPWStr)] string? Name);
    [PreserveSig] int PostMoveItem(uint Flags, IntPtr Item, IntPtr Destination, [MarshalAs(UnmanagedType.LPWStr)] string? Name, int Result, IntPtr Created);
    [PreserveSig] int PreCopyItem(uint Flags, IntPtr Item, IntPtr Destination, [MarshalAs(UnmanagedType.LPWStr)] string? Name);
    [PreserveSig] int PostCopyItem(uint Flags, IntPtr Item, IntPtr Destination, [MarshalAs(UnmanagedType.LPWStr)] string? Name, int Result, IntPtr Created);
    [PreserveSig] int PreDeleteItem(uint Flags, IntPtr Item);
    [PreserveSig] int PostDeleteItem(uint Flags, IntPtr Item, int Result, IntPtr Created);
    [PreserveSig] int PreNewItem(uint Flags, IntPtr Destination, [MarshalAs(UnmanagedType.LPWStr)] string Name);
    [PreserveSig] int PostNewItem(uint Flags, IntPtr Destination, [MarshalAs(UnmanagedType.LPWStr)] string Name,
        [MarshalAs(UnmanagedType.LPWStr)] string? Template, uint Attributes, int Result, IntPtr Created);
    [PreserveSig] int UpdateProgress(uint Total, uint Completed);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}
