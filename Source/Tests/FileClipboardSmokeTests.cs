using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunFileClipboardSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var ClipboardWriter = this.WriteClipboardFiles;
        var ClipboardReader = this.ReadClipboardFiles;
        var ClipboardClearer = this.ClearClipboardFiles;
        var ClipboardVersionReader = this.ReadClipboardVersion;
        uint ClipboardVersion = 1;
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "FileClipboard-" + Guid.NewGuid().ToString("N"))).FullName;
        var Source = Directory.CreateDirectory(Path.Combine(Fixture, "Source")).FullName;
        var Target = Directory.CreateDirectory(Path.Combine(Fixture, "Target")).FullName;
        var FilePath = Path.Combine(Source, "报告.txt");
        var FolderPath = Directory.CreateDirectory(Path.Combine(Source, "Folder", "Nested")).Parent!.FullName;
        IDataObject? ClipboardData = null;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("File clipboard check failed: " + Name);
            Passed.Add(Name);
        }
        void Press(Key Key, UIElement? SourceElement = null, ModifierKeys Modifiers = ModifierKeys.Control)
        {
            var Element = SourceElement ?? this.FileGrid;
            var Input = new KeyEventArgs(new SmokeKeyboardDevice(Modifiers), PresentationSource.FromVisual(Element), 0, Key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Element.RaiseEvent(Input);
            Check(Input.Handled, "The main panel handles the real clipboard key route: " + Modifiers + "+" + Key);
        }
        async Task Paste(bool Move = false)
        {
            Press(Key.V);
            var Deadline = Environment.TickCount64 + 15000;
            var Expected = LanguageService.Text(Move ? "MoveComplete" : "CopyComplete");
            while ((this.OperationBusy || this.Loading || this.StatusText.Text != Expected)
                && Environment.TickCount64 < Deadline) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(!this.OperationBusy && !this.Loading && this.StatusText.Text == LanguageService.Text(Move ? "MoveComplete" : "CopyComplete"),
                "Ctrl+V finishes the filesystem transfer and reports success: " + this.StatusText.Text);
        }
        try
        {
            // Use an owned WPF clipboard payload so the user's Windows clipboard is untouched.
            this.WriteClipboardFiles = Data => { ClipboardData = Data; ClipboardVersion++; };
            this.ReadClipboardFiles = () => ClipboardData;
            this.ClearClipboardFiles = () => { ClipboardData = null; ClipboardVersion++; };
            this.ReadClipboardVersion = () => ClipboardVersion;
            File.WriteAllText(FilePath, "Original file contents");
            File.WriteAllText(Path.Combine(FolderPath, "Nested", "Data.txt"), "Nested folder contents");
            await this.NavigateAsync(Source, RecordHistory: false);
            this.FileGrid.SelectAll();
            this.FileGrid.UpdateLayout();
            var Row = (DataGridRow)this.FileGrid.ItemContainerGenerator.ContainerFromItem(this.FileGrid.SelectedItem);
            Press(Key.C, Row);
            Check(ClipboardData?.GetData(DataFormats.FileDrop) is string[] Paths
                && Paths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(new[] { FilePath, FolderPath })
                && ClipboardData.GetData("Preferred DropEffect") is MemoryStream Effect && BitConverter.ToInt32(Effect.ToArray()) == 1,
                "Ctrl+C publishes Windows file-drop paths and Copy effect for a mixed selection");
            for (var Index = 1; Index <= 2; Index++)
            {
                var Before = Directory.EnumerateFileSystemEntries(Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
                await Paste();
                var Added = Directory.EnumerateFileSystemEntries(Source).Where(Path => !Before.Contains(Path)).ToArray();
                var CopiedFile = Added.SingleOrDefault(File.Exists);
                var CopiedFolder = Added.SingleOrDefault(Directory.Exists);
                Check(Added.Length == 2 && CopiedFile is not null && CopiedFolder is not null
                    && File.ReadAllText(CopiedFile) == "Original file contents"
                    && File.ReadAllText(Path.Combine(CopiedFolder, "Nested", "Data.txt")) == "Nested folder contents"
                    && File.ReadAllText(FilePath) == "Original file contents"
                    && this.Entries.Any(Entry => SamePath(Entry.FullPath, CopiedFile)) && ClipboardData is not null,
                    "Windows chooses same-folder copy names, keeps originals and refreshes each paste: " + Index);
            }
            await this.NavigateAsync(Target, RecordHistory: false);
            await Paste();
            Check(File.ReadAllText(Path.Combine(Target, "报告.txt")) == "Original file contents"
                && File.ReadAllText(Path.Combine(Target, "Folder", "Nested", "Data.txt")) == "Nested folder contents",
                "Navigating to an empty destination retains the copied selection and pastes under its original names");
            var ExternalFile = Path.Combine(Fixture, "External.txt");
            File.WriteAllText(ExternalFile, "Windows file-drop contents");
            var ExternalData = new DataObject();
            ExternalData.SetData(DataFormats.FileDrop, new[] { ExternalFile });
            ExternalData.SetData("Preferred DropEffect", BitConverter.GetBytes(1));
            ClipboardData = ExternalData;
            ClipboardVersion++;
            await Paste();
            Check(File.ReadAllText(Path.Combine(Target, "External.txt")) == "Windows file-drop contents" && File.Exists(ExternalFile),
                "Ctrl+V accepts an external Windows file-drop payload and preserves its source");
            await this.NavigateAsync(Source, RecordHistory: false);
            this.SelectPath(FilePath);
            this.SelectPath(FolderPath, Append: true);
            this.FileGrid.UpdateLayout();
            var Writer = this.WriteClipboardFiles;
            this.WriteClipboardFiles = _ => throw new System.Runtime.InteropServices.COMException("Owned clipboard contention");
            Press(Key.C);
            await this.NavigateAsync(Directory.CreateDirectory(Path.Combine(Fixture, "FailedCopyTarget")).FullName, RecordHistory: false);
            await this.PasteAsync();
            Check(!this.Entries.Any() && this.StatusText.Text.Contains("Owned clipboard contention"),
                "A failed Ctrl+C blocks pasting the previous file payload and reports the clipboard error");
            this.WriteClipboardFiles = Writer;
            await this.NavigateAsync(Source, RecordHistory: false);
            this.SelectPath(FilePath);
            this.SelectPath(FolderPath, Append: true);
            this.Loading = true;
            this.OperationBusy = true;
            try { Press(Key.C); }
            finally { this.Loading = false; this.OperationBusy = false; }
            Check(ClipboardData?.GetData(DataFormats.FileDrop) is string[] NewPaths
                && NewPaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(new[] { FilePath, FolderPath }),
                "Ctrl+C replaces an old clipboard with the complete multi-selection even during loading or a file operation");
            var MultiTarget = Directory.CreateDirectory(Path.Combine(Fixture, "MultiTarget")).FullName;
            await this.NavigateAsync(MultiTarget, RecordHistory: false);
            var Refreshes = Enumerable.Range(0, 4).Select(_ => this.RefreshAsync()).ToArray();
            await Paste();
            await Task.WhenAll(Refreshes);
            Check(File.Exists(Path.Combine(MultiTarget, "报告.txt")) && Directory.Exists(Path.Combine(MultiTarget, "Folder"))
                && this.View!.Cast<CDir.Models.FileEntry>().Select(Entry => Entry.Name).ToHashSet().SetEquals(new[] { "报告.txt", "Folder" }),
                "Mixed multi-selection paste refreshes the visible panel before completion despite overlapping watcher refreshes");
            await this.RunCutClipboardSmokeChecksAsync(Passed, Source, Target, FilePath, FolderPath);
        }
        finally
        {
            this.WriteClipboardFiles = ClipboardWriter;
            this.ReadClipboardFiles = ClipboardReader;
            this.ClearClipboardFiles = ClipboardClearer;
            this.ReadClipboardVersion = ClipboardVersionReader;
            SetCutClipboard(null);
            FailedFileClipboard = null;
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned clipboard fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
