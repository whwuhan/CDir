using System.Globalization;
using System.Runtime.InteropServices;
using CDir.Localization;
using CDir.Models;

namespace CDir.Services;

internal sealed partial class ShellContextMenu
{
    internal sealed record BackgroundViewSettings(FileViewMode View, string Sort, bool Descending, string Group, bool GroupDescending, bool CanSort = true, bool CanPaste = false);
    private readonly Dictionary<uint, string> BrowserCommands = [];
    private uint NextBrowserCommand = 0x8000;
    private void AddBrowserCommands(BackgroundViewSettings State)
    {
        // Explorer contributes these commands through its folder-view host. The
        // filesystem's IContextMenu supplies the remaining commands and extensions.
        var Locale = CultureInfo.GetCultureInfo((int)GetUserDefaultUILanguage()).Name;
        var Texts = LanguageService.GetTexts(Locale);
        string Text(string Key) => Texts.GetValueOrDefault(Key, LanguageService.EnglishText(Key));
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
        {
            var Id = this.NextBrowserCommand++;
            if (!InsertMenu(Menu, (uint)At, 0x400u | (Checked ? 8u : 0u) | (Enabled ? 0u : 1u), Id, Title)) throw new System.ComponentModel.Win32Exception();
            this.BrowserCommands.Add(Id, Verb);
            var Info = new MenuItemInfo { Size = (uint)Marshal.SizeOf<MenuItemInfo>(), Mask = 0x100, Type = 0x200 };
            SetMenuItemInfo(Menu, Id, false, ref Info);
        }
        void Separator(IntPtr Menu, int At) => InsertMenu(Menu, (uint)At, 0x400u | 0x800u, 0, null);
        var View = Popup(Text("ViewMenu"));
        var AccessKeys = "XRMNLDST";
        foreach (var Mode in Enum.GetValues<FileViewMode>()) Command(View, GetMenuItemCount(View), Text("View" + Mode)
            + (Locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? $"(&{AccessKeys[(int)Mode]})" : ""), "CDir.View." + Mode, Mode == State.View);
        var Keys = new[] { "Name", "Modified", "TypeName", "Size" };
        string Caption(string Key) => Text(Key switch { "Modified" => "ModifiedColumn", "TypeName" => "TypeColumn", "Size" => "SizeColumn", _ => "NameColumn" });
        var Sort = Popup(Text("SortBy"), State.CanSort);
        foreach (var Key in Keys) Command(Sort, GetMenuItemCount(Sort), Caption(Key), "CDir.Sort." + Key, Key == State.Sort);
        Separator(Sort, GetMenuItemCount(Sort));
        Command(Sort, GetMenuItemCount(Sort), Text("Ascending"), "CDir.Sort.Ascending", !State.Descending);
        Command(Sort, GetMenuItemCount(Sort), Text("Descending"), "CDir.Sort.Descending", State.Descending);
        var Group = Popup(Text("GroupBy"), State.CanSort);
        foreach (var Key in Keys) Command(Group, GetMenuItemCount(Group), Caption(Key), "CDir.Group." + Key, Key == State.Group);
        Separator(Group, GetMenuItemCount(Group));
        Command(Group, GetMenuItemCount(Group), Text("Ascending"), "CDir.Group.Ascending", !State.GroupDescending);
        Command(Group, GetMenuItemCount(Group), Text("Descending"), "CDir.Group.Descending", State.GroupDescending);
        Separator(Group, GetMenuItemCount(Group));
        Command(Group, GetMenuItemCount(Group), Text("GroupNone"), "CDir.Group.None", State.Group.Length == 0);
        Command(this.MenuHandle, Position++, Text("Refresh") + (Locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "(&E)" : ""), "CDir.Refresh");
        Separator(this.MenuHandle, Position++);
        Command(this.MenuHandle, Position++, Text("CustomizeFolder"), "CDir.Customize");
        Separator(this.MenuHandle, Position++);
        var Native = this.ReadEntries();
        if (!Native.Any(Entry => string.Equals(this.DescribeCommand(Entry.Id), "paste", StringComparison.OrdinalIgnoreCase)))
        {
            Command(this.MenuHandle, Position, Text("Paste") + (Locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "(&P)" : ""), "CDir.Paste", Enabled: State.CanPaste);
            Native = this.ReadEntries();
        }
        var HasTerminal = Native.Any(Entry => Entry.Text.Contains("终端") || Entry.Text.Contains("ターミナル") || Entry.Text.Contains("터미널") || Entry.Text.Contains("terminal", StringComparison.OrdinalIgnoreCase));
        if (!HasTerminal)
        {
            var Paste = Native.ToList().FindLastIndex(Entry => this.DescribeCommand(Entry.Id) is "paste" or "pastelink" or "CDir.Paste");
            Command(this.MenuHandle, Paste >= 0 ? Paste + 1 : Position, Text("OpenInTerminal"), "CDir.Terminal");
        }
    }
    [DllImport("kernel32.dll")] private static extern ushort GetUserDefaultUILanguage();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InsertMenu(IntPtr Menu, uint Position, uint Flags, nuint Id, string? Text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetMenuItemInfo(IntPtr Menu, uint Id, [MarshalAs(UnmanagedType.Bool)] bool ByPosition, ref MenuItemInfo Info);
}
