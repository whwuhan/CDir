using System.IO;
using System.Reflection;
using System.Windows;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunBatchFileTransferSmokeChecksAsync(List<string> Passed, string Fixture, ExplorerTab SourcePage, ExplorerTab DestinationPage)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Batch transfer check failed: " + Name);
            Passed.Add(Name);
        }
        DragEventArgs Drop(IDataObject Data)
        {
            var Input = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [Data, DragDropKeyStates.None, DragDropEffects.Copy | DragDropEffects.Move, this.MainPanelContent, new Point(30, 300)], culture: null)!;
            Input.RoutedEvent = DragDrop.PreviewDropEvent;
            this.MainPanelContent.RaiseEvent(Input);
            return Input;
        }
        foreach (var Copy in new[] { true, false })
        {
            var Source = Directory.CreateDirectory(Path.Combine(Fixture, Copy ? "BatchCopySource" : "BatchMoveSource")).FullName;
            var Target = Directory.CreateDirectory(Path.Combine(Fixture, Copy ? "BatchCopyTarget" : "BatchMoveTarget")).FullName;
            foreach (var Name in new[] { "First.txt", "第二个.txt" }) File.WriteAllText(Path.Combine(Source, Name), Name);
            foreach (var Name in new[] { "First Folder", "第二个文件夹" })
            {
                var Nested = Directory.CreateDirectory(Path.Combine(Source, Name, "Nested")).FullName;
                File.WriteAllText(Path.Combine(Nested, "Contents.txt"), Name);
            }
            await SourcePage.NavigateAsync(Source, RecordHistory: false);
            SourcePage.FileGrid.SelectAll();
            var Selection = SourcePage.SelectedEntries();
            var Data = CreateFileTransferData(Selection);
            await this.NavigateAsync(Target, RecordHistory: false);
            await DestinationPage.NavigateAsync(Target, RecordHistory: false);
            this.ChangeCopyOnDrop(Copy);
            var Input = Drop(Data);
            Check(Input.Handled && Input.Effects == (Copy ? DragDropEffects.Copy : DragDropEffects.Move)
                && Directory.GetFileSystemEntries(Target).Length == 4
                && Selection.All(Entry => Entry.IsDirectory
                    ? File.ReadAllText(Path.Combine(Target, Entry.Name, "Nested", "Contents.txt")) == Entry.Name
                    : File.ReadAllText(Path.Combine(Target, Entry.Name)) == Entry.Name),
                "Routed multi-item drag " + (Copy ? "copy" : "move") + " transfers every selected file and nested folder");
            Check(this.View!.Cast<CDir.Models.FileEntry>().Count() == 4 && !this.OperationBusy,
                "The destination panel shows the entire batch before the Windows drop handler returns");
            var Deadline = Environment.TickCount64 + 10000;
            while ((DestinationPage.Entries.Count != 4 || (!Copy && SourcePage.Entries.Count != 0)) && Environment.TickCount64 < Deadline) await Task.Delay(25);
            Check(DestinationPage.Entries.Count == 4 && (Copy ? SourcePage.Entries.Count == 4 : SourcePage.Entries.Count == 0)
                && (Copy ? Directory.GetFileSystemEntries(Source).Length == 4 : !Directory.EnumerateFileSystemEntries(Source).Any()),
                "Source and other destination tabs refresh automatically after batch " + (Copy ? "copy" : "move"));
        }
    }
}
