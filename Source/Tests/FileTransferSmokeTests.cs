using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunFileTransferSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialCopy = this.Settings.CopyOnDrop;
        var Fixture = Path.Combine(AppMain.SmokeRoot!, "FileTransfer-" + Guid.NewGuid().ToString("N"));
        var Source = Directory.CreateDirectory(Path.Combine(Fixture, "Source")).FullName;
        var Target = Directory.CreateDirectory(Path.Combine(Fixture, "Target")).FullName;
        var Inbox = Directory.CreateDirectory(Path.Combine(Target, "Inbox")).FullName;
        var SettingsFile = Path.Combine(Fixture, "Settings.json");
        MainWindow? SourceWindow = null;
        MainWindow? RestoredWindow = null;
        ExplorerTab? Other = null;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("File transfer UI check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); this.UpdateLayout(); }
        static DataObject FileData(params string[] Paths) { var Data = new DataObject(); Data.SetData(DataFormats.FileDrop, Paths); return Data; }
        try
        {
            File.WriteAllText(Path.Combine(Source, "A.txt"), "Original file");
            Directory.CreateDirectory(Path.Combine(Source, "Folder", "Nested"));
            File.WriteAllText(Path.Combine(Source, "Folder", "Nested", "B.txt"), "Nested file");
            File.WriteAllText(SettingsFile, System.Text.Json.JsonSerializer.Serialize(new AppSettings { LastPath = Source }));
            SourceWindow = new MainWindow(SettingsFile) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            SourceWindow.Show();
            var SourcePage = SourceWindow.ActiveTab!;
            await SourcePage.WaitUntilReadyAsync();
            SourcePage.FileGrid.SelectAll();
            var Data = CreateFileTransferData(SourcePage.SelectedEntries());
            Check(Data.GetData(DataFormats.FileDrop, false) is string[] { Length: 2 }
                && SourcePage.SelectedEntries().Any(Entry => Entry.IsDirectory) && SourcePage.SelectedEntries().Any(Entry => !Entry.IsDirectory),
                "CDir publishes standard Windows file-drop data for mixed file and folder selections");
            var SourceFile = SourcePage.Entries.Single(Entry => !Entry.IsDirectory);
            var SourceRow = (DataGridRow)SourcePage.FileGrid.ItemContainerGenerator.ContainerFromItem(SourceFile);
            Check(SourcePage.PrepareMainPanelFolderDrag(SourceRow, new Point(), ModifierKeys.None, 1)
                && SourcePage.MainPanelDragCandidate == SourceFile && SourcePage.CreateMainPanelFolderDrag() is null,
                "A selected file starts a transferable group while mixed selections remain separate from category shortcuts");
            SourcePage.MainPanelDragCandidate = null;
            SourcePage.MainPanelDeferredSelection = null;
            await this.NavigateAsync(Target, RecordHistory: false);
            await Settle();
            Check(!new AppSettings().CopyOnDrop && !ReadSettings(SettingsFile).CopyOnDrop, "Fresh settings default to moving dropped files");
            var Legacy = Path.Combine(Fixture, "Legacy.json");
            File.WriteAllText(Legacy, "{}");
            Check(!ReadSettings(Legacy).CopyOnDrop, "Existing settings without a drag mode default to move");
            File.WriteAllText(Legacy, "{\"CopyOnDrop\":true}");
            Check(ReadSettings(Legacy).CopyOnDrop, "An explicitly saved copy preference is preserved");
            this.ChangeCopyOnDrop(true);
            Check(this.FileTransferTarget(this.FileGrid, false)?.Path == Target
                && this.ShowFileTransferFeedback(Data, this.FileGrid, false, DragDropEffects.Copy | DragDropEffects.Move) == DragDropEffects.Copy
                && this.FileDropTargetHighlight.IsVisible && this.FileDropHint.Text.Contains(Target),
                "Dropping on the main background targets the current directory and previews Copy");
            var CopyPlan = this.PlanFileTransfer(Data, Target, DragDropEffects.Copy | DragDropEffects.Move)!;
            Check(await this.ExecuteFileTransferAsync(CopyPlan) && File.Exists(Path.Combine(Source, "A.txt"))
                && File.ReadAllText(Path.Combine(Target, "A.txt")) == "Original file"
                && File.Exists(Path.Combine(Source, "Folder", "Nested", "B.txt"))
                && File.ReadAllText(Path.Combine(Target, "Folder", "Nested", "B.txt")) == "Nested file",
                "A different CDir window copies a mixed selection recursively without removing its sources");
            Other = this.HostWindow.AddTab();
            await Other.WaitUntilReadyAsync();
            this.HostWindow.SelectTab(this);
            await this.RunPreferencesSmokeChecksAsync(Passed, CopyOnDrop: false);
            await Settle();
            Check(!this.Settings.CopyOnDrop && !Other.Settings.CopyOnDrop && this.SaveSettings(SettingsFile) && !ReadSettings(SettingsFile).CopyOnDrop,
                "The General checkbox saves move mode and synchronizes existing tabs");
            RestoredWindow = new MainWindow(SettingsFile) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            RestoredWindow.Show();
            await RestoredWindow.ActiveTab!.WaitUntilReadyAsync();
            Check(!RestoredWindow.ActiveTab.Settings.CopyOnDrop, "The drag mode survives reopening a CDir window");
            RestoredWindow.RequestExit();
            RestoredWindow = null;
            this.HostWindow.SelectTab(this);
            var InboxEntry = this.Entries.Single(Entry => SamePath(Entry.FullPath, Inbox));
            var InboxRow = (DataGridRow)this.FileGrid.ItemContainerGenerator.ContainerFromItem(InboxEntry);
            Check(this.FileTransferTarget(InboxRow, false)?.Path == Inbox, "A folder row targets that folder rather than its parent directory");
            var MoveFile = Path.Combine(Source, "Move.txt");
            var MoveFolder = Directory.CreateDirectory(Path.Combine(Source, "Move Folder")).FullName;
            File.WriteAllText(MoveFile, "Move contents");
            File.WriteAllText(Path.Combine(MoveFolder, "Nested.txt"), "Moved folder");
            var MoveData = FileData(MoveFile, MoveFolder);
            var MovePlan = this.PlanFileTransfer(MoveData, Inbox, DragDropEffects.Copy | DragDropEffects.Move)!;
            Check(MovePlan.Effect == DragDropEffects.Move && await this.ExecuteFileTransferAsync(MovePlan)
                && !File.Exists(MoveFile) && !Directory.Exists(MoveFolder)
                && File.ReadAllText(Path.Combine(Inbox, "Move.txt")) == "Move contents"
                && File.Exists(Path.Combine(Inbox, "Move Folder", "Nested.txt")),
                "Unchecked drag-copy mode moves files and directories to the hovered folder");
            Check(this.PlanFileTransfer(FileData(Path.Combine(Source, "A.txt")), Inbox, DragDropEffects.Copy) is null
                && this.PlanFileTransfer(FileData(Path.Combine(Source, "A.txt")), Source, DragDropEffects.All) is null
                && this.PlanFileTransfer(FileData(Path.Combine(Source, "Folder")), Path.Combine(Source, "Folder", "Nested"), DragDropEffects.All) is null
                && this.PlanFileTransfer(FileData("relative.txt"), Target, DragDropEffects.All) is null
                && this.PlanFileTransfer(new DataObject(DataFormats.Text, "Not files"), Target, DragDropEffects.All) is null,
                "Drag negotiation rejects unavailable modes same-directory drops recursive targets and non-file data");
            this.OperationBusy = true;
            Check(this.PlanFileTransfer(Data, Inbox, DragDropEffects.All) is null, "An ongoing file operation prevents a second transfer");
            this.OperationBusy = false;
            this.Loading = true;
            Check(this.PlanFileTransfer(Data, Inbox, DragDropEffects.All) is null, "Navigation loading prevents a transfer to a changing target");
            this.Loading = false;
            var CollisionPlan = this.PlanFileTransfer(FileData(Path.Combine(Source, "A.txt")), Target, DragDropEffects.All)!;
            Check(!await this.ExecuteFileTransferAsync(CollisionPlan)
                && File.ReadAllText(Path.Combine(Source, "A.txt")) == "Original file"
                && File.ReadAllText(Path.Combine(Target, "A.txt")) == "Original file",
                "A failed move never overwrites an existing name or removes its source");
            Check(this.FileTransferTarget(new Button { DataContext = new CategoryFolder { FullPath = Inbox } }, true)?.Path == Inbox,
                "A sidebar folder shortcut resolves its actual filesystem destination");
            await this.RunPreferencesSmokeChecksAsync(Passed, CopyOnDrop: true);
            var NativeFile = Path.Combine(Source, "Native Drop.txt");
            File.WriteAllText(NativeFile, "Windows file-drop format");
            var NativeData = FileData(NativeFile);
            var Input = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [NativeData, DragDropKeyStates.None, DragDropEffects.Copy | DragDropEffects.Move, this.MainPanelContent, new Point(30, 300)], culture: null)!;
            Input.RoutedEvent = DragDrop.PreviewDropEvent;
            this.MainPanelContent.RaiseEvent(Input);
            Check(Input.Handled && Input.Effects == DragDropEffects.Copy && File.Exists(NativeFile)
                && File.ReadAllText(Path.Combine(Target, "Native Drop.txt")) == "Windows file-drop format"
                && !this.OperationBusy && this.FileDropHint.Visibility == Visibility.Collapsed,
                "The real routed Drop handler completes its asynchronous operation before reporting success to the Windows drag source");
            await this.RunSidebarDropRoutingSmokeChecksAsync(Passed);
        }
        finally
        {
            this.OperationBusy = false;
            this.Loading = false;
            this.ClearFileTransferFeedback();
            SourceWindow?.RequestExit();
            RestoredWindow?.RequestExit();
            this.HostWindow.SelectTab(this);
            if (Other is not null) this.HostWindow.CloseTab(Other);
            this.ChangeCopyOnDrop(InitialCopy);
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned file-transfer fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
