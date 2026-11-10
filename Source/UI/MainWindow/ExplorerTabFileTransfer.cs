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
    private sealed record FileTransferPlan(string[] Paths, string Destination, bool Copy, byte[][]? ShellItemIds = null)
    {
        internal DragDropEffects Effect => this.Copy ? DragDropEffects.Copy : DragDropEffects.Move;
    }
    private bool SidebarFileTransferFeedback;
    private bool ReplaceTransferConflictsInSmoke;

    private static bool HasFileDrop(IDataObject Data)
    {
        // Some file managers expose FileNameW/FileName with FileDrop conversion.
        try { return Data.GetDataPresent(DataFormats.FileDrop, true); }
        catch (COMException) { return false; }
    }

    private static DataObject CreateFileTransferData(IEnumerable<FileEntry> Entries)
    {
        var Data = new DataObject();
        var Selection = Entries.ToArray();
        if (Selection.Length > 0 && Selection.All(Entry => Entry.ShellId is not null))
            Data.SetData(ShellDropData.Format, new MemoryStream(ShellDropData.Create(Selection.Select(Entry => Entry.ShellId!).ToArray())));
        else Data.SetData(DataFormats.FileDrop, Selection.Select(Entry => Entry.FullPath).ToArray());
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
            if (this.FileItemContainer(Source) is { DataContext: FileEntry Entry } Row)
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
        if (this.WindowClosed || this.OperationBusy || this.Loading || ShellLocations.IsVirtual(Destination) || string.IsNullOrWhiteSpace(Destination)) return null;
        var Effect = this.Settings.CopyOnDrop ? DragDropEffects.Copy : DragDropEffects.Move;
        try
        {
            var Target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Destination));
            var Incoming = HasFileDrop(Data) ? Data.GetData(DataFormats.FileDrop, true) as string[] : null;
            // A drop back into the source directory is a no-op in both copy and
            // move modes, including Explorer data carrying both HDROP and CIDA.
            if (Incoming is { Length: > 0 } && Incoming.All(Path.IsPathFullyQualified)
                && Incoming.Any(Source => SamePath(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Source))) ?? Source, Target))) return null;
            // Recycle Bin data must retain its Shell identity even if it also
            // advertises an internal $R backing path in CF_HDROP.
            if (ShellDropData.Read(Data) is { Length: > 0 } Ids)
            {
                var Copy = this.Settings.CopyOnDrop;
                if ((Allowed & Effect) == 0)
                {
                    // Explorer's Recycle Bin advertises Move only. Moving out
                    // restores the item at the destination; never request an
                    // unsupported Copy effect or return Copy to that source.
                    if (!Copy || (Allowed & DragDropEffects.Move) == 0 || !ShellDropData.IsFromRecycleBin(Ids)) return null;
                    Copy = false;
                }
                return new([], Target, Copy, Ids);
            }
            if ((Allowed & Effect) == 0) return null;
            if (Incoming is not { Length: > 0 } || Incoming.Any(Source => !Path.IsPathFullyQualified(Source))) return null;
            var Paths = Incoming.Select(Source => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Source)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var Source in Paths)
            {
                if (SamePath(Source, Target)
                    || Target.StartsWith(Source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return new(Paths, Target, this.Settings.CopyOnDrop);
        }
        catch (Exception Error) when (Error is ArgumentException or NotSupportedException or IOException or InvalidDataException or ObjectDisposedException or COMException) { return null; }
    }

    private async Task<bool> ExecuteFileTransferAsync(FileTransferPlan Plan)
    {
        this.EndMarqueeSelection();
        this.ClearFileTransferFeedback();
        return await this.TransferFilesAsync(Plan.Paths, Plan.Destination, Move: !Plan.Copy, Plan.ShellItemIds);
    }

    private sealed record ShellTransferRequest(IntPtr Owner, string[] Paths, string Destination, bool Move,
        FileOperationHistory? History, bool Quiet, bool ReplaceConflicts, CancellationToken Cancellation, byte[][]? ShellItemIds = null);
    private Action<ShellTransferRequest> TransferShellItems = Request => ShellContextMenu.Transfer(Request.Owner,
        Request.Paths, Request.Destination, Request.Move, Request.History, Request.Quiet, Request.ReplaceConflicts, Request.Cancellation, ShellItemIds: Request.ShellItemIds);
    private CancellationToken TransferCancellation;

    private async Task<bool> TransferFilesAsync(string[] Paths, string Destination, bool Move, byte[][]? ShellItemIds = null)
    {
        if (this.OperationBusy) return false;
        var Request = new ShellTransferRequest(new System.Windows.Interop.WindowInteropHelper(this.HostWindow).Handle,
            Paths, Destination, Move, this.Files.OperationHistory, AppMain.SmokeTest,
            AppMain.SmokeTest && this.ReplaceTransferConflictsInSmoke, this.TransferCancellation, ShellItemIds);
        // Windows owns all counting and progress UI, including its Cancel button.
        // No queued WPF progress callback can resurrect an already finished operation.
        this.TransferProgressPanel.Visibility = Visibility.Collapsed;
        this.TransferProgressText.Text = "";
        try
        {
            return await this.RunOperationAsync(LanguageService.Text(Move ? "MovingFiles" : "CopyingFiles"),
                () => this.TransferShellItems(Request), LanguageService.Text(Move ? "MoveComplete" : "CopyComplete"), RequiresSta: true);
        }
        finally
        {
            this.TransferProgressPanel.Visibility = Visibility.Collapsed;
            this.TransferProgressText.Text = "";
            this.LoadingBar.IsIndeterminate = true;
            this.LoadingBar.Width = 96;
            this.LoadingBar.Height = 2;
            this.UpdateActions();
        }
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
            var Bounds = Target.Element is { } Row ? new Rect(Row.TranslatePoint(new Point(), this.ActiveFileControl), Row.RenderSize) : Viewport.Bounds;
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
        // OLE also sends DragLeave when a drag is cancelled while the pointer
        // remains inside the target. Coordinates alone cannot distinguish it.
        this.ClearFileTransferFeedback();
    }

    internal void DismissFileDragFeedback() { this.ClearFileTransferFeedback(); this.ClearSidebarDropFeedback(); }

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
            if (Completion.GetAwaiter().GetResult())
            {
                E.Effects = Plan.Effect;
                if (Plan.ShellItemIds is not null)
                {
                    // IFileOperation already performed the move. Tell the
                    // Explorer source not to delete its original a second time.
                    try
                    {
                        ShellDropData.ReportCompleted(E.Data, Plan.Copy);
                    }
                    catch (Exception Error) when (Error is COMException or NotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    { /* Completed filesystem changes remain successful if a source declines optional feedback. */ }
                }
            }
        }
        catch (Exception Error) { this.ShowNotice(LanguageService.Text("OperationIncomplete", FriendlyError(Error)), true); }
    }
}
