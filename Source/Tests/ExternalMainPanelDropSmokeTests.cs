using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunExternalMainPanelDropSmokeChecksAsync(List<string> Passed, string Fixture)
    {
        var InitialPath = this.CurrentPath;
        var InitialCopy = this.Settings.CopyOnDrop;
        var Root = Directory.CreateDirectory(Path.Combine(Fixture, "External Main Panel")).FullName;
        void Check(bool Value, string Name)
        {
            if (!Value) throw new InvalidOperationException("External main-panel drop check failed: " + Name);
            Passed.Add(Name);
        }
        DragEventArgs Route(UIElement Surface, IDataObject Data, RoutedEvent Event, DragDropEffects Allowed = DragDropEffects.Copy | DragDropEffects.Move)
        {
            var Input = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [Data, DragDropKeyStates.ControlKey | DragDropKeyStates.ShiftKey, Allowed, Surface, new Point(15, 15)], culture: null)!;
            Input.RoutedEvent = Event;
            Surface.RaiseEvent(Input);
            return Input;
        }
        async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); this.UpdateLayout(); }
        try
        {
            foreach (var Copy in new[] { true, false })
            foreach (var IntoFolder in new[] { false, true })
            {
                var Case = Directory.CreateDirectory(Path.Combine(Root, (Copy ? "Copy" : "Move") + (IntoFolder ? "Folder" : "Background"))).FullName;
                var Source = Directory.CreateDirectory(Path.Combine(Case, "Source")).FullName;
                var Target = Directory.CreateDirectory(Path.Combine(Case, "Target")).FullName;
                var Destination = IntoFolder ? Directory.CreateDirectory(Path.Combine(Target, "Inbox")).FullName : Target;
                var FilePath = Path.Combine(Source, "文件 with spaces.txt");
                var Folder = Directory.CreateDirectory(Path.Combine(Source, "Folder 中文")).FullName;
                File.WriteAllText(FilePath, "External file");
                Directory.CreateDirectory(Path.Combine(Folder, "Nested"));
                File.WriteAllText(Path.Combine(Folder, "Nested", "Contents.txt"), "External folder");
                await this.NavigateAsync(Target, RecordHistory: false);
                this.ChangeCopyOnDrop(Copy);
                await Settle();
                UIElement Surface;
                if (IntoFolder)
                {
                    var Entry = this.Entries.Single(Entry => SamePath(Entry.FullPath, Destination));
                    var Row = (DataGridRow)this.FileGrid.ItemContainerGenerator.ContainerFromItem(Entry);
                    Surface = FindVisualChild<TextBlock>(Row)!;
                }
                else
                {
                    Surface = (UIElement)this.MainPanelContent.InputHitTest(new Point(150, this.MainPanelContent.ActualHeight * 0.75));
                    Check(Surface.AllowDrop, "The hit-tested empty main-panel surface accepts external OLE drops");
                }
                // This adapter rejects queries for any private CDir data format.
                var Data = new ExternalSidebarFileData([FilePath, Folder]);
                var Effect = Copy ? DragDropEffects.Copy : DragDropEffects.Move;
                var Enter = Route(Surface, Data, DragDrop.PreviewDragEnterEvent);
                var Over = Route(Surface, Data, DragDrop.PreviewDragOverEvent);
                Check(Enter.Handled && Over.Handled && Enter.Effects == Effect && Over.Effects == Effect
                    && this.FileDropHint.Text.Contains(Destination),
                    "External mixed-selection drag previews the configured " + Effect + " destination on " + (IntoFolder ? "a folder row" : "empty panel space"));
                var Drop = Route(Surface, Data, DragDrop.PreviewDropEvent);
                Check(Drop.Handled && Drop.Effects == Effect && !this.OperationBusy
                    && File.ReadAllText(Path.Combine(Destination, Path.GetFileName(FilePath))) == "External file"
                    && File.ReadAllText(Path.Combine(Destination, "Folder 中文", "Nested", "Contents.txt")) == "External folder"
                    && File.Exists(FilePath) == Copy && Directory.Exists(Folder) == Copy,
                    "External routed " + Effect + " transfers files and nested folders and returns the completed effect to the source manager");
                Check(this.FileDropHint.Visibility == Visibility.Collapsed && this.FileDropTargetHighlight.Visibility == Visibility.Collapsed
                    && this.Entries.Any(Entry => SamePath(Entry.FullPath, IntoFolder ? Destination : Path.Combine(Destination, Path.GetFileName(FilePath)))),
                    "External drop refreshes the panel and clears destination feedback before returning");
            }

            var LegacySource = Path.Combine(Root, "Legacy external.txt");
            File.WriteAllText(LegacySource, "Converted file path");
            var LegacyTarget = Directory.CreateDirectory(Path.Combine(Root, "Legacy Target")).FullName;
            await this.NavigateAsync(LegacyTarget, RecordHistory: false);
            this.ChangeCopyOnDrop(true);
            var LegacyData = new DataObject("FileNameW", new[] { LegacySource });
            Check(!LegacyData.GetDataPresent(DataFormats.FileDrop, false) && LegacyData.GetDataPresent(DataFormats.FileDrop, true),
                "The compatibility fixture advertises a convertible external filename rather than direct FileDrop data");
            var LegacyDrop = Route(this.FileGrid, LegacyData, DragDrop.PreviewDropEvent);
            Check(LegacyDrop.Effects == DragDropEffects.Copy && File.Exists(LegacySource)
                && File.ReadAllText(Path.Combine(LegacyTarget, Path.GetFileName(LegacySource))) == "Converted file path",
                "External managers providing convertible FileNameW data can copy into the main panel");

            var SameFolder = Directory.CreateDirectory(Path.Combine(Root, "Same Folder")).FullName;
            var Original = Path.Combine(SameFolder, "Original.txt");
            var OriginalFolder = Directory.CreateDirectory(Path.Combine(SameFolder, "Original Folder")).FullName;
            File.WriteAllText(Original, "Original content");
            File.WriteAllText(Path.Combine(OriginalFolder, "Nested.txt"), "Original nested content");
            await this.NavigateAsync(SameFolder, RecordHistory: false);
            await Settle();
            var Before = Directory.GetFileSystemEntries(SameFolder).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var SameData = new ExternalSidebarFileData([Original, OriginalFolder]);
            var InternalData = CreateFileTransferData(this.Entries.Where(Entry => SamePath(Entry.FullPath, Original) || SamePath(Entry.FullPath, OriginalFolder)));
            var DualData = new DataObject(DataFormats.FileDrop, new[] { Original, OriginalFolder });
            DualData.SetData(CDir.Services.ShellDropData.Format, new MemoryStream(CDir.Services.ShellDropData.Create([[4, 0, 1, 2, 0, 0]])));
            var TransferAdapter = this.TransferShellItems;
            var SameFolderCalls = 0;
            this.TransferShellItems = Request => { SameFolderCalls++; TransferAdapter(Request); };
            try
            {
                foreach (var Copy in new[] { true, false })
                foreach (var Data in new IDataObject[] { new ExternalSidebarFileData([Original]), new ExternalSidebarFileData([OriginalFolder]), SameData, InternalData, DualData })
                {
                    this.ChangeCopyOnDrop(Copy);
                    var Over = Route(this.FileGrid, Data, DragDrop.PreviewDragOverEvent);
                    var Drop = Route(this.FileGrid, Data, DragDrop.PreviewDropEvent);
                    Check(Over.Handled && Drop.Handled && Over.Effects == DragDropEffects.None && Drop.Effects == DragDropEffects.None
                        && Before.SetEquals(Directory.GetFileSystemEntries(SameFolder)) && SameFolderCalls == 0 && !this.OperationBusy
                        && File.ReadAllText(Original) == "Original content" && File.ReadAllText(Path.Combine(OriginalFolder, "Nested.txt")) == "Original nested content"
                        && this.FileDropHint.Visibility == Visibility.Collapsed && this.FileDropTargetHighlight.Visibility == Visibility.Collapsed,
                        "Same-folder " + (Copy ? "copy" : "move") + " drops do nothing for single files, folders, mixed selections, internal tabs and Shell data");
                }
                var Row = (DataGridRow)this.FileGrid.ItemContainerGenerator.ContainerFromItem(this.Entries.Single(Entry => SamePath(Entry.FullPath, Original)));
                this.ChangeCopyOnDrop(true);
                var OntoFile = Route(FindVisualChild<TextBlock>(Row)!, SameData, DragDrop.PreviewDropEvent);
                var RejectedText = Route(this.FileGrid, new DataObject(DataFormats.Text, Original), DragDrop.PreviewDropEvent);
                Check(OntoFile.Effects == DragDropEffects.None && RejectedText.Effects == DragDropEffects.None && SameFolderCalls == 0
                    && Before.SetEquals(Directory.GetFileSystemEntries(SameFolder)),
                    "Dropping back onto an existing file row or plain text leaves the source directory unchanged");
            }
            finally { this.TransferShellItems = TransferAdapter; }

        }
        finally
        {
            this.ClearFileTransferFeedback();
            this.ChangeCopyOnDrop(InitialCopy);
            await this.NavigateAsync(InitialPath, RecordHistory: false);
        }
    }
}
