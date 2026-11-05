using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunFileHistorySmokeChecksAsync(List<string> Passed)
    {
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "History-" + Guid.NewGuid().ToString("N"))).FullName;
        var OriginalPath = this.CurrentPath;
        var OriginalHistory = HistoryInstance;
        var OriginalFilesHistory = this.Files.OperationHistory;
        var OriginalDelete = this.DeleteShellItems;
        var History = new FileOperationHistory(Path.Combine(Fixture, "Journal"));
        var Files = new FileSystemService { OperationHistory = History };
        var Work = Directory.CreateDirectory(Path.Combine(Fixture, "Work 中文")).FullName;
        var Target = Directory.CreateDirectory(Path.Combine(Fixture, "Target")).FullName;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("File history check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Run(Action Action) => await Task.Run(Action);
        async Task MustRefuse(Action Action, string Name)
        {
            var Refused = false;
            try { await Run(Action); } catch (IOException) { Refused = true; }
            Check(Refused, Name);
        }
        try
        {
            HistoryInstance = History;
            this.Files.OperationHistory = History;
            await this.NavigateAsync(Work, RecordHistory: false);
            await Run(() => Files.CreateFolder(Work, "Created"));
            var Created = Path.Combine(Work, "Created");
            var UndoKey = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control), PresentationSource.FromVisual(this.FileGrid), 0, Key.Z)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            this.FileGrid.RaiseEvent(UndoKey);
            var Deadline = Environment.TickCount64 + 10000;
            while ((Directory.Exists(Created) || this.OperationBusy || this.Loading) && Environment.TickCount64 < Deadline) await Task.Delay(20);
            Check(UndoKey.Handled && !Directory.Exists(Created) && History.CanRedo, "Fixed Ctrl+Z undoes a new folder through the actual window key route");
            await this.HandleShortcutAsync(Key.Y, ModifierKeys.Control, false, true);
            Check(Directory.Exists(Created) && History.CanUndo, "Fixed Ctrl+Y restores the created folder");
            Check(!this.ChangeShortcut("Undo", "F8") && !this.ChangeShortcut("Redo", "")
                && KeyboardShortcut.Resolve(new Dictionary<string, string> { ["Undo"] = "F8", ["Redo"] = "" }) is var Bindings
                && Bindings["Undo"] == "Ctrl+Z" && Bindings["Redo"] == "Ctrl+Y",
                "Undo and redo cannot be rebound or disabled by preferences or manually edited settings");
            Check(!CanHandleShortcut(KeyboardShortcut.All.Single(Item => Item.Id == "Undo"), Key.Z, ModifierKeys.Control, true, false)
                && !CanHandleShortcut(KeyboardShortcut.All.Single(Item => Item.Id == "Redo"), Key.Y, ModifierKeys.Control, true, false),
                "Text fields retain native editing undo and redo");

            await Run(() => Files.Rename(Created, "Renamed"));
            await Run(() => History.Replay(false));
            Check(Directory.Exists(Created) && !Directory.Exists(Path.Combine(Work, "Renamed")), "Rename undo restores the original path");
            await Run(() => History.Replay(true));
            var Renamed = Path.Combine(Work, "Renamed");
            File.WriteAllText(Path.Combine(Renamed, "Later.txt"), "External edit");
            await MustRefuse(() => History.Replay(false), "Undo refuses a folder changed after renaming");
            Check(File.ReadAllText(Path.Combine(Renamed, "Later.txt")) == "External edit" && History.CanUndo,
                "A refused undo preserves later contents and the history stack");
            File.Delete(Path.Combine(Renamed, "Later.txt"));
            Directory.CreateDirectory(Created);
            await MustRefuse(() => History.Replay(false), "Undo refuses an occupied original path");
            Directory.Delete(Created);
            await Run(() => History.Replay(false));

            var First = Path.Combine(Work, "First.txt");
            var Second = Path.Combine(Work, "Second.txt");
            File.WriteAllText(First, "First original");
            File.WriteAllText(Second, "Second original");
            await Run(() => Files.CopyItems([First, Second], Target, false));
            await Run(() => History.Replay(false));
            Check(!File.Exists(Path.Combine(Target, "First.txt")) && !File.Exists(Path.Combine(Target, "Second.txt"))
                && File.Exists(First) && File.Exists(Second), "One undo removes the entire copied batch and preserves its sources");
            File.WriteAllText(First, "Source changed later");
            await Run(() => History.Replay(true));
            Check(File.ReadAllText(Path.Combine(Target, "First.txt")) == "First original", "Copy redo restores the original copied bytes despite later source edits");
            File.WriteAllText(Path.Combine(Target, "Second.txt"), "Later output edit");
            await MustRefuse(() => History.Replay(false), "Batch undo preflights every output before moving any file");
            Check(File.Exists(Path.Combine(Target, "First.txt")), "A changed batch member leaves every other output intact");
            File.WriteAllText(Path.Combine(Target, "Second.txt"), "Second original");
            // Restore the original output timestamp as well as its contents before proceeding.
            File.SetLastWriteTimeUtc(Path.Combine(Target, "Second.txt"), File.GetLastWriteTimeUtc(Second));
            await Run(() => History.Replay(false));
            await Run(() => Files.CopyItems([First, Second], Target, true));
            Check(!History.CanRedo && !File.Exists(First), "A new file operation clears the abandoned redo branch");
            await Run(() => History.Replay(false));
            Check(File.ReadAllText(First) == "Source changed later" && File.Exists(Second), "Move undo restores a complete batch to its original directory");
            await Run(() => History.Replay(true));
            Check(File.Exists(Path.Combine(Target, "First.txt")) && !File.Exists(First), "Move redo returns the batch to its destination");
            await Run(() => Files.RenameItems([Path.Combine(Target, "First.txt"), Path.Combine(Target, "Second.txt")], "Batch.txt"));
            await Run(() => History.Replay(false));
            Check(File.Exists(Path.Combine(Target, "First.txt")) && File.Exists(Path.Combine(Target, "Second.txt")), "One undo reverses a native-menu style multi-file rename");

            this.DeleteShellItems = (Paths, Permanent) =>
            {
                foreach (var PathValue in Paths)
                {
                    if (!AppMain.IsInsideSmokeScope(PathValue)) throw new InvalidOperationException("Unowned deletion fixture.");
                    if (Directory.Exists(PathValue)) Directory.Delete(PathValue, true); else File.Delete(PathValue);
                }
            };
            var Deleted = Path.Combine(Target, "First.txt");
            Check(await this.DeleteShellSelectionAsync([Deleted, Created], true) && !File.Exists(Deleted) && !Directory.Exists(Created),
                "Deletion backs up an owned file and folder before the deletion worker runs");
            await Run(() => History.Replay(false));
            Check(File.ReadAllText(Deleted) == "Source changed later" && Directory.Exists(Created), "Delete undo restores file contents and an empty folder");
            await Run(() => History.Replay(true));
            Check(!File.Exists(Deleted) && !Directory.Exists(Created), "Delete redo uses the original deletion semantics without touching the real recycle bin in tests");
            await Run(() => History.Replay(false));
            this.DeleteShellItems = (Paths, _) => { File.Delete(Paths[0]); throw new OperationCanceledException(); };
            Check(!await this.DeleteShellSelectionAsync([Deleted, Path.Combine(Target, "Second.txt")], false), "A partially completed deletion reports cancellation");
            await Run(() => History.Replay(false));
            Check(File.Exists(Deleted) && File.Exists(Path.Combine(Target, "Second.txt")), "The completed prefix of a cancelled deletion remains undoable");
            var RedoCancelled = false;
            try { await Run(() => History.Replay(true)); } catch (OperationCanceledException) { RedoCancelled = true; }
            Check(RedoCancelled && !File.Exists(Deleted) && History.CanUndo,
                "Redo cancellation after deleting an item still records the completed change");
            await Run(() => History.Replay(false));
            Check(File.Exists(Deleted), "The completed item from a cancelled redo can immediately be restored");
            var Journal = await Task.Run(() => new FileOperationHistory(History.JournalRoot).ReadJournal());
            Check(Journal.Contains("Completed") && Journal.Contains("Cancelled") && Journal.Contains("Redo") && Journal.Contains("Undo")
                && Journal.Contains("First.txt"), "Timestamped file paths results failures undo and redo remain readable from disk in a new history instance");
            foreach (var Surface in new UIElement[] { this.SearchBox, this.SidebarPanel, this.HostWindow.FileMenuButton, this.AddressBar })
            {
                var Escape = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(Surface), 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                Surface.RaiseEvent(Escape);
                Check(Escape.Handled && ReferenceEquals(FocusManager.GetFocusedElement(this), this.FileGrid),
                    "Escape returns logical focus to the main file panel from " + Surface.GetType().Name);
            }
            var Dialog = new FileHistoryWindow(this.HostWindow, Journal);
            Dialog.Show();
            var DialogEscape = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(Dialog), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Dialog.RaiseEvent(DialogEscape);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(!Dialog.IsVisible && ReferenceEquals(FocusManager.GetFocusedElement(this), this.FileGrid), "Escape closes operation history and returns focus to the file panel");
            var DetachedMenu = new ContextMenu();
            DetachedMenu.RaiseEvent(new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(this.FileGrid), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(DetachedMenu.PlacementTarget is null, "Escape safely ignores menus without a main-window placement target");
            var Version = typeof(ExplorerTab).Assembly.GetName().Version!;
            Check(this.HostWindow.TitleVersion.Text == $"v{Version.Major}.{Version.Minor}.{Version.Build}", "The title-bar release version matches the tested application assembly");
        }
        finally
        {
            this.DeleteShellItems = OriginalDelete;
            this.Files.OperationHistory = OriginalFilesHistory;
            HistoryInstance = OriginalHistory;
            await this.NavigateAsync(OriginalPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unowned history fixture cleanup.");
            Directory.Delete(Fixture, true);
        }
    }
}
