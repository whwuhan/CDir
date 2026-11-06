using System.IO;
using System.Reflection;
using System.Windows;
using CDir.Localization;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunRecycleBinTransferSmokeChecksAsync(List<string> Passed, string FixtureRoot)
    {
        var InitialPath = this.CurrentPath;
        var InitialCopy = this.Settings.CopyOnDrop;
        var InitialOwner = ShellContextMenu.ValidateSmokeShellSource;
        var Fixtures = new List<ShellContextMenu.OwnedRecycleFixture>();
        var Root = Directory.CreateDirectory(Path.Combine(FixtureRoot, "Recycled Transfers")).FullName;
        // Recycle Bin rejects overlong original paths. Keep only the owned
        // recycling inputs short; destinations and all other data stay in scope.
        var ShortParent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Build", "RecycleTestRuns"));
        var ShortRoot = Directory.CreateDirectory(Path.Combine(ShortParent, Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(ShortRoot, ".CDirRecycleRunOwner"), AppMain.SmokeRoot);
        void Check(bool Value, string Name)
        {
            if (!Value) throw new InvalidOperationException("Recycle transfer check failed: " + Name + "; " + this.StatusText.Text);
            Passed.Add(Name);
        }
        DragEventArgs Drop(IDataObject Data, DragDropEffects Allowed = DragDropEffects.Copy | DragDropEffects.Move)
        {
            var Input = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [Data, DragDropKeyStates.None, Allowed, this.MainPanelContent, new Point(30, 300)], culture: null)!;
            Input.RoutedEvent = DragDrop.PreviewDropEvent;
            this.MainPanelContent.RaiseEvent(Input);
            return Input;
        }
        try
        {
            ShellContextMenu.ValidateSmokeShellSource = (Id, PathValue) => Fixtures.Any(Fixture => Fixture.IsOwned(Id, PathValue));
            foreach (var Copy in new[] { true, false })
            foreach (var IsFolder in new[] { true, false })
            {
                var Name = "Deleted " + (Copy ? "Copy" : "Move") + (IsFolder ? " Folder" : " File") + " 中文 " + Guid.NewGuid().ToString("N") + (IsFolder ? "" : ".txt");
                var Source = Path.Combine(ShortRoot, Name);
                var Token = Guid.NewGuid().ToString("N");
                if (IsFolder)
                {
                    Directory.CreateDirectory(Path.Combine(Source, "Nested"));
                    File.WriteAllText(Path.Combine(Source, ".CDirRecycleTestOwner"), Token);
                    File.WriteAllText(Path.Combine(Source, "Nested", "Contents.txt"), "Owned recycled contents");
                }
                else File.WriteAllText(Source, Token);
                ShellContextMenu.OwnedRecycleFixture? Recycled = null;
                Check(await this.RunOperationAsync("Preparing an owned recycled folder…", () =>
                {
                    try { Recycled = ShellContextMenu.RecycleOwnedItemForSmoke(Source, Token, Fixtures.Add); }
                    catch (Exception Error) { AppMain.RecordSmokeProgress("Owned recycling failure: " + Error); throw; }
                }, "Owned item recycled", RequiresSta: true) && Recycled is not null && !Directory.Exists(Source) && !File.Exists(Source),
                    "Windows supplies the actual recycled identity directly for an owned test item without enumerating the user's bin");
                var Destination = Directory.CreateDirectory(Path.Combine(Root, (Copy ? "Copy" : "Move") + (IsFolder ? " Folder Target" : " File Target"))).FullName;
                await this.NavigateAsync(Destination, RecordHistory: false);
                this.ChangeCopyOnDrop(Copy);
                var Buffer = Recycled!.DragData(RelativeToBin: Copy);
                using var Stream = new MemoryStream(Buffer);
                Stream.Position = 3;
                var Data = new DataObject();
                Data.SetData(ShellDropData.Format, Stream);
                // A real Recycle Bin may advertise both formats. Its namespace
                // identity must win over this internal/missing backing path.
                Data.SetData(DataFormats.FileDrop, new[] { Path.Combine(Root, "Missing $R backing path") });
                var Plan = this.PlanFileTransfer(Data, Destination, DragDropEffects.All);
                Check(Plan?.ShellItemIds is { Length: 1 } && Plan.Paths.Length == 0 && Stream.Position == 3,
                    "Recycle Bin Shell identities take priority over file paths and reading preserves the foreign stream position");
                using var Explorer = ShellContextMenu.OpenOwnedRecycleDrag(Recycled);
                AppMain.RecordSmokeProgress("Native Recycle Bin source: effects=" + Explorer.AllowedEffects + "; formats=" + string.Join(",", Explorer.Data.GetFormats(false)));
                var NativeValue = Explorer.Data.GetData(ShellDropData.Format, false);
                AppMain.RecordSmokeProgress("Native CIDA value: " + (NativeValue?.GetType().FullName ?? "null"));
                Check(this.PlanFileTransfer(Explorer.Data, Destination, Explorer.AllowedEffects) is not null,
                    "The Windows Recycle Bin's own COM data object and real source capabilities can negotiate the configured operation");
                Check(Explorer.AllowedEffects == DragDropEffects.Move && this.Settings.CopyOnDrop == Copy,
                    "Windows Recycle Bin advertises Move only and negotiating it does not rewrite the user's copy preference");
                var ActualPlan = this.PlanFileTransfer(Explorer.Data, Destination, Explorer.AllowedEffects)!;
                Check(!ActualPlan.Copy && this.ShowFileTransferFeedback(Explorer.Data, this.FileGrid, false, Explorer.AllowedEffects) == DragDropEffects.Move
                    && this.FileDropHint.Text == LanguageService.Text("FileDropMoveTo", Destination),
                    "Both preference settings show a truthful Move preview for the Windows Recycle Bin's move-only source");
                var Result = Drop(Explorer.Data, Explorer.AllowedEffects);
                var Target = Path.Combine(Destination, Name);
                Check(Result.Effects == DragDropEffects.Move
                    && (IsFolder ? File.ReadAllText(Path.Combine(Target, "Nested", "Contents.txt")) == "Owned recycled contents"
                        && File.ReadAllText(Path.Combine(Target, ".CDirRecycleTestOwner")) == Token : File.ReadAllText(Target) == Token)
                    && this.Entries.Any(Entry => SamePath(Entry.FullPath, Target)) && !this.OperationBusy,
                    "An Explorer-native recycled " + (IsFolder ? "folder" : "file") + " moves through the routed drop handler with its original name and contents even when copy is configured");
                Check(!Recycled.Exists,
                    "The move-only Windows source is removed from the bin after its folder reaches the destination");
                static int Feedback(IDataObject Object, string Format) => Object.GetData(Format, false) switch
                {
                    MemoryStream Value when Value.Length >= 4 => BitConverter.ToInt32(Value.ToArray()),
                    byte[] Value when Value.Length >= 4 => BitConverter.ToInt32(Value),
                    _ => -1
                };
                Check(Feedback(Explorer.Data, "Performed DropEffect") == 0 && Feedback(Explorer.Data, "Logical Performed DropEffect") == 2,
                    "Explorer receives optimized-move feedback so it never tries deleting the recycled source a second time");
            }
            var Mixed = new List<ShellContextMenu.OwnedRecycleFixture>();
            var MixedNames = new[] { "Mixed Folder " + Guid.NewGuid().ToString("N"), "Mixed File " + Guid.NewGuid().ToString("N") + ".txt" };
            foreach (var Name in MixedNames)
            {
                var Source = Path.Combine(ShortRoot, Name);
                var Token = Guid.NewGuid().ToString("N");
                if (Name.EndsWith(".txt", StringComparison.Ordinal)) File.WriteAllText(Source, Token);
                else { Directory.CreateDirectory(Source); File.WriteAllText(Path.Combine(Source, ".CDirRecycleTestOwner"), Token); }
                Check(await this.RunOperationAsync("Preparing an owned mixed selection…", () =>
                {
                    var Item = ShellContextMenu.RecycleOwnedItemForSmoke(Source, Token, Fixtures.Add);
                    Mixed.Add(Item);
                }, "Owned item ready", RequiresSta: true), "A mixed Windows Recycle Bin selection contains only owned test items");
            }
            var MixedTarget = Directory.CreateDirectory(Path.Combine(Root, "Mixed Target")).FullName;
            await this.NavigateAsync(MixedTarget, RecordHistory: false);
            this.ChangeCopyOnDrop(true);
            using (var MixedDrag = ShellContextMenu.OpenOwnedRecycleDrag(Mixed.ToArray()))
            {
                var Result = Drop(MixedDrag.Data, MixedDrag.AllowedEffects);
                Check(MixedDrag.AllowedEffects == DragDropEffects.Move && Result.Effects == DragDropEffects.Move
                    && File.ReadAllText(Path.Combine(MixedTarget, MixedNames[1])) == Mixed[1].Token
                    && File.ReadAllText(Path.Combine(MixedTarget, MixedNames[0], ".CDirRecycleTestOwner")) == Mixed[0].Token
                    && this.Entries.Count == 2 && Mixed.All(Item => !Item.Exists) && this.Settings.CopyOnDrop,
                    "A native Windows Recycle Bin file-and-folder batch moves completely even with copying enabled, without changing the saved preference");
            }
            var Ordinary = Directory.CreateDirectory(Path.Combine(Root, "Ordinary Source")).FullName;
            File.WriteAllText(Path.Combine(Ordinary, "Keep.txt"), "Ordinary source");
            CDir.Models.FileEntry? OrdinaryEntry = null;
            Check(await this.RunOperationAsync("Reading an owned ordinary Shell item…", () =>
                OrdinaryEntry = RecycleBinService.ReadEntries(Ordinary).Single(), "Owned ordinary item ready", RequiresSta: true),
                "The capability-boundary fixture is an ordinary owned filesystem Shell item");
            var OrdinaryData = new DataObject(ShellDropData.Format, new MemoryStream(ShellDropData.Create([OrdinaryEntry!.ShellId!])));
            var MixedIdentityData = new DataObject(ShellDropData.Format, new MemoryStream(ShellDropData.Create([Fixtures[0].Id, OrdinaryEntry.ShellId!])));
            Check(this.PlanFileTransfer(OrdinaryData, MixedTarget, DragDropEffects.Move) is null
                && this.PlanFileTransfer(MixedIdentityData, MixedTarget, DragDropEffects.Move) is null
                && File.ReadAllText(OrdinaryEntry.FullPath) == "Ordinary source" && this.Settings.CopyOnDrop,
                "The move-only exception applies only to an entirely recycled selection and never silently moves ordinary files when copying is configured");
            foreach (var Bytes in new[] { Array.Empty<byte>(), new byte[12], new byte[] { 255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0 } })
            {
                var Data = new DataObject(ShellDropData.Format, new MemoryStream(Bytes));
                Check(this.PlanFileTransfer(Data, this.CurrentPath, DragDropEffects.All) is null,
                    "Malformed or empty Shell IDList data is rejected before any file operation");
            }
            var Valid = ShellDropData.Create([Fixtures[0].Id]);
            var BadOffset = (byte[])Valid.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(BadOffset.AsSpan(8), 0);
            foreach (var Invalid in new[] { BadOffset, Valid[..^1] })
                Check(this.PlanFileTransfer(new DataObject(ShellDropData.Format, new MemoryStream(Invalid)), this.CurrentPath, DragDropEffects.All) is null,
                    "Out-of-bounds and unterminated Shell identities are rejected without passing unsafe memory to Windows");
            Check(ShellDropData.Parse(ShellDropData.Create([Fixtures[0].Id, Fixtures[0].Id])).Length == 1,
                "Duplicate Shell drag identities are transferred only once");
            var CancelTarget = Directory.CreateDirectory(Path.Combine(Root, "Cancelled Target")).FullName;
            var CancelSource = Directory.CreateDirectory(Path.Combine(ShortRoot, "Cancelled " + Guid.NewGuid().ToString("N"))).FullName;
            var CancelToken = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(CancelSource, ".CDirRecycleTestOwner"), CancelToken);
            ShellContextMenu.OwnedRecycleFixture? CancelFixture = null;
            Check(await this.RunOperationAsync("Preparing a cancelled transfer…", () =>
                CancelFixture = ShellContextMenu.RecycleOwnedItemForSmoke(CancelSource, CancelToken, Fixtures.Add),
                "Owned cancellation fixture ready", RequiresSta: true), "Cancellation uses a separately owned live recycled item");
            var InitialTransfer = this.TransferShellItems;
            try
            {
                this.ChangeCopyOnDrop(true);
                this.TransferShellItems = _ => throw new OperationCanceledException();
                using var CancelDrag = ShellContextMenu.OpenOwnedRecycleDrag(CancelFixture!);
                var Result = Drop(CancelDrag.Data, CancelDrag.AllowedEffects);
                Check(Result.Effects == DragDropEffects.None && !this.OperationBusy
                    && this.StatusText.Text == LanguageService.Text("OperationCancelled") && Directory.Exists(CancelFixture!.BackingPath)
                    && !Directory.EnumerateFileSystemEntries(CancelTarget).Any(),
                    "Cancelling a recycled transfer clears status and never reports a successful move to the source");
            }
            finally { this.TransferShellItems = InitialTransfer; }
        }
        finally
        {
            try
            {
                await this.RunOperationAsync("Cleaning owned recycled fixtures…", () =>
                {
                    foreach (var Fixture in Fixtures) ShellContextMenu.RemoveOwnedRecycleFixture(Fixture);
                }, "Owned recycled fixtures removed", RequiresSta: true);
                if (Fixtures.Any(Fixture => Fixture.Exists)) throw new IOException("An owned recycled fixture remains.");
            }
            finally
            {
                ShellContextMenu.ValidateSmokeShellSource = InitialOwner;
                this.ChangeCopyOnDrop(InitialCopy);
                await this.NavigateAsync(InitialPath, RecordHistory: false);
                if (!ShortRoot.StartsWith(ShortParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || (File.GetAttributes(ShortRoot) & FileAttributes.ReparsePoint) != 0
                    || File.ReadAllText(Path.Combine(ShortRoot, ".CDirRecycleRunOwner")) != AppMain.SmokeRoot)
                    throw new UnauthorizedAccessException("Unowned short recycling test root.");
                Directory.Delete(ShortRoot, true);
            }
        }
    }
}
