using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunShellCutClipboardSmokeChecksAsync(List<string> Passed, string Folder, string[] Files, string Subfolder)
    {
        var Writer = this.WriteClipboardFiles;
        var Reader = this.ReadClipboardFiles;
        var Clearer = this.ClearClipboardFiles;
        var VersionReader = this.ReadClipboardVersion;
        IDataObject? Data = null;
        uint Version = 1;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Shell cut clipboard check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Cut(string[] Paths)
        {
            using var Menu = new ShellContextMenu(new WindowInteropHelper(this.HostWindow).Handle, Folder, Paths);
            var Item = Menu.ReadEntries().Single(Entry => Menu.DescribeCommand(Entry.Id)?.Equals("cut", StringComparison.OrdinalIgnoreCase) == true);
            await this.ExecuteShellCommandAsync(Menu, Item.Id);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        try
        {
            // Keep the user's actual Windows clipboard untouched while dispatching real Shell command IDs.
            this.WriteClipboardFiles = Value => { Data = Value; Version++; };
            this.ReadClipboardFiles = () => Data;
            this.ClearClipboardFiles = () => { Data = null; Version++; };
            this.ReadClipboardVersion = () => Version;
            foreach (var Paths in new[] { new[] { Files[0] }, new[] { Subfolder }, Files, new[] { Files[0], Subfolder } })
            {
                this.SelectPath(Files[1]);
                var Navigation = this.NavigationVersion;
                await Cut(Paths);
                Check(Data?.GetData(DataFormats.FileDrop) is string[] CutPaths && CutPaths.SequenceEqual(Paths)
                    && Data.GetData("Preferred DropEffect") is MemoryStream Effect && BitConverter.ToInt32(Effect.ToArray()) == 2
                    && this.Entries.All(Entry => Entry.IsCut == Paths.Contains(Entry.FullPath, StringComparer.OrdinalIgnoreCase))
                    && this.Entries.Where(Entry => Entry.IsCut).All(Entry =>
                        this.FileGrid.ItemContainerGenerator.ContainerFromItem(Entry) is not DataGridRow Row || Row.Opacity == 0.45)
                    && this.NavigationVersion == Navigation,
                    "The native Cut command fades its captured file/folder selection immediately without refreshing or cutting unrelated selected items: " + Paths.Length);
                foreach (var Entry in this.Entries.Where(Entry => Entry.IsCut))
                {
                    this.FileGrid.ScrollIntoView(Entry);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    this.UpdateLayout();
                    Check(this.FileGrid.ItemContainerGenerator.ContainerFromItem(Entry) is DataGridRow { Opacity: 0.45 },
                        "A cut item keeps its faded appearance when scrolled back into view");
                }
                this.CaptureAddressBreadcrumbPreview("ShellCutClipboardPreview.png");
                await this.RefreshAsync();
                Check(this.Entries.All(Entry => Entry.IsCut == Paths.Contains(Entry.FullPath, StringComparer.OrdinalIgnoreCase)),
                    "A list refresh preserves the pending native-menu cut feedback");
                var Escape = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(this.FileGrid), 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                this.FileGrid.RaiseEvent(Escape);
                Check(Escape.Handled && Data is null && this.Entries.All(Entry => !Entry.IsCut)
                    && Paths.All(Path => File.Exists(Path) || Directory.Exists(Path)),
                    "Escape cancels a native-menu cut and restores normal appearance without moving its sources");
            }
            var Destination = Directory.CreateDirectory(Path.Combine(Folder, "Cut Destination")).FullName;
            await Cut([Files[1]]);
            await this.NavigateAsync(Destination, RecordHistory: false);
            await this.PasteAsync();
            Check(Data is null && PendingCutClipboard is null && !File.Exists(Files[1])
                && File.ReadAllText(Path.Combine(Destination, Path.GetFileName(Files[1]))) == "second unchanged file"
                && this.Entries.All(Entry => !Entry.IsCut),
                "Pasting a native-menu cut completes the move and clears its clipboard and faded state");
            File.Move(Path.Combine(Destination, Path.GetFileName(Files[1])), Files[1]);
        }
        finally
        {
            SetCutClipboard(null);
            this.WriteClipboardFiles = Writer;
            this.ReadClipboardFiles = Reader;
            this.ClearClipboardFiles = Clearer;
            this.ReadClipboardVersion = VersionReader;
            await this.NavigateAsync(Folder, RecordHistory: false);
        }
    }
}
