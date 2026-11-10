using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CDir.Services;

internal sealed partial class ShellContextMenu
{
    internal static string[] CheckOwnedHistoryMenu(IntPtr Owner, string Folder, string CopiedFile)
    {
        if (!AppMain.SmokeTest || !AppMain.IsInsideSmokeScope(CopiedFile)) throw new InvalidOperationException("Owned UI-check files are required.");
        var Holding = CopiedFile + ".held";
        var Menu = new OwnedHistoryMenu(() => File.Exists(CopiedFile));
        var Passed = new List<string>();
        foreach (var Redo in new[] { false, true, false, true })
        {
            uint? Invoked = null;
            ReplayHistoryMenu(Owner, Folder, Redo, Menu, Offset =>
            {
                Invoked = Offset;
                if (Redo) File.Move(Holding, CopiedFile); else File.Move(CopiedFile, Holding);
            });
            if (Invoked != (Redo ? 7u : 3u) || File.Exists(CopiedFile) != Redo)
                throw new InvalidOperationException("The Shell history menu selected the wrong command.");
            Passed.Add("The real history menu dispatcher " + (Redo ? "redoes" : "undoes") + " an owned same-folder copy using its command offset");
        }
        var Rejected = false;
        try { ReplayHistoryMenu(Owner, Folder, true, Menu, _ => throw new InvalidOperationException("A disabled command was invoked.")); }
        catch (IOException) { Rejected = true; }
        if (!Rejected) throw new InvalidOperationException("Disabled redo was not rejected.");
        Passed.Add("A disabled native history command cannot replay an unrelated operation");
        return Passed.ToArray();
    }

    private sealed class OwnedHistoryMenu(Func<bool> CanUndo) : IContextMenu
    {
        public int QueryContextMenu(IntPtr Menu, uint Index, uint First, uint Last, uint Flags)
        {
            AppendHistoryItem(Menu, CanUndo() ? 0u : 1u, First + 3, "撤销复制");
            AppendHistoryItem(Menu, CanUndo() ? 1u : 0u, First + 7, "重做复制");
            return 8;
        }
        public int InvokeCommand(ref InvokeInfo Info) => throw new InvalidOperationException("Checks must use owned commands.");
        public int GetCommandString(nuint Id, uint Flags, IntPtr Reserved, StringBuilder Name, uint Max)
        {
            if (Id is not (3 or 7)) return unchecked((int)0x80070057);
            Name.Append(Id == 3 ? "undo" : "redo");
            return 0;
        }
    }
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendHistoryItem(IntPtr Menu, uint Flags, nuint Id, string Text);
}
