using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunPaneSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Pane check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Panes")).FullName;
        var SettingsFile = Path.Combine(Root, "Settings.json");
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ExitOnClose = true }));
        MainWindow? Probe = null;
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Probe!.UpdateLayout();
        }
        async Task Open(WorkspaceState? Initial = null)
        {
            Probe = new MainWindow(SettingsFile, RestoreWorkspace: true, InitialWorkspace: Initial)
            { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show(); await Probe.WorkspaceRestored;
            await Probe.ActiveTab!.WaitUntilReadyAsync(); await Settle();
        }
        async Task<ExplorerTab> Add(string Alias)
        {
            var Page = Probe!.AddTab(); await Page.WaitUntilReadyAsync(); Page.SetTabAlias(Alias); await Settle(); return Page;
        }
        DataObject Begin(ExplorerTab Page)
        {
            var Payload = new MainWindow.TabDragPayload(Probe!, Page);
            Probe!.ActiveTabDrag = Payload;
            var Data = new DataObject(); Data.SetData(MainWindow.TabDragFormat, Payload); return Data;
        }
        Point At(WorkspacePaneView Pane, MainWindow.PaneDock Dock)
        {
            var Body = Pane.Selected!.SplitDropBounds(Pane);
            return Dock switch
            {
                MainWindow.PaneDock.Left => new(Body.Left + 4, Body.Top + Body.Height / 2),
                MainWindow.PaneDock.Right => new(Body.Right - 4, Body.Top + Body.Height / 2),
                MainWindow.PaneDock.Top => new(Body.Left + Body.Width / 2, Body.Top + 4),
                _ => new(Body.Left + Body.Width / 2, Body.Bottom - 4)
            };
        }
        void Capture(string Name)
        {
            if (AppMain.ScreenshotPath is not { } Output) return;
            Probe!.UpdateLayout();
            var Image = new RenderTargetBitmap((int)Probe.ActualWidth, (int)Probe.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            Image.Render(Probe);
            var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Image));
            using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, Name + ".png")); Encoder.Save(Stream);
        }
        async Task CheckLiveResize(WorkspacePaneView Pane, MainWindow.PaneDock Dock)
        {
            var Surface = (Grid)Pane.Parent;
            var Splitter = Surface.Children.OfType<GridSplitter>().Single();
            var Horizontal = Splitter.ResizeDirection == GridResizeDirection.Columns;
            var FirstPane = (WorkspacePaneView)Surface.Children[0];
            var SecondPane = (WorkspacePaneView)Surface.Children[2];
            double First() => Horizontal ? FirstPane.ActualWidth : FirstPane.ActualHeight;
            double Second() => Horizontal ? SecondPane.ActualWidth : SecondPane.ActualHeight;
            void Start() => Splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            void Delta(double Amount) => Splitter.RaiseEvent(new DragDeltaEventArgs(Horizontal ? Amount : 0, Horizontal ? 0 : Amount)
                { RoutedEvent = Thumb.DragDeltaEvent });
            void Finish(bool Cancel) => Splitter.RaiseEvent(new DragCompletedEventArgs(0, 0, Cancel) { RoutedEvent = Thumb.DragCompletedEvent });
            var BeforeFirst = First(); var BeforeSecond = Second();
            Start(); Delta(24); await Settle();
            Check(Math.Abs(First() - BeforeFirst - 24) < 1 && Math.Abs(Second() - BeforeSecond + 24) < 1,
                Dock + " divider resizes both full workspaces before the drag completes");
            Delta(16); await Settle();
            Check(Math.Abs(First() - BeforeFirst - 40) < 1 && Math.Abs(Second() - BeforeSecond + 40) < 1
                && FirstPane.Selected!.ContentPanels.IsVisible && SecondPane.Selected!.ContentPanels.IsVisible,
                Dock + " divider renders successive live content sizes throughout the drag");
            Capture("PaneResize" + Dock + "Live");
            Finish(false); await Settle();
            var Saved = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(SettingsFile + ".workspace.json"))!;
            Check(Math.Abs(Saved.Layout!.Ratio - First() / (First() + Second())) < .001,
                Dock + " divider saves its live ratio when released");
            var Committed = First();
            Start(); Delta(-20); await Settle();
            Splitter.RaiseEvent(new System.Windows.Input.KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(Splitter), Environment.TickCount, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            Finish(true); await Settle();
            Check(Math.Abs(First() - Committed) < 1 && Math.Abs(Second() - BeforeSecond + 40) < 1,
                Dock + " cancelling a live resize restores the previously committed sizes");
            Start(); Delta(-40); await Settle(); Finish(false); await Settle();
            Check(Math.Abs(First() - BeforeFirst) < 1 && Math.Abs(Second() - BeforeSecond) < 1,
                Dock + " divider can immediately resize back after a cancelled drag");
        }
        try
        {
            await Open();
            var A = Probe!.ActiveTab!; A.SetTabAlias("Workspace");
            Check(Probe.Panes.Count == 1 && InstanceWorkspaceService.Peers().Length == 0 && !InstanceWorkspaceService.IsUserInstance(Environment.ProcessId),
                "Pane checks own their settings and never register or discover user instances");
            var Single = Begin(A); var Original = Probe.ActivePane;
            Check(!Probe.CompleteSplitDrop(Single, Original, At(Original, MainWindow.PaneDock.Left)) && Probe.Panes.Count == 1,
                "The only tab cannot split its own pane into an empty workspace");
            Probe.ActiveTabDrag = null;
            foreach (var Dock in Enum.GetValues<MainWindow.PaneDock>())
            {
                var B = await Add("Split " + Dock); var Pane = Probe.ActivePane;
                var Data = Begin(B); var Position = At(Pane, Dock);
                var Path = B.TabPath; var History = B.History.ToArray();
                Check(Probe.UpdateSplitFeedback(Data, Pane, Position) == DragDropEffects.Move && Pane.SplitPreview.IsVisible
                    && !Pane.SplitPreview.IsHitTestVisible && Probe.Panes.Count == 1,
                    Dock + " previews before changing the layout");
                Pane.UpdateLayout();
                Check(Probe.SplitDockAt(Pane, Position) == Dock, Dock + " uses the body edge as its snap target");
                Capture("PanePreview" + Dock);
                if (Dock == MainWindow.PaneDock.Right)
                {
                    var Body = Pane.Selected!.SplitDropBounds(Pane);
                    foreach (var Inset in new[] { 0d, .25, 1, 4, 12, 24 })
                    foreach (var Height in new[] { .05, .5, .95 })
                        Check(Probe.UpdateSplitFeedback(Data, Pane, new Point(Pane.ActualWidth - Inset, Body.Top + Body.Height * Height)) == DragDropEffects.Move
                            && Pane.SplitPreview.HorizontalAlignment == HorizontalAlignment.Right,
                            $"Right-edge snap stays available across gutter inset {Inset} and body height {Height}");
                    var WindowPoint = new Point(Probe.ActualWidth - .25, Pane.TranslatePoint(new Point(0, Body.Top + Body.Height / 2), Probe).Y);
                    DragEventArgs Route(RoutedEvent Event)
                    {
                        var Input = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                            binder: null, args: [Data, DragDropKeyStates.None, DragDropEffects.Move, Probe, WindowPoint], culture: null)!;
                        Input.RoutedEvent = Event; Probe.RaiseEvent(Input); return Input;
                    }
                    var Enter = Route(DragDrop.PreviewDragEnterEvent); var Over = Route(DragDrop.PreviewDragOverEvent);
                    Check(Enter.Handled && Over.Handled && Enter.Effects == DragDropEffects.Move && Over.Effects == DragDropEffects.Move && Pane.SplitPreview.IsVisible,
                        "The outermost window border routes a tab drag to the right pane snap preview");
                    var Drop = Route(DragDrop.PreviewDropEvent);
                    Check(Drop.Handled && Drop.Effects == DragDropEffects.Move && Probe.Panes.Count == 2,
                        "Dropping on the outermost right window border completes the split");
                }
                else Check(Probe.CompleteSplitDrop(Data, Pane, Position), Dock + " drop creates a pane");
                Probe.ActiveTabDrag = null; await Settle();
                var New = Probe.ActivePane;
                var Origin = New.TranslatePoint(new Point(), Pane);
                Check(Probe.Panes.Count == 2 && A.IsVisible && B.IsVisible && A.IsHitTestVisible && B.IsHitTestVisible
                    && A.IsTabSelected && B.IsTabSelected && B.TabPath == Path && B.History.SequenceEqual(History),
                    Dock + " keeps both full workspaces live and preserves the moved tab state");
                Check(Dock switch
                {
                    MainWindow.PaneDock.Left => Origin.X < 0 && Math.Abs(New.ActualWidth - Pane.ActualWidth) < 2,
                    MainWindow.PaneDock.Right => Origin.X > 0 && Math.Abs(New.ActualWidth - Pane.ActualWidth) < 2,
                    MainWindow.PaneDock.Top => Origin.Y < 0 && Math.Abs(New.ActualHeight - Pane.ActualHeight) < 2,
                    _ => Origin.Y > 0 && Math.Abs(New.ActualHeight - Pane.ActualHeight) < 2
                }, Dock + " creates the previewed equal halves");
                foreach (var Workspace in Probe.Panes)
                {
                    var Page = Workspace.Selected!;
                    Check(Workspace.TabHeaders.IsVisible && Page.AddressBar.IsVisible && Page.SearchBox.IsVisible
                        && Page.SidebarPanel.IsVisible && Page.MainPanelContent.IsVisible && Page.StatusBarPanel.IsVisible,
                        Dock + " includes independent tabs, address, search, tree, file panel and status");
                }
                Capture("Pane" + Dock);
                await CheckLiveResize(Pane, Dock);
                if (Dock == MainWindow.PaneDock.Right)
                {
                    var PreviousState = Probe.WindowState;
                    var PreviousWidth = Probe.Width;
                    Probe.WindowState = WindowState.Normal; Probe.Width = Probe.MinWidth;
                    await Settle();
                    Check(Probe.Panes.All(Workspace => Workspace.Selected!.AddressBar.ActualWidth >= 80
                        && Workspace.Selected!.SearchBox.ActualWidth > 80 && Workspace.Selected!.MainPanelContent.ActualWidth >= 120),
                        "Side-by-side panes retain usable address, search and file content at the minimum window width");
                    var Small = Probe.ActivePane; var SmallData = Begin(B);
                    Check(Probe.UpdateSplitFeedback(SmallData, Small, At(Small, MainWindow.PaneDock.Right)) == DragDropEffects.None,
                        "A pane too narrow for two complete workspaces rejects another horizontal split");
                    Probe.ActiveTabDrag = null; Probe.Width = PreviousWidth; Probe.WindowState = PreviousState; await Settle();
                }
                Check(Probe.CloseTab(B), "Closing the last tab in the " + Dock + " pane succeeds");
                await Settle();
                Check(Probe.Panes.Count == 1 && A.IsVisible && ReferenceEquals(Probe.ActiveTab, A), "Closing " + Dock + " collapses the empty branch");
            }
            var C = await Add("Projects");
            var D = await Add("Reference");
            var Target = Probe.ActivePane;
            var Drag = Begin(C);
            var Center = C.SplitDropBounds(Target);
            Check(Probe.UpdateSplitFeedback(Drag, Target, new Point(Center.Left + Center.Width / 2, Center.Top + Center.Height / 2)) == DragDropEffects.None
                && Target.SplitPreview.Visibility == Visibility.Collapsed,
                "Dropping in the center leaves the workspace unsplit");
            Check(Probe.UpdateSplitFeedback(new DataObject(DataFormats.FileDrop, new[] { Root }), Target, At(Target, MainWindow.PaneDock.Right)) == DragDropEffects.None,
                "Filesystem drops cannot trigger tab splitting");
            var Forged = new DataObject(); Forged.SetData(MainWindow.TabDragFormat, new MainWindow.TabDragPayload(Probe, C));
            Check(!Probe.CompleteSplitDrop(Forged, Target, At(Target, MainWindow.PaneDock.Right)), "A forged tab drag cannot split the layout");
            Probe.ActiveTabDrag = null;
            Check(!Probe.CompleteSplitDrop(Drag, Target, At(Target, MainWindow.PaneDock.Right)), "A cancelled drag token cannot split the layout");
            Probe.SelectTab(C); Probe.UpdateLayout();
            var Header = (FrameworkElement)Probe.TabHeaders.ItemContainerGenerator.ContainerFromItem(Probe.Headers.Single(Item => Item.Page == C));
            Probe.StartTabDragDrop = (_, Data, _) =>
            {
                Check(Probe.UpdateSplitFeedback((IDataObject)Data, Target, At(Target, MainWindow.PaneDock.Right)) == DragDropEffects.Move,
                    "The actual header drag session supports snap preview");
                return DragDropEffects.None;
            };
            Check(Probe.PrepareTabDrag(Header, 1, new Point()) && Probe.TryStartTabDrag(new Point(40, 40), true)
                && Probe.ActiveTabDrag is null && Target.SplitPreview.Visibility == Visibility.Collapsed && Probe.Panes.Count == 1,
                "Cancelling the header drag clears the preview without changing tabs");
            Drag = Begin(C);
            Check(Probe.CompleteSplitDrop(Drag, Target, At(Target, MainWindow.PaneDock.Right)), "A right pane can be created for nesting");
            Probe.ActiveTabDrag = null; await Settle();
            var Right = Probe.ActivePane;
            Probe.SelectTab(A);
            var Left = Probe.ActivePane;
            Drag = Begin(D);
            Check(Probe.CompleteSplitDrop(Drag, Left, At(Left, MainWindow.PaneDock.Bottom)), "An existing pane can split vertically inside a horizontal layout");
            Probe.ActiveTabDrag = null; await Settle();
            Check(Probe.Panes.Count == 3 && Probe.Panes.All(Pane => Pane.Selected!.IsVisible), "A nested layout presents three independently selected tabs");
            var Outer = (Grid)((Grid)Left.Parent).Parent;
            Check(Outer.ColumnDefinitions[0].MinWidth >= 440 && ((Grid)Left.Parent).RowDefinitions[0].MinHeight >= 180,
                "Nested branches reserve the minimum size of their complete child workspaces");
            A.SearchBox.Text = "Alpha"; C.SearchBox.Text = "Beta";
            Check(A.SearchBox.Text == "Alpha" && C.SearchBox.Text == "Beta" && D.SearchBox.Text == "", "Search fields are independent across panes");
            A.SearchBox.Text = ""; C.SearchBox.Text = "";
            // Route input to the other visible pane without moving the desktop pointer.
            A.AddressBar.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent });
            Check(ReferenceEquals(Probe.ActiveTab, A), "Pointer input in a visible pane selects its command target");
            await Probe.ExecuteTabCommandAsync("NewTab"); var Extra = Probe.ActiveTab!;
            await Extra.WaitUntilReadyAsync(); await Settle();
            Check(ReferenceEquals(Extra.Parent, A.Parent) && Right.Selected == C, "New-tab shortcuts affect the active pane only");
            await Probe.ExecuteTabCommandAsync("PreviousTab");
            Check(ReferenceEquals(Probe.ActiveTab, A), "Tab cycling stays inside the active pane");
            Probe.CloseTab(Extra);
            Probe.SelectTab(C);
            foreach (var Page in Probe.Tabs) Page.Settings.SeparatePinnedTabs = true;
            Probe.SetTabPinned(C, true); Probe.UpdatePinnedTabLayout(true);
            await Settle();
            Check(Right.PinnedHeaders.Single().Page == C && Left.PinnedHeaders.Count == 0, "Pinned rows belong to their own pane");
            var Branch = (Grid)Left.Parent;
            Branch.RowDefinitions[0].Height = new GridLength(2, GridUnitType.Star);
            Branch.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
            await Settle();
            var State = Probe.CaptureWorkspace()!;
            Check(State.Layout?.Split == "Horizontal" && State.Layout.First?.Split == "Vertical" && Math.Abs(State.Layout.First.Ratio - 2d / 3) < .001,
                "Workspace capture includes nested orientation and splitter proportions");
            Check(Probe.PersistWorkspaceNow(), "Split layouts are persisted in the owned workspace");
            Capture("PaneNested");
            Probe.RequestExit(); Probe = null;
            await Open();
            A = Probe!.Tabs.Single(Page => Page.TabAlias == "Workspace");
            C = Probe.Tabs.Single(Page => Page.TabAlias == "Projects");
            D = Probe.Tabs.Single(Page => Page.TabAlias == "Reference");
            Check(Probe.Panes.Count == 3 && Probe.Panes.All(Pane => Pane.Selected!.IsVisible) && Probe.ActiveTab == C && C.IsTabPinned
                && Probe.CaptureWorkspace()!.Layout!.First!.Ratio == State.Layout!.First!.Ratio,
                $"Reopening restores panes, ratios, per-pane selection, pin state and the active pane (panes={Probe.Panes.Count}, visible={string.Join(',', Probe.Panes.Select(Pane => Pane.Selected!.IsVisible))}, active={Probe.ActiveTab?.TabAlias}, pinned={C.IsTabPinned}, ratio={Probe.CaptureWorkspace()!.Layout!.First!.Ratio:R}, expected={State.Layout!.First!.Ratio:R})");
            Capture("PaneRestored");
            var Source = Probe.ActivePane;
            Probe.SelectTab(A); Target = Probe.ActivePane;
            Drag = Begin(C);
            Check(Probe.CompleteTabDrop(Drag, false, new Point(Target.NormalTabRow.ActualWidth - 4, 8)), "A tab can move onto another pane's tab strip");
            Probe.ActiveTabDrag = null; await Settle();
            Check(Probe.Panes.Count == 2 && ReferenceEquals(C.Parent, A.Parent) && !C.IsTabPinned && !Probe.Panes.Contains(Source),
                "Moving the last tab to another strip merges its empty pane and updates the pin state");
            Probe.SelectTab(D); D.OperationBusy = true;
            Check(!Probe.CloseTab(D) && Probe.Panes.Count == 2, "A busy tab keeps its pane open");
            D.OperationBusy = false;
            Check(Probe.CloseTab(D) && Probe.Panes.Count == 1, "Closing the last nested tab restores a single full workspace");
            await Settle();
            // Leave a clean two-pane example as the release preview.
            Probe.SelectTab(C); Drag = Begin(C); Target = Probe.ActivePane;
            Check(Probe.CompleteSplitDrop(Drag, Target, At(Target, MainWindow.PaneDock.Right)), "The final preview uses the tested two-pane release");
            Probe.ActiveTabDrag = null; await Settle(); Capture("PaneReleasePreview");
            var Invalid = Probe.CaptureWorkspace()!;
            Invalid.Layout!.First!.Tabs.Add(Invalid.Layout.Second!.Tabs[0]);
            Probe.RequestExit(); Probe = null;
            await Open(Invalid);
            Check(Probe!.Panes.Count == 1 && Probe.Tabs.Count == 2 && Probe.Tabs.All(Page => ReferenceEquals(Page.Parent, Probe.TabPages)),
                "An invalid or duplicated pane layout safely falls back to the complete flat tab list");
        }
        finally { if (Probe is not null) { Probe.ActiveTabDrag = null; foreach (var Page in Probe.Tabs) Page.OperationBusy = false; Probe.RequestExit(); } }
    }
}
