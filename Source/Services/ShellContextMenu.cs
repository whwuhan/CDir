using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace CDir.Services;

/// <summary>Hosts the complete Windows Shell menu without application menu entries.</summary>
internal sealed class ShellContextMenu : IDisposable
{
    internal sealed record MenuEntry(uint Id, string Text, bool Enabled, bool HasIcon, bool IsSeparator, IReadOnlyList<MenuEntry> Children);
    private readonly int ThreadId = Environment.CurrentManagedThreadId;
    private readonly IntPtr Owner;
    private readonly string WorkingDirectory;
    internal string[] SelectionPaths { get; }
    private IContextMenu? NativeMenu;
    private IntPtr MenuHandle;
    private uint CommandCount;
    private HwndSource? MessageSource;

    internal ShellContextMenu(IntPtr Owner, string CurrentFolder, string[] Paths, bool Extended = false, byte[][]? ShellItemIds = null)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Shell menus require an STA thread.");
        this.SelectionPaths = (string[])Paths.Clone();
        this.Owner = Owner;
        this.WorkingDirectory = ShellLocations.IsRecycleBin(CurrentFolder) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : CurrentFolder;
        var Ids = new List<IntPtr>();
        object? Folder = null;
        try
        {
            var MenuId = typeof(IContextMenu).GUID;
            if (Paths.Length == 0)
            {
                var ItemId = typeof(IShellItem).GUID;
                IShellItem Item;
                if (ShellLocations.IsRecycleBin(CurrentFolder))
                {
                    var Root = ShellLocations.RecycleBinIdList();
                    try { Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(Root, ref ItemId, out Item)); }
                    finally { Marshal.FreeCoTaskMem(Root); }
                }
                else Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(CurrentFolder, IntPtr.Zero, ref ItemId, out Item));
                Folder = Item;
                // SFViewObject requests the folder background menu. SFUIObject
                // would target the folder as an item and omit the New submenu.
                var Handler = new Guid("3981E226-F559-11D3-8E3A-00C04F6837D5"); // BHID_SFViewObject
                Marshal.ThrowExceptionForHR(Item.BindToHandler(IntPtr.Zero, ref Handler, ref MenuId, out var Menu));
                this.NativeMenu = (IContextMenu)Menu;
            }
            else
            {
                if (ShellItemIds is not null && ShellItemIds.Length != Paths.Length) throw new ArgumentException("Shell item identities must match the selection.");
                for (var Index = 0; Index < Paths.Length; Index++)
                {
                    IntPtr Id;
                    if (ShellItemIds is not null)
                    {
                        var Bytes = ShellItemIds[Index];
                        if (Bytes.Length < 2) throw new ArgumentException("Invalid Shell item identity.");
                        Id = Marshal.AllocCoTaskMem(Bytes.Length);
                        Marshal.Copy(Bytes, 0, Id, Bytes.Length);
                    }
                    else Marshal.ThrowExceptionForHR(SHParseDisplayName(Paths[Index], IntPtr.Zero, out Id, 0, out _));
                    Ids.Add(Id);
                }
                var Parents = Paths.Select(Path => System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(Path))).ToArray();
                IntPtr[] Children;
                if (ShellItemIds is not null || Parents[0] is not null && Parents.All(Parent => string.Equals(Parent, Parents[0], StringComparison.OrdinalIgnoreCase)))
                {
                    var FolderId = typeof(IShellFolder).GUID;
                    Marshal.ThrowExceptionForHR(SHBindToParent(Ids[0], ref FolderId, out var Parent, out _));
                    Folder = Parent;
                    Children = Ids.Select(ILFindLastID).ToArray();
                }
                else
                {
                    Marshal.ThrowExceptionForHR(SHGetDesktopFolder(out var Desktop));
                    Folder = Desktop;
                    Children = Ids.ToArray();
                }
                Marshal.ThrowExceptionForHR(((IShellFolder)Folder).GetUIObjectOf(Owner, (uint)Children.Length, Children, ref MenuId, IntPtr.Zero, out var Menu));
                this.NativeMenu = (IContextMenu)Menu;
            }
            this.MenuHandle = CreatePopupMenu();
            if (this.MenuHandle == IntPtr.Zero) throw new Win32Exception();
            this.MessageSource = HwndSource.FromHwnd(Owner)
                ?? throw new InvalidOperationException("The Shell menu owner is unavailable.");
            // Extensions may send initialization and owner-draw messages while building the first menu.
            this.MessageSource.AddHook(this.MenuMessage);
            try
            {
                // CMF_SYNCCASCADEMENU completes dynamic cascades before their first display.
                var Result = this.NativeMenu.QueryContextMenu(this.MenuHandle, 0, 1, 0x7fff,
                    4u | 0x1000u | (Paths.Length > 0 ? 0x80u | 0x10u : 0u) | (Extended ? 0x100u : 0u));
                Marshal.ThrowExceptionForHR(Result);
                this.CommandCount = (uint)(Result & 0xffff);
            }
            finally { this.MessageSource.RemoveHook(this.MenuMessage); }
        }
        catch { this.Dispose(); throw; }
        finally
        {
            if (Folder is not null) Marshal.ReleaseComObject(Folder);
            foreach (var Id in Ids) Marshal.FreeCoTaskMem(Id);
        }
    }

    internal uint Show(HwndSource Source, Point ScreenPosition)
    {
        this.CheckAlive();
        if (!ReferenceEquals(Source, this.MessageSource)) throw new InvalidOperationException("The Shell menu must use its original owner.");
        Source.AddHook(this.MenuMessage);
        try { return TrackPopupMenuEx(this.MenuHandle, 0x100 | 0x2, (int)ScreenPosition.X, (int)ScreenPosition.Y, this.Owner, IntPtr.Zero); }
        finally { Source.RemoveHook(this.MenuMessage); }
    }

    internal string? DescribeCommand(uint Id)
    {
        this.CheckAlive();
        if (Id == 0 || Id - 1 >= this.CommandCount) return null;
        var Text = new StringBuilder(256);
        return this.NativeMenu!.GetCommandString((nuint)(Id - 1), 4, IntPtr.Zero, Text, (uint)Text.Capacity) >= 0 ? Text.ToString() : null;
    }

    internal bool Invoke(uint Id)
    {
        this.CheckAlive();
        if (Id == 0 || Id - 1 >= this.CommandCount) return false;
        var Info = new InvokeInfo
        {
            Size = Marshal.SizeOf<InvokeInfo>(), Mask = 0x4000u | (GetKeyState(0x10) < 0 ? 0x10000000u : 0u)
                | (GetKeyState(0x11) < 0 ? 0x40000000u : 0u), Window = this.Owner,
            Verb = (IntPtr)(Id - 1), VerbW = (IntPtr)(Id - 1), Show = 1,
            Directory = this.WorkingDirectory, DirectoryW = this.WorkingDirectory
        };
        Marshal.ThrowExceptionForHR(this.NativeMenu!.InvokeCommand(ref Info));
        return true;
    }

    internal static void Rename(IntPtr Owner, string[] Paths, string NewName)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Shell operations require an STA thread.");
        var Ids = new List<IntPtr>();
        object? Items = null;
        IFileOperation? Operation = null;
        try
        {
            foreach (var Path in Paths)
            {
                Marshal.ThrowExceptionForHR(SHParseDisplayName(Path, IntPtr.Zero, out var Id, 0, out _));
                Ids.Add(Id);
            }
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)Ids.Count, Ids.ToArray(), out Items));
            Operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            Marshal.ThrowExceptionForHR(Operation.SetOwnerWindow(Owner));
            Marshal.ThrowExceptionForHR(Operation.SetOperationFlags(0x40u | (Paths.Length > 1 ? 0x8u : 0u)));
            Marshal.ThrowExceptionForHR(Operation.RenameItems(Items, NewName));
            Marshal.ThrowExceptionForHR(Operation.PerformOperations());
            Marshal.ThrowExceptionForHR(Operation.GetAnyOperationsAborted(out var Aborted));
            if (Aborted) throw new OperationCanceledException();
        }
        finally
        {
            if (Operation is not null) Marshal.ReleaseComObject(Operation);
            if (Items is not null) Marshal.ReleaseComObject(Items);
            foreach (var Id in Ids) Marshal.FreeCoTaskMem(Id);
        }
    }

    internal static uint DeleteOperationFlags(bool Permanently) => Permanently ? 0u : 0x40u | 0x00080000u | 0x4000u;

    internal static void Delete(string[] Paths, bool Permanently)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Shell operations require an STA thread.");
        if (Paths.Length == 0) return;
        var Ids = new List<IntPtr>();
        object? Items = null;
        IFileOperation? Operation = null;
        try
        {
            foreach (var Path in Paths)
            {
                if (Directory.GetParent(Path) is null) throw new IOException(CDir.Localization.LanguageService.Text("ServiceDeleteRootDenied"));
                Marshal.ThrowExceptionForHR(SHParseDisplayName(Path, IntPtr.Zero, out var Id, 0, out _));
                Ids.Add(Id);
            }
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)Ids.Count, Ids.ToArray(), out Items));
            Operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            // Shell progress and confirmation UI belongs to this worker, never the browsing window.
            Marshal.ThrowExceptionForHR(Operation.SetOwnerWindow(IntPtr.Zero));
            Marshal.ThrowExceptionForHR(Operation.SetOperationFlags(DeleteOperationFlags(Permanently)));
            Marshal.ThrowExceptionForHR(Operation.DeleteItems(Items));
            Marshal.ThrowExceptionForHR(Operation.PerformOperations());
            Marshal.ThrowExceptionForHR(Operation.GetAnyOperationsAborted(out var Aborted));
            if (Aborted) throw new OperationCanceledException();
        }
        finally
        {
            if (Operation is not null) Marshal.ReleaseComObject(Operation);
            if (Items is not null) Marshal.ReleaseComObject(Items);
            foreach (var Id in Ids) Marshal.FreeCoTaskMem(Id);
        }
    }

    private IntPtr MenuMessage(IntPtr Window, int Message, IntPtr WParam, IntPtr LParam, ref bool Handled)
    {
        if (Message is not (0x117 or 0x125 or 0x120 or 0x2b or 0x2c)) return IntPtr.Zero;
        if (Message is 0x2b or 0x2c && (LParam == IntPtr.Zero || Marshal.ReadInt32(LParam) != 1)) return IntPtr.Zero;
        if (this.NativeMenu is IContextMenu3 Menu3)
        {
            if (Menu3.HandleMenuMsg2((uint)Message, WParam, LParam, out var Result) != 0) return IntPtr.Zero;
            Handled = true;
            return Result;
        }
        if (this.NativeMenu is IContextMenu2 Menu2 && Message != 0x120)
        {
            Handled = Menu2.HandleMenuMsg((uint)Message, WParam, LParam) == 0;
            return Handled && Message is 0x2b or 0x2c ? (IntPtr)1 : IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    internal IReadOnlyList<MenuEntry> ReadEntries(bool InitializeSubmenus = false)
    {
        this.CheckAlive();
        return Read(this.MenuHandle);
        MenuEntry[] Read(IntPtr Menu) => Enumerable.Range(0, GetMenuItemCount(Menu)).Select(Index =>
        {
            var Text = new StringBuilder(1024);
            GetMenuString(Menu, (uint)Index, Text, Text.Capacity, 0x400);
            var Info = new MenuItemInfo { Size = (uint)Marshal.SizeOf<MenuItemInfo>(), Mask = 0x1 | 0x100 | 0x80 };
            if (!GetMenuItemInfo(Menu, (uint)Index, true, ref Info)) throw new Win32Exception();
            var Submenu = GetSubMenu(Menu, Index);
            if (InitializeSubmenus && Submenu != IntPtr.Zero)
            {
                // Shell extensions populate New and other dynamic submenus when opened.
                var Handled = false;
                this.MenuMessage(this.Owner, 0x117, Submenu, (IntPtr)Index, ref Handled);
            }
            return new MenuEntry(GetMenuItemID(Menu, Index), Text.ToString(), (Info.State & 3) == 0,
                Info.Bitmap != IntPtr.Zero || (Info.Type & 0x100) != 0, (Info.Type & 0x800) != 0,
                Submenu == IntPtr.Zero ? [] : Read(Submenu));
        }).ToArray();
    }

    private void CheckAlive()
    {
        if (Environment.CurrentManagedThreadId != this.ThreadId) throw new InvalidOperationException("Shell menus must remain on their owning STA thread.");
        ObjectDisposedException.ThrowIf(this.MenuHandle == IntPtr.Zero, this);
    }

    public void Dispose()
    {
        this.MessageSource = null;
        if (this.MenuHandle != IntPtr.Zero) { DestroyMenu(this.MenuHandle); this.MenuHandle = IntPtr.Zero; }
        if (this.NativeMenu is not null) { Marshal.ReleaseComObject(this.NativeMenu); this.NativeMenu = null; }
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise();
        void Unadvise();
        [PreserveSig] int SetOperationFlags(uint Flags);
        void SetProgressMessage();
        void SetProgressDialog();
        void SetProperties();
        [PreserveSig] int SetOwnerWindow(IntPtr Owner);
        void ApplyPropertiesToItem();
        void ApplyPropertiesToItems();
        void RenameItem();
        [PreserveSig] int RenameItems([MarshalAs(UnmanagedType.IUnknown)] object Items, [MarshalAs(UnmanagedType.LPWStr)] string Name);
        void MoveItem();
        void MoveItems();
        void CopyItem();
        void CopyItems();
        void DeleteItem();
        [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object Items);
        void NewItem();
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool Aborted);
    }
    [DllImport("shell32.dll")] private static extern int SHCreateShellItemArrayFromIDLists(uint Count,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] Ids, [MarshalAs(UnmanagedType.Interface)] out object Items);
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr Context, ref Guid Handler, ref Guid Id, [MarshalAs(UnmanagedType.Interface)] out object Result);
    }
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName();
        void EnumObjects();
        void BindToObject();
        void BindToStorage();
        void CompareIDs();
        void CreateViewObject();
        void GetAttributesOf();
        [PreserveSig] int GetUIObjectOf(IntPtr Owner, uint Count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] Children,
            ref Guid Id, IntPtr Reserved, [MarshalAs(UnmanagedType.Interface)] out object Result);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string Path, IntPtr Context, ref Guid Id, out IShellItem Item);
    [DllImport("shell32.dll")] private static extern int SHCreateItemFromIDList(IntPtr IdList, ref Guid Id, out IShellItem Item);
    [DllImport("shell32.dll")] private static extern int SHBindToParent(IntPtr Id, ref Guid Interface, out IShellFolder Parent, out IntPtr Child);
    [DllImport("shell32.dll")] private static extern int SHGetDesktopFolder(out IShellFolder Folder);
    [DllImport("shell32.dll")] private static extern IntPtr ILFindLastID(IntPtr Id);
    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr Menu, uint Index, uint First, uint Last, uint Flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo Info);
        [PreserveSig] int GetCommandString(nuint Id, uint Flags, IntPtr Reserved, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder Name, uint Max);
    }
    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr Menu, uint Index, uint First, uint Last, uint Flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo Info);
        [PreserveSig] int GetCommandString(nuint Id, uint Flags, IntPtr Reserved, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder Name, uint Max);
        [PreserveSig] int HandleMenuMsg(uint Message, IntPtr WParam, IntPtr LParam);
    }
    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr Menu, uint Index, uint First, uint Last, uint Flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo Info);
        [PreserveSig] int GetCommandString(nuint Id, uint Flags, IntPtr Reserved, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder Name, uint Max);
        [PreserveSig] int HandleMenuMsg(uint Message, IntPtr WParam, IntPtr LParam);
        [PreserveSig] int HandleMenuMsg2(uint Message, IntPtr WParam, IntPtr LParam, out IntPtr Result);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InvokeInfo
    {
        public int Size; public uint Mask; public IntPtr Window; public IntPtr Verb;
        [MarshalAs(UnmanagedType.LPStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? Directory;
        public int Show; public uint HotKey; public IntPtr Icon;
        [MarshalAs(UnmanagedType.LPStr)] public string? Title;
        public IntPtr VerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? DirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TitleW;
        public int X; public int Y;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MenuItemInfo
    {
        public uint Size, Mask, Type, State, Id; public IntPtr Submenu, CheckedBitmap, UncheckedBitmap, Data, Text; public uint TextLength; public IntPtr Bitmap;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string Name, IntPtr Context, out IntPtr Id, uint Attributes, out uint ResultAttributes);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr Menu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr Menu);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr Menu, int Index);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr Menu, int Index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuString(IntPtr Menu, uint Index, StringBuilder Text, int Max, uint Flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMenuItemInfo(IntPtr Menu, uint Item, [MarshalAs(UnmanagedType.Bool)] bool ByPosition, ref MenuItemInfo Info);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr Menu, uint Flags, int X, int Y, IntPtr Owner, IntPtr Parameters);
    [DllImport("user32.dll")] private static extern short GetKeyState(int Key);
}
