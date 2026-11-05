using System.Diagnostics;
using System.IO;

namespace CDir.Services;

internal readonly record struct FolderSizeResult(long Bytes, bool Complete);

/// <summary>Reads logical lengths without opening file contents or following links.</summary>
internal static class FolderSizeService
{
    private static readonly SemaphoreSlim ScanGate = new(1, 1);

    internal static async Task<FolderSizeResult?> MeasureAsync(string Path, CancellationToken Token,
        Func<FolderSizeResult, Task>? Progress = null)
    {
        await ScanGate.WaitAsync(Token).ConfigureAwait(false);
        try
        {
            Token.ThrowIfCancellationRequested();
            var Root = new DirectoryInfo(Path);
            try { if ((Root.Attributes & FileAttributes.ReparsePoint) != 0) return null; }
            catch (Exception Error) when (Unavailable(Error)) { return null; }
            var Pending = new Stack<DirectoryInfo>();
            Pending.Push(Root);
            long Total = 0;
            var Complete = true;
            var Work = 0;
            var Reporting = Stopwatch.StartNew();
            var Options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
            while (Pending.TryPop(out var Folder))
            {
                Token.ThrowIfCancellationRequested();
                try
                {
                    foreach (var Item in Folder.EnumerateFileSystemInfos("*", Options))
                    {
                        Token.ThrowIfCancellationRequested();
                        try
                        {
                            // Links can escape the tree or form cycles; never traverse them.
                            if ((Item.Attributes & FileAttributes.ReparsePoint) != 0) Complete = false;
                            else if (Item is DirectoryInfo Directory) Pending.Push(Directory);
                            else if (Item is FileInfo File) Total = checked(Total + File.Length);
                        }
                        catch (Exception Error) when (Unavailable(Error) || Error is OverflowException) { Complete = false; }
                        if (++Work % 64 == 0)
                        {
                            if (Reporting.ElapsedMilliseconds >= 250 && Progress is not null)
                            {
                                await Progress(new(Total, false)).ConfigureAwait(false);
                                Reporting.Restart();
                            }
                            // Reserve storage time for navigation and foreground applications.
                            await Task.Delay(15, Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception Error) when (Unavailable(Error))
                {
                    if (ReferenceEquals(Folder, Root)) return null;
                    Complete = false;
                }
            }
            Token.ThrowIfCancellationRequested();
            return new(Total, Complete);
        }
        finally { ScanGate.Release(); }
    }

    private static bool Unavailable(Exception Error) => Error is IOException or UnauthorizedAccessException
        or System.Security.SecurityException;
}
