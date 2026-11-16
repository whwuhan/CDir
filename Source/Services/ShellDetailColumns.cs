using System.IO;
using System.Runtime.InteropServices;
using CDir.Models;

namespace CDir.Services;

// Use the folder's own column enumeration, including third-party property handlers.
// Stable PROPERTYKEYs survive OS language changes and differences in column indices.
internal static class ShellDetailColumns
{
    internal sealed record Column(string Key, string Header, PropertyKey Property, uint Index, bool RecycleOnly);
    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 272)] private struct StrRet
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Details { public int Format; public int Width; public StrRet Text; }

    internal static IReadOnlyList<Column> ReadCatalog(string Directory, bool Recycle = false)
    {
        var Root = RecycleBinService.GetIdList(Recycle ? ShellLocations.RecycleBin : Directory);
        IShellFolder2? Folder = null;
        try
        {
            var Id = typeof(IShellFolder2).GUID;
            Marshal.ThrowExceptionForHR(SHBindToObject(IntPtr.Zero, Root, IntPtr.Zero, ref Id, out Folder));
            var Result = new List<Column>();
            for (uint Index = 0; ; Index++)
            {
                if (Folder.GetDetailsOf(IntPtr.Zero, Index, out var Detail) < 0) break;
                Marshal.ThrowExceptionForHR(StrRetToStr(ref Detail.Text, IntPtr.Zero, out var Text));
                string Header;
                try { Header = Marshal.PtrToStringUni(Text) ?? ""; }
                finally { Marshal.FreeCoTaskMem(Text); }
                if (Header.Length == 0 || Folder.MapColumnToSCID(Index, out var Key) < 0) continue;
                if (Folder.GetDefaultColumnState(Index, out var Flags) >= 0 && (Flags & 0x100) != 0) continue;
                var Name = "Shell_" + Key.Format.ToString("N") + "_" + Key.Id;
                var Canonical = IntPtr.Zero;
                try
                {
                    if (PSGetNameFromPropertyKey(ref Key, out Canonical) >= 0)
                        Name = Marshal.PtrToStringUni(Canonical) ?? Name;
                }
                finally { if (Canonical != IntPtr.Zero) Marshal.FreeCoTaskMem(Canonical); }
                Name = Name switch
                {
                    "System.ItemNameDisplay" => nameof(FileEntry.Name), "System.Size" => nameof(FileEntry.Size),
                    "System.ItemTypeText" => nameof(FileEntry.TypeName), "System.DateModified" => nameof(FileEntry.Modified),
                    "System.Recycle.DeletedFrom" => nameof(FileEntry.OriginalLocation),
                    "System.Recycle.DateDeleted" => nameof(FileEntry.DateDeleted), _ => Name
                };
                if (Result.All(Column => Column.Key != Name)) Result.Add(new(Name, Header, Key, Index, Recycle));
            }
            return Result;
        }
        finally { if (Folder is not null) Marshal.ReleaseComObject(Folder); Marshal.FreeCoTaskMem(Root); }
    }

    internal static IReadOnlyDictionary<string, (string Text, object? Value)> ReadValues(FileEntry Entry, IReadOnlyList<Column> Columns)
    {
        var Root = IntPtr.Zero;
        IShellFolder2? Folder = null;
        try
        {
            if (Entry.ShellId is { } Bytes)
            {
                Root = Marshal.AllocCoTaskMem(Bytes.Length);
                Marshal.Copy(Bytes, 0, Root, Bytes.Length);
            }
            else Root = RecycleBinService.GetIdList(Entry.FullPath);
            var Id = typeof(IShellFolder2).GUID;
            Marshal.ThrowExceptionForHR(SHBindToParent(Root, ref Id, out Folder, out var Child));
            var Result = new Dictionary<string, (string, object?)>();
            foreach (var Column in Columns)
            {
                var Key = Column.Property;
                object? Value = null;
                try { if (Folder.GetDetailsEx(Child, ref Key, out Value) < 0) Value = null; }
                catch (Exception Error) when (Error is COMException or InvalidCastException or ArgumentException) { }
                if (Value is not null && Marshal.IsComObject(Value)) { Marshal.ReleaseComObject(Value); Value = null; }
                // The caller supplies the context-specific catalog: Recycle Bin indices differ from filesystem indices.
                string Text = "";
                if (Folder.GetDetailsOf(Child, Column.Index, out var Detail) >= 0)
                {
                    if (StrRetToStr(ref Detail.Text, Child, out var Pointer) >= 0)
                    {
                        try { Text = Marshal.PtrToStringUni(Pointer) ?? ""; }
                        finally { Marshal.FreeCoTaskMem(Pointer); }
                    }
                }
                Result[Column.Key] = (Text, Value);
            }
            return Result;
        }
        finally { if (Folder is not null) Marshal.ReleaseComObject(Folder); if (Root != IntPtr.Zero) Marshal.FreeCoTaskMem(Root); }
    }

    [ComImport, Guid("93F2F68C-1D1B-11D3-A30E-00C04F79ABD1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder2
    {
        void ParseDisplayName(); void EnumObjects(); void BindToObject(); void BindToStorage(); void CompareIDs();
        void CreateViewObject(); void GetAttributesOf(); void GetUIObjectOf(); void GetDisplayNameOf(); void SetNameOf();
        void GetDefaultSearchGUID(); void EnumSearches(); void GetDefaultColumn();
        [PreserveSig] int GetDefaultColumnState(uint Index, out uint Flags);
        [PreserveSig] int GetDetailsEx(IntPtr Child, ref PropertyKey Key, [MarshalAs(UnmanagedType.Struct)] out object? Value);
        [PreserveSig] int GetDetailsOf(IntPtr Child, uint Index, out Details Detail);
        [PreserveSig] int MapColumnToSCID(uint Index, out PropertyKey Key);
    }
    [DllImport("shell32.dll")] private static extern int SHBindToObject(IntPtr Parent, IntPtr IdList, IntPtr Context, ref Guid Id, out IShellFolder2 Folder);
    [DllImport("shell32.dll")] private static extern int SHBindToParent(IntPtr IdList, ref Guid Id, out IShellFolder2 Folder, out IntPtr Child);
    [DllImport("shlwapi.dll", EntryPoint = "StrRetToStrW")] private static extern int StrRetToStr(ref StrRet Text, IntPtr Child, out IntPtr Result);
    [DllImport("propsys.dll")] private static extern int PSGetNameFromPropertyKey(ref PropertyKey Key, out IntPtr Name);
}
