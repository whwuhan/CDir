using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CDir.Localization;
using CDir.Services;
using CDir.UI.Dialogs;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private Action<string[], bool> DeleteShellItems = ShellContextMenu.Delete;
    private bool ShellMenuOpen;
    private FileEntry[]? PendingFileContextEntries;
    private int PendingFileContextNavigation;

    private FileEntry[] FileContextEntries(DependencyObject? Source)
    {
        // The unused area beyond the actual cells is folder background even
        // when WPF stretches a DataGridRow across it.
        if (FindAncestor<DataGridCell>(Source) is null
            || FindAncestor<DataGridRow>(Source) is not { Item: FileEntry Entry } Row
            || !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(Row), this.FileGrid)) return [];
        var Selection = this.SelectedEntries();
        return Selection.Contains(Entry) ? Selection : [Entry];
    }

    private FileEntry[] TakeFileContextEntries(bool Keyboard, DependencyObject? MouseSource)
    {
        var Pending = this.PendingFileContextEntries;
        this.PendingFileContextEntries = null;
        if (Keyboard) return this.SelectedEntries();
        return Pending is not null && this.PendingFileContextNavigation == this.NavigationVersion
            ? Pending : this.FileContextEntries(MouseSource);
    }
    private async void FileContextOpening(object Sender, ContextMenuEventArgs E)
    {
        E.Handled = true;
        if (this.IsComputer || this.ShellMenuOpen || this.OperationBusy || this.Loading || this.CurrentPath.Length == 0 || this.WindowClosed) return;
        this.ShellMenuOpen = true;
        try
        {
            var Entries = this.TakeFileContextEntries(E.CursorLeft < 0 || E.CursorTop < 0,
                this.FileGrid.InputHitTest(Mouse.GetPosition(this.FileGrid)) as DependencyObject);
            var Paths = Entries.Select(Entry => Entry.FullPath).ToArray();
            var Source = HwndSource.FromHwnd(new WindowInteropHelper(this.HostWindow).Handle);
            if (Source is null) return;
            Point Position;
            if (E.CursorLeft >= 0 && E.CursorTop >= 0) Position = this.FileGrid.PointToScreen(Mouse.GetPosition(this.FileGrid));
            else if (this.FileGrid.ItemContainerGenerator.ContainerFromItem(this.FileGrid.SelectedItem) is FrameworkElement Row)
                Position = Row.PointToScreen(new Point(Math.Min(80, Row.ActualWidth), Row.ActualHeight));
            else Position = this.FileGrid.PointToScreen(new Point(16, 32));
            using var Menu = new ShellContextMenu(Source.Handle, this.IsRecycleBin ? this.RecycleBinViewTarget : this.CurrentPath, Paths,
                Extended: (Keyboard.Modifiers & ModifierKeys.Shift) != 0,
                ShellItemIds: this.IsRecycleBin ? Entries.Select(Entry => Entry.ShellId!).ToArray() : null);
            var Id = Menu.Show(Source, Position);
            if (Id == 0) this.FocusMainFilePanel();
            await this.ExecuteShellCommandAsync(Menu, Id);
        }
        catch (Exception Error) when (Error is COMException or Win32Exception or InvalidOperationException or System.IO.IOException
            or UnauthorizedAccessException or System.Security.SecurityException)
        {
            this.ShowNotice(LanguageService.Text("ShellMenuError", FriendlyError(Error)), true);
        }
        finally { this.ShellMenuOpen = false; }
    }
    private async Task ExecuteShellCommandAsync(ShellContextMenu Menu, uint Id)
    {
        if (Id == 0 || Id == uint.MaxValue) return;
        var Verb = Menu.DescribeCommand(Id) ?? "ShellCommand";
        if (this.IsRecycleBin)
        {
            if (Verb is "delete" or "undelete") await this.ExecuteRecycleSelectionAsync(Verb);
            else if (Menu.Invoke(Id)) { SharedFileHistory.Audit(Verb, Menu.SelectionPaths, "Invoked"); await this.RefreshRecycleBinAsync(); }
            return;
        }
        if (string.Equals(Verb, "copy", StringComparison.OrdinalIgnoreCase))
        {
            this.CopyFilePathsToClipboard((string[])Menu.SelectionPaths.Clone(), Cut: false);
            return;
        }
        if (string.Equals(Verb, "paste", StringComparison.OrdinalIgnoreCase)) { await this.PasteAsync(); return; }
        if (string.Equals(Verb, "NewFolder", StringComparison.OrdinalIgnoreCase))
        {
            await this.NewFolderAsync();
            return;
        }
        if (string.Equals(Menu.DescribeCommand(Id), "cut", StringComparison.OrdinalIgnoreCase))
        {
            // Keep the native menu's captured selection and share Ctrl+X's pending cut feedback.
            this.CopyFilePathsToClipboard((string[])Menu.SelectionPaths.Clone(), Cut: true);
            return;
        }
        if (string.Equals(Menu.DescribeCommand(Id), "delete", StringComparison.OrdinalIgnoreCase))
        {
            // Copy inputs before yielding. The menu and its COM objects stay on the UI thread.
            var Paths = (string[])Menu.SelectionPaths.Clone();
            var Permanently = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            await this.DeleteShellSelectionAsync(Paths, Permanently);
            return;
        }
        // Rename is a view operation: the Shell supplies the menu item and the host edits its name.
        if (string.Equals(Menu.DescribeCommand(Id), "rename", StringComparison.OrdinalIgnoreCase))
        {
            var Paths = Menu.SelectionPaths;
            if (Paths.Length == 0) return;
            if (Paths.Length == 1) { this.BeginFileRename(Paths[0]); return; }
            var InitialName = System.IO.Path.GetFileName(Paths[0]);
            var Dialog = new NameDialog(this.HostWindow, LanguageService.Text("Rename"), InitialName, !System.IO.Directory.Exists(Paths[0]));
            if (Dialog.ShowDialog() != true || Paths.Length == 1 && Dialog.EnteredName == InitialName) return;
            var NewName = Dialog.EnteredName;
            var Renamed = await this.RunOperationAsync(LanguageService.Text("Renaming"),
                () => ShellContextMenu.Rename(IntPtr.Zero, Paths, NewName, AppMain.SmokeTest), LanguageService.Text("RenameComplete"), RequiresSta: true);
            if (Renamed && Paths.Length == 1)
                this.SelectPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Paths[0])!, NewName));
            return;
        }
        SharedFileHistory.Audit(Verb, Menu.SelectionPaths, "Started");
        if (Menu.Invoke(Id))
        {
            SharedFileHistory.Audit(Verb, Menu.SelectionPaths, "Invoked");
            this.InvalidateVersionStatuses();
            this.ScheduleVersionRefresh();
            await this.RefreshAsync(true);
        }
    }

    private Task<bool> DeleteShellSelectionAsync(string[] Paths, bool Permanently)
    {
        this.CancelInlineRename();
        var Delete = this.DeleteShellItems;
        return this.RunOperationAsync(LanguageService.Text("Recycling"), () =>
        {
            var TopLevel = Paths.Where(Path => !Paths.Any(Other => !SamePath(Path, Other)
                && Path.StartsWith(Other.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var Path in TopLevel)
                if (System.IO.Directory.GetParent(Path) is null) throw new System.IO.IOException(LanguageService.Text("ServiceDeleteRootDenied"));
            Delete(TopLevel, Permanently);
        },
            LanguageService.Text("RecycleComplete"), RequiresSta: true);
    }

}
