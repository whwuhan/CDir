using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CDir.Services;

/// <summary>
/// Retrieves owned Windows Shell icons on bounded background COM STA workers. Returned images are
/// frozen; callers may display them on any dispatcher. Size is in physical pixels, not DIPs.
/// </summary>
public static class ShellIconService
{
    internal const int CacheCapacity = 1024;
    internal const int ThumbnailWorkerCount = 2;
    internal const int PendingCapacity = 128 + 1 + ThumbnailWorkerCount;
    private const int QueueCapacity = 128;
    private const int RequestTimeoutMilliseconds = 2000;
    private const long CacheLifetimeMilliseconds = 300000;
    private const long FailureLifetimeMilliseconds = 10000;
    private static readonly object Sync = new();
    private static readonly AutoResetEvent WorkAvailable = new(false);
    private static readonly SemaphoreSlim ThumbnailWorkAvailable = new(0);
    private static readonly Queue<IconRequest> Queue = new();
    private static readonly Queue<IconRequest> ThumbnailQueue = new();
    private static readonly Dictionary<IconKey, IconRequest> Pending = new();
    private static readonly Dictionary<IconKey, LinkedListNode<CacheEntry>> Cache = new();
    private static readonly LinkedList<CacheEntry> Recent = new();
    // Four stock kinds and 249 allowed pixel sizes bound this separate, never-evicted fallback cache.
    private static readonly Dictionary<StockKey, BitmapSource> StockCache = new();
    private static bool Stopping;
    private static long NativeRequestCount;
    private static long Generation;
    // Only the explicit, isolated smoke-test process may install a scheduling probe.
    internal static Action<string>? BeforeThumbnailReadForTests;
    public static event EventHandler? CacheInvalidated;

    static ShellIconService()
    {
        StartWorker(false, 0);
        for (var Index = 0; Index < ThumbnailWorkerCount; Index++) StartWorker(true, Index);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (Sync) Stopping = true;
            WorkAvailable.Set();
            ThumbnailWorkAvailable.Release(ThumbnailWorkerCount);
        };
    }

    private static void StartWorker(bool Thumbnails, int Index)
    {
        var Worker = new Thread(() => WorkerMain(Thumbnails)) { IsBackground = true, Name = Thumbnails ? "CDir.Thumbnails." + Index : "CDir.ShellIcons" };
        Worker.SetApartmentState(ApartmentState.STA);
        Worker.Start();
    }

    public static Task<ImageSource?> GetIconAsync(string? Path, bool IsDirectory, int Size = 16, bool OpenFolder = false, bool ShowThumbnails = false, long ThumbnailStamp = 0, CancellationToken CancellationToken = default)
    {
        if (CancellationToken.IsCancellationRequested) return Task.FromCanceled<ImageSource?>(CancellationToken);
        var Pixels = NormalizeSize(Size);
        var Normalized = NormalizePath(Path);
        var Kind = GetStockKind(Normalized, IsDirectory, OpenFolder);
        // A null path deliberately requests the system fallback, also through the STA queue.
        var Key = new IconKey(Normalized ?? string.Empty, IsDirectory, Pixels, OpenFolder && IsDirectory, ShowThumbnails && !IsDirectory && Pixels > 32, ShowThumbnails && !IsDirectory && Pixels > 32 ? ThumbnailStamp : 0);
        lock (Sync)
        {
            if (TryGetCached(Key, out var Cached)) return Task.FromResult<ImageSource?>(Cached);
            if (Normalized is null && GetCachedStock(Kind, Pixels, false) is { } Stock)
                return Task.FromResult<ImageSource?>(Stock);
            if (Pending.TryGetValue(Key, out var Existing) && !Existing.Abandoned) return Subscribe(Existing, CancellationToken);
            if (Stopping) return Task.FromResult<ImageSource?>(GetCachedStock(Kind, Pixels));

            var Request = new IconRequest(Key, Kind, Generation);
            Request.Result = WaitForResultAsync(Request);
            Pending[Key] = Request;
            // Cached thumbnails are cheap to probe on the icon lane; only a cache
            // miss is handed to the extraction lanes that can wait on codecs.
            Enqueue(Request, false);
            return Subscribe(Request, CancellationToken);
        }
    }

    private static void Enqueue(IconRequest Request, bool ExtractThumbnail)
    {
        // Called under Sync. Prefer newly-visible rows during virtualized scrolling.
        if (Queue.Count + ThumbnailQueue.Count >= QueueCapacity)
        {
            var OldQueue = ThumbnailQueue.Count > 0 && (Queue.Count == 0 || ThumbnailQueue.Peek().CreatedAt <= Queue.Peek().CreatedAt) ? ThumbnailQueue : Queue;
            var Dropped = OldQueue.Dequeue();
            if (Pending.TryGetValue(Dropped.Key, out var Current) && ReferenceEquals(Current, Dropped)) Pending.Remove(Dropped.Key);
            Dropped.Completion.TrySetResult(GetCachedStock(Dropped.Kind, Dropped.Key.Size));
        }
        if (ExtractThumbnail) { ThumbnailQueue.Enqueue(Request); ThumbnailWorkAvailable.Release(); }
        else { Queue.Enqueue(Request); WorkAvailable.Set(); }
    }

    // Cancellation belongs to a caller, not a shared native request. Recycled rows
    // abandon queued work only after its last subscriber has left.
    private static Task<ImageSource?> Subscribe(IconRequest Request, CancellationToken Cancellation)
    {
        Request.Consumers++;
        return Wait();
        async Task<ImageSource?> Wait()
        {
            try { return await Request.Result.WaitAsync(Cancellation).ConfigureAwait(false); }
            finally
            {
                lock (Sync)
                    if (--Request.Consumers == 0 && !Request.Started) Request.Abandoned = true;
            }
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
            if (GetCachedStock(Kind, Pixels, false) is not null) return Cached;
        }
        _ = GetIconAsync(null, IsDirectory, Pixels);
        lock (Sync) return GetCachedStock(Kind, Pixels);
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
            ThumbnailQueue.Clear();
        }
        // Subscribers may immediately enqueue fresh requests; never invoke them under the cache lock.
        CacheInvalidated?.Invoke(null, EventArgs.Empty);
    }

    internal static ShellIconDiagnostics GetDiagnostics()
    {
        lock (Sync) return new(Cache.Count, Pending.Count, Queue.Count + ThumbnailQueue.Count, Interlocked.Read(ref NativeRequestCount), StockCache.Count);
    }

    private static async Task<ImageSource?> WaitForResultAsync(IconRequest Request)
    {
        try
        {
            // A slow installed video provider must not leave a still-visible item
            // permanently showing a fallback after an arbitrary ten-second cutoff.
            return Request.Key.Thumbnail ? await Request.Completion.Task.ConfigureAwait(false)
                : await Request.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(RequestTimeoutMilliseconds)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Windows offers no safe cancellation for a running SHGetFileInfo call. Bound the caller's
            // wait while leaving at most one native call active; never spawn replacement threads.
            lock (Sync) return GetCachedStock(Request.Kind, Request.Key.Size);
        }
    }

    private static void WorkerMain(bool Thumbnails)
    {
        var ComResult = CoInitializeEx(IntPtr.Zero, 0x2); // COINIT_APARTMENTTHREADED
        try
        {
            if (ComResult < 0) return;
            if (!Thumbnails) foreach (var Pixels in new[] { 16, 32, 256 })
                foreach (var Kind in new[] { StockIconId.Document, StockIconId.Folder, StockIconId.OpenFolder, StockIconId.FixedDrive })
                    LoadStockIcon(Kind, Pixels);

            while (true)
            {
                if (Thumbnails) ThumbnailWorkAvailable.Wait();
                IconRequest? Request;
                lock (Sync)
                {
                    if (Stopping) break;
                    var Requests = Thumbnails ? ThumbnailQueue : Queue;
                    Request = Requests.Count > 0 ? Requests.Dequeue() : null;
                    if (Request is not null) Request.Started = true;
                }
                if (Request is null) { if (!Thumbnails) WorkAvailable.WaitOne(); continue; }
                BitmapSource? Icon = null;
                var Succeeded = false;
                var NeedsExtraction = false;
                try
                {
                    if (!Request.Abandoned && (Request.Key.Thumbnail || Environment.TickCount64 - Request.CreatedAt <= RequestTimeoutMilliseconds))
                    {
                        if (Thumbnails && AppMain.SmokeTest) Volatile.Read(ref BeforeThumbnailReadForTests)?.Invoke(Request.Key.Path);
                        if (Request.Key.Thumbnail && !Thumbnails)
                        {
                            Icon = ReadLargeIcon(Request.Key, CacheOnly: true);
                            NeedsExtraction = Icon is null;
                        }
                        else if (Request.Key.Path.Length > 0) Icon = ReadPathIcon(Request.Key);
                        Succeeded = Icon is not null;
                        if (!NeedsExtraction) Icon ??= LoadStockIcon(Request.Kind, Request.Key.Size);
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
                    if (NeedsExtraction && Request.Consumers == 0) Request.Abandoned = true;
                    if (NeedsExtraction && Request.Consumers > 0 && Request.Generation == Generation && !Stopping)
                    {
                        Request.Started = false;
                        Enqueue(Request, true);
                        continue;
                    }
                    if (Pending.TryGetValue(Request.Key, out var Current) && ReferenceEquals(Current, Request))
                        Pending.Remove(Request.Key);
                    if (!Request.Abandoned && Request.Key.Path.Length > 0 && Request.Generation == Generation)
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
            // If COM initialization or a lane itself fails, finish every waiter
            // with a fallback rather than leave visible previews waiting forever.
            lock (Sync)
            {
                Stopping = true;
                foreach (var Request in Pending.Values)
                    Request.Completion.TrySetResult(GetCachedStock(Request.Kind, Request.Key.Size));
                Pending.Clear(); Queue.Clear(); ThumbnailQueue.Clear();
            }
            WorkAvailable.Set();
            ThumbnailWorkAvailable.Release(ThumbnailWorkerCount);
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
        if (Key.Size > 32 && ReadLargeIcon(Key) is { } Large) return Large;
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
        var Flags = StockIconFlags.Icon | StockIconFlags.SystemImageIndex | (Size <= 16 ? StockIconFlags.SmallIcon : StockIconFlags.LargeIcon);
        try
        {
            Interlocked.Increment(ref NativeRequestCount);
            if (SHGetStockIconInfo(Kind, Flags, ref Info) < 0 || Info.Icon == IntPtr.Zero) return null;
            var Bitmap = (Size > 32 ? ReadSystemIcon(Info.SystemImageIndex, Size) : null) ?? CopyIcon(Info.Icon, Size);
            lock (Sync) StockCache[new StockKey(Kind, Size)] = Bitmap;
            return Bitmap;
        }
        finally
        {
            if (Info.Icon != IntPtr.Zero) DestroyIcon(Info.Icon);
        }
    }

    private static BitmapSource? ReadLargeIcon(IconKey Key, bool CacheOnly = false)
    {
        object? Item = null;
        IntPtr Bitmap = IntPtr.Zero;
        try
        {
            var Id = typeof(IShellItemImageFactory).GUID;
            Interlocked.Increment(ref NativeRequestCount);
            if (SHCreateItemFromParsingName(Key.Path, IntPtr.Zero, ref Id, out Item) < 0 || Item is not IShellItemImageFactory Factory) return null;
            // RESIZETOFIT allows Windows' installed image/video thumbnail providers.
            // Respect Explorer's global “Always show icons, never thumbnails” setting.
            var Thumbnails = Key.Thumbnail;
            if (Thumbnails)
            {
                using var Preferences = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                Thumbnails = Preferences?.GetValue("IconsOnly") is not int IconsOnly || IconsOnly == 0;
            }
            // Let WPF downsample the original high-resolution bitmap rather than
            // asking the Shell to resize it with a lower-quality GDI stretch.
            var Flags = 1u | (Thumbnails ? CacheOnly ? 0x18u : 0u : 4u);
            if (Factory.GetImage(new NativeIconSize { Width = Key.Size, Height = Key.Size }, Flags, out Bitmap) < 0 || Bitmap == IntPtr.Zero) return null;
            var Image = Imaging.CreateBitmapSourceFromHBitmap(Bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            Image.Freeze();
            return Image;
        }
        catch (COMException) { return null; }
        finally
        {
            if (Bitmap != IntPtr.Zero) DeleteObject(Bitmap);
            if (Item is not null && Marshal.IsComObject(Item)) Marshal.FinalReleaseComObject(Item);
        }
    }

    private static BitmapSource? ReadSystemIcon(int Index, int Size)
    {
        IImageList? Images = null;
        IntPtr Icon = IntPtr.Zero;
        try
        {
            var Id = typeof(IImageList).GUID;
            // Jumbo contains the native 256px artwork, not a stretched 32px HICON.
            if (SHGetImageList(Size > 48 ? 4 : 2, ref Id, out Images) < 0 || Images is null
                || Images.GetIcon(Index, 1, out Icon) < 0 || Icon == IntPtr.Zero) return null;
            return CopyIcon(Icon, Size);
        }
        finally
        {
            if (Icon != IntPtr.Zero) DestroyIcon(Icon);
            if (Images is not null) Marshal.FinalReleaseComObject(Images);
        }
    }

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr Image, IntPtr Mask, out int Index);
        [PreserveSig] int ReplaceIcon(int Index, IntPtr Icon, out int NewIndex);
        [PreserveSig] int SetOverlayImage(int Image, int Overlay);
        [PreserveSig] int Replace(int Index, IntPtr Image, IntPtr Mask);
        [PreserveSig] int AddMasked(IntPtr Image, uint Color, out int Index);
        [PreserveSig] int Draw(IntPtr Parameters);
        [PreserveSig] int Remove(int Index);
        [PreserveSig] int GetIcon(int Index, uint Flags, out IntPtr Icon);
    }
    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetImageList(int ImageList, ref Guid InterfaceId, [MarshalAs(UnmanagedType.Interface)] out IImageList? Images);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIconSize { public int Width; public int Height; }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeIconSize Size, uint Flags, out IntPtr Bitmap);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string Path, IntPtr Context, ref Guid InterfaceId, [MarshalAs(UnmanagedType.Interface)] out object? Item);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr Object);

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
        BitmapSource? Larger = null;
        var LargerSize = int.MaxValue;
        foreach (var Entry in StockCache)
            if (Entry.Key.Kind == Kind && Entry.Key.Size >= Size && Entry.Key.Size < LargerSize) { Larger = Entry.Value; LargerSize = Entry.Key.Size; }
        if (Larger is not null) return Larger;
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

    private static int NormalizeSize(int Size) => Math.Clamp(Size, 8, 256);

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

    private sealed record IconKey(string Path, bool IsDirectory, int Size, bool OpenFolder, bool Thumbnail, long ThumbnailStamp);
    private sealed record StockKey(StockIconId Kind, int Size);
    private sealed record CacheEntry(IconKey Key, BitmapSource? Icon, long ExpiresAt);
    private sealed class IconRequest(IconKey Key, StockIconId Kind, long Generation)
    {
        public IconKey Key { get; } = Key;
        public StockIconId Kind { get; } = Kind;
        public long Generation { get; } = Generation;
        public long CreatedAt { get; } = Environment.TickCount64;
        public int Consumers;
        public bool Started;
        public bool Abandoned;
        public TaskCompletionSource<ImageSource?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ImageSource?> Result { get; set; } = Task.FromResult<ImageSource?>(null);
    }

    internal sealed record ShellIconDiagnostics(int CacheCount, int PendingCount, int QueuedCount, long NativeRequestCount, int StockCacheCount);

    [Flags]
    private enum FileInfoFlags : uint { SmallIcon = 0x1, OpenIcon = 0x2, Icon = 0x100, LinkOverlay = 0x8000 }
    [Flags]
    private enum StockIconFlags : uint { LargeIcon = 0, SmallIcon = 0x1, Icon = 0x100, SystemImageIndex = 0x4000 }
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
