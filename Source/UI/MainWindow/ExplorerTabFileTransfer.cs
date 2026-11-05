using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private sealed record FileTransferPlan(string[] Paths, string Destination, bool Copy)
    {
        internal DragDropEffects Effect => this.Copy ? DragDropEffects.Copy : DragDropEffects.Move;
    }
    private bool SidebarFileTransferFeedback;

    private static bool HasFileDrop(IDataObject Data)
    {
        try { return Data.GetDataPresent(DataFormats.FileDrop, false); }
        catch (COMException) { return false; }
    }

    private static DataObject CreateFileTransferData(IEnumerable<FileEntry> Entries)
    {
        var Data = new DataObject();
        Data.SetData(DataFormats.FileDrop, Entries.Select(Entry => Entry.FullPath).ToArray());
        return Data;
    }

    private (string Path, FrameworkElement? Element)? FileTransferTarget(DependencyObject? Source, bool Sidebar)
    {
        if (FindAncestor<ScrollBar>(Source) is not null || FindAncestor<DataGridColumnHeader>(Source) is not null) return null;
        if (!Sidebar)
        {
            if (Source is not null && (ReferenceEquals(Source, this.AddressDisplay) || this.AddressDisplay.IsAncestorOf(Source)))
                return FindAncestor<Button>(Source) is { Tag: string AddressPath } AddressButton
                    && this.AddressBreadcrumbs.IsAncestorOf(AddressButton) ? (AddressPath, AddressButton) : null;
            if (FindAncestor<DataGridRow>(Source) is { Item: FileEntry Entry } Row)
                return (Entry.IsDirectory ? Entry.FullPath : Path.GetDirectoryName(Entry.FullPath)!, Row);
            return this.CurrentPath.Length > 0 ? (this.CurrentPath, null) : null;
        }
        if (FindAncestor<TreeViewItem>(Source) is { DataContext: FolderNode { IsPlaceholder: false } Node } Item)
            return (Node.FullPath, Item.Template.FindName("PART_Header", Item) as FrameworkElement ?? Item);
        if (FindAncestor<Button>(Source) is { } Button)
        {
            if (Button.DataContext is FavoriteLocation Favorite) return (Favorite.FullPath, Button);
            if (Button.DataContext is CategoryFolder Folder) return (Folder.FullPath, Button);
        }
        return null;
    }

    private FileTransferPlan? PlanFileTransfer(IDataObject Data, string? Destination, DragDropEffects Allowed)
    {
        if (this.WindowClosed || this.OperationBusy || this.Loading || ShellLocations.IsRecycleBin(Destination) || string.IsNullOrWhiteSpace(Destination)) return null;
        var Effect = this.Settings.CopyOnDrop ? DragDropEffects.Copy : DragDropEffects.Move;
        if ((Allowed & Effect) == 0) return null;
        try
        {
            if (!HasFileDrop(Data) || Data.GetData(DataFormats.FileDrop, false) is not string[] { Length: > 0 } Incoming
                || Incoming.Any(Source => !Path.IsPathFullyQualified(Source))) return null;
            var Paths = Incoming.Select(Source => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Source)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var Target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Destination));
            foreach (var Source in Paths)
            {
                if (SamePath(Source, Target) || SamePath(Path.GetDirectoryName(Source) ?? Source, Target)
                    || Target.StartsWith(Source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return new(Paths, Target, this.Settings.CopyOnDrop);
        }
        catch (Exception Error) when (Error is ArgumentException or NotSupportedException or IOException or COMException) { return null; }
    }

    private async Task<bool> ExecuteFileTransferAsync(FileTransferPlan Plan)
    {
        this.EndMarqueeSelection();
        this.ClearFileTransferFeedback();
        return await this.RunOperationAsync(LanguageService.Text(Plan.Copy ? "CopyingFiles" : "MovingFiles"),
            () => this.Files.CopyItems(Plan.Paths, Plan.Destination, Move: !Plan.Copy), LanguageService.Text(Plan.Copy ? "CopyComplete" : "MoveComplete"));
    }

    private void ClearFileTransferFeedback()
    {
        this.FileDropTargetHighlight.Visibility = Visibility.Collapsed;
        this.AddressDropTargetHighlight.Visibility = Visibility.Collapsed;
        this.FileDropHint.Visibility = Visibility.Collapsed;
        if (this.SidebarFileTransferFeedback) this.ClearSidebarDropFeedback();
        this.SidebarFileTransferFeedback = false;
    }

    private DragDropEffects ShowFileTransferFeedback(IDataObject Data, DependencyObject? Source, bool Sidebar, DragDropEffects Allowed)
    {
        this.ClearFileTransferFeedback();
        if (this.FileTransferTarget(Source, Sidebar) is not { } Target || this.PlanFileTransfer(Data, Target.Path, Allowed) is not { } Plan) return DragDropEffects.None;
        if (Target.Element is Button AddressButton && this.AddressBreadcrumbs.IsAncestorOf(AddressButton))
        {
            this.CancelAddressNavigation();
            var Position = AddressButton.TranslatePoint(new Point(), this.AddressDropOverlay);
            Canvas.SetLeft(this.AddressDropTargetHighlight, Position.X);
            Canvas.SetTop(this.AddressDropTargetHighlight, Position.Y);
            this.AddressDropTargetHighlight.Width = AddressButton.ActualWidth;
            this.AddressDropTargetHighlight.Height = AddressButton.ActualHeight;
            this.AddressDropTargetHighlight.Visibility = Visibility.Visible;
        }
        else if (Sidebar && Target.Element is { } Element)
        {
            this.SidebarFileTransferFeedback = true;
            this.SidebarCategoryDropHighlight.Margin = new Thickness(Element.TranslatePoint(new Point(), this.SidebarPanel).X,
                Element.TranslatePoint(new Point(), this.SidebarPanel).Y, 0, 0);
            this.SidebarCategoryDropHighlight.Width = Element.ActualWidth;
            this.SidebarCategoryDropHighlight.Height = Element.ActualHeight;
            this.SidebarCategoryDropHighlight.Visibility = Visibility.Visible;
        }
        else if (this.MarqueeViewport() is { } Viewport)
        {
            var Bounds = Target.Element is { } Row ? new Rect(Row.TranslatePoint(new Point(), this.FileGrid), Row.RenderSize) : Viewport.Bounds;
            Bounds.Intersect(Viewport.Bounds);
            if (!Bounds.IsEmpty)
            {
                Canvas.SetLeft(this.FileDropTargetHighlight, Bounds.Left);
                Canvas.SetTop(this.FileDropTargetHighlight, Bounds.Top);
                this.FileDropTargetHighlight.Width = Bounds.Width;
                this.FileDropTargetHighlight.Height = Bounds.Height;
                this.FileDropTargetHighlight.Visibility = Visibility.Visible;
            }
        }
        this.FileDropHint.Text = LanguageService.Text(Plan.Copy ? "FileDropCopyTo" : "FileDropMoveTo", Plan.Destination);
        this.FileDropHint.Visibility = Visibility.Visible;
        return Plan.Effect;
    }

    private void FileTransferDragOver(object Sender, DragEventArgs E)
    {
        E.Handled = true;
        var Sidebar = ReferenceEquals(Sender, this.SidebarPanel);
        E.Effects = this.ShowFileTransferFeedback(E.Data, E.OriginalSource as DependencyObject, Sidebar, E.AllowedEffects);
    }

    private void FileTransferDragLeave(object Sender, DragEventArgs E)
    {
        var Surface = (FrameworkElement)Sender;
        if (!new Rect(Surface.RenderSize).Contains(E.GetPosition(Surface))) this.ClearFileTransferFeedback();
    }

    private void FileTransferDrop(object Sender, DragEventArgs E)
    {
        E.Handled = true;
        E.Effects = DragDropEffects.None;
        this.ClearFileTransferFeedback();
        if (this.FileTransferTarget(E.OriginalSource as DependencyObject, ReferenceEquals(Sender, this.SidebarPanel)) is not { } Target
            || this.PlanFileTransfer(E.Data, Target.Path, E.AllowedEffects) is not { } Plan) return;
        try
        {
            var Completion = this.ExecuteFileTransferAsync(Plan);
            if (!Completion.IsCompleted)
            {
                // Keep the OLE drop alive until its actual result is known while the UI continues pumping.
                var Frame = new DispatcherFrame();
                async Task FinishFrame()
                {
                    try { await Completion; }
                    catch { /* Observe/report the original task below. */ }
                    finally { Frame.Continue = false; }
                }
                _ = FinishFrame();
                if (!Completion.IsCompleted) Dispatcher.PushFrame(Frame);
            }
            if (Completion.GetAwaiter().GetResult()) E.Effects = Plan.Effect;
        }
        catch (Exception Error) { this.ShowNotice(LanguageService.Text("OperationIncomplete", FriendlyError(Error)), true); }
    }
}
