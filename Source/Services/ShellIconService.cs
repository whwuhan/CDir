using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CDir.Services;

/// <summary>
/// Retrieves owned Windows Shell icons on one background COM STA thread. Returned images are
/// frozen; callers may display them on any dispatcher. Size is in physical pixels, not DIPs.
/// </summary>
public static class ShellIconService
{
    internal const int CacheCapacity = 1024;
    internal const int PendingCapacity = 129;
    private const int QueueCapacity = PendingCapacity - 1;
    private const int RequestTimeoutMilliseconds = 2000;
    private const long CacheLifetimeMilliseconds = 300000;
    private const long FailureLifetimeMilliseconds = 10000;
    private static readonly object Sync = new();
    private static readonly AutoResetEvent WorkAvailable = new(false);
    private static readonly Queue<IconRequest> Queue = new();
    private static readonly Dictionary<IconKey, IconRequest> Pending = new();
    private static readonly Dictionary<IconKey, LinkedListNode<CacheEntry>> Cache = new();
    private static readonly LinkedList<CacheEntry> Recent = new();
    // Four stock kinds and 57 allowed pixel sizes bound this separate, never-evicted fallback cache.
    private static readonly Dictionary<StockKey, BitmapSource> StockCache = new();
    private static bool Stopping;
    private static long NativeRequestCount;
    private static long Generation;
    public static event EventHandler? CacheInvalidated;

    static ShellIconService()
    {
        var Worker = new Thread(WorkerMain) { IsBackground = true, Name = "CDir.ShellIcons" };
        Worker.SetApartmentState(ApartmentState.STA);
        Worker.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (Sync) Stopping = true;
            WorkAvailable.Set();
        };
    }

    public static Task<ImageSource?> GetIconAsync(string? Path, bool IsDirectory, int Size = 16, bool OpenFolder = false)
    {
        var Pixels = NormalizeSize(Size);
        var Normalized = NormalizePath(Path);
        var Kind = GetStockKind(Normalized, IsDirectory, OpenFolder);
        // A null path deliberately requests the system fallback, also through the STA queue.
        var Key = new IconKey(Normalized ?? string.Empty, IsDirectory, Pixels, OpenFolder && IsDirectory);
        lock (Sync)
        {
            if (TryGetCached(Key, out var Cached)) return Task.FromResult<ImageSource?>(Cached);
            if (Normalized is null && GetCachedStock(Kind, Pixels, false) is { } Stock)
                return Task.FromResult<ImageSource?>(Stock);
            if (Pending.TryGetValue(Key, out var Existing)) return Existing.Result;
            if (Stopping) return Task.FromResult<ImageSource?>(GetCachedStock(Kind, Pixels));

            // Prefer newly-visible rows to old requests accumulated during rapid virtualized scrolling.
            if (Queue.Count >= QueueCapacity)
            {
                var Dropped = Queue.Dequeue();
                Pending.Remove(Dropped.Key);
                Dropped.Completion.TrySetResult(GetCachedStock(Dropped.Kind, Dropped.Key.Size));
            }
            var Request = new IconRequest(Key, Kind, Generation);
            Request.Result = WaitForResultAsync(Request);
            Pending.Add(Key, Request);
            Queue.Enqueue(Request);
            WorkAvailable.Set();
            return Request.Result;
        }
    }

    /// <summary>Never calls the Shell or waits. May be null until asynchronous startup preheating finishes.</summary>
    public static ImageSource? GetFallbackIcon(bool IsDirectory, int Size = 16)
    {
        var Pixels = NormalizeSize(Size);
        var Kind = IsDirectory ? StockIconId.Folder : StockIconId.Document;
        lock (Sync)
        {
            var Cached = GetCachedStock(Kind, Pixels);
            if (Cached is not null) return Cached;
        }
        _ = GetIconAsync(null, IsDirectory, Pixels);
        return null;
    }

    /// <summary>Returns a previously-loaded generic system drive icon without performing filesystem I/O.</summary>
    public static ImageSource? GetDriveFallbackIcon(int Size = 16)
    {
        var Pixels = NormalizeSize(Size);
        lock (Sync) return GetCachedStock(StockIconId.FixedDrive, Pixels);
    }

    /// <summary>Refreshes path-specific icons without interrupting an in-progress native Shell call.</summary>
    public static void InvalidateCache()
    {
        lock (Sync)
        {
            Generation++;
            Cache.Clear();
            Recent.Clear();
            foreach (var Request in Pending.Values)
                Request.Completion.TrySetResult(GetCachedStock(Request.Kind, Request.Key.Size));
            Pending.Clear();
            Queue.Clear();
        }
        // Subscribers may immediately enqueue fresh requests; never invoke them under the cache lock.
        CacheInvalidated?.Invoke(null, EventArgs.Empty);
    }

    internal static ShellIconDiagnostics GetDiagnostics()
    {
        lock (Sync) return new(Cache.Count, Pending.Count, Queue.Count, Interlocked.Read(ref NativeRequestCount), StockCache.Count);
    }

    private static async Task<ImageSource?> WaitForResultAsync(IconRequest Request)
    {
        try
        {
            return await Request.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(RequestTimeoutMilliseconds)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Windows offers no safe cancellation for a running SHGetFileInfo call. Bound the caller's
            // wait while leaving at most one native call active; never spawn replacement threads.
            lock (Sync) return GetCachedStock(Request.Kind, Request.Key.Size);
        }
    }

    private static void WorkerMain()
    {
        var ComResult = CoInitializeEx(IntPtr.Zero, 0x2); // COINIT_APARTMENTTHREADED
        try
        {
            if (ComResult < 0) return;
            foreach (var Pixels in new[] { 16, 32 })
                foreach (var Kind in new[] { StockIconId.Document, StockIconId.Folder, StockIconId.OpenFolder, StockIconId.FixedDrive })
                    LoadStockIcon(Kind, Pixels);

            while (true)
            {
                IconRequest? Request;
                lock (Sync)
                {
                    if (Stopping) break;
                    Request = Queue.Count > 0 ? Queue.Dequeue() : null;
                }
                if (Request is null) { WorkAvailable.WaitOne(); continue; }
                BitmapSource? Icon = null;
                var Succeeded = false;
                try
                {
                    if (Environment.TickCount64 - Request.CreatedAt <= RequestTimeoutMilliseconds)
                    {
                        if (Request.Key.Path.Length > 0) Icon = ReadPathIcon(Request.Key);
                        Succeeded = Icon is not null;
                        Icon ??= LoadStockIcon(Request.Kind, Request.Key.Size);
                    }
                    else
                    {
                        lock (Sync) Icon = GetCachedStock(Request.Kind, Request.Key.Size);
                    }
                }
                catch (Exception Error) when (Error is not OutOfMemoryException and not StackOverflowException)
                {
                    Debug.WriteLine($"Shell icon retrieval failed: {Error.GetType().Name}");
                    lock (Sync) Icon = GetCachedStock(Request.Kind, Request.Key.Size);
                }
                lock (Sync)
                {
                    if (Pending.TryGetValue(Request.Key, out var Current) && ReferenceEquals(Current, Request))
                        Pending.Remove(Request.Key);
                    if (Request.Key.Path.Length > 0 && Request.Generation == Generation)
                        AddCached(Request.Key, Icon, Succeeded ? CacheLifetimeMilliseconds : FailureLifetimeMilliseconds);
                }
                Request.Completion.TrySetResult(Icon);
            }
        }
        catch (Exception Error) when (Error is not OutOfMemoryException and not StackOverflowException)
        {
            Debug.WriteLine($"Shell icon worker stopped: {Error.GetType().Name}");
        }
        finally
        {
            lock (Sync)
            {
                Stopping = true;
                foreach (var Request in Pending.Values)
                    Request.Completion.TrySetResult(GetCachedStock(Request.Kind, Request.Key.Size));
                Pending.Clear();
                Queue.Clear();
            }
            if (ComResult >= 0) CoUninitialize();
        }
    }

    private static BitmapSource? ReadPathIcon(IconKey Key)
    {
        if (ShellLocations.IsVirtual(Key.Path))
        {
            var List = ShellLocations.IsComputer(Key.Path) ? ShellLocations.ComputerIdList() : ShellLocations.RecycleBinIdList();
            var RecycleInfo = new ShellFileInfo { DisplayName = string.Empty, TypeName = string.Empty };
            try
            {
                Interlocked.Increment(ref NativeRequestCount);
                var RecycleFlags = FileInfoFlags.Icon | (FileInfoFlags)8; // SHGFI_PIDL reads the actual virtual folder's icon.
                if (Key.Size <= 16) RecycleFlags |= FileInfoFlags.SmallIcon;
                var RecycleResult = SHGetFileInfoIdList(List, 0, ref RecycleInfo, (uint)Marshal.SizeOf<ShellFileInfo>(), RecycleFlags);
                return RecycleResult != IntPtr.Zero && RecycleInfo.Icon != IntPtr.Zero ? CopyIcon(RecycleInfo.Icon, Key.Size) : null;
            }
            finally
            {
                if (RecycleInfo.Icon != IntPtr.Zero) DestroyIcon(RecycleInfo.Icon);
                Marshal.FreeCoTaskMem(List);
            }
        }
        // This metadata check also stays on the STA worker. Missing or inaccessible paths use stock icons.
        try { File.GetAttributes(Key.Path); }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
        var Info = new ShellFileInfo { DisplayName = string.Empty, TypeName = string.Empty };
        // Version-control and sync overlays belong to external Shell extensions and may be stale.
        // We do not verify their status, so request the base icon; add only shortcut arrows below.
        var Flags = FileInfoFlags.Icon;
        if (Key.Size <= 16) Flags |= FileInfoFlags.SmallIcon;
        if (Key.OpenFolder) Flags |= FileInfoFlags.OpenIcon;
        if (string.Equals(System.IO.Path.GetExtension(Key.Path), ".lnk", StringComparison.OrdinalIgnoreCase)) Flags |= FileInfoFlags.LinkOverlay;
        try
        {
            Interlocked.Increment(ref NativeRequestCount);
            var Result = SHGetFileInfo(Key.Path, 0, ref Info, (uint)Marshal.SizeOf<ShellFileInfo>(), Flags);
            return Result != IntPtr.Zero && Info.Icon != IntPtr.Zero ? CopyIcon(Info.Icon, Key.Size) : null;
        }
        finally
        {
            if (Info.Icon != IntPtr.Zero) DestroyIcon(Info.Icon);
        }
    }

    private static BitmapSource? LoadStockIcon(StockIconId Kind, int Size)
    {
        lock (Sync)
            if (GetCachedStock(Kind, Size, false) is { } Cached) return Cached;
        var Info = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>(), Path = string.Empty };
        var Flags = StockIconFlags.Icon | (Size <= 16 ? StockIconFlags.SmallIcon : StockIconFlags.LargeIcon);
        try
        {
            Interlocked.Increment(ref NativeRequestCount);
            if (SHGetStockIconInfo(Kind, Flags, ref Info) < 0 || Info.Icon == IntPtr.Zero) return null;
            var Bitmap = CopyIcon(Info.Icon, Size);
            lock (Sync) StockCache[new StockKey(Kind, Size)] = Bitmap;
            return Bitmap;
        }
        finally
        {
            if (Info.Icon != IntPtr.Zero) DestroyIcon(Info.Icon);
        }
    }

    private static BitmapSource CopyIcon(IntPtr Icon, int Size)
    {
        var Bitmap = Imaging.CreateBitmapSourceFromHIcon(Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(Size, Size));
        Bitmap.Freeze();
        return Bitmap;
    }

    // All cache helpers are called while holding Sync. No Shell call occurs inside the cache lock.
    private static BitmapSource? GetCachedStock(StockIconId Kind, int Size, bool AllowOtherSize = true)
    {
        if (StockCache.TryGetValue(new StockKey(Kind, Size), out var Exact)) return Exact;
        if (!AllowOtherSize) return null;
        if (StockCache.TryGetValue(new StockKey(Kind, Size <= 16 ? 16 : 32), out var Standard)) return Standard;
        return StockCache.TryGetValue(new StockKey(Kind, 16), out var Small) ? Small : null;
    }

    private static bool TryGetCached(IconKey Key, out BitmapSource? Icon)
    {
        Icon = null;
        if (!Cache.TryGetValue(Key, out var Node)) return false;
        if (Node.Value.ExpiresAt < Environment.TickCount64)
        {
            Recent.Remove(Node);
            Cache.Remove(Key);
            return false;
        }
        Recent.Remove(Node);
        Recent.AddFirst(Node);
        Icon = Node.Value.Icon;
        return true;
    }

    private static void AddCached(IconKey Key, BitmapSource? Icon, long Lifetime)
    {
        if (Cache.Remove(Key, out var Existing)) Recent.Remove(Existing);
        var Node = Recent.AddFirst(new CacheEntry(Key, Icon, Environment.TickCount64 + Lifetime));
        Cache[Key] = Node;
        while (Cache.Count > CacheCapacity && Recent.Last is { } Oldest)
        {
            Recent.RemoveLast();
            Cache.Remove(Oldest.Value.Key);
        }
    }

    private static int NormalizeSize(int Size) => Math.Clamp(Size, 8, 64);

    private static string? NormalizePath(string? Path)
    {
        if (ShellLocations.IsRecycleBin(Path)) return ShellLocations.RecycleBin;
        if (ShellLocations.IsComputer(Path)) return ShellLocations.Computer;
        if (string.IsNullOrWhiteSpace(Path) || Path.Length > 32767) return null;
        try { return System.IO.Path.GetFullPath(Path); }
        catch (Exception Error) when (Error is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private static StockIconId GetStockKind(string? Path, bool IsDirectory, bool OpenFolder)
    {
        if (!IsDirectory) return StockIconId.Document;
        if (Path is not null && string.Equals(Path.TrimEnd('\\', '/'), System.IO.Path.GetPathRoot(Path)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return StockIconId.FixedDrive;
        return OpenFolder ? StockIconId.OpenFolder : StockIconId.Folder;
    }

    private sealed record IconKey(string Path, bool IsDirectory, int Size, bool OpenFolder);
    private sealed record StockKey(StockIconId Kind, int Size);
    private sealed record CacheEntry(IconKey Key, BitmapSource? Icon, long ExpiresAt);
    private sealed class IconRequest(IconKey Key, StockIconId Kind, long Generation)
    {
        public IconKey Key { get; } = Key;
        public StockIconId Kind { get; } = Kind;
        public long Generation { get; } = Generation;
        public long CreatedAt { get; } = Environment.TickCount64;
        public TaskCompletionSource<ImageSource?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ImageSource?> Result { get; set; } = Task.FromResult<ImageSource?>(null);
    }

    internal sealed record ShellIconDiagnostics(int CacheCount, int PendingCount, int QueuedCount, long NativeRequestCount, int StockCacheCount);

    [Flags]
    private enum FileInfoFlags : uint { SmallIcon = 0x1, OpenIcon = 0x2, Icon = 0x100, LinkOverlay = 0x8000 }
    [Flags]
    private enum StockIconFlags : uint { LargeIcon = 0, SmallIcon = 0x1, Icon = 0x100 }
    // Documented SHSTOCKICONID identifiers; these are not DLL resource indexes.
    private enum StockIconId : uint { Document = 0, Folder = 3, OpenFolder = 4, FixedDrive = 8 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StockIconInfo
    {
        public uint Size;
        public IntPtr Icon;
        public int SystemImageIndex;
        public int IconIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string Path, uint FileAttributes, ref ShellFileInfo Info, uint Size, FileInfoFlags Flags);
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoIdList(IntPtr Path, uint FileAttributes, ref ShellFileInfo Info, uint Size, FileInfoFlags Flags);
    [DllImport("shell32.dll", EntryPoint = "SHGetStockIconInfo", CharSet = CharSet.Unicode)]
    private static extern int SHGetStockIconInfo(StockIconId Kind, StockIconFlags Flags, ref StockIconInfo Info);
    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr Icon);
    [DllImport("ole32.dll", EntryPoint = "CoInitializeEx")]
    private static extern int CoInitializeEx(IntPtr Reserved, uint Flags);
    [DllImport("ole32.dll", EntryPoint = "CoUninitialize")]
    private static extern void CoUninitialize();
}
