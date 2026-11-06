using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunTabDragSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Tab drag check failed: " + Name);
            Passed.Add(Name);
        }
        var SettingsFile = Path.Combine(AppMain.SmokeRoot!, "TabDrag.json");
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = this.CurrentPath }));
        MainWindow? Probe = null;
        async Task Open()
        {
            Probe = new MainWindow(SettingsFile, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show();
            await Probe.ActiveTab!.WaitUntilReadyAsync();
            await Probe.WorkspaceRestored;
        }
        FrameworkElement Header(ExplorerTab Page)
        {
            Probe!.UpdateLayout();
            var Item = Probe.Headers.Single(Header => ReferenceEquals(Header.Page, Page));
            var Items = Probe.PinnedHeaders.Contains(Item) ? Probe.PinnedTabHeaders : Probe.TabHeaders;
            return (FrameworkElement)Items.ItemContainerGenerator.ContainerFromItem(Item);
        }
        Button[] Buttons(ExplorerTab Page) => ((Grid)FindVisualChild<Border>(Header(Page))!.Child).Children.OfType<Button>().ToArray();
        Point At(ExplorerTab Page, bool After = false)
        {
            var Container = Header(Page);
            var Row = Probe!.PinnedHeaders.Any(Item => ReferenceEquals(Item.Page, Page)) ? Probe.PinnedTabRow : Probe.NormalTabRow;
            return Container.TranslatePoint(new Point(After ? Container.ActualWidth - 1 : 1, 8), Row);
        }
        DataObject Begin(ExplorerTab Page)
        {
            var Payload = new MainWindow.TabDragPayload(Probe!, Page);
            var Data = new DataObject();
            Data.SetData(MainWindow.TabDragFormat, Payload);
            Probe!.ActiveTabDrag = Payload;
            return Data;
        }
        void Drop(ExplorerTab Page, bool PinnedRow, Point Position, string Expected)
        {
            var Data = Begin(Page);
            try { Check(Probe!.CompleteTabDrop(Data, PinnedRow, Position), "A tab drop changes pin state or order: " + Expected); }
            finally { Probe!.ActiveTabDrag = null; }
            Check(string.Join("", Probe!.Tabs.Select(Page => Page.TabAlias)) == Expected
                && Probe.Headers.Select(Item => Item.Page).SequenceEqual(Probe.Tabs)
                && Probe.PinnedTabDropMarker.Visibility == Visibility.Collapsed && Probe.NormalTabDropMarker.Visibility == Visibility.Collapsed,
                "Rendered and logical tab order agree after drop: " + Expected);
        }
        void Capture(string Name)
        {
            if (AppMain.ScreenshotPath is not { } Output) return;
            Probe!.UpdateLayout();
            var Bitmap = new RenderTargetBitmap((int)Probe.ActualWidth, (int)Probe.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            Bitmap.Render(Probe);
            var Encoder = new PngBitmapEncoder();
            Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
            using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, Name + ".png"));
            Encoder.Save(Stream);
        }
        try
        {
            await Open();
            var A = Probe!.ActiveTab!; A.SetTabAlias("A");
            var B = Probe.AddTab(); await B.WaitUntilReadyAsync(); B.SetTabAlias("B");
            var C = Probe.AddTab(); await C.WaitUntilReadyAsync(); C.SetTabAlias("C");
            var D = Probe.AddTab(); await D.WaitUntilReadyAsync(); D.SetTabAlias("D");
            var E = Probe.AddTab(); await E.WaitUntilReadyAsync(); E.SetTabAlias("E");
            var Active = Probe.ActiveTab;
            var OriginalPaths = Probe.Tabs.ToDictionary(Page => Page, Page => (Page.TabPath, History: Page.History.ToArray()));
            var HeaderButtons = Buttons(A);
            Check(Probe.PrepareTabDrag(FindVisualChild<TextBlock>(HeaderButtons[0])!, 1, new Point(10, 10))
                && !Probe.TryStartTabDrag(new Point(11, 11), true)
                && !Probe.TryStartTabDrag(new Point(80, 10), false)
                && !Probe.TryStartTabDrag(new Point(80, 10), true),
                "A click below the drag threshold or a released button never starts a tab drag");
            Check(!Probe.PrepareTabDrag(HeaderButtons[1], 1, new Point())
                && !Probe.PrepareTabDrag(FindVisualChild<TextBlock>(HeaderButtons[1])!, 1, new Point())
                && !Probe.PrepareTabDrag(HeaderButtons[2], 1, new Point())
                && !Probe.PrepareTabDrag(Probe.NewTabButton, 1, new Point())
                && !Probe.PrepareTabDrag(HeaderButtons[0], 2, new Point()),
                "Pin, Close, New Tab and double-click actions cannot start a drag");
            var StartCount = 0;
            Probe.StartTabDragDrop = (Source, Data, Effects) =>
            {
                StartCount++;
                Check(ReferenceEquals(Source, Probe.TabBar) && Effects == DragDropEffects.Move && Probe.ActiveTabDrag?.Page == A,
                    "Crossing the threshold starts an owned Move session for the pressed tab");
                Check(Probe.UpdateTabDragFeedback((IDataObject)Data, false, At(B)) == DragDropEffects.Move
                    && Probe.NormalTabDropMarker.IsVisible, "An active drag displays its insertion marker");
                return DragDropEffects.None;
            };
            Check(Probe.PrepareTabDrag(HeaderButtons[0], 1, new Point()) && Probe.TryStartTabDrag(new Point(100, 0), true)
                && StartCount == 1 && Probe.ActiveTabDrag is null && !Probe.NormalTabDropMarker.IsVisible
                && Probe.Tabs.SequenceEqual(new[] { A, B, C, D, E }),
                "Cancelling a drag clears feedback and preserves all tab order");
            Probe.SetTabPinned(A, true); Probe.SetTabPinned(B, true); Probe.SetTabPinned(C, true);
            Drop(C, false, At(A), "CABDE");
            Drop(C, false, At(B), "ACBDE");
            Drop(A, false, At(B, After: true), "CBADE");
            Drop(E, false, At(D), "CBAED");
            Drop(D, false, At(E), "CBADE");
            Drop(B, false, new Point(Probe.NormalTabRow.ActualWidth - 4, 8), "CABDE");
            Drop(E, false, new Point(1, 8), "CABED");
            Check(Probe.Tabs.Take(3).All(Page => Page.IsTabPinned) && Probe.Tabs.Skip(3).All(Page => !Page.IsTabPinned),
                "Single-row reordering preserves the pinned and ordinary groups");
            var Stale = Begin(A); Probe.ActiveTabDrag = null;
            var Forged = new DataObject();
            var Token = new MainWindow.TabDragPayload(Probe, A); Probe.ActiveTabDrag = Token;
            Forged.SetData(MainWindow.TabDragFormat, new MainWindow.TabDragPayload(Probe, A));
            Check(!Probe.CompleteTabDrop(Stale, false, At(C)) && !Probe.CompleteTabDrop(Forged, false, At(C))
                && !Probe.CompleteTabDrop(new DataObject(DataFormats.FileDrop, new[] { this.CurrentPath }), false, At(C)),
                "Stale sessions, forged tokens and filesystem drops cannot reorder tabs");
            var Foreign = new DataObject();
            var ForeignToken = new MainWindow.TabDragPayload(this.HostWindow, this);
            Foreign.SetData(MainWindow.TabDragFormat, ForeignToken); Probe.ActiveTabDrag = ForeignToken;
            Check(!Probe.CompleteTabDrop(Foreign, false, At(C)), "A tab from another window cannot change this window's order");
            Probe.ActiveTabDrag = null;
            foreach (var Page in Probe.Tabs) Page.Settings.SeparatePinnedTabs = true;
            Probe.UpdatePinnedTabLayout(true);
            Drop(B, true, At(C), "BCAED");
            Drop(D, false, At(E), "BCADE");
            Drop(D, true, At(C), "BDCAE");
            Check(D.IsTabPinned, "Dropping an ordinary tab into the pinned row pins it at the insertion point");
            Drop(C, false, At(E, After: true), "BDAEC");
            Check(!C.IsTabPinned, "Dropping a pinned tab into the ordinary row unpins it at the insertion point");
            Check(ReferenceEquals(Probe.ActiveTab, Active) && OriginalPaths.All(Pair => Pair.Key.TabPath == Pair.Value.TabPath
                && Pair.Key.History.SequenceEqual(Pair.Value.History)), "Dragging preserves the active workspace, every folder and navigation history");
            var Feedback = Begin(C);
            Check(Probe.UpdateTabDragFeedback(Feedback, true, At(D)) == DragDropEffects.Move && Probe.PinnedTabDropMarker.IsVisible,
                "A cross-row drop shows its destination before changing any state");
            Capture("TabDragInsertion");
            Probe.ActiveTabDrag = null;
            Probe.CompleteTabDrop(new DataObject(), true, new Point());
            Capture("TabDragBothRows");
            Check(Probe.PersistWorkspaceNow(), "The dragged tab order is saved while the window remains open");
            Probe.RequestExit(); Probe = null;
            await Open();
            Check(string.Join("", Probe!.Tabs.Select(Page => Page.TabAlias)) == "BDAEC"
                && Probe.Tabs.Select(Page => Page.IsTabPinned).SequenceEqual(new[] { true, true, true, false, false })
                && Probe.ActiveTab!.TabAlias == "E" && Probe.PinnedHeaders.Count == 3 && Probe.VisibleHeaders.Count == 2,
                "Reopening restores both reordered groups, pin states and the active tab");
            foreach (var Page in Probe.Tabs.Where(Page => Page.IsTabPinned).ToArray())
            {
                var Data = Begin(Page);
                Check(Probe.CompleteTabDrop(Data, false, new Point(Probe.NormalTabRow.ActualWidth - 4, 8)), "A pinned tab can be dragged into the ordinary row");
                Probe.ActiveTabDrag = null;
            }
            Check(Probe.PinnedTabRow.Visibility == Visibility.Collapsed && Probe.VisibleHeaders.Count == 5,
                "Dragging the last pinned tab out collapses its row");
            var MissingRowData = Begin(Probe.ActiveTab!);
            Check(Probe.UpdateTabDragFeedback(MissingRowData, true, new Point()) == DragDropEffects.None,
                "The hidden pinned row is not a drop target until a tab is pinned");
            Probe.ActiveTabDrag = null;
            Probe.Width = Probe.MinWidth;
            foreach (var Page in Probe.Tabs) Page.SetTabAlias("A long tab caption for drag overflow " + Probe.Tabs.IndexOf(Page));
            Probe.UpdatePinnedTabLayout(false);
            Probe.SelectTab(Probe.Tabs.Last());
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var Offset = Probe.TabScroll.HorizontalOffset;
            var Overflow = Begin(Probe.Tabs.Last());
            Check(Offset > 0 && Probe.UpdateTabDragFeedback(Overflow, false, new Point(1, 8)) == DragDropEffects.Move
                && Probe.TabScroll.HorizontalOffset < Offset, "Dragging near an overflowing ordinary row's edge scrolls it");
            Probe.ActiveTabDrag = null;
            foreach (var Page in Probe.Tabs.ToArray()) Probe.SetTabPinned(Page, true);
            Probe.UpdatePinnedTabLayout(true);
            Probe.SelectTab(Probe.Tabs.Last());
            await Task.Delay(60);
            Probe.UpdateLayout();
            Offset = Probe.PinnedTabScroll.HorizontalOffset;
            Overflow = Begin(Probe.Tabs.Last());
            Check(Offset > 0 && Probe.UpdateTabDragFeedback(Overflow, true, new Point(1, 8)) == DragDropEffects.Move
                && Probe.PinnedTabScroll.HorizontalOffset < Offset, "Dragging near an overflowing pinned row's edge scrolls it");
            Probe.ActiveTabDrag = null;
        }
        finally { if (Probe is not null) { Probe.ActiveTabDrag = null; Probe.RequestExit(); } }
    }
}
