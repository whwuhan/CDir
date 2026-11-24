using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunNavigationFieldResizeSmokeAsync(List<string> Passed)
    {
        void Check(bool Value, string Message)
        {
            if (!Value) throw new InvalidOperationException("Navigation field resize: " + Message);
            Passed.Add(Message);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Navigation splitter")).FullName;
        File.WriteAllText(Path.Combine(Root, "Retained result.txt"), "Owned fixture");
        var SettingsFile = Path.Combine(Root, "Settings.json");
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ExitOnClose = true }));
        var Probe = new MainWindow(SettingsFile) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false, Width = 1280 };
        async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); Probe.UpdateLayout(); }
        async Task Drag(ExplorerTab Page, double Delta)
        {
            var Splitter = Page.AddressSearchSplitter;
            Splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            Splitter.RaiseEvent(new DragDeltaEventArgs(Delta, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            Splitter.RaiseEvent(new DragCompletedEventArgs(Delta, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            await Settle();
        }
        async Task PressBalance(ExplorerTab Page)
        {
            Check(await Page.HandleShortcutAsync(Key.R, ModifierKeys.Alt, InText: true, InFileList: false),
                "Alt+R dispatches layout reset while a search field has focus");
            await Settle();
        }
        try
        {
            Probe.Show(); await Probe.ActiveTab!.WaitUntilReadyAsync(); await Settle();
            var A = Probe.ActiveTab!;
            Check(Math.Abs(A.SearchContainer.ActualWidth - 200) < 1 && A.AddressSearchSplitter.ActualWidth == 8,
                "The address/search divider starts with the compact default search width and an eight-pixel hit area");
            A.SearchBox.Text = "Retained"; await A.WaitForSearchSmokeAsync();
            var Search = A.SearchVersion;
            var Results = A.View!.Cast<FileEntry>().Select(Entry => Entry.FullPath).ToArray();
            var OriginalAddress = A.AddressBar.ActualWidth;
            await Drag(A, -144);
            Check(A.SearchContainer.ActualWidth > 300 && A.AddressBar.ActualWidth < OriginalAddress - 100,
                "Dragging left expands Search and shortens the address field through the real GridSplitter drag route");
            var ManualWidth = A.SearchContainer.ActualWidth;
            A.BeginAddressEdit(); A.AddressBox.Text = "An unsent address draft";
            await Drag(A, 64);
            Check(A.SearchContainer.ActualWidth < ManualWidth - 40 && A.AddressBox.Text == "An unsent address draft"
                && A.SearchBox.Text == "Retained" && A.SearchVersion == Search && A.View!.Cast<FileEntry>().Select(Entry => Entry.FullPath).SequenceEqual(Results),
                "Dragging right preserves address drafts query text and the exact search result generation");
            A.CancelAddressEdit();
            await Drag(A, 10000);
            Check(A.SearchContainer.ActualWidth >= 96 && A.AddressBar.ActualWidth >= 72,
                "Dragging to the extreme right preserves usable minimum field widths");
            await Drag(A, -10000);
            Check(A.SearchContainer.ActualWidth >= 96 && A.AddressBar.ActualWidth >= 72,
                "Dragging to the extreme left keeps both fields inside the navigation bar");
            await PressBalance(A);
            Check(Math.Abs(A.SearchContainer.ActualWidth - 200) < 1 && Probe.Panes.Count == 1 && A.SearchVersion == Search,
                "Alt+R restores the default address/search widths even in a single-pane window without restarting the query");
            await Drag(A, -128);
            var AWidth = A.SearchContainer.ActualWidth;
            var B = Probe.AddTab(); await B.WaitUntilReadyAsync(); await Settle();
            Check(Math.Abs(B.SearchContainer.ActualWidth - 200) < 1, "New tabs start with their own default navigation widths");
            Probe.SelectTab(A); await Settle();
            Check(Math.Abs(A.SearchContainer.ActualWidth - AWidth) < 1, "Switching tabs preserves an existing tab's manual navigation widths");
            Probe.Width += 120; await Settle();
            Check(Math.Abs(A.SearchContainer.ActualWidth - 200) < 1 && A.SearchBox.Text == "Retained",
                "Resizing the window restores default navigation widths and retains the search");
            await Drag(A, -128);
            Probe.SelectTab(B); await Settle(); await Drag(B, -96);
            await PressBalance(B);
            Probe.SelectTab(A); await Settle();
            Check(Math.Abs(A.SearchContainer.ActualWidth - 200) < 1 && Math.Abs(B.SearchFieldColumn.Width.Value - 200) < 1,
                "Layout reset restores navigation widths in both active and hidden tabs");
            Probe.Width = Probe.MinWidth; await Settle();
            var Right = A.SearchContainer.TranslatePoint(new Point(A.SearchContainer.ActualWidth, 0), A.NavigationFields).X;
            Check(A.AddressBar.ActualWidth >= 72 && A.SearchContainer.ActualWidth >= 96
                && Right <= A.NavigationFields.ActualWidth + .5 && A.ColumnMenuButton.IsVisible,
                "A narrow window clamps the default fields without clipping the view menu");
            Probe.Width = 1280; await Settle(); await Drag(A, -176);
            A.CaptureAddressBreadcrumbPreview("AddressSearchResized.png");
        }
        finally { Probe.RequestExit(); }
    }
}
