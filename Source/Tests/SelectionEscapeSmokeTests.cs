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
    private async Task RunSelectionEscapeSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var Fixture = Path.Combine(AppMain.SmokeRoot!, "SelectionEscape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Fixture, "First Folder"));
        Directory.CreateDirectory(Path.Combine(Fixture, "Second 中文"));
        File.WriteAllText(Path.Combine(Fixture, "Example.txt"), "Selection fixture");
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Selection Escape UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        KeyEventArgs PressEscape(UIElement Source)
        {
            var Input = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(Source), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Source.RaiseEvent(Input);
            if (!Input.Handled)
            {
                Input.RoutedEvent = Keyboard.KeyDownEvent;
                Source.RaiseEvent(Input);
            }
            return Input;
        }
        MouseButtonEventArgs Press(UIElement Source, MouseButton Button = MouseButton.Left)
        {
            var Input = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, Button)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
            Source.RaiseEvent(Input);
            return Input;
        }
        try
        {
            Check(await this.NavigateAsync(Fixture, RecordHistory: false), "Escape selection fixture opens");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
            var Folders = this.Entries.Where(Entry => Entry.IsDirectory).ToArray();
            foreach (var Folder in Folders) this.FileGrid.SelectedItems.Add(Folder);
            var Row = (DataGridRow)this.FileGrid.ItemContainerGenerator.ContainerFromItem(Folders[0]);
            var Cell = FindVisualChild<DataGridCell>(Row)!;
            Check(this.FileGrid.SelectedItems.Count == 2 && Cell is not null, "Two folders are selected in the file list");
            this.UpdateLayout();
            Check(this.StatusText.Text.Contains(LanguageService.Text("FolderFileCount", 2, 1), StringComparison.Ordinal)
                && this.SelectionStatusText.Text == LanguageService.Text("SelectedCount", 2) && this.SelectionStatusText.IsVisible
                && this.SelectionStatusText.TranslatePoint(new Point(), this.StatusText).X >= this.StatusText.ActualWidth,
                "Selection has its own non-overlapping status area while total, folder and file counts remain visible");
            this.MainPanelDragCandidate = Folders[0];
            this.MainPanelDeferredSelection = Folders[0];
            var Escape = PressEscape(Cell!);
            Check(Escape.Handled && this.FileGrid.SelectedItems.Count == 0
                && this.MainPanelDragCandidate is null && this.MainPanelDeferredSelection is null,
                "Escape from a file-list cell clears all selected folders and pending drag selection");
            Check(this.StatusText.Text.Contains(LanguageService.Text("FolderFileCount", 2, 1), StringComparison.Ordinal)
                && SamePath(this.CurrentPath, Fixture) && this.Entries.Count == 3
                && this.SelectionStatusText.Visibility == Visibility.Collapsed && this.SelectionStatusText.Text.Length == 0,
                "Canceling selection restores folder and file counts without changing the directory or its contents");
            Check(PressEscape(this.FileGrid).Handled && this.FileGrid.SelectedItems.Count == 0,
                "Escape with no selection is harmless");
            this.FileGrid.SelectAll();
            Check(this.StatusText.Text.Contains(LanguageService.Text("FolderFileCount", 2, 1), StringComparison.Ordinal)
                && this.SelectionStatusText.Text == LanguageService.Text("SelectedCount", 3),
                "A mixed selection updates only the separate selection count");
            Check(PressEscape(this.FileGrid).Handled && this.FileGrid.SelectedItems.Count == 0,
                "Escape also clears a mixed selection of files and folders");
            this.FileGrid.SelectedItem = Folders[0];
            Check(PressEscape(this.FileGrid).Handled && this.FileGrid.SelectedItems.Count == 0,
                "Escape can clear a single selected item");
            foreach (var Folder in Folders) this.FileGrid.SelectedItems.Add(Folder);
            PressEscape(this.SearchBox);
            Check(this.FileGrid.SelectedItems.Count == 2, "Escape from the search field preserves file-list selection");
            this.BeginAddressEdit();
            Check(PressEscape(this.AddressBox).Handled && !this.AddressEditing && this.FileGrid.SelectedItems.Count == 2,
                "Escape in the address editor cancels editing while preserving file-list selection");

            this.UpdateLayout();
            var Blank = this.FileGrid.InputHitTest(new Point(20, this.FileGrid.ActualHeight - 20)) as UIElement;
            Check(Blank is not null && FindAncestor<DataGridRow>(Blank) is null,
                "The selection fixture exposes real empty space below the file rows");
            foreach (var Surface in new UIElement[] { Blank!, this.FileGrid, this.RightPanel, this.SidebarPanel,
                this.SidebarScroll, this.StatusText, this.HostWindow.TitleBrand, this.NavigationBar, this.HostWindow.WindowFrame })
            {
                this.FileGrid.SelectAll();
                this.MainPanelDragCandidate = Folders[0];
                this.MainPanelDeferredSelection = Folders[0];
                this.PendingSearchSelection.Add(Folders[0].FullPath);
                var Input = Press(Surface);
                Check((!Input.Handled || ReferenceEquals(Surface, this.FileGrid) || FindAncestor<DataGrid>(Surface) == this.FileGrid)
                    && this.FileGrid.SelectedItems.Count == 0 && this.PendingSearchSelection.Count == 0
                    && this.MainPanelDragCandidate is null && this.MainPanelDeferredSelection is null
                    && this.SelectionStatusText.Visibility == Visibility.Collapsed && SamePath(this.CurrentPath, Fixture),
                    "A blank click clears mixed selection, pending drag and selection status without consuming the click: " + Surface.GetType().Name);
            }
            this.FileGrid.SelectAll();
            foreach (var Surface in new UIElement[] { Cell!, this.SearchBox, this.HostWindow.FileMenuButton, this.AddressBar })
            {
                // Check the window's blank-click rule without inventing a row click at the desktop pointer's coordinates.
                var Input = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = Surface };
                this.ClearSelectionOnBlankMouseDown(Input);
                Check(this.FileGrid.SelectedItems.Count == 3,
                    "Window-level blank-click clearing preserves rows fields and action controls: " + Surface.GetType().Name);
            }
            Press(this.SidebarPanel, MouseButton.Right);
            Check(this.FileGrid.SelectedItems.Count == 3, "A sidebar context click preserves the file selection");
            var CaptionHandled = false;
            this.AddressWindowMouseMessage(IntPtr.Zero, 0x00A1, (IntPtr)2, IntPtr.Zero, ref CaptionHandled);
            Check(!CaptionHandled && this.FileGrid.SelectedItems.Count == 0,
                "A native blank caption click clears selection while retaining normal window dragging");
            this.FileGrid.SelectedItem = Folders[0];
            Press(Blank!);
            Check(this.FileGrid.SelectedItems.Count == 0, "A blank click also clears a single selection");
        }
        finally
        {
            this.FileGrid.SelectedItems.Clear();
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned selection fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
