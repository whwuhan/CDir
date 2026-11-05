using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunTabContextMenuSmokeChecksAsync(List<string> Passed)
    {
        var InitialLanguage = LanguageService.CurrentLanguage;
        var InitialFonts = new Dictionary<string, double>(this.Settings.FontSizes!);
        var SettingsFile = Path.Combine(AppMain.SmokeRoot!, "TabMenus-" + Guid.NewGuid().ToString("N") + ".json");
        MainWindow? Probe = null;
        ContextMenu? OpenedMenu = null;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Tab context menu check failed: " + Name);
            Passed.Add(Name);
        }
        async Task<ContextMenu> Open(ExplorerTab Page)
        {
            if (OpenedMenu is not null) OpenedMenu.IsOpen = false;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Probe!.UpdateLayout();
            var Presenter = (ContentPresenter)Probe.TabHeaders.ItemContainerGenerator.ContainerFromIndex(Probe.Tabs.IndexOf(Page));
            var Surface = FindVisualChild<Border>(Presenter)!;
            var Menu = Surface.ContextMenu;
            Menu.StaysOpen = true;
            Menu.PlacementTarget = Surface;
            Menu.Placement = PlacementMode.Bottom;
            Menu.IsOpen = true;
            OpenedMenu = Menu;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Menu.UpdateLayout();
            Check(Menu.IsVisible, "The tab context menu opens for the right-clicked tab");
            return Menu;
        }
        static MenuItem Command(ContextMenu Menu, string Key) => Menu.Items.OfType<MenuItem>().Single(Item => Item.Tag?.ToString() == Key);
        async Task Invoke(ExplorerTab Page, string Key)
        {
            var Menu = await Open(Page);
            var Item = Command(Menu, Key);
            Check(Item.IsEnabled, "Tab menu command is available: " + Key);
            Menu.IsOpen = false;
            Item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        async Task<ExplorerTab> Add()
        {
            var Page = Probe!.AddTab();
            await Page.WaitUntilReadyAsync();
            return Page;
        }
        void CheckCompact(ContextMenu Menu, double FontSize)
        {
            var Items = Menu.Items.OfType<MenuItem>().ToArray();
            var Width = Items.Max(Item => new FormattedText(Item.Header.ToString()!, LanguageService.Culture, Item.FlowDirection,
                new Typeface(Item.FontFamily, Item.FontStyle, Item.FontWeight, Item.FontStretch), Item.FontSize,
                Item.Foreground, VisualTreeHelper.GetDpi(Item).PixelsPerDip).WidthIncludingTrailingWhitespace);
            Check(Items.All(Item => Item.FontSize == FontSize && Item.ActualHeight <= FontSize * 1.4 + 8)
                && Menu.ActualWidth <= Width + 25,
                "Compact tab menu preserves the configured font and removes excess row padding and icon gutters: " + FontSize);
        }
        try
        {
            Check(this.SaveSettings(SettingsFile), "Tab-menu probe uses its own saved settings file");
            Probe = new MainWindow(SettingsFile) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show();
            var A = Probe.ActiveTab!;
            await A.WaitUntilReadyAsync();
            var B = await Add();
            var C = await Add();
            var D = await Add();
            var E = await Add();
            foreach (var Language in LanguageService.SupportedLanguages)
            {
                E.ChangeLanguage(Language.Code, Persist: false);
                var Menu = await Open(C);
                Check(Menu.Items.Cast<object>().Select(Item => Item is MenuItem Action ? Action.Tag?.ToString() : "Separator")
                    .SequenceEqual(new[] { "RenameTab", "Separator", "CloseRightTabs", "CloseLeftTabs", "CloseOtherTabs", "Separator", "CloseTab" })
                    && Menu.Items.OfType<MenuItem>().All(Item => Item.Header?.ToString() == LanguageService.Text((string)Item.Tag)),
                    Language.Code + " displays Rename, separator, Close Right, Close Left, Close Others, separator and Close in order: "
                    + string.Join(" | ", Menu.Items.OfType<MenuItem>().Select(Item => Item.Tag + "=" + Item.Header)));
                CheckCompact(Menu, 13);
                if (Language.Code == "en-US" && AppMain.ScreenshotPath is { } Preview)
                {
                    var Bitmap = new RenderTargetBitmap((int)Math.Ceiling(Menu.ActualWidth), (int)Math.Ceiling(Menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    Bitmap.Render(Menu);
                    var Encoder = new PngBitmapEncoder();
                    Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                    using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Preview)!, "TabContextMenu.png"));
                    Encoder.Save(Stream);
                }
                Menu.IsOpen = false;
            }
            E.ChangeLanguage(InitialLanguage, Persist: false);
            Check(E.ChangeUIFontSize("Menus", 20), "The tab menu retains the normal menu font setting");
            CheckCompact(await Open(C), 20);
            OpenedMenu!.IsOpen = false;
            E.ChangeUIFontSize("Menus", 13);
            await Invoke(C, "CloseRightTabs");
            Check(Probe.Tabs.SequenceEqual(new[] { A, B, C }) && ReferenceEquals(Probe.ActiveTab, C)
                && D.WindowClosed && E.WindowClosed && !D.RefreshTimer.IsEnabled && !E.DriveUsageTimer.IsEnabled,
                "Close Right uses the clicked tab as anchor, closes every right neighbor and releases their resources");
            var FirstMenu = await Open(A);
            Check(!Command(FirstMenu, "CloseLeftTabs").IsEnabled && Command(FirstMenu, "CloseRightTabs").IsEnabled,
                "The first tab disables Close Left while keeping Close Right available");
            var LastMenu = await Open(C);
            Check(!Command(LastMenu, "CloseRightTabs").IsEnabled && Command(LastMenu, "CloseLeftTabs").IsEnabled,
                "The last tab disables Close Right while keeping Close Left available");
            await Invoke(C, "CloseLeftTabs");
            Check(Probe.Tabs.Count == 1 && ReferenceEquals(Probe.ActiveTab, C) && A.WindowClosed && B.WindowClosed,
                "Close Left removes every left neighbor and preserves the clicked tab");
            var SingleMenu = await Open(C);
            Check(new[] { "CloseRightTabs", "CloseLeftTabs", "CloseOtherTabs" }.All(Key => !Command(SingleMenu, Key).IsEnabled)
                && Command(SingleMenu, "CloseTab").IsEnabled,
                "A lone tab disables empty bulk operations and keeps Close Tab available");
            SingleMenu.IsOpen = false;
            var Middle = await Add();
            var Right = await Add();
            Right.OperationBusy = true;
            await Invoke(C, "CloseRightTabs");
            Check(Probe.Tabs.SequenceEqual(new[] { C, Middle, Right }) && !Middle.WindowClosed && !Right.WindowClosed
                && ReferenceEquals(Probe.ActiveTab, Right),
                "Bulk close checks running file operations before removing any tabs and reveals the busy workspace");
            Right.OperationBusy = false;
            Probe.SelectTab(C);
            await Invoke(Middle, "CloseOtherTabs");
            Check(Probe.Tabs.Count == 1 && ReferenceEquals(Probe.ActiveTab, Middle) && C.WindowClosed && Right.WindowClosed,
                "Close Others preserves only the clicked tab, including when that tab was inactive");
            var Survivor = await Add();
            await Invoke(Middle, "CloseTab");
            Check(Probe.Tabs.Count == 1 && Middle.WindowClosed && ReferenceEquals(Probe.ActiveTab, Survivor),
                "Close Tab closes the clicked inactive tab without disturbing the active survivor");
            var Current = await Add();
            await Invoke(Current, "CloseTab");
            Check(Current.WindowClosed && ReferenceEquals(Probe.ActiveTab, Survivor),
                "Close Tab selects the surviving neighbor when the clicked tab was active");
            await Invoke(Survivor, "CloseTab");
            Check(Survivor.WindowClosed && !Probe.IsVisible && this.HostWindow.IsVisible && !this.WindowClosed,
                "Closing the final tab keeps normal window-close behavior without affecting another window");
        }
        finally
        {
            if (OpenedMenu is not null) OpenedMenu.IsOpen = false;
            if (Probe is not null)
            {
                foreach (var Page in Probe.Tabs) Page.OperationBusy = false;
                Probe.RequestExit();
            }
            this.Settings.FontSizes = InitialFonts;
            this.ApplyUIFonts();
            this.ChangeLanguage(InitialLanguage, Persist: false);
            File.Delete(SettingsFile);
        }
    }
}
