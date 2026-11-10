using System.IO;
using System.Reflection;
using System.Windows;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunNativeTransferConflictSmokeChecksAsync(List<string> Passed, string Fixture)
    {
        var InitialHistory = this.Files.OperationHistory;
        var InitialPath = this.CurrentPath;
        var Owner = new System.Windows.Interop.WindowInteropHelper(this.HostWindow).Handle;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Native conflict check failed: " + Name);
            Passed.Add(Name);
        }
        try
        {
            Check((ShellContextMenu.TransferOperationFlags(false) & 0x20000040u) == 0x20000040u
                && (ShellContextMenu.TransferOperationFlags(false) & (0x4u | 0x10u | 0x400u)) == 0
                && (ShellContextMenu.TransferOperationFlags(true) & 0x20000040u) == 0
                && (ShellContextMenu.TransferOperationFlags(false, RecordUndo: false) & 0x20000040u) == 0,
                "Production enables Windows progress confirmation and undo while quiet checks never modify the user's global undo stack");
            foreach (var Move in new[] { false, true })
            {
                var Root = Directory.CreateDirectory(Path.Combine(Fixture, Move ? "NativeMoveConflict" : "NativeCopyConflict")).FullName;
                var Source = Directory.CreateDirectory(Path.Combine(Root, "Source")).FullName;
                var Target = Directory.CreateDirectory(Path.Combine(Root, "Target")).FullName;
                File.WriteAllText(Path.Combine(Source, "Same.txt"), "New content");
                File.WriteAllText(Path.Combine(Target, "Same.txt"), "Old content");
                var SourceFolder = Directory.CreateDirectory(Path.Combine(Source, "Shared")).FullName;
                var TargetFolder = Directory.CreateDirectory(Path.Combine(Target, "Shared")).FullName;
                File.WriteAllText(Path.Combine(SourceFolder, "Replace.txt"), "New nested content");
                File.WriteAllText(Path.Combine(SourceFolder, "Added.txt"), "New item");
                File.WriteAllText(Path.Combine(TargetFolder, "Replace.txt"), "Old nested content");
                File.WriteAllText(Path.Combine(TargetFolder, "Retained.txt"), "Keep target-only item");
                var History = new FileOperationHistory(Path.Combine(Root, "History"));
                this.Files.OperationHistory = History;
                await this.NavigateAsync(Target, RecordHistory: false);
                this.ReplaceTransferConflictsInSmoke = true;
                Check(await this.TransferFilesAsync([Path.Combine(Source, "Same.txt"), SourceFolder], Target, Move)
                    && File.ReadAllText(Path.Combine(Target, "Same.txt")) == "New content"
                    && File.ReadAllText(Path.Combine(TargetFolder, "Replace.txt")) == "New nested content"
                    && File.ReadAllText(Path.Combine(TargetFolder, "Retained.txt")) == "Keep target-only item"
                    && File.Exists(Path.Combine(TargetFolder, "Added.txt")) && this.Entries.Any(Entry => Entry.Name == "Shared"),
                    "Windows " + (Move ? "move" : "copy") + " replaces chosen conflicts merges folders and refreshes the visible destination");
                Check(Move ? !Directory.EnumerateFileSystemEntries(Source).Any() : Directory.GetFileSystemEntries(Source).Length == 2,
                    "Windows conflict handling preserves or removes each original according to copy or move mode");
                Check(!History.CanUndo && !History.CanRedo && !Directory.EnumerateDirectories(History.JournalRoot, "Data", SearchOption.AllDirectories).Any(),
                    "Native transfers write audit metadata without app-authored content backups or undo transactions");
            }
            // No source keystrokes or pointer movement: routed cancellation inside
            // the target must clear the tint even when its coordinates remain inside.
            var FeedbackSource = Path.Combine(Fixture, "Feedback.txt");
            File.WriteAllText(FeedbackSource, "Owned drag feedback");
            var Data = new DataObject(DataFormats.FileDrop, new[] { FeedbackSource });
            this.ShowFileTransferFeedback(Data, this.FileGrid, false, DragDropEffects.All);
            var Leave = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [Data, DragDropKeyStates.LeftMouseButton, DragDropEffects.All, this.MainPanelContent, new Point(30, 50)], culture: null)!;
            this.FileTransferDragLeave(this.MainPanelContent, Leave);
            Check(this.FileDropTargetHighlight.Visibility == Visibility.Collapsed && this.FileDropHint.Visibility == Visibility.Collapsed,
                "Cancelling a drag inside the main panel clears the stale blue target tint and Move To hint");
            this.ShowFileTransferFeedback(Data, this.FileGrid, false, DragDropEffects.All);
            var Escape = new System.Windows.Input.KeyEventArgs(this.UnmodifiedSmokeKeyboard,
                PresentationSource.FromVisual(this.FileGrid), 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            this.FileGrid.RaiseEvent(Escape);
            Check(Escape.Handled && this.FileDropTargetHighlight.Visibility == Visibility.Collapsed && this.FileDropHint.Visibility == Visibility.Collapsed,
                "Escape clears stale main-panel drag feedback as well as the ordinary selection");
        }
        finally
        {
            this.ReplaceTransferConflictsInSmoke = false;
            this.Files.OperationHistory = InitialHistory;
            await this.NavigateAsync(InitialPath, RecordHistory: false);
        }
    }
}
