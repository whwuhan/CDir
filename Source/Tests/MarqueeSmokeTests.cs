using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunMarqueeSmokeChecksAsync(List<string> Passed)
    {
        await this.RunSelectionEscapeSmokeChecksAsync(Passed);
        var InitialPath = this.CurrentPath;
        var InitialSort = this.SortProperty;
        var InitialDirection = this.SortDirection;
        var InitialHitTest = this.FileGrid.IsHitTestVisible;
        var Fixture = Path.Combine(AppMain.SmokeRoot!, "Marquee-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Fixture, "A Folder"));
        Directory.CreateDirectory(Path.Combine(Fixture, "B Folder"));
        for (var Index = 0; Index < 90; Index++) File.WriteAllText(Path.Combine(Fixture, $"File-{Index:00}.txt"), "Marquee fixture");
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Marquee UI check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        DataGridRow Row(int Index) => (DataGridRow)this.FileGrid.ItemContainerGenerator.ContainerFromIndex(Index);
        DataGridCell NameCell(int Index) => FindVisualChild<DataGridCell>(Row(Index))!;
        try
        {
            await this.NavigateAsync(Fixture, RecordHistory: false);
            this.SortProperty = nameof(FileEntry.Name);
            this.SortDirection = ListSortDirection.Ascending;
            this.ApplySort();
            await Settle();
            // This test drives owned geometry directly. Ignore incidental desktop mouse
            // movement over the inactive window, whose released button would end the simulated drag.
            this.FileGrid.IsHitTestVisible = false;
            var Viewport = this.MarqueeViewport()!.Value;
            var Cell = NameCell(0);
            var Start = Cell.TranslatePoint(new Point(Cell.ActualWidth - 8, Cell.ActualHeight / 2), this.FileGrid);
            var End = new Point(Start.X - 70, Viewport.Bounds.Top + Viewport.RowHeight * 3.5);
            Check(this.PrepareMarqueeSelection(Cell, Start, ModifierKeys.None, 1), "Name-cell whitespace starts a marquee without starting a folder drag");
            Check(!this.UpdateMarqueeSelection(new Point(Start.X + 1, Start.Y + 1)) && !this.MarqueeActive
                && this.FileSelectionRectangle.Visibility == Visibility.Collapsed, "An ordinary click does not display a selection rectangle");
            this.UpdateMarqueeSelection(End);
            Check(this.SelectedEntries().ToHashSet().SetEquals(this.FileGrid.Items.Cast<FileEntry>().Take(4))
                && this.SelectedEntries().Any(Entry => Entry.IsDirectory) && this.SelectedEntries().Any(Entry => !Entry.IsDirectory)
                && this.SelectionStatusText.Text == LanguageService.Text("SelectedCount", 4)
                && this.MainPanelDragCandidate is null && this.FileSelectionRectangle.IsVisible,
                "Dragging the marquee selects mixed files and folders and updates the selection count");
            Check(!((UIElement)this.FileSelectionRectangle.Parent).IsHitTestVisible,
                "The selection rectangle cannot intercept item clicks or drags");
            await Settle();
            Check(this.FileSelectionRectangle.ActualWidth > 0 && this.FileSelectionRectangle.ActualHeight > 0
                && Enumerable.Range(0, 4).All(Index => Row(Index).IsSelected),
                "The marquee overlay has a rendered area and all intersected rows display their selected state");
            if (AppMain.ScreenshotPath is { } Destination)
            {
                var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(this);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Destination)!, "MarqueeSelection.png"));
                Encoder.Save(Stream);
            }
            this.UpdateMarqueeSelection(new Point(Start.X - 40, Viewport.Bounds.Top + Viewport.RowHeight * 1.5));
            Check(this.SelectedEntries().ToHashSet().SetEquals(this.FileGrid.Items.Cast<FileEntry>().Take(2)),
                "Shrinking the rectangle removes entries outside it");
            this.EndMarqueeSelection();
            Check(this.FileGrid.SelectedItems.Count == 2 && this.MarqueeOrigin is null && !this.MarqueeActive
                && this.FileSelectionRectangle.Visibility == Visibility.Collapsed, "Releasing the marquee retains selection and removes its visual");
            Check(this.PrepareMarqueeSelection(Cell, Start, ModifierKeys.Control, 1), "Control marquee preserves the initial selection snapshot");
            this.UpdateMarqueeSelection(End);
            Check(this.SelectedEntries().ToHashSet().SetEquals(this.FileGrid.Items.Cast<FileEntry>().Skip(2).Take(2)),
                "Control marquee toggles intersected entries against the original selection");
            var Escape = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(this.FileGrid), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            this.FileGrid.RaiseEvent(Escape);
            Check(Escape.Handled && this.SelectedEntries().ToHashSet().SetEquals(this.FileGrid.Items.Cast<FileEntry>().Take(2))
                && this.MarqueeOrigin is null && this.FileSelectionRectangle.Visibility == Visibility.Collapsed,
                "Escape cancels the active rectangle and restores its initial selection");
            var ReverseCell = NameCell(3);
            var ReverseStart = ReverseCell.TranslatePoint(new Point(ReverseCell.ActualWidth - 8, ReverseCell.ActualHeight / 2), this.FileGrid);
            this.PrepareMarqueeSelection(ReverseCell, ReverseStart, ModifierKeys.Shift, 1);
            this.UpdateMarqueeSelection(new Point(ReverseStart.X - 60, Viewport.Bounds.Top + Viewport.RowHeight * 2.5));
            Check(this.FileGrid.SelectedItems.Count == 4, "A reverse-direction Shift marquee adds to existing selection");
            this.EndMarqueeSelection();
            var Text = FindVisualChild<TextBlock>(Cell)!;
            Check(!this.PrepareMarqueeSelection(Text, Text.TranslatePoint(new Point(1, 1), this.FileGrid), ModifierKeys.None, 1)
                && !this.PrepareMarqueeSelection(Cell, Start, ModifierKeys.None, 2)
                && !this.PrepareMarqueeSelection(Cell, Start, ModifierKeys.Alt, 1),
                "Item text double-clicks and alternate mouse gestures retain their existing behavior");
            var Header = FindVisualChild<DataGridColumnHeader>(this.FileGrid)!;
            Check(!this.PrepareMarqueeSelection(Header, Start, ModifierKeys.None, 1), "Headers cannot start marquee selection");
            this.OperationBusy = true;
            Check(!this.PrepareMarqueeSelection(Cell, Start, ModifierKeys.None, 1), "Busy file operations prevent new marquee selection");
            this.OperationBusy = false;
            this.PrepareMarqueeSelection(Cell, Start, ModifierKeys.None, 1);
            this.UpdateMarqueeSelection(new Point(Start.X - 50, Viewport.Bounds.Bottom + 50));
            var BeforeScroll = this.FileGrid.SelectedItems.Count;
            for (var Tick = 0; Tick < 40; Tick++) { this.ScrollMarqueeSelection(); await Settle(); }
            Check(Viewport.Viewer.VerticalOffset > 0 && this.FileGrid.SelectedItems.Count > BeforeScroll
                && this.SelectedEntries().Any(Entry => this.FileGrid.ItemContainerGenerator.ContainerFromItem(Entry) is null),
                "Edge scrolling extends selection across virtualized rows beyond the viewport"
                + $" (offset={Viewport.Viewer.VerticalOffset}, before={BeforeScroll}, selected={this.FileGrid.SelectedItems.Count}, active={this.MarqueeActive}, origin={this.MarqueeOrigin}, unrealized={this.SelectedEntries().Count(Entry => this.FileGrid.ItemContainerGenerator.ContainerFromItem(Entry) is null)})");
            this.EndMarqueeSelection();
            Check(this.MarqueeScrollTimer?.IsEnabled != true, "Completing marquee selection stops edge scrolling");
            Viewport.Viewer.ScrollToTop();
            await Settle();
            await this.NavigateAsync(Path.Combine(Fixture, "A Folder"), RecordHistory: false);
            await Settle();
            var EmptyViewport = this.MarqueeViewport()!.Value;
            var BlankStart = new Point(EmptyViewport.Bounds.Left + 30, EmptyViewport.Bounds.Top + 40);
            this.FileGrid.IsHitTestVisible = InitialHitTest;
            var Blank = this.FileGrid.InputHitTest(BlankStart) as DependencyObject;
            this.FileGrid.IsHitTestVisible = false;
            Check(this.PrepareMarqueeSelection(Blank, BlankStart, ModifierKeys.None, 1), "The empty main-panel background supports starting a marquee");
            this.UpdateMarqueeSelection(new Point(BlankStart.X + 80, BlankStart.Y + 60));
            Check(this.FileGrid.SelectedItems.Count == 0 && this.FileSelectionRectangle.IsVisible, "Marquee selection in an empty folder is harmless");
            await this.NavigateAsync(Fixture, RecordHistory: false);
            Check(this.MarqueeOrigin is null && this.FileSelectionRectangle.Visibility == Visibility.Collapsed,
                "Navigating or replacing results ends the active marquee");
        }
        finally
        {
            this.FileGrid.IsHitTestVisible = InitialHitTest;
            this.OperationBusy = false;
            this.EndMarqueeSelection();
            this.SortProperty = InitialSort;
            this.SortDirection = InitialDirection;
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned marquee fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
