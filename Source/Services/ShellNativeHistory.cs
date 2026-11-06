using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CDir.Localization;

namespace CDir.Services;

internal sealed partial class ShellContextMenu
{
    internal static string CreateFolder(IntPtr Owner, string Parent, string Name, bool SuppressPrompts = false)
    {
        FileSystemService.ValidateName(Name);
        var Target = Path.Combine(Parent, Name);
        if (File.Exists(Target) || Directory.Exists(Target)) throw new IOException(LanguageService.Text("ServiceTargetConflict", Name));
        var Id = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Parent, IntPtr.Zero, ref Id, out var Folder));
        IFileOperation? Operation = null;
        try
        {
            Operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            Marshal.ThrowExceptionForHR(Operation.SetOwnerWindow(Owner));
            Marshal.ThrowExceptionForHR(Operation.SetOperationFlags(TransferOperationFlags(SuppressPrompts, RecordUndo: !AppMain.SmokeTest)));
            Marshal.ThrowExceptionForHR(Operation.NewItem(Folder, 0x10, Name, null, null));
            var Result = Operation.PerformOperations();
            Marshal.ThrowExceptionForHR(Operation.GetAnyOperationsAborted(out var Aborted));
            CompleteShellOperation(Result, Aborted);
            return Target;
        }
        finally
        {
            if (Operation is not null) Marshal.ReleaseComObject(Operation);
            Marshal.ReleaseComObject(Folder);
        }
    }

    internal static string HistoryVerb(bool Redo) => Redo ? "redo" : "undo";

    // Invoke Explorer's canonical background command, not a CDir backup replay.
    internal static void Replay(IntPtr Owner, string FolderPath, bool Redo)
    {
        if (AppMain.SmokeTest) throw new InvalidOperationException("UI checks must not modify the user's Windows undo history.");
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Shell operations require an STA thread.");
        var Id = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(FolderPath, IntPtr.Zero, ref Id, out var Folder));
        IContextMenu? Menu = null;
        var Handle = IntPtr.Zero;
        try
        {
            var MenuId = typeof(IContextMenu).GUID;
            var Handler = new Guid("3981E226-F559-11D3-8E3A-00C04F6837D5");
            Marshal.ThrowExceptionForHR(Folder.BindToHandler(IntPtr.Zero, ref Handler, ref MenuId, out var Bound));
            Menu = (IContextMenu)Bound;
            Handle = CreatePopupMenu();
            if (Handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            var Result = Menu.QueryContextMenu(Handle, 0, 1, 0x7fff, 4 | 0x1000);
            Marshal.ThrowExceptionForHR(Result);
            for (uint Index = 0; Index < (Result & 0xffff); Index++)
            {
                var Verb = new StringBuilder(256);
                if (Menu.GetCommandString(Index, 4, IntPtr.Zero, Verb, (uint)Verb.Capacity) < 0
                    || !string.Equals(Verb.ToString(), HistoryVerb(Redo), StringComparison.OrdinalIgnoreCase)
                    || (GetMenuState(Handle, Index + 1, 0) & 3) != 0) continue;
                var Info = new InvokeInfo { Size = Marshal.SizeOf<InvokeInfo>(), Mask = 0x4000 | 0x100,
                    Window = Owner, Verb = (IntPtr)Index, VerbW = (IntPtr)Index, Show = 1,
                    Directory = FolderPath, DirectoryW = FolderPath };
                // CMIC_MASK_NOASYNC keeps the operation result ahead of list refresh.
                CompleteShellOperation(Menu.InvokeCommand(ref Info), Aborted: false);
                return;
            }
            throw new IOException(LanguageService.Text(Redo ? "NothingToRedo" : "NothingToUndo"));
        }
        finally
        {
            if (Handle != IntPtr.Zero) DestroyMenu(Handle);
            if (Menu is not null) Marshal.ReleaseComObject(Menu);
            Marshal.ReleaseComObject(Folder);
        }
    }

    [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr Menu, uint Id, uint Flags);
}
