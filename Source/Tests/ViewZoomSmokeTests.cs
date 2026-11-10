using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunViewZoomSmokeAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("View zoom check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "View Zoom")).FullName;
        var Other = Directory.CreateDirectory(Path.Combine(Root, "Other")).FullName;
        var Deep = Directory.CreateDirectory(Path.Combine(Root, "Child", "Grandchild", "Deepest")).FullName;
        for (var Index = 0; Index < 80; Index++) File.WriteAllText(Path.Combine(Root, $"Sample {Index:000}.txt"), "Owned zoom fixture");
        var Native = new NativeShellView();
        var Reference = new Window { Owner = this.HostWindow, Content = Native, Width = 600, Height = 400, ShowActivated = false, ShowInTaskbar = false };
        var Results = new List<object>();
        try
        {
            Reference.Show(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Native.UseModernZoomView();
            await Native.NavigateAsync(Root);
            foreach (var Size in Enumerable.Range(16, 241))
                foreach (var Delta in new[] { -120, 120 })
                {
                    var Actual = Native.ProbeZoom(1, Size, Delta);
                    var Expected = FileViewZoom.Step(FileViewMode.MediumIcons, Size, Delta > 0);
                    // Explorer's outer view selector continues into List at the
                    // smallest size; the embedded icon view itself clamps to 16.
                    Check(Expected.Size == Actual.Size, $"Icon zoom matches Windows Shell: {Size}, delta {Delta}, result {Actual.Size}");
                    Results.Add(new { Size, Delta, ActualMode = Actual.Mode, ActualSize = Actual.Size });
                }
            if (AppMain.ScreenshotPath is { } Output)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!, "NativeViewZoom.json"), JsonSerializer.Serialize(Results));
        }
        finally { Reference.Close(); }

        var Configuration = Path.Combine(AppMain.SmokeRoot!, "ZoomSettings.json");
        File.WriteAllText(Configuration, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ExitOnClose = true }));
        MainWindow? Probe = null;
        async Task Open()
        {
            Probe = new MainWindow(Configuration, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show(); await Probe.WorkspaceRestored; await Probe.ActiveTab!.WaitUntilReadyAsync();
        }
        async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); Probe!.UpdateLayout(); }
        bool Wheel(ExplorerTab Page, int Delta, ModifierKeys Modifiers = ModifierKeys.Control, UIElement? Target = null)
        {
            var Read = Page.ReadFileViewWheelModifiers;
            try
            {
                Page.ReadFileViewWheelModifiers = () => Modifiers;
                var Input = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, Delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                (Target ?? Page.MainPanelContent).RaiseEvent(Input);
                return Input.Handled;
            }
            finally { Page.ReadFileViewWheelModifiers = Read; }
        }
        try
        {
            await Open(); var Page = Probe!.ActiveTab!;
            var Selected = Page.Entries.First(Entry => !Entry.IsDirectory);
            Page.SelectPath(Selected.FullPath);
            var Widths = Page.FileGrid.Columns.Select(Column => Column.Width).ToArray();
            FocusManager.SetFocusedElement(Probe, Page.SearchBox);
            Check(Wheel(Page, 120) && Page.Settings.FileView == FileViewMode.List,
                "Ctrl+wheel up over Details changes the real main pane to List");
            Check(ReferenceEquals(FocusManager.GetFocusedElement(Probe), Page.SearchBox),
                "Hover zoom preserves the current logical keyboard focus");
            Wheel(Page, 120, Target: Page.SearchBox); Wheel(Page, 120, Target: Page.FolderTree);
            Check(Page.Settings.FileView == FileViewMode.List,
                "Wheel zoom is scoped to the main file pane, excluding search and the directory tree");
            Check(!Wheel(Page, 120, ModifierKeys.None) && !Wheel(Page, 120, ModifierKeys.Control | ModifierKeys.Alt)
                && Page.Settings.FileView == FileViewMode.List, "Ordinary scrolling and other modifier combinations do not change the file view");
            Check(Wheel(Page, 60) && Page.Settings.FileView == FileViewMode.List
                && Wheel(Page, 60) && Page.Settings.FileView == FileViewMode.SmallIcons,
                "High-resolution wheel deltas accumulate to one 120-unit detent");
            Wheel(Page, -240);
            Check(Page.IsDetailsView, "A multi-detent wheel event processes every view transition");
            Wheel(Page, -240);
            Check(Page.Settings.FileView == FileViewMode.Content && Wheel(Page, -120)
                && Page.Settings.FileView == FileViewMode.Content, "The minimum Content view clamps without cycling");
            var Seen = new HashSet<FileViewMode> { Page.Settings.FileView };
            for (var Index = 0; Index < 70; Index++) { Wheel(Page, 120); Seen.Add(Page.Settings.FileView); }
            await Settle();
            Check(Seen.SetEquals(Enum.GetValues<FileViewMode>()) && Page.Settings.FileView == FileViewMode.ExtraLargeIcons
                && Page.Settings.FileViewIconSize == 256, "Wheel zoom traverses every layout and clamps at 256-pixel extra-large icons");
            Check(Page.SelectedEntries().Single() == Selected && Page.FileGrid.Columns.Select(Column => Column.Width).SequenceEqual(Widths),
                "Zoom preserves file selection and existing detail-column widths");
            Page.ChangeFileView(FileViewMode.MediumIcons); Wheel(Page, 120); await Settle();
            var Pixels = Page.Settings.FileViewIconSize;
            Check(Pixels == 53 && Page.Resources["FileViewIconPixels"] is int ActualPixels && ActualPixels == Pixels
                && FindVisualChild<FileTilePanel>(Page.FileTiles)!.CellWidth == Pixels + 48,
                "Intermediate native icon sizes update both the thumbnail request and actual cell geometry");
            await Task.Delay(350); await Settle();
            Check(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Configuration))!.FileViewIconSize == Pixels,
                "Wheel gestures persist their exact icon size after the short write debounce");
            Page.ColumnMenuClick(Page.ColumnMenuButton, new RoutedEventArgs(Button.ClickEvent));
            var Menu = Page.ColumnMenuButton.ContextMenu!;
            Check(Menu.Items.OfType<MenuItem>().Single(Item => Item.Tag is FileViewMode Mode && Mode == Page.Settings.FileView).IsChecked,
                "The view dropdown reflects the layout reached by wheel zoom");
            Menu.IsOpen = false;
            Page.SearchBox.Text = "/file"; await Page.WaitForSearchSmokeAsync();
            Wheel(Page, 120); var SearchSize = Page.Settings.FileViewIconSize;
            Check(SearchSize > Pixels && Page.FileTiles.IsVisible && Page.Resources["FileViewIconPixels"] is int SearchPixels && SearchPixels == SearchSize,
                "Search results use and update the same zoomed view as their current folder");
            Page.SearchBox.Clear(); await Page.WaitForSearchSmokeAsync();
            Check(Page.Settings.FileViewIconSize == SearchSize, "Leaving search preserves the folder's exact icon size");
            await Page.NavigateAsync(Other); Page.ChangeFileView(FileViewMode.Content);
            await Page.NavigateAsync(Root);
            Check(Page.Settings.FileViewIconSize == SearchSize, "Returning to a folder restores its exact intermediate zoom");
            Page.HostWindow.ApplyFolderViewRecursively(Root, Page.Settings.FileView, SearchSize);
            Check(Probe.FolderIconSizeFor(Deep) == SearchSize
                && Probe.FolderIconSizeFor(Path.Combine(Deep, "Future")) == SearchSize,
                "Recursive Apply includes intermediate icon sizes at every depth and in future folders");
            await Page.NavigateAsync(Other); Page.ChangeFileView(FileViewMode.Content);
            await Page.NavigateAsync(Root); Page.SaveSettings(); Probe.PersistWorkspaceNow();
            Probe.RequestExit(); Probe = null;
            await Open(); Page = Probe!.ActiveTab!;
            Check(Page.Settings.FileViewIconSize == SearchSize && Probe.FolderIconSizeFor(Deep) == SearchSize,
                "Restart restores current-folder zoom and inherited descendant icon sizes");
            await Page.NavigateAsync(Other);
            Check(Page.Settings.FileView == FileViewMode.Content && Page.Settings.FileViewIconSize == 0,
                "Restart retains independent folder layouts without leaking icon sizes into Content");
            Probe.ApplyFolderViewRecursively(Root, FileViewMode.Details);
            Check(Probe.FolderIconSizeFor(Deep) == 0, "Reset removes descendant icon-size overrides along with their view choices");
            Page.ChangeFileView(FileViewMode.LargeIcons); Wheel(Page, 60);
            Page.ChangeFileView(FileViewMode.MediumIcons); Wheel(Page, 60);
            Check(Page.Settings.FileView == FileViewMode.MediumIcons && Page.Settings.FileViewIconSize == 0,
                "Selecting a menu view clears an incomplete previous wheel gesture");
        }
        finally { Probe?.RequestExit(); }
    }
}
