using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private object? InlineRenameTarget;
    private TextBox? InlineRenameBox;
    private Task InlineRenameTask = Task.CompletedTask;
    private int InlineRenameNavigation;

    private static string RenamePath(object Target) => Target is FileEntry Entry ? Entry.FullPath : ((FolderNode)Target).FullPath;
    private static string RenameName(object Target) => Target is FileEntry Entry ? Entry.Name : ((FolderNode)Target).Name;
    private static void SetRenaming(object Target, bool Value)
    {
        if (Target is FileEntry Entry) Entry.IsRenaming = Value;
        else ((FolderNode)Target).IsRenaming = Value;
    }

    private void BeginFileRename(string Path)
    {
        if (this.IsComputer) return;
        if (!this.Entries.Any(Entry => SamePath(Entry.FullPath, Path))) return;
        if (this.Entries.First(Entry => SamePath(Entry.FullPath, Path)) is { } Candidate && this.View?.Contains(Candidate) != true)
            this.SearchBox.Clear();
        if (this.Entries.FirstOrDefault(Entry => SamePath(Entry.FullPath, Path)) is not { } Target) return;
        this.SelectPath(Path);
        if (this.IsDetailsView) this.FileGrid.ScrollIntoView(Target, this.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Name)));
        else this.RevealFile(Target);
        this.BeginInlineRename(Target);
    }

    private void BeginInlineRename(object Target)
    {
        this.CancelInlineRename();
        ++this.FolderRefreshVersion;
        this.RefreshTimer.Stop();
        this.InlineRenameTarget = Target;
        this.InlineRenameNavigation = this.NavigationVersion;
        SetRenaming(Target, true);
        this.UpdateLayout();
        this.FindInlineRenameBox();
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(this.InlineRenameTarget, Target)) return;
            this.UpdateLayout();
            this.FindInlineRenameBox();
        }, DispatcherPriority.Loaded);
    }

    private void FindInlineRenameBox()
    {
        if (this.InlineRenameBox is not null || this.InlineRenameTarget is null) return;
        var Pending = new Stack<DependencyObject>();
        Pending.Push(this);
        while (Pending.TryPop(out var Element))
        {
            if (Element is TextBox { IsVisible: true } Box && ReferenceEquals(Box.DataContext, this.InlineRenameTarget))
            {
                this.InlineRenameVisibleChanged(Box, new DependencyPropertyChangedEventArgs(UIElement.IsVisibleProperty, false, true));
                return;
            }
            for (var Index = 0; Index < VisualTreeHelper.GetChildrenCount(Element); Index++) Pending.Push(VisualTreeHelper.GetChild(Element, Index));
        }
    }

    private void InlineRenameVisibleChanged(object Sender, DependencyPropertyChangedEventArgs E)
    {
        if (this.InlineRenameTarget is not { } Target || Sender is not TextBox { IsVisible: true } Box
            || !ReferenceEquals(Box.DataContext, Target)) return;
        this.InlineRenameBox = Box;
        Box.Text = RenameName(Target);
        var Length = Box.Text.Length;
        if (this.InlineRenameTarget is FileEntry { IsDirectory: false })
        {
            var Extension = Path.GetExtension(Box.Text);
            if (Extension.Length < Length) Length -= Extension.Length;
        }
        if (!AppMain.SmokeTest) { Box.BringIntoView(); Box.Focus(); }
        Box.Select(0, Length);
    }

    private void CancelInlineRename()
    {
        var Target = this.InlineRenameTarget;
        this.InlineRenameTarget = null;
        this.InlineRenameBox = null;
        if (Target is null) return;
        SetRenaming(Target, false);
        if (this.FolderRefreshPending) this.ScheduleRefresh();
    }

    private async Task CommitInlineRenameAsync()
    {
        if (this.InlineRenameTarget is not { } Target || this.InlineRenameBox is not { } Box) return;
        var Name = Box.Text;
        var Navigation = this.InlineRenameNavigation;
        var Path = RenamePath(Target);
        this.CancelInlineRename();
        if (this.WindowClosed || Navigation != this.NavigationVersion || Name == RenameName(Target)) return;
        string? NewPath = null;
        var Success = await this.RunOperationAsync(LanguageService.Text("Renaming"),
            () => { CDir.Services.ShellContextMenu.Rename(IntPtr.Zero, [Path], Name, AppMain.SmokeTest);
                NewPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, Name); },
            LanguageService.Text("RenameComplete"), RequiresSta: true);
        if (this.WindowClosed || Navigation != this.NavigationVersion) return;
        if (Success && NewPath is not null && Target is FileEntry) this.SelectPath(NewPath);
        if (!Success && Target is FileEntry && this.Entries.Any(Entry => SamePath(Entry.FullPath, Path)))
        {
            this.BeginFileRename(Path);
            if (this.InlineRenameBox is { } Retry) { Retry.Text = Name; Retry.SelectAll(); }
        }
    }

    private async void InlineRenameLostFocus(object Sender, KeyboardFocusChangedEventArgs E)
    {
        if (!ReferenceEquals(Sender, this.InlineRenameBox)) return;
        await (this.InlineRenameTask = this.CommitInlineRenameAsync());
    }

    internal bool HandleInlineRenameKey(KeyEventArgs E)
    {
        if (this.InlineRenameTarget is null || E.OriginalSource is not DependencyObject Source
            || !ReferenceEquals(FindAncestor<TextBox>(Source), this.InlineRenameBox)) return false;
        if (E.Key == Key.Escape)
        {
            E.Handled = true;
            this.CancelInlineRename();
            this.FocusMainFilePanel();
        }
        else if (E.Key is Key.Enter or Key.Tab)
        {
            E.Handled = true;
            this.InlineRenameTask = ConfirmAsync();
            async Task ConfirmAsync()
            {
                var Navigation = this.NavigationVersion;
                await this.CommitInlineRenameAsync();
                if (this.InlineRenameTarget is null && Navigation == this.NavigationVersion) this.FocusMainFilePanel();
            }
        }
        // Preserve text selection, clipboard, IME and undo inside the editor.
        return true;
    }

    private string CreateDefaultFolder(string Parent)
    {
        var Name = LanguageService.Text("NewFolder");
        for (var Index = 2; Directory.Exists(Path.Combine(Parent, Name)) || File.Exists(Path.Combine(Parent, Name)); Index++)
            Name = LanguageService.Text("NumberedNewFolder", Index);
        return CDir.Services.ShellContextMenu.CreateFolder(IntPtr.Zero, Parent, Name, AppMain.SmokeTest);
    }
}
