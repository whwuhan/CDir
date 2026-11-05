using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunCutClipboardSmokeChecksAsync(List<string> Passed, string Source, string ExistingTarget, string FilePath, string FolderPath)
    {
        ExplorerTab? Other = null;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Cut clipboard UI check failed: " + Name);
            Passed.Add(Name);
        }
        void SelectOriginals()
        {
            this.FileGrid.SelectedItems.Clear();
            foreach (var Entry in this.Entries.Where(Entry => SamePath(Entry.FullPath, FilePath) || SamePath(Entry.FullPath, FolderPath)))
                this.FileGrid.SelectedItems.Add(Entry);
        }
        async Task Press(Key Key, ModifierKeys Modifiers = ModifierKeys.Control, ExplorerTab? Page = null)
        {
            Page ??= this;
            var Input = new KeyEventArgs(new SmokeKeyboardDevice(Modifiers), PresentationSource.FromVisual(Page.FileGrid), 0, Key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Page.FileGrid.RaiseEvent(Input);
            Check(Input.Handled, "The routed clipboard gesture is handled: " + Modifiers + "+" + Key);
            var Deadline = Environment.TickCount64 + 15000;
            while ((Page.OperationBusy || Page.Loading) && Environment.TickCount64 < Deadline) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Page.UpdateLayout();
        }
        bool OriginalRowsFaded(ExplorerTab Page, bool Faded) => Page.Entries.All(Entry =>
        {
            var Expected = Faded && (SamePath(Entry.FullPath, FilePath) || SamePath(Entry.FullPath, FolderPath));
            return Entry.IsCut == Expected && (Page.FileGrid.ItemContainerGenerator.ContainerFromItem(Entry) is not DataGridRow Row
                || Math.Abs(Row.Opacity - (Expected ? 0.45 : 1)) < 0.001);
        });
        try
        {
            await this.NavigateAsync(Source, RecordHistory: false);
            SelectOriginals();
            await Press(Key.X);
            Check(PendingCutClipboard?.Paths.Count == 2 && OriginalRowsFaded(this, true)
                && File.Exists(FilePath) && Directory.Exists(FolderPath),
                "Ctrl+X fades only the selected file and folder without changing the filesystem");
            this.CaptureAddressBreadcrumbPreview("CutClipboardPreview.png");
            await this.RefreshAsync();
            this.UpdateLayout();
            Check(OriginalRowsFaded(this, true), "Refreshing the file list preserves pending cut feedback on regenerated entries");
            Other = this.HostWindow.AddTab();
            await Other.WaitUntilReadyAsync();
            Other.UpdateLayout();
            Check(OriginalRowsFaded(Other, true), "Another tab showing the source directory shares the pending cut feedback");
            await Press(Key.Escape, ModifierKeys.None, Other);
            Check(PendingCutClipboard is null && this.ReadClipboardFiles() is null
                && OriginalRowsFaded(this, false) && OriginalRowsFaded(Other, false)
                && File.Exists(FilePath) && Directory.Exists(FolderPath),
                "Escape in another tab cancels the owned cut clipboard and restores both tabs without moving sources");
            this.HostWindow.SelectTab(this);
            this.HostWindow.CloseTab(Other);
            Other = null;
            SelectOriginals();
            await Press(Key.X);
            await Press(Key.C);
            Check(PendingCutClipboard is null && OriginalRowsFaded(this, false) && this.ReadClipboardFiles() is not null,
                "Copying after cutting restores normal appearance and keeps a reusable copy payload");
            await Press(Key.X);
            this.WriteClipboardFiles(new DataObject(DataFormats.Text, "Newer clipboard text"));
            CheckCutClipboardVersion();
            Check(PendingCutClipboard is null && OriginalRowsFaded(this, false)
                && this.ReadClipboardFiles()?.GetData(DataFormats.Text) as string == "Newer clipboard text",
                "Replacing the clipboard clears stale cut feedback without clearing newer clipboard contents");
            SelectOriginals();
            await Press(Key.X);
            await this.NavigateAsync(ExistingTarget, RecordHistory: false);
            await Press(Key.V);
            Check(PendingCutClipboard is not null && File.Exists(FilePath) && Directory.Exists(FolderPath),
                "A failed cut paste into existing names preserves the pending cut and original sources");
            await this.NavigateAsync(Source, RecordHistory: false);
            Check(OriginalRowsFaded(this, true), "Failed cut paste leaves the source items faded for retry or cancellation");
            SelectOriginals();
            await Press(Key.Escape, ModifierKeys.None);
            Check(this.FileGrid.SelectedItems.Count == 2 && OriginalRowsFaded(this, false),
                "Escape cancels cut before clearing the ordinary file selection");
            var Target = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Source)!, "Cut Destination")).FullName;
            await this.NavigateAsync(Target, RecordHistory: false);
            await Press(Key.V);
            Check(!Directory.EnumerateFileSystemEntries(Target).Any() && File.Exists(FilePath),
                "Pasting after Escape cannot execute the canceled cut");
            await this.NavigateAsync(Source, RecordHistory: false);
            SelectOriginals();
            await Press(Key.X);
            await this.NavigateAsync(Target, RecordHistory: false);
            await Press(Key.V);
            Check(PendingCutClipboard is null && this.ReadClipboardFiles() is null
                && !File.Exists(FilePath) && !Directory.Exists(FolderPath)
                && File.ReadAllText(Path.Combine(Target, Path.GetFileName(FilePath))) == "Original file contents"
                && File.ReadAllText(Path.Combine(Target, Path.GetFileName(FolderPath), "Nested", "Data.txt")) == "Nested folder contents"
                && this.Entries.All(Entry => !Entry.IsCut) && this.StatusText.Text == LanguageService.Text("MoveComplete"),
                "Ctrl+X then Ctrl+V moves files and folders recursively and clears the completed cut state");
        }
        finally
        {
            this.HostWindow.SelectTab(this);
            if (Other is not null) this.HostWindow.CloseTab(Other);
            SetCutClipboard(null);
        }
    }
}
