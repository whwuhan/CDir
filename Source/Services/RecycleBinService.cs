using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CDir.Models;

namespace CDir.Services;

// Read the Shell namespace rather than reverse engineering $I/$R files. Retain
// absolute PIDLs so Restore/Delete continue targeting the original Shell items.
internal static class RecycleBinService
{
    private static readonly ConcurrentDictionary<string, PropertyKey> Keys = new();

    internal static IReadOnlyList<FileEntry> ReadEntries(string Location, CancellationToken Cancellation = default,
        Action<IReadOnlyList<FileEntry>>? BatchReady = null)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Recycle Bin reads require an STA thread.");
        var Root = GetIdList(Location);
        IShellFolder? Folder = null;
        IEnumIDList? Enumerator = null;
        try
        {
            var FolderId = typeof(IShellFolder).GUID;
            Marshal.ThrowExceptionForHR(SHBindToObject(IntPtr.Zero, Root, IntPtr.Zero, ref FolderId, out Folder));
            Marshal.ThrowExceptionForHR(Folder.EnumObjects(IntPtr.Zero, 0x20 | 0x40 | 0x80, out Enumerator));
            var Result = new List<FileEntry>();
            var Batch = new List<FileEntry>();
            var BatchTimer = Stopwatch.StartNew();
            if (Enumerator is null) return Result;
            while (true)
            {
                Cancellation.ThrowIfCancellationRequested();
                var Status = Enumerator.Next(1, out var Child, out var Fetched);
                Marshal.ThrowExceptionForHR(Status);
                if (Fetched == 0) break;
                try
                {
                    var Absolute = ILCombine(Root, Child);
                    if (Absolute == IntPtr.Zero) throw new OutOfMemoryException();
                    try
                    {
                        var Entry = ReadEntry(Absolute);
                        Result.Add(Entry);
                        if (BatchReady is not null) Batch.Add(Entry);
                    }
                    catch (Exception Error) when (Error is COMException or FileNotFoundException or DirectoryNotFoundException) { /* An item may be restored while enumerating. */ }
                    finally { Marshal.FreeCoTaskMem(Absolute); }
                }
                finally { Marshal.FreeCoTaskMem(Child); }
                // Show the first row immediately, then amortize UI work over batches.
                // The synchronous callback provides backpressure instead of queuing
                // one dispatcher operation for every item in a large Recycle Bin.
                if (BatchReady is not null && Batch.Count > 0
                    && (Result.Count == 1 || Batch.Count >= 256 || BatchTimer.ElapsedMilliseconds >= 75))
                {
                    Cancellation.ThrowIfCancellationRequested();
                    BatchReady(Batch.ToArray());
                    Batch.Clear();
                    BatchTimer.Restart();
                }
            }
            Cancellation.ThrowIfCancellationRequested();
            if (Batch.Count > 0) BatchReady!(Batch.ToArray());
            // The collection view owns sorting; do not sort a second full copy here.
            return Result;
        }
        finally
        {
            if (Enumerator is not null) Marshal.ReleaseComObject(Enumerator);
            if (Folder is not null) Marshal.ReleaseComObject(Folder);
            Marshal.FreeCoTaskMem(Root);
        }
    }

    private static FileEntry ReadEntry(IntPtr IdList)
    {
        var PropertyId = typeof(IPropertyStore).GUID;
        // GPS_DELAYCREATION: the displayed filesystem and Recycle Bin properties
        // are innate. Avoid eagerly loading content/third-party property handlers
        // for every deleted file, while retaining complete property values.
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromIDList(IdList, 0x20, ref PropertyId, out var Store));
        try
        {
            object? Property(string Name)
            {
                var Key = Keys.GetOrAdd(Name, Name => { Marshal.ThrowExceptionForHR(PSGetPropertyKeyFromName(Name, out var Key)); return Key; });
                if (Store.GetValue(ref Key, out var Value) < 0) return null;
                try
                {
                    return Value.Type switch
                    {
                        31 or 8 => Marshal.PtrToStringUni(Value.Pointer),
                        19 => (long)(uint)Value.Number,
                        21 or 20 => Value.Number,
                        64 => DateTime.FromFileTimeUtc(Value.Number).ToLocalTime(),
                        7 => DateTime.FromOADate(Value.Real),
                        _ => null
                    };
                }
                finally { PropVariantClear(ref Value); }
            }
            string DisplayName(uint Kind)
            {
                Marshal.ThrowExceptionForHR(SHGetNameFromIDList(IdList, Kind, out var Name));
                try { return Marshal.PtrToStringUni(Name) ?? ""; }
                finally { Marshal.FreeCoTaskMem(Name); }
            }
            var Name = Property("System.ItemNameDisplay") as string;
            if (string.IsNullOrWhiteSpace(Name)) Name = DisplayName(0);
            string PathValue;
            try { PathValue = DisplayName(0x80058000); }
            catch (COMException) { PathValue = DisplayName(0x80028000); }
            var Attributes = Property("System.FileAttributes") is long Flags ? (FileAttributes)Flags : 0;
            var IsDirectory = (Attributes & FileAttributes.Directory) != 0;
            var Extension = IsDirectory ? "" : Path.GetExtension(Name).ToLowerInvariant();
            var Bytes = new byte[checked((int)ILGetSize(IdList))];
            Marshal.Copy(IdList, Bytes, 0, Bytes.Length);
            return new FileEntry
            {
                Name = Name, FullPath = PathValue, IsDirectory = IsDirectory, Extension = Extension,
                TypeKey = FileSystemService.GetTypeKey(IsDirectory, Extension), TypeArguments = [Extension.TrimStart('.').ToUpperInvariant()],
                Size = IsDirectory ? null : Property("System.Size") as long?,
                Modified = Property("System.DateModified") as DateTime? ?? DateTime.MinValue,
                IsHidden = (Attributes & FileAttributes.Hidden) != 0,
                OriginalLocation = Property("System.Recycle.DeletedFrom") as string,
                DateDeleted = Property("System.Recycle.DateDeleted") as DateTime?, ShellId = Bytes
            };
        }
        finally { Marshal.ReleaseComObject(Store); }
    }

    internal static IntPtr GetIdList(string Location)
    {
        if (ShellLocations.IsRecycleBin(Location)) return ShellLocations.RecycleBinIdList();
        Marshal.ThrowExceptionForHR(SHParseDisplayName(Location, IntPtr.Zero, out var IdList, 0, out _));
        return IdList;
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(8)] public long Number;
        [FieldOffset(8)] public double Real;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint Count);
        void GetAt(uint Index, out PropertyKey Key);
        [PreserveSig] int GetValue(ref PropertyKey Key, out PropertyValue Value);
    }
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName();
        [PreserveSig] int EnumObjects(IntPtr Owner, uint Flags, out IEnumIDList? Items);
    }
    [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumIDList { [PreserveSig] int Next(uint Count, out IntPtr Child, out uint Fetched); }
    [DllImport("shell32.dll")] private static extern int SHBindToObject(IntPtr Folder, IntPtr IdList, IntPtr Context, ref Guid Interface, out IShellFolder Result);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreFromIDList(IntPtr IdList, uint Flags, ref Guid Interface, out IPropertyStore Store);
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(IntPtr IdList, uint Kind, out IntPtr Name);
    [DllImport("shell32.dll")] private static extern IntPtr ILCombine(IntPtr Parent, IntPtr Child);
    [DllImport("shell32.dll")] private static extern uint ILGetSize(IntPtr IdList);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string Name, IntPtr Context, out IntPtr IdList, uint Attributes, out uint ResultAttributes);
    [DllImport("propsys.dll", CharSet = CharSet.Unicode)] private static extern int PSGetPropertyKeyFromName(string Name, out PropertyKey Key);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyValue Value);
}
