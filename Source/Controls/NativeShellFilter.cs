using System.Runtime.InteropServices;
using CDir.Models;

namespace CDir.Controls;

internal sealed partial class NativeShellView
{
    private NativeFolderFilter? FolderFilter;

    internal void SetSearchQuery(SearchQuery Query)
    {
        if (this.Browser is null) return;
        if (this.FolderFilter is null)
        {
            this.FolderFilter = new NativeFolderFilter();
            ((IFolderFilterSite)this.Browser).SetFilter(this.FolderFilter);
        }
        this.FolderFilter.Query = Query;
        this.RefreshContents();
    }

    [ComImport, Guid("C0A651F5-B48B-11D2-B5ED-006097C686F6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderFilterSite
    {
        void SetFilter([MarshalAs(UnmanagedType.IUnknown)] object Filter);
    }
}

[ComVisible(true), Guid("9CC22886-DC8E-11D2-B1D0-00C04F8EEB3E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INativeFolderFilter
{
    [PreserveSig] int ShouldShow(IntPtr Folder, IntPtr FolderId, IntPtr ItemId);
    [PreserveSig] int GetEnumFlags(IntPtr Folder, IntPtr FolderId, IntPtr Window, out uint Flags);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class NativeFolderFilter : INativeFolderFilter
{
    internal SearchQuery Query { get; set; } = SearchQuery.Parse("");

    public int GetEnumFlags(IntPtr Folder, IntPtr FolderId, IntPtr Window, out uint Flags)
    { Flags = 0x20 | 0x40 | 0x80; return 0; }

    public int ShouldShow(IntPtr Folder, IntPtr FolderId, IntPtr ItemId)
    {
        var Query = this.Query;
        if (!Query.IsActive) return 0;
        if (Query.ErrorKey is not null) return 1;
        IShellFilterItem? Item = null;
        IntPtr Name = IntPtr.Zero;
        try
        {
            var Id = typeof(IShellFilterItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemWithParent(IntPtr.Zero, Folder, ItemId, ref Id, out Item));
            Item.GetDisplayName(0, out Name);
            Item.GetAttributes(0x20000000, out var Attributes);
            return Query.Matches(Marshal.PtrToStringUni(Name) ?? "", (Attributes & 0x20000000) != 0) ? 0 : 1;
        }
        catch (COMException) { return 1; }
        finally
        {
            if (Name != IntPtr.Zero) Marshal.FreeCoTaskMem(Name);
            if (Item is not null) Marshal.ReleaseComObject(Item);
        }
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFilterItem
    {
        void BindToHandler(IntPtr BindContext, ref Guid Handler, ref Guid Interface, out IntPtr Result);
        void GetParent(out IShellFilterItem Parent);
        void GetDisplayName(uint Kind, out IntPtr Name);
        void GetAttributes(uint Mask, out uint Attributes);
    }
    [DllImport("shell32.dll")]
    private static extern int SHCreateItemWithParent(IntPtr ParentId, IntPtr Parent, IntPtr ChildId, ref Guid Interface,
        [MarshalAs(UnmanagedType.Interface)] out IShellFilterItem Item);
}
