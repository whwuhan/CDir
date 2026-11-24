using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    internal sealed record MenuNode(string Text, bool IsSeparator, bool Enabled, bool Checked, string Verb, MenuNode[] Children)
    {
        public override string ToString() => IsSeparator ? "----" : Text + (Enabled ? "" : " [disabled]") + (Checked ? " [checked]" : "");
        // Compare what a user sees: captions, separators, enabled and checked state.
        internal bool SameAs(MenuNode Other) => this.ToString() == Other.ToString() && this.Children.Length == Other.Children.Length
            && this.Children.Zip(Other.Children).All(Pair => Pair.First.SameAs(Pair.Second));
        internal static MenuNode[] Normalize(IEnumerable<MenuNode> Nodes, Func<MenuNode, bool>? Omit = null)
        {
            var Result = new List<MenuNode>();
            foreach (var Node in Nodes)
            {
                if (Omit?.Invoke(Node) == true) continue;
                if (Node.IsSeparator && (Result.Count == 0 || Result[^1].IsSeparator)) continue;
                Result.Add(Node with { Children = Normalize(Node.Children, Omit) });
            }
            if (Result.Count > 0 && Result[^1].IsSeparator) Result.RemoveAt(Result.Count - 1);
            return Result.ToArray();
        }
        internal static void Write(StringBuilder Output, IEnumerable<MenuNode> Nodes, int Depth = 0)
        {
            foreach (var Node in Nodes)
            {
                Output.AppendLine(new string(' ', Depth * 2) + Node + (Node.Verb.Length > 0 ? "  {" + Node.Verb + "}" : ""));
                Write(Output, Node.Children, Depth + 1);
            }
        }
    }

    // Compares the complete blank-space menu with the one Windows' own folder view
    // (ExplorerBrowser hosting DefView) builds for the same owned folder, columns,
    // view, sort, grouping and the user's current clipboard (read only).
    private async Task RunBackgroundMenuSmokeAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialGroup = this.FileGroupProperty;
        var InitialGroupDescending = this.FileGroupDescending;
        var InitialView = this.Settings.FileView;
        var InitialSize = this.Settings.FileViewIconSize;
        var InitialSort = this.SortProperty;
        var InitialDirection = this.SortDirection;
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "GroupByMenu-" + Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(Fixture, "Folder"));
        File.WriteAllText(Path.Combine(Fixture, "Alpha.txt"), "Group by fixture");
        File.WriteAllBytes(Path.Combine(Fixture, "Beta.bin"), new byte[4096]);
        const string Created = "System.DateCreated";
        var CreatedWasEnabled = this.IsColumnEnabled(Created);
        var CreatedWasVisible = this.FileGrid.Columns.FirstOrDefault(Column => Column.SortMemberPath == Created)?.Visibility == Visibility.Visible;
        var ReadClipboard = this.ReadClipboardFiles;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Background menu check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); this.UpdateLayout(); }
        var Owner = new WindowInteropHelper(this.HostWindow).Handle;
        var HistoryTimes = new List<double>();
        async Task<MenuNode[]> ReadApplicationMenu()
        {
            // Mirror FileContextOpening: the history read runs while CDir builds its menu.
            var Timer = System.Diagnostics.Stopwatch.StartNew();
            var HistoryRead = this.ReadExplorerHistoryAsync();
            using var Menu = new ShellContextMenu(Owner, Fixture, [], ViewSettings: new(this.Settings.FileView, this.SortProperty,
                this.SortDirection == System.ComponentModel.ListSortDirection.Descending, this.FileGroupProperty, this.FileGroupDescending,
                true, this.CanPasteBackground(), this.ExplorerGroupColumns()));
            var Built = Timer.Elapsed.TotalMilliseconds;
            Menu.AddHistoryCommands(await HistoryRead);
            HistoryTimes.Add(Timer.Elapsed.TotalMilliseconds - Built);
            MenuNode[] Convert(IReadOnlyList<ShellContextMenu.MenuEntry> Entries) => Entries.Select(Entry => new MenuNode(Entry.IsSeparator ? "" : Entry.Text,
                Entry.IsSeparator, Entry.IsSeparator || Entry.Enabled, !Entry.IsSeparator && Entry.Checked,
                Entry.Children.Count == 0 && !Entry.IsSeparator ? Menu.DescribeCommand(Entry.Id) ?? "" : "", Convert(Entry.Children))).ToArray();
            return Convert(Menu.ReadEntries(true));
        }
        // Windows' hosted folder view has no Explorer frame, so it cannot offer
        // Customize This Folder; Explorer's own windows list it from the same template.
        static bool HostOnly(MenuNode Node) => Node.Verb == "CDir.Customize";
        // Grouped Windows views also offer Expand/Collapse All Groups; CDir groups cannot collapse.
        static bool CollapsibleGroupsOnly(MenuNode Node) => Node.Verb is "viewexpandall" or "viewcollapseall";
        async Task<(MenuNode[] Windows, MenuNode[] Application)> Compare(string Name, uint NativeView, bool Assert = true)
        {
            var Keys = this.FileGrid.Columns.Where(Column => Column.Visibility == Visibility.Visible).OrderBy(Column => Column.DisplayIndex)
                .Select(Column => Column.SortMemberPath).ToArray();
            MenuNode[] Windows = [], Application = [];
            bool Matches() => Windows.Length == Application.Length && Windows.Zip(Application).All(Pair => Pair.First.SameAs(Pair.Second));
            // The first menus a process requests can omit packaged (Windows 11) handlers
            // that are still loading, on either side; re-read both until they settle.
            for (var Attempt = 0; Attempt < 5 && (Attempt == 0 || !Matches()); Attempt++)
            {
                if (Attempt > 0) await Task.Delay(400);
                Application = MenuNode.Normalize(await ReadApplicationMenu(), HostOnly);
                Windows = MenuNode.Normalize(await BackgroundMenuProbe.ReadAsync(Owner, Fixture, NativeView, Keys.Select(NativeColumnName).ToArray(),
                    NativeColumnName(this.SortProperty), this.SortDirection == System.ComponentModel.ListSortDirection.Ascending,
                    this.FileGroupProperty.Length == 0 ? null : NativeColumnName(this.FileGroupProperty), !this.FileGroupDescending), CollapsibleGroupsOnly);
            }
            var Report = new StringBuilder("### " + Name + Environment.NewLine + "--- Windows" + Environment.NewLine);
            MenuNode.Write(Report, Windows);
            Report.AppendLine("--- CDir");
            MenuNode.Write(Report, Application);
            if (AppMain.ScreenshotPath is { } Output)
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(Output)!, "BackgroundMenuComparison.txt"), Report + Environment.NewLine);
            if (Assert) Check(Matches(), Name + Environment.NewLine + Report);
            return (Windows, Application);
        }
        MenuNode GroupBy(MenuNode[] Menu) => Menu.Single(Node => Node.Children.Any(Child => Child.Verb is "groupascending" or "CDir.Group.Ascending"));
        try
        {
            this.ReadClipboardFiles = System.Windows.Clipboard.GetDataObject; // Real, read-only clipboard state for both menus.
            await this.NavigateAsync(Fixture, RecordHistory: false);
            this.ChangeFileView(FileViewMode.Details, FocusPanel: false, SaveImmediately: false);
            if (this.FileGroupProperty.Length > 0) await this.ExecuteBackgroundCommandAsync("CDir.Group.None");
            await this.ExecuteBackgroundCommandAsync("CDir.Sort." + nameof(FileEntry.Name));
            await this.ExecuteBackgroundCommandAsync("CDir.Sort.Ascending");
            await Settle();
            await Compare("The complete Details background menu matches Windows' folder view", 4);
            await this.ExecuteBackgroundCommandAsync("CDir.Sort." + nameof(FileEntry.Size));
            await this.ExecuteBackgroundCommandAsync("CDir.Sort.Descending");
            await Settle();
            await Compare("Sorting by another column in descending order matches Windows' checked Sort By items", 4);
            await this.ExecuteBackgroundCommandAsync("CDir.Sort." + nameof(FileEntry.Name));
            await this.ExecuteBackgroundCommandAsync("CDir.Sort.Ascending");
            await this.ExecuteBackgroundCommandAsync("CDir.Group." + nameof(FileEntry.TypeName));
            await this.ExecuteBackgroundCommandAsync("CDir.Group.Ascending");
            await Settle();
            var Grouped = await Compare("The grouped Details background menu matches Windows apart from collapsible groups", 4);
            Check(GroupBy(Grouped.Windows).SameAs(GroupBy(Grouped.Application)),
                "Grouped Group By matches Windows: checked column, (None) after the columns and checked direction");
            if (WindowsColumns.Value.Any(Column => Column.Key == Created))
            {
                this.ChangeColumnEnabled(Created, true);
                this.ChangeColumnVisibility(Created, true);
                await Settle();
                await this.ExecuteBackgroundCommandAsync("CDir.Group." + Created);
                await this.ExecuteBackgroundCommandAsync("CDir.Group.Descending");
                await this.ShellColumnsTask;
                await Settle();
                var Added = await Compare("An added column grouped descending matches Windows apart from collapsible groups", 4);
                Check(GroupBy(Added.Windows).SameAs(GroupBy(Added.Application)),
                    "An added Windows column appears in Sort By and Group By and can be the checked descending group");
                var Unspecified = Localization.LanguageService.Text("GroupUnspecified");
                Check(this.FileGroupProperty == Created && this.View!.Groups?.Count > 0
                    && this.Entries.All(Entry => Entry.ViewGroup.Length > 0 && Entry.ViewGroup != Unspecified),
                    "Grouping by a Windows date column uses the loaded property values");
                await this.ExecuteBackgroundCommandAsync("CDir.Group.None");
                this.ChangeColumnVisibility(Created, false);
                await Settle();
            }
            await this.ExecuteBackgroundCommandAsync("CDir.Group.None");
            this.ChangeFileView(FileViewMode.MediumIcons, FocusPanel: false, SaveImmediately: false);
            await Settle();
            await Compare("The icon-view background menu matches Windows", 1);
            this.ChangeFileView(FileViewMode.Details, FocusPanel: false, SaveImmediately: false);
            AppMain.RecordSmokeProgress("Extra wait for Windows undo/redo captions after building the menu (ms): " + string.Join(", ", HistoryTimes.Select(Time => Time.ToString("F0"))));
            await this.ExecuteBackgroundCommandAsync("CDir.Group.More");
            await Settle();
            Check(this.ColumnMenuButton.ContextMenu is { IsOpen: true },
                "More... opens the column chooser, like Windows' Choose Details");
            this.ColumnMenuButton.ContextMenu!.IsOpen = false;
            // Paste Shortcut uses the folder's own drop target with only the link effect.
            var Target = Directory.CreateDirectory(Path.Combine(Fixture, "Shortcuts")).FullName;
            var Data = new System.Windows.DataObject();
            Data.SetFileDropList([Path.Combine(Fixture, "Alpha.txt")]);
            this.ReadClipboardFiles = () => Data;
            await this.NavigateAsync(Target, RecordHistory: false);
            using (var Menu = new ShellContextMenu(Owner, Target, [], ViewSettings: new(this.Settings.FileView, "Name", false, "", false, true, this.CanPasteBackground(), this.ExplorerGroupColumns())))
            {
                var Top = Menu.ReadEntries(true);
                var Paste = Top.ToList().FindIndex(Entry => Menu.DescribeCommand(Entry.Id) is "paste" or "CDir.Paste");
                Check(Paste >= 0 && Top[Paste].Enabled && Menu.DescribeCommand(Top[Paste + 1].Id) is "pastelink" or "CDir.PasteShortcut" && Top[Paste + 1].Enabled,
                    "With files on the clipboard, Paste is followed by an enabled Paste Shortcut");
            }
            await this.ExecuteBackgroundCommandAsync("CDir.PasteShortcut");
            await Settle();
            var Links = Directory.GetFiles(Target, "*.lnk");
            Check(Links.Length == 1 && File.Exists(Path.Combine(Fixture, "Alpha.txt")) && this.Entries.Any(Entry => SamePath(Entry.FullPath, Links[0])),
                "Paste Shortcut creates a Windows-named shortcut, keeps the source and shows it in the panel"
                + $" (links={string.Join(", ", Links.Select(Path.GetFileName))}; files={string.Join(", ", Directory.GetFileSystemEntries(Target).Select(Path.GetFileName))}; status={this.StatusText.Text})");
        }
        finally
        {
            this.ReadClipboardFiles = ReadClipboard;
            if (this.ColumnMenuButton.ContextMenu is { } Menu) Menu.IsOpen = false;
            if (this.IsColumnEnabled(Created) && !CreatedWasVisible) this.ChangeColumnVisibility(Created, false);
            if (!CreatedWasEnabled && this.IsColumnEnabled(Created)) this.ChangeColumnEnabled(Created, false);
            this.ChangeFileView(InitialView, InitialSize, FocusPanel: false, SaveImmediately: false);
            await this.ExecuteBackgroundCommandAsync("CDir.Sort." + InitialSort);
            await this.ExecuteBackgroundCommandAsync(InitialDirection == System.ComponentModel.ListSortDirection.Descending ? "CDir.Sort.Descending" : "CDir.Sort.Ascending");
            await this.ExecuteBackgroundCommandAsync(InitialGroup.Length == 0 ? "CDir.Group.None" : "CDir.Group." + InitialGroup);
            if (InitialGroup.Length > 0) await this.ExecuteBackgroundCommandAsync(InitialGroupDescending ? "CDir.Group.Descending" : "CDir.Group.Ascending");
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned background menu fixture.");
            Directory.Delete(Fixture, true);
        }
    }
    private static string NativeColumnName(string Key) => Key switch
    {
        nameof(FileEntry.Name) => "System.ItemNameDisplay",
        nameof(FileEntry.Modified) => "System.DateModified",
        nameof(FileEntry.TypeName) => "System.ItemTypeText",
        nameof(FileEntry.Size) => "System.Size",
        nameof(FileEntry.OriginalLocation) => "System.Recycle.DeletedFrom",
        nameof(FileEntry.DateDeleted) => "System.Recycle.DateDeleted",
        _ => Key
    };

    private static class BackgroundMenuProbe
    {
        internal static async Task<MenuNode[]> ReadAsync(IntPtr Owner, string Folder, uint ViewMode, string[] Columns, string SortColumn, bool SortAscending,
            string? Group, bool GroupAscending)
        {
            if (!AppMain.SmokeTest || !AppMain.IsInsideSmokeScope(Folder)) throw new InvalidOperationException("Native menu probes require an owned folder.");
            var Browser = (IExplorerBrowser)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("71F96385-DDD6-48D3-A0C1-AE06E8B055FB"), true)!)!;
            var List = IntPtr.Zero;
            var Menu = IntPtr.Zero;
            object? ViewObject = null;
            try
            {
                var Bounds = new NativeRect { Right = 480, Bottom = 320 };
                var Settings = new FolderSettings { ViewMode = ViewMode, Flags = 0x10000 };
                Browser.SetOptions(0x40 | 0x80); // No border; never persist view changes to the user's Explorer settings.
                Browser.Initialize(Owner, ref Bounds, ref Settings);
                Marshal.ThrowExceptionForHR(SHParseDisplayName(Folder, IntPtr.Zero, out List, 0, out _));
                Browser.BrowseToIDList(List, 0);
                var Deadline = Environment.TickCount64 + 15000;
                while (true)
                {
                    try
                    {
                        var Id = typeof(IShellView).GUID;
                        Browser.GetCurrentView(ref Id, out ViewObject);
                        if (ViewObject is not null) break;
                    }
                    catch (COMException) { }
                    if (Environment.TickCount64 > Deadline) throw new TimeoutException("Windows' folder view did not open the owned fixture.");
                    await Task.Delay(50);
                }
                var Manager = (IColumnManager)ViewObject;
                PropertyKey Key(string Name) { Marshal.ThrowExceptionForHR(PSGetPropertyKeyFromName(Name, out var Value)); return Value; }
                var Keys = Columns.Select(Key).ToArray();
                Manager.SetColumns(Keys, (uint)Keys.Length);
                var Unknown = Marshal.GetIUnknownForObject(ViewObject);
                try
                {
                    var FolderView2 = new Guid("1AF3A467-214F-4298-908E-06B03E0B39F9");
                    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(Unknown, in FolderView2, out var View2));
                    try
                    {
                        // IFolderView2 slots after IUnknown (3) and IFolderView (14): SetGroupBy 17, SetSortColumns 27.
                        var Table = Marshal.ReadIntPtr(View2);
                        var Sort = new[] { new SortColumn { Key = Key(SortColumn), Direction = SortAscending ? 1 : -1 } };
                        Marshal.ThrowExceptionForHR(Marshal.GetDelegateForFunctionPointer<SetSortColumnsDelegate>(Marshal.ReadIntPtr(Table, 27 * IntPtr.Size))(View2, Sort, 1));
                        if (Group is not null)
                        {
                            var GroupKey = Key(Group);
                            Marshal.ThrowExceptionForHR(Marshal.GetDelegateForFunctionPointer<SetGroupByDelegate>(Marshal.ReadIntPtr(Table, 17 * IntPtr.Size))(View2, ref GroupKey, GroupAscending ? 1 : 0));
                        }
                    }
                    finally { Marshal.Release(View2); }
                }
                finally { Marshal.Release(Unknown); }
                await Task.Delay(200);
                var MenuId = typeof(IContextMenu).GUID;
                ((IShellView)ViewObject).GetItemObject(0, ref MenuId, out var MenuObject);
                try
                {
                    var Context = (IContextMenu)MenuObject;
                    Menu = CreatePopupMenu();
                    // Request the menu the way Explorer's own windows do.
                    Marshal.ThrowExceptionForHR(Context.QueryContextMenu(Menu, 0, 1, 0x7FFF, 4));
                    MenuNode[] Read(IntPtr Level) => Enumerable.Range(0, GetMenuItemCount(Level)).Select(Position =>
                    {
                        var Child = GetSubMenu(Level, Position);
                        if (Child != IntPtr.Zero) (MenuObject as IContextMenu2)?.HandleMenuMsg(0x117, Child, (IntPtr)Position);
                        var Info = new MenuItemInfo { Size = (uint)Marshal.SizeOf<MenuItemInfo>(), Mask = 0x1 | 0x100 };
                        GetMenuItemInfo(Level, (uint)Position, true, ref Info);
                        var Separator = (Info.Type & 0x800) != 0;
                        var Id = GetMenuItemID(Level, Position);
                        var Verb = new StringBuilder(260);
                        var VerbText = !Separator && Child == IntPtr.Zero && Id is >= 1 and <= 0x7FFF
                            && Context.GetCommandString(Id - 1, 4, IntPtr.Zero, Verb, 260) == 0 ? Verb.ToString() : "";
                        return new MenuNode(Separator ? "" : Text(Level, Position), Separator, Separator || (Info.State & 3) == 0,
                            !Separator && (Info.State & 8) != 0, VerbText, Child == IntPtr.Zero ? [] : Read(Child));
                    }).ToArray();
                    return Read(Menu);
                }
                finally { Marshal.ReleaseComObject(MenuObject); }
            }
            finally
            {
                if (Menu != IntPtr.Zero) DestroyMenu(Menu);
                if (ViewObject is not null) Marshal.ReleaseComObject(ViewObject);
                if (List != IntPtr.Zero) Marshal.FreeCoTaskMem(List);
                Browser.Destroy();
                Marshal.FinalReleaseComObject(Browser);
            }
        }
        private static string Text(IntPtr Menu, int Position)
        {
            var Buffer = new StringBuilder(512);
            GetMenuString(Menu, (uint)Position, Buffer, Buffer.Capacity, 0x400);
            return Buffer.ToString();
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct FolderSettings { public uint ViewMode, Flags; }
        [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MenuItemInfo { public uint Size, Mask, Type, State, Id; public IntPtr SubMenu, Checked, Unchecked, Data, TypeData; public uint Cch; public IntPtr Bitmap; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGroupByDelegate(IntPtr Self, ref PropertyKey Key, int Ascending);
        [StructLayout(LayoutKind.Sequential)] private struct SortColumn { public PropertyKey Key; public int Direction; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetSortColumnsDelegate(IntPtr Self, [In] SortColumn[] Columns, int Count);
        [ComImport, Guid("DFD3B6B5-C10C-4BE9-85F6-A66969F402F6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IExplorerBrowser
        {
            void Initialize(IntPtr Parent, ref NativeRect Bounds, ref FolderSettings Settings);
            void Destroy();
            void SetRect(IntPtr DeferredPosition, NativeRect Bounds);
            void SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string Name);
            void SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string Text);
            void SetFolderSettings(ref FolderSettings Settings);
            void Advise(IntPtr Events, out uint Cookie);
            void Unadvise(uint Cookie);
            void SetOptions(uint Options);
            void GetOptions(out uint Options);
            void BrowseToIDList(IntPtr IdList, uint Flags);
            void BrowseToObject([MarshalAs(UnmanagedType.IUnknown)] object Object, uint Flags);
            void FillFromObject([MarshalAs(UnmanagedType.IUnknown)] object Object, uint Flags);
            void RemoveAll();
            void GetCurrentView(ref Guid Interface, [MarshalAs(UnmanagedType.IUnknown)] out object View);
        }
        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellView
        {
            void GetWindow(out IntPtr Window);
            void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool Enter);
            [PreserveSig] int TranslateAccelerator(IntPtr Message);
            void EnableModeless([MarshalAs(UnmanagedType.Bool)] bool Enable);
            void UIActivate(uint State);
            void Refresh();
            void CreateViewWindow();
            void DestroyViewWindow();
            void GetCurrentInfo();
            void AddPropertySheetPages();
            void SaveViewState();
            void SelectItem();
            void GetItemObject(uint Scope, ref Guid Interface, [MarshalAs(UnmanagedType.IUnknown)] out object Item);
        }
        [ComImport, Guid("D8EC27BB-3F3B-4042-B10A-4ACFD924D453"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IColumnManager
        {
            void SetColumnInfo(ref PropertyKey Key, IntPtr Info);
            void GetColumnInfo(ref PropertyKey Key, IntPtr Info);
            void GetColumnCount(uint Flags, out uint Count);
            void GetColumns(uint Flags, [Out, MarshalAs(UnmanagedType.LPArray)] PropertyKey[] Keys, uint Count);
            void SetColumns([In, MarshalAs(UnmanagedType.LPArray)] PropertyKey[] Keys, uint Count);
        }
        [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr Menu, uint Index, uint First, uint Last, uint Flags);
            [PreserveSig] int InvokeCommand(IntPtr Info);
            [PreserveSig] int GetCommandString(nuint Command, uint Type, IntPtr Reserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder Name, uint Max);
        }
        [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu2
        {
            [PreserveSig] int QueryContextMenu(IntPtr Menu, uint Index, uint First, uint Last, uint Flags);
            [PreserveSig] int InvokeCommand(IntPtr Info);
            [PreserveSig] int GetCommandString(nuint Command, uint Type, IntPtr Reserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder Name, uint Max);
            [PreserveSig] int HandleMenuMsg(uint Message, IntPtr WParam, IntPtr LParam);
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string Name, IntPtr Context, out IntPtr List, uint Requested, out uint Attributes);
        [DllImport("propsys.dll", CharSet = CharSet.Unicode)] private static extern int PSGetPropertyKeyFromName(string Name, out PropertyKey Key);
        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr Menu);
        [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr Menu);
        [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr Menu, int Position);
        [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr Menu, int Position);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuString(IntPtr Menu, uint Item, StringBuilder Buffer, int Max, uint Flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMenuItemInfo(IntPtr Menu, uint Item, [MarshalAs(UnmanagedType.Bool)] bool ByPosition, ref MenuItemInfo Info);
    }
}
