using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunLifecycleSmokeChecksAsync(List<string> Passed)
    {
        await this.RunExitGeometrySmokeChecksAsync(Passed);
        await this.RunWorkspaceRecoverySmokeChecksAsync(Passed);
        await this.RunWorkspaceStartupSmokeChecksAsync(Passed);
        await this.RunInstanceWorkspaceSmokeChecksAsync(Passed);
        await this.RunShellLaunchSmokeChecksAsync(Passed);
        await this.RunExitConflictSmokeChecksAsync(Passed);
        await this.RunSessionEndingSmokeChecksAsync(Passed);
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Lifecycle UI check failed: " + Name);
            Passed.Add(Name);
        }
        var SettingsFile = Path.Combine(AppMain.SmokeRoot!, "Lifecycle-" + Guid.NewGuid().ToString("N") + ".json");
        MainWindow? Probe = null;
        async Task Open()
        {
            Probe = new MainWindow(SettingsFile) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show();
            await Probe.ActiveTab!.WaitUntilReadyAsync();
        }
        static void ClickClose(MainWindow Window) => Window.CloseWindowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void TrayMessage(TrayIconService Tray, int Message) => SendTraySmokeMessage(Tray.WindowHandle,
            TrayIconService.CallbackMessage, new IntPtr(1), new IntPtr(Message));
        async Task CheckCaptionButtons(string Stage)
        {
            var Buttons = Probe!.TitleBar.Children.OfType<StackPanel>()
                .SelectMany(Panel => Panel.Children.OfType<Button>()).ToArray();
            Check(Buttons.Length == 3 && Buttons.All(Button => !Button.Focusable && !Button.IsTabStop && !Button.IsKeyboardFocused),
                Stage + ": caption actions cannot retain keyboard focus or enter Tab navigation");
            foreach (var Button in Buttons)
            {
                // Control hover locally without moving the user's pointer or activating the test window.
                var HitTestVisible = Button.IsHitTestVisible;
                Button.IsHitTestVisible = false;
                try
                {
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    Button.ApplyTemplate();
                    var Surface = (Border)Button.Template.FindName("Surface", Button);
                    Check(Surface.Background is SolidColorBrush { Color.A: 0 } && !Button.IsMouseOver,
                        Stage + ": a caption button without pointer hover renders its neutral background: " + Button.Content);
                }
                finally { Button.IsHitTestVisible = HitTestVisible; }
            }
        }
        try
        {
            File.WriteAllText(SettingsFile, "{}"); // Existing settings without the new field use the new default.
            await Open();
            var Page = Probe!.ActiveTab!;
            Probe.WindowState = WindowState.Normal;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            CheckResizeBorders(Probe, Passed);
            CheckVisibleWindowFrame(Probe, Passed);
            await CheckCaptionButtons("Initial display");
            Check(!Page.Settings.ExitOnClose && !new AppSettings().ExitOnClose, "Fresh and legacy settings default to hiding on close");
            var Tray = Probe.TrayIcon!;
            Check(Probe.IsVisible && Tray is { IsVisible: true }, "The tray icon is registered at startup while the window is visible");
            TrayMessage(Tray!, 0x203);
            Check(Probe.IsVisible && Tray!.IsVisible && ReferenceEquals(Tray, Probe.TrayIcon),
                "Double-clicking the tray while already visible keeps the window and its single icon");
            var Extra = Probe.AddTab();
            await Extra.WaitUntilReadyAsync();
            Probe.SelectTab(Page);
            var BeforePath = Page.CurrentPath;
            Page.OperationBusy = true;
            Probe.FileMenuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Probe.ExitMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(Probe.IsVisible && !Page.WindowClosed && !Extra.WindowClosed && !Probe.FileMenu.IsOpen && Tray.IsVisible,
                "File Exit dismisses its menu and keeps the busy-operation close protection");
            TrayMessage(Tray, 0x205);
            Tray.Menu.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(Probe.IsVisible && !Page.WindowClosed && Tray.IsVisible && !Tray.Menu.IsOpen,
                "Cancelling tray exit for a busy operation keeps the icon and closes its menu");
            ClickClose(Probe);
            Check(!Probe.IsVisible && Probe.TrayIcon?.IsVisible == true && !Page.WindowClosed && !Extra.WindowClosed,
                "The caption close button hides to a real Shell icon without disposing tabs or interrupting operations");
            TrayMessage(Tray, 0x203);
            Check(Probe.IsVisible && Tray.IsVisible && Probe.Tabs.Count == 2 && Page.CurrentPath == BeforePath,
                "Double-click notification restores the same window tabs and directory while retaining the icon");
            await CheckCaptionButtons("After tray restore");
            Page.OperationBusy = false;
            Probe.WindowState = WindowState.Maximized;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Probe.UpdateLayout();
            var WorkArea = WindowWorkArea.ReadWorkArea(new System.Windows.Interop.WindowInteropHelper(Probe).Handle);
            CheckResizeBorders(Probe, Passed);
            Check(Probe.BorderThickness == new Thickness(0) && Probe.WindowFrame.BorderThickness == new Thickness(0)
                && Probe.WindowFrame.TranslatePoint(new Point(), Probe) == new Point()
                && Math.Abs(Probe.WindowFrame.ActualWidth - Probe.ActualWidth) <= 1
                && Math.Abs(Probe.WindowFrame.ActualHeight - Probe.ActualHeight) <= 1,
                "Maximizing fills the window with the application frame without a dark border gutter");
            var MaximizedImage = new RenderTargetBitmap((int)Math.Ceiling(Probe.ActualWidth), (int)Math.Ceiling(Probe.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            MaximizedImage.Render(Probe);
            foreach (var Edge in new[] { new Point(0, 0), new Point(MaximizedImage.PixelWidth - 1, 0),
                new Point(0, MaximizedImage.PixelHeight - 1), new Point(MaximizedImage.PixelWidth - 1, MaximizedImage.PixelHeight - 1) })
            {
                var Pixel = new byte[4];
                MaximizedImage.CopyPixels(new Int32Rect((int)Edge.X, (int)Edge.Y, 1, 1), Pixel, 4, 0);
                Check(Pixel[3] == 255 && Pixel.Take(3).Any(Channel => Channel > 32),
                    "The maximized window corner is opaque and has no black frame: " + Edge);
            }
            if (AppMain.ScreenshotPath is { } MaxOutput)
            {
                var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(MaximizedImage));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(MaxOutput)!, "MaximizedWindow.png"));
                Encoder.Save(Stream);
            }
            var StatusBottom = Page.StatusBarPanel.PointToScreen(new Point(Page.StatusBarPanel.ActualWidth, Page.StatusBarPanel.ActualHeight));
            var StatusTop = Page.StatusBarPanel.PointToScreen(new Point());
            Check(!WorkArea.IsEmpty && StatusTop.Y >= WorkArea.Top && StatusBottom.Y <= WorkArea.Bottom + 1
                && StatusBottom.X <= WorkArea.Right + 1 && Page.StatusBarPanel.IsVisible && Page.StatusBarPanel.ActualHeight >= 30,
                $"Maximizing keeps the entire status bar inside the monitor work area (bottom={StatusBottom.Y}, workBottom={WorkArea.Bottom})");
            TrayMessage(Tray, 0x203);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(Probe.IsVisible && Probe.WindowState == WindowState.Maximized && Tray.IsVisible,
                "Double-clicking the persistent icon preserves an already maximized window");
            ClickClose(Probe);
            TrayMessage(Tray, 0x203);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(Probe.IsVisible && Probe.WindowState == WindowState.Maximized && Tray.IsVisible && ReferenceEquals(Tray, Probe.TrayIcon),
                $"Repeated tray cycles reuse one icon and preserve maximized state (visible={Probe.IsVisible}, state={Probe.WindowState}, icon={Tray.IsVisible})");
            Probe.WindowState = WindowState.Normal;
            Check(Probe.BorderThickness == new Thickness(0) && Probe.WindowFrame.BorderThickness == new Thickness(1),
                "Restoring the window reinstates its neutral thin application border");
            Probe.WindowState = WindowState.Minimized;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            TrayMessage(Tray, 0x203);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(Probe.IsVisible && Probe.WindowState == WindowState.Normal && Tray.IsVisible,
                "The persistent tray icon restores a minimized window and remains registered");
            await Page.RunPreferencesSmokeChecksAsync(Passed, ExitOnClose: true);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(Page.Settings.ExitOnClose && Extra.Settings.ExitOnClose
                && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile))!.ExitOnClose,
                "The General checkbox persists exit-on-close and synchronizes existing tabs");
            var NewTab = Probe.AddTab();
            await NewTab.WaitUntilReadyAsync();
            Check(NewTab.Settings.ExitOnClose, "New tabs inherit exit-on-close");
            Probe.SelectTab(Page);
            Page.OperationBusy = true;
            ClickClose(Probe);
            Check(Probe.IsVisible && !Page.WindowClosed && Tray.IsVisible, "Direct exit keeps the busy-operation close protection and tray icon");
            Page.OperationBusy = false;
            ClickClose(Probe);
            Check(!Probe.IsVisible && Page.WindowClosed && Extra.WindowClosed && NewTab.WindowClosed && !Tray.IsVisible,
                "Checked caption close disposes all tabs and leaves no tray icon");
            Probe = null;
            await Open();
            Page = Probe!.ActiveTab!;
            Check(Page.Settings.ExitOnClose && Probe.TrayIcon?.IsVisible == true,
                "Exit-on-close survives reopening and does not suppress the startup tray icon");
            await Page.RunPreferencesSmokeChecksAsync(Passed, ExitOnClose: false);
            ClickClose(Probe);
            Tray = Probe.TrayIcon!;
            Check(Tray.IsVisible && !Probe.IsVisible && !JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile))!.ExitOnClose,
                "Unchecking General saves and restores the default tray behavior");
            TrayMessage(Tray, 0x205);
            Check(Tray.Menu.IsOpen && Tray.Menu.Items.Count == 3 && Tray.Menu.Items[0] is MenuItem
                && Tray.Menu.Items[1] is Separator && Tray.Menu.Items[2] is MenuItem,
                "The tray context menu retains Open, its original separator, and Exit without an About entry");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Tray.Menu.UpdateLayout();
            Check(Tray.Menu.ActualWidth >= (double)Application.Current.Resources["UIMenuMinimumWidth"],
                "The tray menu respects the readable minimum width at the configured menu font size");
            var Divider = (Separator)Tray.Menu.Items[1];
            var Line = FindVisualChild<Border>(Divider)!;
            var LineStart = Line.TranslatePoint(new Point(), Tray.Menu);
            var ExpectedLeft = Tray.Menu.BorderThickness.Left + Tray.Menu.Padding.Left + Divider.Margin.Left;
            var ExpectedRight = Tray.Menu.BorderThickness.Right + Tray.Menu.Padding.Right + Divider.Margin.Right;
            Check(Line.ActualHeight == 1 && Math.Abs(LineStart.X - ExpectedLeft) <= 1
                && Math.Abs(LineStart.X + Line.ActualWidth - (Tray.Menu.ActualWidth - ExpectedRight)) <= 1
                && Math.Abs(Line.ActualWidth - Divider.ActualWidth) <= 1
                && Tray.Menu.Items.OfType<MenuItem>().All(Item => Item.FontSize == Tray.Menu.FontSize),
                "The compact tray menu separator spans the content width without a Windows icon gutter and preserves the menu font");
            if (AppMain.ScreenshotPath is not null)
            {
                var Bitmap = new RenderTargetBitmap((int)Math.Ceiling(Tray.Menu.ActualWidth), (int)Math.Ceiling(Tray.Menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(Tray.Menu);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(AppMain.ScreenshotPath)!, "TrayMenu.png"));
                Encoder.Save(Stream);
            }
            ((MenuItem)Tray.Menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(Probe.IsVisible && Tray.IsVisible && !Tray.Menu.IsOpen, "The tray Open command restores the window and keeps its icon");
            ClickClose(Probe);
            TrayMessage(Tray, 0x205);
            Tray.Menu.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(Page.WindowClosed && !Tray.IsVisible && !Tray.Menu.IsOpen && this.HostWindow.IsVisible,
                "The tray Exit command closes a hidden window and releases its icon and menu");
            Probe = null;
            await Open();
            Page = Probe!.ActiveTab!;
            var FileTray = Probe.TrayIcon!;
            Probe.HelpMenuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Probe.IsVisible && !Page.WindowClosed && !Probe.FileMenu.IsOpen
                && ReferenceEquals(Probe.HelpMenuButton.ContextMenu, Probe.HelpMenu) && Probe.HelpMenu.IsOpen
                && Probe.AboutMenuItem.Header?.ToString() == LanguageService.Text("About") && Probe.OwnedWindows.Count == 0,
                "Help opens the localized About menu without opening a dialog");
            Probe.HelpMenu.IsOpen = false;
            Probe.FileMenuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Probe.UpdateLayout();
            Check(!Page.Settings.ExitOnClose && Probe.FileMenuSeparator.IsVisible && Probe.ExitMenuItem.IsVisible,
                "File shows a separator and Exit even when caption close is configured to hide");
            Probe.ExitMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(Page.WindowClosed && !Probe.IsVisible && !FileTray.IsVisible && this.HostWindow.IsVisible,
                "File Exit closes the window and disposes its workspace instead of hiding to the tray");
            Probe = null;
            await Open();
            Page = Probe!.ActiveTab!;
            var LastTabTray = Probe.TrayIcon!;
            Page.ChangeExitOnClose(false);
            Page.OperationBusy = true;
            await Probe.ExecuteTabCommandAsync("CloseTab");
            Check(Probe.IsVisible && !Page.WindowClosed && Probe.Tabs.Count == 1,
                "Closing the final tab still protects an active file operation");
            Page.OperationBusy = false;
            var LastTabPath = Page.CurrentPath;
            await Probe.ExecuteTabCommandAsync("CloseTab");
            Check(!Probe.IsVisible && !Page.WindowClosed && Probe.Tabs.Count == 1
                && ReferenceEquals(Probe.ActiveTab, Page) && LastTabTray.IsVisible,
                "Ctrl+W on the final tab hides to the tray without disposing the workspace when exit-on-close is unchecked");
            TrayMessage(LastTabTray, 0x203);
            Check(Probe.IsVisible && ReferenceEquals(Probe.ActiveTab, Page) && Page.CurrentPath == LastTabPath,
                "Restoring after closing the final tab retains the same tab and directory");
            Page.ChangeExitOnClose(true);
            await Probe.ExecuteTabCommandAsync("CloseTab");
            Check(Page.WindowClosed && !LastTabTray.IsVisible && this.HostWindow.IsVisible,
                "Ctrl+W on the final tab closes the workspace and removes its tray icon when exit-on-close is checked");
            Probe = null;
        }
        finally
        {
            if (Probe is not null)
            {
                foreach (var Page in Probe.Tabs) Page.OperationBusy = false;
                Probe.RequestExit();
            }
            File.Delete(SettingsFile);
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendTraySmokeMessage(IntPtr Window, int Message, IntPtr WParam, IntPtr LParam);
}
