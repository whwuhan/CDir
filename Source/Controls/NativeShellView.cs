using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CDir.Services;

namespace CDir.Controls;

// ExplorerBrowser hosts Windows' own virtual-folder view, including its item menus and operations.
internal sealed partial class NativeShellView : HwndHost
{
    private IExplorerBrowser? Browser;
    private ShellViewEvents? Events;
    private uint Cookie;
    private IntPtr Host;
    private TaskCompletionSource? Navigation;
    internal string? NavigationTarget { get; private set; }
    internal Func<Key, ModifierKeys, bool>? Shortcut { get; set; }
    internal bool HasNativeView => this.Browser is not null && this.Host != IntPtr.Zero;

    protected override HandleRef BuildWindowCore(HandleRef Parent)
    {
        this.Host = CreateWindowEx(0, "static", "CDir Shell View", 0x56000000, 0, 0, 1, 1, Parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (this.Host == IntPtr.Zero) throw new Win32Exception();
        try
        {
            this.Browser = (IExplorerBrowser)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("71F96385-DDD6-48D3-A0C1-AE06E8B055FB"), true)!)!;
            var Bounds = new NativeRect { Right = 1, Bottom = 1 };
            var Settings = new FolderSettings { ViewMode = 4, Flags = 0x10000 }; // Details; no web view.
            this.Browser.SetOptions(0x40 | 0x80); // No border or persisted changes to Explorer's view preferences.
            this.Browser.Initialize(this.Host, ref Bounds, ref Settings);
            this.Events = new ShellViewEvents(this.CompleteNavigation);
            this.Browser.Advise(this.Events, out this.Cookie);
            return new HandleRef(this, this.Host);
        }
        catch { this.ReleaseBrowser(); DestroyWindow(this.Host); this.Host = IntPtr.Zero; throw; }
    }

    internal async Task NavigateAsync(string Location)
    {
        if (this.Browser is null) throw new InvalidOperationException("The embedded Shell view is not initialized.");
        var Pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this.Navigation?.TrySetCanceled();
        this.Navigation = Pending;
        IntPtr List = IntPtr.Zero;
        try
        {
            if (ShellLocations.IsRecycleBin(Location)) List = ShellLocations.RecycleBinIdList();
            else Marshal.ThrowExceptionForHR(SHParseDisplayName(Location, IntPtr.Zero, out List, 0, out _));
            this.NavigationTarget = Location;
            this.Browser.BrowseToIDList(List, 0);
            await Pending.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            if (List != IntPtr.Zero) Marshal.FreeCoTaskMem(List);
            if (ReferenceEquals(this.Navigation, Pending)) this.Navigation = null;
        }
    }

    private void CompleteNavigation(bool Success)
    {
        if (Success) this.Navigation?.TrySetResult();
        else this.Navigation?.TrySetException(new IOException("Windows could not open this Shell folder."));
    }

    internal int ItemCount => this.ReadItemCount(2);
    internal int SelectedItemCount => this.ReadItemCount(1);

    private int ReadItemCount(uint Flags)
    {
            if (this.Browser is null) return -1;
            object? View = null;
            try
            {
                var Id = typeof(IFolderView).GUID;
                this.Browser.GetCurrentView(ref Id, out View);
                ((IFolderView)View).ItemCount(Flags, out var Count);
                return Count;
            }
            catch (COMException) { return -1; }
            finally { if (View is not null) Marshal.ReleaseComObject(View); }
    }

    internal void SelectAllItems()
    {
        if (this.Browser is null) return;
        object? View = null;
        try
        {
            var Id = typeof(IFolderView).GUID;
            this.Browser.GetCurrentView(ref Id, out View);
            var Folder = (IFolderView)View;
            Folder.ItemCount(2, out var Count);
            for (var Index = 0; Index < Count; Index++) Folder.SelectItem(Index, 1);
        }
        finally { if (View is not null) Marshal.ReleaseComObject(View); }
    }

    internal void RefreshContents()
    {
        if (this.Browser is null) return;
        object? View = null;
        try
        {
            var Id = typeof(IShellView).GUID;
            this.Browser.GetCurrentView(ref Id, out View);
            ((IShellView)View).Refresh();
        }
        finally { if (View is not null) Marshal.ReleaseComObject(View); }
    }

    protected override void OnWindowPositionChanged(Rect Bounds)
    {
        base.OnWindowPositionChanged(Bounds);
        if (this.Browser is not null && GetClientRect(this.Host, out var Client)) this.Browser.SetRect(IntPtr.Zero, Client);
    }

    protected override bool TranslateAcceleratorCore(ref MSG Message, ModifierKeys Modifiers)
    {
        if (Message.message is 0x100 or 0x104 && this.Shortcut?.Invoke(KeyInterop.KeyFromVirtualKey((int)Message.wParam), Modifiers) == true) return true;
        return base.TranslateAcceleratorCore(ref Message, Modifiers);
    }

    private void ReleaseBrowser()
    {
        this.Navigation?.TrySetCanceled();
        this.Navigation = null;
        if (this.Browser is not { } Browser) return;
        this.Browser = null;
        try { if (this.Events is not null) Browser.Unadvise(this.Cookie); Browser.Destroy(); }
        finally { this.Events = null; Marshal.FinalReleaseComObject(Browser); }
    }

    protected override void DestroyWindowCore(HandleRef Window)
    {
        try { this.ReleaseBrowser(); }
        finally { DestroyWindow(Window.Handle); this.Host = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct FolderSettings { public uint ViewMode, Flags; }
    [ComImport, Guid("DFD3B6B5-C10C-4BE9-85F6-A66969F402F6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IExplorerBrowser
    {
        void Initialize(IntPtr Parent, ref NativeRect Bounds, ref FolderSettings Settings);
        void Destroy();
        void SetRect(IntPtr DeferredPosition, NativeRect Bounds);
        void SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string Name);
        void SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string Text);
        void SetFolderSettings(ref FolderSettings Settings);
        void Advise(IShellViewEvents Events, out uint Cookie);
        void Unadvise(uint Cookie);
        void SetOptions(uint Options);
        void GetOptions(out uint Options);
        void BrowseToIDList(IntPtr IdList, uint Flags);
        void BrowseToObject([MarshalAs(UnmanagedType.IUnknown)] object Object, uint Flags);
        void FillFromObject([MarshalAs(UnmanagedType.IUnknown)] object Object, uint Flags);
        void RemoveAll();
        void GetCurrentView(ref Guid Interface, [MarshalAs(UnmanagedType.IUnknown)] out object View);
    }
    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode(out uint Mode);
        void SetCurrentViewMode(uint Mode);
        void GetFolder(ref Guid Interface, [MarshalAs(UnmanagedType.IUnknown)] out object Folder);
        void Item(int Index, out IntPtr IdList);
        void ItemCount(uint Flags, out int Count);
        void Items(uint Flags, ref Guid Interface, [MarshalAs(UnmanagedType.IUnknown)] out object Items);
        void GetSelectionMarkedItem(out int Index);
        void GetFocusedItem(out int Index);
        void GetItemPosition(IntPtr Item, out NativeItemPoint Position);
        void GetSpacing(ref NativeItemPoint Spacing);
        void GetDefaultSpacing(out NativeItemPoint Spacing);
        void GetAutoArrange();
        void SelectItem(int Index, uint Flags);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeItemPoint { public int X, Y; }
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr Window);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool Enter);
        [PreserveSig] int TranslateAccelerator(ref MSG Message);
        void EnableModeless([MarshalAs(UnmanagedType.Bool)] bool Enable);
        void UIActivate(uint State);
        void Refresh();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint Extended, string Class, string Name, uint Style, int X, int Y, int Width, int Height, IntPtr Parent, IntPtr Menu, IntPtr Instance, IntPtr Data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr Window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr Window, out NativeRect Bounds);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string Name, IntPtr BindContext, out IntPtr IdList, uint Attributes, out uint ResultAttributes);
}

[ComVisible(true), Guid("361BBDC7-E6EE-4E13-BE58-58E2240C810F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellViewEvents
{
    [PreserveSig] int OnNavigationPending(IntPtr Folder);
    [PreserveSig] int OnViewCreated([MarshalAs(UnmanagedType.IUnknown)] object View);
    [PreserveSig] int OnNavigationComplete(IntPtr Folder);
    [PreserveSig] int OnNavigationFailed(IntPtr Folder);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ShellViewEvents(Action<bool> Complete) : IShellViewEvents
{
    public int OnNavigationPending(IntPtr Folder) => 0;
    public int OnViewCreated(object View) => 0;
    public int OnNavigationComplete(IntPtr Folder) { Complete(true); return 0; }
    public int OnNavigationFailed(IntPtr Folder) { Complete(false); return 0; }
}
