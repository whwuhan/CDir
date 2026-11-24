using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using CDir.Localization;
using CDir.Models;

namespace CDir.Services;

internal sealed partial class ShellContextMenu
{
    internal sealed record BackgroundViewSettings(FileViewMode View, string Sort, bool Descending, string Group, bool GroupDescending, bool CanSort = true, bool CanPaste = false,
        IReadOnlyList<(string Key, string Header)>? GroupColumns = null);
    // Explorer's folder-view background menu is shell32 MENU 215. Windows removes
    // entries that do not apply and inserts the view's columns into Sort/Group By;
    // reading the template keeps every caption in the user's Windows language.
    private const uint RefreshCommand = 28931, CustomizeCommand = 28722, PasteCommand = 28698, PasteShortcutCommand = 28700;
    private const uint SortAscendingCommand = 31488, SortDescendingCommand = 31489, ExpandGroupsCommand = 28755, CollapseGroupsCommand = 28756;
    private const uint GroupNoneCommand = 30210, GroupAscendingCommand = 30208, GroupDescendingCommand = 30209, ChooseColumnsCommand = 28723;
    private const uint NameColumnString = 52160;
    private static readonly uint[] ViewCommands = [28749, 28751, 28750, 28752, 28753, 28747, 28748, 28754]; // FileViewMode order
    private sealed record MenuTemplate(string? Title, IReadOnlyDictionary<uint, string> Items);
    private sealed record ExplorerMenuTemplate(MenuTemplate Background, MenuTemplate View, MenuTemplate Sort, MenuTemplate Group, string? NameColumn);
    private static readonly Lazy<ExplorerMenuTemplate> ExplorerTexts = new(ReadExplorerTexts);

    private static ExplorerMenuTemplate ReadExplorerTexts()
    {
        var Empty = new MenuTemplate(null, new Dictionary<uint, string>());
        MenuTemplate Background = Empty, View = Empty, Sort = Empty, Group = Empty;
        string? NameColumn = null;
        var Module = GetModuleHandle("shell32.dll");
        if (Module == IntPtr.Zero) return new(Empty, Empty, Empty, Empty, null);
        var Buffer = new System.Text.StringBuilder(260);
        if (LoadString(Module, NameColumnString, Buffer, Buffer.Capacity) > 0 && Buffer.Length > 0) NameColumn = Buffer.ToString();
        var Template = LoadMenu(Module, (IntPtr)215);
        if (Template == IntPtr.Zero) return new(Empty, Empty, Empty, Empty, NameColumn);
        try
        {
            string Read(IntPtr Menu, int Index)
            {
                Buffer.Clear();
                return GetMenuString(Menu, (uint)Index, Buffer, Buffer.Capacity, 0x400) > 0 ? Buffer.ToString() : "";
            }
            void Visit(IntPtr Menu)
            {
                for (var Index = 0; Index < GetMenuItemCount(Menu); Index++)
                {
                    var Child = GetSubMenu(Menu, Index);
                    if (Child == IntPtr.Zero) continue;
                    var Items = new Dictionary<uint, string>();
                    for (var Position = 0; Position < GetMenuItemCount(Child); Position++)
                        if (GetSubMenu(Child, Position) == IntPtr.Zero && GetMenuItemID(Child, Position) is var Id and not uint.MaxValue and not 0)
                            Items.TryAdd(Id, Read(Child, Position));
                    var Found = new MenuTemplate(Read(Menu, Index) is { Length: > 0 } Title ? Title : null, Items);
                    if (Background == Empty && Items.ContainsKey(RefreshCommand)) Background = Found;
                    else if (View == Empty && Items.ContainsKey(ViewCommands[0])) View = Found;
                    else if (Sort == Empty && Items.ContainsKey(SortAscendingCommand)) Sort = Found;
                    else if (Group == Empty && Items.ContainsKey(GroupNoneCommand)) Group = Found;
                    Visit(Child);
                }
            }
            Visit(Template);
        }
        finally { DestroyMenu(Template); }
        return new(Background, View, Sort, Group, NameColumn);
    }

    // Explorer's Paste Shortcut creates one shortcut per clipboard item, named by the
    // Shell (for example "Alpha.txt - Shortcut.lnk" in the user's Windows language).
    internal static string[] PasteShortcut(string Folder, string[] Paths)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Shell shortcuts require an STA thread.");
        var Created = new List<string>();
        foreach (var Source in Paths)
        {
            var Name = new System.Text.StringBuilder(32768);
            if (!SHGetNewLinkInfo(Source, Folder, Name, out var MustCopy, 0))
                throw new System.IO.IOException(CDir.Localization.LanguageService.Text("PasteShortcutUnavailable"));
            var Target = Name.ToString();
            if (MustCopy) { File.Copy(Source, Target, overwrite: false); Created.Add(Target); continue; }
            Marshal.ThrowExceptionForHR(SHParseDisplayName(Source, IntPtr.Zero, out var Id, 0, out _));
            object? Link = null;
            try
            {
                Link = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!)!;
                var Shell = (IShellLinkW)Link;
                Marshal.ThrowExceptionForHR(Shell.SetIDList(Id));
                if (File.Exists(Source) && Path.GetDirectoryName(Source) is { } Directory) Marshal.ThrowExceptionForHR(Shell.SetWorkingDirectory(Directory));
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)Link).Save(Target, true);
                Created.Add(Target);
            }
            finally
            {
                if (Link is not null) Marshal.ReleaseComObject(Link);
                Marshal.FreeCoTaskMem(Id);
            }
            SHChangeNotify(0x2, 0x5, Target, null); // SHCNE_CREATE, SHCNF_PATHW
        }
        return Created.ToArray();
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath(IntPtr File, int Max, IntPtr FindData, uint Flags);
        [PreserveSig] int GetIDList(out IntPtr Id);
        [PreserveSig] int SetIDList(IntPtr Id);
        [PreserveSig] int GetDescription(IntPtr Name, int Max);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string Name);
        [PreserveSig] int GetWorkingDirectory(IntPtr Directory, int Max);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string Directory);
    }
    [DllImport("shell32.dll", EntryPoint = "SHGetNewLinkInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SHGetNewLinkInfo(string LinkTo, string Directory, System.Text.StringBuilder Name, [MarshalAs(UnmanagedType.Bool)] out bool MustCopy, uint Flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(int Event, uint Flags, string Item1, string? Item2);
    private readonly Dictionary<uint, string> BrowserCommands = [];
    private uint NextBrowserCommand = 0x8000;
    private void AddBrowserCommands(BackgroundViewSettings State)
    {
        // Explorer contributes these commands through its folder-view host. The
        // filesystem's IContextMenu supplies the remaining commands and extensions.
        var Locale = CultureInfo.GetCultureInfo((int)GetUserDefaultUILanguage()).Name;
        var Texts = LanguageService.GetTexts(Locale);
        string Text(string Key) => Texts.GetValueOrDefault(Key, LanguageService.EnglishText(Key));
        var Explorer = ExplorerTexts.Value;
        string From(MenuTemplate Template, uint Id, string Fallback) => Template.Items.GetValueOrDefault(Id) is { Length: > 0 } Caption ? Caption : Fallback;
        var Position = 0;
        IntPtr Popup(string Title, bool Enabled = true)
        {
            var Child = CreatePopupMenu();
            if (Child == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            if (!InsertMenu(this.MenuHandle, (uint)Position++, 0x400u | 0x10u | (Enabled ? 0u : 1u), (nuint)Child, Title))
            { DestroyMenu(Child); throw new System.ComponentModel.Win32Exception(); }
            return Child;
        }
        void Command(IntPtr Menu, int At, string Title, string Verb, bool Checked = false, bool Enabled = true)
            => this.InsertBrowserCommand(Menu, At, Title, Verb, Checked, Enabled);
        void Separator(IntPtr Menu, int At) => InsertMenu(Menu, (uint)At, 0x400u | 0x800u, 0, null);
        var Grouped = State.Group.Length > 0;
        var View = Popup(Explorer.View.Title ?? Text("ViewMenu"));
        foreach (var Mode in Enum.GetValues<FileViewMode>())
            Command(View, GetMenuItemCount(View), From(Explorer.View, ViewCommands[(int)Mode], Text("View" + Mode)), "CDir.View." + Mode, Mode == State.View);
        // Columns are the view's visible Details columns, in display order, as in Explorer.
        var Keys = new[] { "Name", "Modified", "TypeName", "Size" };
        string Caption(string Key) => Text(Key switch { "Modified" => "ModifiedColumn", "TypeName" => "TypeColumn", "Size" => "SizeColumn", _ => "NameColumn" });
        var Columns = State.GroupColumns ?? Keys.Select(Key => (Key, Caption(Key))).ToArray();
        string ColumnCaption(string Key, string Header) => Key == "Name" ? Explorer.NameColumn ?? Header : Header;
        var Sort = Popup(Explorer.Sort.Title ?? Text("SortBy"), State.CanSort);
        foreach (var (Key, Header) in Columns) Command(Sort, GetMenuItemCount(Sort), ColumnCaption(Key, Header), "CDir.Sort." + Key, Key == State.Sort);
        Separator(Sort, GetMenuItemCount(Sort));
        Command(Sort, GetMenuItemCount(Sort), From(Explorer.Sort, SortAscendingCommand, Text("Ascending")), "CDir.Sort.Ascending", !State.Descending);
        Command(Sort, GetMenuItemCount(Sort), From(Explorer.Sort, SortDescendingCommand, Text("Descending")), "CDir.Sort.Descending", State.Descending);
        // "(None)" appears only while grouped; direction is disabled while ungrouped.
        var Group = Popup(Explorer.Group.Title ?? Text("GroupBy"), State.CanSort);
        foreach (var (Key, Header) in Columns) Command(Group, GetMenuItemCount(Group), ColumnCaption(Key, Header), "CDir.Group." + Key, Key == State.Group);
        if (Grouped) Command(Group, GetMenuItemCount(Group), From(Explorer.Group, GroupNoneCommand, Text("GroupNone")), "CDir.Group.None");
        Separator(Group, GetMenuItemCount(Group));
        Command(Group, GetMenuItemCount(Group), From(Explorer.Group, GroupAscendingCommand, Text("Ascending")), "CDir.Group.Ascending", Grouped && !State.GroupDescending, Grouped);
        Command(Group, GetMenuItemCount(Group), From(Explorer.Group, GroupDescendingCommand, Text("Descending")), "CDir.Group.Descending", Grouped && State.GroupDescending, Grouped);
        Separator(Group, GetMenuItemCount(Group));
        Command(Group, GetMenuItemCount(Group), From(Explorer.Group, ChooseColumnsCommand, Text("GroupMoreColumns")), "CDir.Group.More");
        Command(this.MenuHandle, Position++, From(Explorer.Background, RefreshCommand, Text("Refresh")), "CDir.Refresh");
        Separator(this.MenuHandle, Position++);
        Command(this.MenuHandle, Position++, From(Explorer.Background, CustomizeCommand, Text("CustomizeFolder")), "CDir.Customize");
        Separator(this.MenuHandle, Position++);
        var Native = this.ReadEntries();
        bool HasVerb(string Verb) => Native.Any(Entry => string.Equals(this.DescribeCommand(Entry.Id), Verb, StringComparison.OrdinalIgnoreCase));
        // Explorer always lists Paste (disabled when unavailable) and adds Paste Shortcut
        // only while the clipboard holds files.
        if (!HasVerb("paste")) Command(this.MenuHandle, Position, From(Explorer.Background, PasteCommand, Text("Paste")), "CDir.Paste", Enabled: State.CanPaste);
        if (State.CanPaste && !HasVerb("pastelink"))
            Command(this.MenuHandle, Position + 1, From(Explorer.Background, PasteShortcutCommand, Text("PasteShortcut")), "CDir.PasteShortcut");
    }

    private void InsertBrowserCommand(IntPtr Menu, int At, string Title, string Verb, bool Checked = false, bool Enabled = true)
    {
        var Id = this.NextBrowserCommand++;
        if (!InsertMenu(Menu, (uint)At, 0x400u | (Checked ? 8u : 0u) | (Enabled ? 0u : 1u), Id, Title)) throw new System.ComponentModel.Win32Exception();
        this.BrowserCommands.Add(Id, Verb);
        var Info = new MenuItemInfo { Size = (uint)Marshal.SizeOf<MenuItemInfo>(), Mask = 0x100, Type = 0x200 };
        SetMenuItemInfo(Menu, Id, false, ref Info);
    }

    // Windows lists Undo/Redo directly after Paste and Paste Shortcut in background menus.
    internal void AddHistoryCommands(IReadOnlyList<HistoryCommand> History)
    {
        this.CheckAlive();
        if (History.Count == 0) return;
        var After = this.ReadEntries().ToList().FindLastIndex(Entry => this.DescribeCommand(Entry.Id) is "paste" or "pastelink" or "CDir.Paste" or "CDir.PasteShortcut");
        if (After < 0) return;
        foreach (var Item in History) this.InsertBrowserCommand(this.MenuHandle, ++After, Item.Text, Item.Redo ? "CDir.Redo" : "CDir.Undo", Enabled: Item.Enabled);
    }    [DllImport("kernel32.dll")] private static extern ushort GetUserDefaultUILanguage();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string Name);
    [DllImport("user32.dll", EntryPoint = "LoadStringW", CharSet = CharSet.Unicode)] private static extern int LoadString(IntPtr Module, uint Id, System.Text.StringBuilder Buffer, int Max);
    [DllImport("user32.dll", EntryPoint = "LoadMenuW", CharSet = CharSet.Unicode)] private static extern IntPtr LoadMenu(IntPtr Module, IntPtr Name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InsertMenu(IntPtr Menu, uint Position, uint Flags, nuint Id, string? Text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetMenuItemInfo(IntPtr Menu, uint Id, [MarshalAs(UnmanagedType.Bool)] bool ByPosition, ref MenuItemInfo Info);
}
