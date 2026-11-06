using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task AcceptSmokeInlineRenameAsync(Func<Task> Action, string Value, bool Confirm = true)
    {
        AppMain.RecordSmokeProgress("Editing inline name: " + Value);
        await Action();
        var ImmediateTarget = this.InlineRenameTarget is { } Immediate ? RenamePath(Immediate) : "none";
        var ImmediateNavigation = this.NavigationVersion;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        this.UpdateLayout();
        if (this.InlineRenameBox is not { IsVisible: true } Box || this.InlineRenameTarget is null
            || Application.Current.Windows.OfType<Dialogs.NameDialog>().Any())
            throw new InvalidOperationException("An inline filename editor must appear without a modal dialog: " + Value
                + "; target=" + (this.InlineRenameTarget is { } Target ? RenamePath(Target) : "none")
                + "; box=" + this.InlineRenameBox?.IsVisible + "; path=" + this.CurrentPath + "; status=" + this.StatusText.Text
                + "; immediateTarget=" + ImmediateTarget + "; navigation=" + ImmediateNavigation + "->" + this.NavigationVersion
                + "; entries=" + string.Join(",", this.Entries.Select(Entry => Entry.Name)));
        Box.Text = Value;
        var Input = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(Box), 0, Confirm ? Key.Enter : Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Box.RaiseEvent(Input);
        if (!Input.Handled) throw new InvalidOperationException("The inline editor must handle confirmation and cancellation.");
        await this.InlineRenameTask;
    }

    private async Task RunInlineRenameSmokeChecksAsync(List<string> Passed)
    {
        var Initial = this.CurrentPath;
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Rename-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Inline rename check failed: " + Name);
            Passed.Add(Name);
        }
        try
        {
            var Original = Path.Combine(Fixture, "报告.txt");
            File.WriteAllText(Original, "Owned rename contents");
            File.WriteAllText(Path.Combine(Fixture, "Taken.txt"), "Keep this file");
            await this.NavigateAsync(Fixture, RecordHistory: false);
            this.SelectPath(Original);
            var RenameKey = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(this.FileGrid), 0, Key.F2)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            this.FileGrid.RaiseEvent(RenameKey);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(RenameKey.Handled && this.InlineRenameBox is { IsVisible: true, SelectedText: "报告" },
                "F2 edits the filename in its row and selects the stem while preserving the extension");
            var Box = this.InlineRenameBox!;
            var SelectAll = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control), PresentationSource.FromVisual(Box), 0, Key.A)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Box.RaiseEvent(SelectAll);
            Check(this.FileGrid.SelectedItems.Count == 1 && ReferenceEquals(this.InlineRenameBox, Box),
                "Ctrl+A inside the rename editor preserves file selection and native text editing");
            Box.Text = "Typed before refresh.txt";
            await this.RefreshAsync();
            Check(ReferenceEquals(this.InlineRenameBox, Box) && Box.Text == "Typed before refresh.txt",
                "A background refresh preserves the active inline editor and typed filename");
            this.CaptureAddressBreadcrumbPreview("InlineRename.png");
            await this.AcceptSmokeInlineRenameAsync(() => Task.CompletedTask, "Renamed 中文.txt");
            var Renamed = Path.Combine(Fixture, "Renamed 中文.txt");
            Check(!File.Exists(Original) && File.ReadAllText(Renamed) == "Owned rename contents"
                && this.FileGrid.SelectedItem is FileEntry Entry && SamePath(Entry.FullPath, Renamed),
                "Enter commits Unicode file renaming and immediately selects the refreshed row");
            await this.AcceptSmokeInlineRenameAsync(this.RenameAsync, "Taken.txt");
            Check(File.Exists(Renamed) && File.ReadAllText(Path.Combine(Fixture, "Taken.txt")) == "Keep this file"
                && this.InlineRenameBox is { IsVisible: true, Text: "Taken.txt" },
                "A conflicting filename preserves both files and reopens inline editing for correction");
            await this.AcceptSmokeInlineRenameAsync(() => Task.CompletedTask, "Cancelled.txt", Confirm: false);
            Check(File.Exists(Renamed) && !File.Exists(Path.Combine(Fixture, "Cancelled.txt")), "Escape cancels inline renaming without modifying the file");
            this.SearchBox.Text = "No match";
            await this.SearchTask;
            await this.AcceptSmokeInlineRenameAsync(this.NewFolderAsync, "Created While Filtered");
            Check(Directory.Exists(Path.Combine(Fixture, "Created While Filtered")) && this.Entries.Any(Entry => Entry.Name == "Created While Filtered"),
                "Creating a folder while filtering reveals its row for inline editing");
            await this.NewFolderAsync();
            var DefaultPath = RenamePath(this.InlineRenameTarget!);
            await this.AcceptSmokeInlineRenameAsync(() => Task.CompletedTask, "Cancelled Folder", Confirm: false);
            Check(Directory.Exists(DefaultPath), "Escape after folder creation keeps the newly created default folder");
        }
        finally
        {
            this.CancelInlineRename();
            await this.NavigateAsync(Initial, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unowned inline rename fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
