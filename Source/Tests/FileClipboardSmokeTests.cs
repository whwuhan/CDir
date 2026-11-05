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
            while ((this.OperationBusy || this.Loading) && Environment.TickCount64 < Deadline) await Task.Delay(20);
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
                await Paste();
                var CopiedFile = Index == 1 ? LanguageService.Text("CopyName", "报告", ".txt")
                    : LanguageService.Text("NumberedCopyName", "报告", Index, ".txt");
                var CopiedFolder = Index == 1 ? LanguageService.Text("CopyName", "Folder", "")
                    : LanguageService.Text("NumberedCopyName", "Folder", Index, "");
                Check(File.ReadAllText(Path.Combine(Source, CopiedFile)) == "Original file contents"
                    && File.ReadAllText(Path.Combine(Source, CopiedFolder, "Nested", "Data.txt")) == "Nested folder contents"
                    && File.ReadAllText(FilePath) == "Original file contents"
                    && this.Entries.Any(Entry => Entry.Name == CopiedFile) && ClipboardData is not null,
                    "Repeated same-folder Ctrl+V creates visible numbered file and folder copies and preserves the originals: " + Index);
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
            await this.RunCutClipboardSmokeChecksAsync(Passed, Source, Target, FilePath, FolderPath);
        }
        finally
        {
            this.WriteClipboardFiles = ClipboardWriter;
            this.ReadClipboardFiles = ClipboardReader;
            this.ClearClipboardFiles = ClipboardClearer;
            this.ReadClipboardVersion = ClipboardVersionReader;
            SetCutClipboard(null);
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned clipboard fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
