using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;

namespace CDir.Services;

internal readonly record struct FolderSizeResult(long Bytes, bool Complete);

/// <summary>Reads logical lengths without opening file contents or following links.</summary>
internal static class FolderSizeService
{
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
    private readonly record struct SizeStep(bool IsDirectory, bool IsLink, long Length, string? FullPath);

    internal static async Task<FolderSizeResult?> MeasureAsync(string Path, CancellationToken Token,
        Func<FolderSizeResult, Task>? Progress = null)
    {
        await ScanGate.WaitAsync(Token).ConfigureAwait(false);
        try
        {
            Token.ThrowIfCancellationRequested();
            try { if ((File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0) return null; }
            catch (Exception Error) when (Unavailable(Error)) { return null; }
            var Pending = new Stack<string>();
            Pending.Push(Path);
            long Total = 0;
            var Complete = true;
            var Work = 0;
            var Reporting = Stopwatch.StartNew();
            var Options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false };
            while (Pending.TryPop(out var Folder))
            {
                Token.ThrowIfCancellationRequested();
                try
                {
                    var Enumerator = new FileSystemEnumerable<SizeStep>(
                        Folder,
                        (ref FileSystemEntry Entry) =>
                        {
                            var IsLink = (Entry.Attributes & FileAttributes.ReparsePoint) != 0;
                            var IsDirectory = Entry.IsDirectory;
                            return new SizeStep(IsDirectory, IsLink, IsDirectory || IsLink ? 0 : Entry.Length,
                                IsDirectory && !IsLink ? Entry.ToFullPath() : null);
                        },
                        Options);
                    foreach (var Step in Enumerator)
                    {
                        Token.ThrowIfCancellationRequested();
                        try
                        {
                            // Links can escape the tree or form cycles; never traverse them.
                            if (Step.IsLink) Complete = false;
                            else if (Step.IsDirectory) Pending.Push(Step.FullPath!);
                            else Total = checked(Total + Step.Length);
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
                    if (ReferenceEquals(Folder, Path)) return null;
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
