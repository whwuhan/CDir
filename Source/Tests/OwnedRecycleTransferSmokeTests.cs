using System.IO;
using System.Runtime.InteropServices;

namespace CDir.Services;

internal sealed partial class ShellContextMenu
{
    internal sealed class NativeRecycleDrag(object NativeObject, uint Effects) : IDisposable
    {
        internal System.Windows.IDataObject Data { get; } = new System.Windows.DataObject(NativeObject);
        internal System.Windows.DragDropEffects AllowedEffects { get; } = (System.Windows.DragDropEffects)(Effects & 7);
        public void Dispose() => Marshal.ReleaseComObject(NativeObject);
    }

    internal static NativeRecycleDrag OpenOwnedRecycleDrag(params OwnedRecycleFixture[] Fixtures)
    {
        if (!AppMain.SmokeTest || Fixtures.Length == 0 || Fixtures.Any(Fixture => !Fixture.IsOwned(Fixture.Id, Fixture.BackingPath)))
            throw new UnauthorizedAccessException("Unowned native drag fixture.");
        var Memory = new List<IntPtr>();
        IShellFolder? Parent = null;
        try
        {
            foreach (var Fixture in Fixtures)
            {
                var Id = Marshal.AllocCoTaskMem(Fixture.Id.Length);
                Memory.Add(Id);
                Marshal.Copy(Fixture.Id, 0, Id, Fixture.Id.Length);
            }
            var FolderId = typeof(IShellFolder).GUID;
            Marshal.ThrowExceptionForHR(SHBindToParent(Memory[0], ref FolderId, out Parent, out _));
            var Children = Memory.Select(ILFindLastID).ToArray();
            uint Effects = 7;
            Marshal.ThrowExceptionForHR(Parent.GetAttributesOf((uint)Children.Length, Children, ref Effects));
            var DataId = typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID;
            Marshal.ThrowExceptionForHR(Parent.GetUIObjectOf(IntPtr.Zero, (uint)Children.Length, Children, ref DataId, IntPtr.Zero, out var Data));
            return new NativeRecycleDrag(Data, Effects);
        }
        finally
        {
            if (Parent is not null) Marshal.ReleaseComObject(Parent);
            foreach (var Id in Memory) Marshal.FreeCoTaskMem(Id);
        }
    }

    internal sealed record OwnedRecycleFixture(byte[] Id, string BackingPath, string Token)
    {
        internal bool Exists => File.Exists(this.BackingPath) || Directory.Exists(this.BackingPath);
        internal bool IsOwned(byte[] Identity, string? PathValue) => this.Id.AsSpan().SequenceEqual(Identity)
            && string.Equals(this.BackingPath, PathValue, StringComparison.OrdinalIgnoreCase)
            && (Directory.Exists(this.BackingPath)
                ? File.Exists(Path.Combine(this.BackingPath, ".CDirRecycleTestOwner")) && File.ReadAllText(Path.Combine(this.BackingPath, ".CDirRecycleTestOwner")) == this.Token
                : File.Exists(this.BackingPath) && File.ReadAllText(this.BackingPath) == this.Token);

        internal byte[] DragData(bool RelativeToBin)
        {
            if (!RelativeToBin) return ShellDropData.Create([this.Id]);
            var Offset = 0;
            var Last = 0;
            while (BitConverter.ToUInt16(this.Id, Offset) is > 0 and var Size) { Last = Offset; Offset += Size; }
            var Parent = new byte[Last + 2];
            this.Id.AsSpan(0, Last).CopyTo(Parent);
            return ShellDropData.Create([this.Id], Parent);
        }
    }

    internal static OwnedRecycleFixture RecycleOwnedItemForSmoke(string Source, string Token, Action<OwnedRecycleFixture> Register)
    {
        var ShortRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Build", "RecycleTestRuns"));
        var ShortOwned = Source.StartsWith(ShortRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(Path.GetDirectoryName(Source)!, ".CDirRecycleRunOwner"))
            && File.ReadAllText(Path.Combine(Path.GetDirectoryName(Source)!, ".CDirRecycleRunOwner")) == AppMain.SmokeRoot;
        if (!AppMain.SmokeTest || !(AppMain.IsInsideSmokeScope(Source) || ShortOwned)
            || (File.GetAttributes(Source) & FileAttributes.ReparsePoint) != 0
            || (Directory.Exists(Source) ? File.ReadAllText(Path.Combine(Source, ".CDirRecycleTestOwner")) : File.ReadAllText(Source)) != Token)
            throw new UnauthorizedAccessException("Unowned recycling fixture.");
        var InterfaceId = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Source, IntPtr.Zero, ref InterfaceId, out var SourceItem));
        IFileOperation? Operation = null;
        OwnedRecycleFixture? Fixture = null;
        Exception? CallbackFailure = null;
        var Sink = new ShellTransferProgressSink(default, (_, Created, Result) =>
        {
            try
            {
            Marshal.ThrowExceptionForHR(Result);
            // Children can report a successful deletion without a new Shell
            // item; the top-level folder supplies the recycled identity.
            if (Created == IntPtr.Zero) return;
            var Item = (IShellItem)Marshal.GetObjectForIUnknown(Created);
            IntPtr Identity = IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(SHGetIDListFromObject(Item, out Identity));
                var Bytes = new byte[checked((int)SmokeIdListSize(Identity))];
                Marshal.Copy(Identity, Bytes, 0, Bytes.Length);
                Marshal.ThrowExceptionForHR(Item.GetDisplayName(0x80058000, out var PathPointer));
                string Backing;
                try { Backing = Marshal.PtrToStringUni(PathPointer)!; }
                finally { Marshal.FreeCoTaskMem(PathPointer); }
                // PostDeleteItem returns the backing filesystem item. Bind its
                // exact name in the bin to obtain the namespace identity that
                // an Explorer drag exports, without enumerating other items.
                Bytes = RecycleNamespaceIdentity(Backing);
                Fixture = new(Bytes, Backing, Token);
                Register(Fixture);
                if (!Fixture.IsOwned(Bytes, Backing)) throw new IOException("Recycled test ownership changed.");
            }
            finally
            {
                if (Identity != IntPtr.Zero) Marshal.FreeCoTaskMem(Identity);
                Marshal.ReleaseComObject(Item);
            }
            }
            catch (Exception Error) { CallbackFailure = Error; throw; }
        });
        try
        {
            Operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            Marshal.ThrowExceptionForHR(Operation.SetOperationFlags(DeleteOperationFlags(false, true)));
            Marshal.ThrowExceptionForHR(Operation.DeleteItem(SourceItem, Sink));
            var Result = Operation.PerformOperations();
            if (CallbackFailure is not null) throw new InvalidOperationException("Recycle callback failed: " + CallbackFailure, CallbackFailure);
            Marshal.ThrowExceptionForHR(Operation.GetAnyOperationsAborted(out var Aborted));
            CompleteShellOperation(Result, Aborted);
            return Fixture ?? throw new IOException("Windows returned no recycled test identity.");
        }
        finally
        {
            if (Operation is not null) Marshal.ReleaseComObject(Operation);
            Marshal.ReleaseComObject(SourceItem);
        }
    }

    internal static void RemoveOwnedRecycleFixture(OwnedRecycleFixture Fixture)
    {
        if (!Fixture.Exists) return;
        if (!AppMain.SmokeTest || !Fixture.IsOwned(Fixture.Id, Fixture.BackingPath))
            throw new UnauthorizedAccessException("Unowned recycled fixture cleanup.");
        var Memory = Marshal.AllocCoTaskMem(Fixture.Id.Length);
        IShellItem? Item = null;
        IFileOperation? Operation = null;
        try
        {
            Marshal.Copy(Fixture.Id, 0, Memory, Fixture.Id.Length);
            var InterfaceId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(Memory, ref InterfaceId, out Item));
            Operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            Marshal.ThrowExceptionForHR(Operation.SetOperationFlags(DeleteOperationFlags(true, true)));
            Marshal.ThrowExceptionForHR(Operation.DeleteItem(Item, null));
            var Result = Operation.PerformOperations();
            Marshal.ThrowExceptionForHR(Operation.GetAnyOperationsAborted(out var Aborted));
            CompleteShellOperation(Result, Aborted);
            if (Fixture.Exists) throw new IOException("Owned recycled fixture cleanup did not finish.");
        }
        finally
        {
            if (Operation is not null) Marshal.ReleaseComObject(Operation);
            if (Item is not null) Marshal.ReleaseComObject(Item);
            Marshal.FreeCoTaskMem(Memory);
        }
    }

    private static byte[] RecycleNamespaceIdentity(string Backing)
    {
        var Root = ShellLocations.RecycleBinIdList();
        IShellItem? Item = null;
        IShellFolder? Folder = null;
        IntPtr Child = IntPtr.Zero, Absolute = IntPtr.Zero;
        try
        {
            var InterfaceId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(Root, ref InterfaceId, out Item));
            var FolderId = typeof(IShellFolder).GUID;
            var Handler = new Guid("3981E224-F559-11D3-8E3A-00C04F6837D5");
            Marshal.ThrowExceptionForHR(Item.BindToHandler(IntPtr.Zero, ref Handler, ref FolderId, out var Bound));
            Folder = (IShellFolder)Bound;
            uint Attributes = 0;
            Marshal.ThrowExceptionForHR(Folder.ParseDisplayName(IntPtr.Zero, IntPtr.Zero, Backing, out _, out Child, ref Attributes));
            Absolute = SmokeCombineIds(Root, Child);
            if (Absolute == IntPtr.Zero) throw new OutOfMemoryException();
            var Bytes = new byte[checked((int)SmokeIdListSize(Absolute))];
            Marshal.Copy(Absolute, Bytes, 0, Bytes.Length);
            return Bytes;
        }
        finally
        {
            if (Absolute != IntPtr.Zero) Marshal.FreeCoTaskMem(Absolute);
            if (Child != IntPtr.Zero) Marshal.FreeCoTaskMem(Child);
            if (Folder is not null) Marshal.ReleaseComObject(Folder);
            if (Item is not null) Marshal.ReleaseComObject(Item);
            Marshal.FreeCoTaskMem(Root);
        }
    }

    [DllImport("shell32.dll")] private static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object Item, out IntPtr Id);
    [DllImport("shell32.dll", EntryPoint = "ILGetSize")] private static extern uint SmokeIdListSize(IntPtr Id);
    [DllImport("shell32.dll", EntryPoint = "ILCombine")] private static extern IntPtr SmokeCombineIds(IntPtr Parent, IntPtr Child);
}
