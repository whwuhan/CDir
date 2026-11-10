using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunPaneBalanceSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Pane balance check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Balanced Panes")).FullName;
        var SettingsFile = Path.Combine(Root, "Settings.json");
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ExitOnClose = true }));
        MainWindow? Probe = null;
        async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); Probe!.UpdateLayout(); }
        async Task Open(WorkspaceState? Initial = null)
        {
            Probe = new MainWindow(SettingsFile, RestoreWorkspace: true, InitialWorkspace: Initial) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show(); await Probe.WorkspaceRestored; await Probe.ActiveTab!.WaitUntilReadyAsync(); await Settle();
        }
        async Task AddSplit(ExplorerTab Page, WorkspacePaneView Pane, MainWindow.PaneDock Dock)
        {
            var Body = Pane.Selected!.SplitDropBounds(Pane);
            var Point = Dock == MainWindow.PaneDock.Right ? new Point(Pane.ActualWidth - 2, Body.Top + Body.Height / 2)
                : new Point(Pane.ActualWidth / 2, Body.Bottom - 2);
            var Payload = new MainWindow.TabDragPayload(Probe!, Page);
            var Data = new DataObject(); Data.SetData(MainWindow.TabDragFormat, Payload); Probe!.ActiveTabDrag = Payload;
            Check(Probe.CompleteSplitDrop(Data, Pane, Point), "Balance fixture creates a nested " + Dock + " workspace");
            Probe.ActiveTabDrag = null; await Settle();
        }
        async Task<KeyEventArgs> Press(ExplorerTab Page, Key Key, ModifierKeys Modifiers)
        {
            var Input = new KeyEventArgs(new SmokeKeyboardDevice(Modifiers), PresentationSource.FromVisual(Page.SearchBox), 0, Key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Page.SearchBox.RaiseEvent(Input); await Settle(); return Input;
        }
        try
        {
            await Open();
            var A = Probe!.ActiveTab!; A.SetTabAlias("Left");
            var B = Probe.AddTab(); await B.WaitUntilReadyAsync(); B.SetTabAlias("Top Right");
            var C = Probe.AddTab(); await C.WaitUntilReadyAsync(); C.SetTabAlias("Bottom Right"); await Settle();
            var Original = Probe.ActivePane;
            await AddSplit(B, Original, MainWindow.PaneDock.Right);
            await AddSplit(C, Probe.ActivePane, MainWindow.PaneDock.Bottom);
            Probe.SelectTab(B); await Settle();
            var Surface = (Grid)Original.Parent;
            Surface.ColumnDefinitions[0].Width = new GridLength(4, GridUnitType.Star);
            Surface.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star); await Settle();
            var Pages = Probe.Tabs.ToArray(); var Paths = Pages.Select(Page => Page.TabPath).ToArray();
            var KeyResult = await Press(B, Key.R, ModifierKeys.Alt);
            var Widths = Probe.Panes.Select(Pane => Pane.ActualWidth).ToArray();
            var RightRows = Probe.Panes.Where(Pane => Pane.Selected != A).ToArray();
            Check(KeyResult.Handled && Widths.Max() - Widths.Min() < 1
                && Math.Abs(RightRows[0].ActualHeight - RightRows[1].ActualHeight) < 1
                && Math.Abs(Original.ActualHeight - RightRows.Sum(Pane => Pane.ActualHeight) - 5) < 1,
                "Alt+R gives the single left pane and the two stacked right panes equal column widths and equal right-row heights");
            var State = Probe.CaptureWorkspace()!;
            Check(State.Layout?.Split == "Horizontal" && Math.Abs(State.Layout.Ratio - .5) < .001
                && State.Layout.Second?.Split == "Vertical" && State.Layout.Second.Ratio == .5,
                "Balancing counts columns separately from rows and retains the original split directions");
            Check(Probe.Tabs.SequenceEqual(Pages) && Probe.Tabs.Select(Page => Page.TabPath).SequenceEqual(Paths)
                && Probe.ActiveTab == B && Probe.Panes.All(Pane => Pane.Selected!.IsVisible),
                "Balancing keeps tab order, paths, selections and the active pane unchanged");
            Check(B.ChangeShortcut("DistributePanesEvenly", "Ctrl+Alt+R"), "Pane distribution accepts a custom shortcut");
            Surface.ColumnDefinitions[0].Width = new GridLength(4, GridUnitType.Star);
            Surface.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star); await Settle();
            await Press(B, Key.R, ModifierKeys.Alt);
            Check(Math.Abs(Probe.CaptureWorkspace()!.Layout!.Ratio - .8) < .001, "The old distribution shortcut stops dispatching after reassignment");
            KeyResult = await Press(B, Key.R, ModifierKeys.Control | ModifierKeys.Alt);
            Check(KeyResult.Handled && Math.Abs(Probe.CaptureWorkspace()!.Layout!.Ratio - .5) < .001,
                "The custom distribution shortcut works while a text field has focus");
            var PreviousWidth = Probe.Width;
            Probe.WindowState = WindowState.Normal; Probe.Width = Probe.MinWidth; await Settle();
            await Press(B, Key.R, ModifierKeys.Control | ModifierKeys.Alt);
            Check(Probe.Panes.All(Pane => Pane.ActualWidth >= 439 && Pane.ActualHeight >= 179 && Pane.Selected!.MainPanelContent.IsVisible),
                "Distribution honors complete-pane minimums in a narrow window");
            Probe.Width = PreviousWidth; await Settle();
            Probe.PersistWorkspaceNow(); Probe.RequestExit(); Probe = null; await Open();
            B = Probe!.Tabs.Single(Page => Page.TabAlias == "Top Right");
            Check(Probe.Panes.Count == 3 && Probe.ActiveTab == B && B.ShortcutBindings["DistributePanesEvenly"] == "Ctrl+Alt+R"
                && Math.Abs(Probe.CaptureWorkspace()!.Layout!.Ratio - .5) < .001,
                "Balanced proportions and a personal distribution shortcut survive workspace restoration");
            if (AppMain.ScreenshotPath is { } Output)
            {
                var Bitmap = new RenderTargetBitmap((int)Probe.ActualWidth, (int)Probe.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(Probe); var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, "BalancedPanesReleasePreview.png")); Encoder.Save(Stream);
            }
            foreach (var Page in Probe.Tabs.Where(Page => Page != B).ToArray()) Probe.CloseTab(Page);
            Probe.DistributePanesEvenly();
            Check(Probe.Panes.Count == 1 && Probe.ActiveTab == B && !B.WindowClosed, "Distribution safely leaves a single-pane window unchanged");

            // Exercise the same command at larger sizes and with different binary
            // nesting orders; pane count must never become column width weight.
            WorkspacePaneState Chain(string Axis, WorkspacePaneState[] Items, bool LeftNested)
            {
                if (Items.Length == 1) return Items[0];
                return new WorkspacePaneState
                {
                    Split = Axis, Ratio = .72,
                    First = LeftNested ? Chain(Axis, Items[..^1], true) : Items[0],
                    Second = LeftNested ? Items[^1] : Chain(Axis, Items[1..], false)
                };
            }
            foreach (var (RowCounts, LeftNested) in new[]
            {
                (new[] { 2, 1 }, false), (new[] { 1, 3, 2 }, false),
                (new[] { 3, 1, 4, 2 }, true), (new[] { 4, 4, 4, 4 }, false)
            })
            {
                Probe.RequestExit(); Probe = null;
                var Tabs = new List<WorkspaceTabState>();
                var Columns = RowCounts.Select((Rows, Column) => Chain("Vertical", Enumerable.Range(0, Rows).Select(Row =>
                {
                    var Index = Tabs.Count;
                    Tabs.Add(new WorkspaceTabState { Alias = $"Column {Column + 1} Row {Row + 1}",
                        Settings = new AppSettings { LastPath = Root, ExitOnClose = true } });
                    return new WorkspacePaneState { Tabs = [Index], ActiveTab = Index };
                }).ToArray(), Column % 2 == 0)).ToArray();
                await Open(new WorkspaceState { Width = 2000, Height = 1100, Tabs = Tabs,
                    ActiveTab = 0, Layout = Chain("Horizontal", Columns, LeftNested) });
                var Before = Probe!.Tabs.ToArray();
                KeyResult = await Press(Probe.ActiveTab!, Key.R, ModifierKeys.Alt);
                var ColumnWidths = Probe.Panes.Select(Pane => Pane.ActualWidth).ToArray();
                var Description = string.Join("/", RowCounts) + (LeftNested ? " left nested" : " right nested");
                Check(KeyResult.Handled && Probe.Panes.Count == RowCounts.Sum()
                    && ColumnWidths.Max() - ColumnWidths.Min() < 1.1,
                    "Multiple columns have equal widths independent of row counts and split order: " + Description);
                foreach (var (Rows, Column) in RowCounts.Select((Rows, Column) => (Rows, Column)))
                {
                    var Panes = Probe.Panes.Where(Pane => Pane.Selected!.TabAlias.StartsWith($"Column {Column + 1} ", StringComparison.Ordinal)).ToArray();
                    var Heights = Panes.Select(Pane => Pane.ActualHeight).ToArray();
                    var Tops = Panes.Select(Pane => Pane.TranslatePoint(new Point(), Probe.WorkspaceSurface).Y).Order().ToArray();
                    Check(Panes.Length == Rows && Heights.Max() - Heights.Min() < 1.1
                        && Tops.Zip(Tops.Skip(1), (Top, Next) => Next - Top).All(Gap => Gap >= Heights.Min()),
                        "Rows evenly divide their own column without overlapping: " + Description + $", column {Column + 1}");
                }
                if (AppMain.ScreenshotPath is { } PatternOutput && RowCounts.Length == 4)
                {
                    var Bitmap = new RenderTargetBitmap((int)Probe.ActualWidth, (int)Probe.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    Bitmap.Render(Probe); var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                    using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(PatternOutput)!,
                        RowCounts.Sum() == 16 ? "BalancedSixteenPanes.png" : "BalancedManyPanes.png")); Encoder.Save(Stream);
                }
                var Once = JsonSerializer.Serialize(Probe.CaptureWorkspace()!.Layout);
                Probe.DistributePanesEvenly(); await Settle();
                Check(Probe.Tabs.SequenceEqual(Before) && Probe.ActiveTab == Before[0]
                    && JsonSerializer.Serialize(Probe.CaptureWorkspace()!.Layout) == Once,
                    "Repeated balancing preserves tabs, focus and stable proportions: " + Description);
            }

        }
        finally { Probe?.RequestExit(); }
    }
}
