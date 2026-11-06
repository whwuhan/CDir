using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;
using CDir.UI.Dialogs;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunAboutSmokeChecksAsync(List<string> Passed)
    {
        var Code = LanguageService.CurrentLanguage;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("About UI smoke test failed: " + Name);
            Passed.Add(Code + " " + Name);
        }
        var Menu = this.HostWindow.HelpMenu;
        var StaysOpen = Menu.StaysOpen;
        Menu.StaysOpen = true;
        try
        {
            for (var Attempt = 0; Attempt < 2; Attempt++)
            {
                this.HostWindow.HelpMenuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Check(Menu.IsOpen && Menu.IsVisible && Menu.Items.Count == 1
                    && ReferenceEquals(Menu.Items[0], this.HostWindow.AboutMenuItem)
                    && this.HostWindow.AboutMenuItem.IsVisible && this.HostWindow.AboutMenuItem.IsEnabled
                    && this.HostWindow.AboutMenuItem.Header?.ToString() == LanguageService.Text("About")
                    && Menu.FontSize == this.HostWindow.FileMenu.FontSize
                    && Menu.ActualWidth >= (double)Application.Current.Resources["UIMenuMinimumWidth"]
                    && this.HostWindow.AboutMenuItem.ActualWidth <= Menu.ActualWidth - 2,
                    "Help opens a compact localized About menu");

                Exception? Failure = null;
                var Inspected = false;
                AboutWindow? FindDialog() => this.HostWindow.OwnedWindows.OfType<AboutWindow>().FirstOrDefault();
                var Timeout = new DispatcherTimer(DispatcherPriority.Send, Dispatcher) { Interval = TimeSpan.FromSeconds(8) };
                Timeout.Tick += (_, _) =>
                {
                    Timeout.Stop();
                    Failure = new TimeoutException("Timed out inspecting the About window.");
                    FindDialog()?.Close();
                };
                var Pending = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(async () =>
                {
                    var Dialog = FindDialog();
                    try
                    {
                        Check(Dialog is { IsVisible: true } && Dialog.Title == LanguageService.Text("About")
                            && System.Windows.Interop.ComponentDispatcher.IsThreadModal && !Menu.IsOpen,
                            "About opens an owned modal window and closes Help");
                        CheckSquareWindowCorners(Dialog!);
                        var ExpectedAuthor = Code == "zh-CN" ? "吴寒" : "chesterwu";
                        Check(Dialog!.AuthorText.Text == LanguageService.Text("AboutAuthor", ExpectedAuthor),
                            "About shows the author " + ExpectedAuthor + ": " + Dialog.AuthorText.Text);
                        Check(Dialog.RepositoryText.Inlines.FirstInline is Run Label && Label.Text.Trim() == LanguageService.Text("AboutGitHub")
                            && Dialog.RepositoryText.Inlines.Contains(Dialog.RepositoryLink)
                            && Dialog.RepositoryLink.NavigateUri?.AbsoluteUri == "https://github.com/whwuhan/CDir"
                            && string.Concat(Dialog.RepositoryLink.Inlines.OfType<Run>().Select(Inline => Inline.Text)) == "https://github.com/whwuhan/CDir",
                            "About shows the exact clickable GitHub address");
                        Check(Dialog.CloseButton.Content?.ToString() == LanguageService.Text("Close")
                            && Dialog.FontSize == this.Settings.FontSizes!["Preferences"],
                            $"About uses the localized Close button and configured font: {Dialog.CloseButton.Content}, {Dialog.FontSize}/{this.Settings.FontSizes!["Preferences"]}");
                        if (Attempt == 1) Dialog.FontSize = 24;
                        Dialog.UpdateLayout();
                        Check(Dialog.AuthorText.ActualHeight >= Dialog.AuthorText.DesiredSize.Height - Dialog.AuthorText.Margin.Top - Dialog.AuthorText.Margin.Bottom - 0.5
                            && Dialog.RepositoryText.ActualHeight >= Dialog.RepositoryText.DesiredSize.Height - 0.5
                            && Dialog.RepositoryText.TranslatePoint(new Point(0, Dialog.RepositoryText.ActualHeight), Dialog).Y
                                < Dialog.CloseButton.TranslatePoint(new Point(), Dialog).Y
                            && Dialog.CloseButton.TranslatePoint(new Point(0, Dialog.CloseButton.ActualHeight), Dialog).Y <= Dialog.ActualHeight,
                            "About content and Close fit at font size " + Dialog.FontSize);
                        ProcessStartInfo? Launch = null;
                        Dialog.OpenLink = Info => Launch = Info;
                        var LinkInvoker = (IInvokeProvider)new HyperlinkAutomationPeer(Dialog.RepositoryLink).GetPattern(PatternInterface.Invoke);
                        LinkInvoker.Invoke();
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                        Check(Launch is { UseShellExecute: true, FileName: "https://github.com/whwuhan/CDir" }
                            && Dialog.IsVisible, "Activating the GitHub link requests the default browser and keeps About open");
                        if (AppMain.ScreenshotPath is not null)
                        {
                            var Bitmap = new RenderTargetBitmap((int)Math.Ceiling(Dialog.ActualWidth), (int)Math.Ceiling(Dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            Bitmap.Render(Dialog);
                            var Encoder = new PngBitmapEncoder();
                            Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                            using var Stream = System.IO.File.Create(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AppMain.ScreenshotPath)!, $"About-{Code}-{Attempt}.png"));
                            Encoder.Save(Stream);
                        }
                        Inspected = true;
                        if (Attempt == 0) Dialog.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        else Dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Dialog), 0, Key.Escape)
                            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                        Check(!Dialog.IsVisible, Attempt == 0 ? "Close dismisses About" : "Escape dismisses About");
                    }
                    catch (Exception Error) { Failure = Error; }
                    finally { Timeout.Stop(); if (Dialog?.IsVisible == true) Dialog.Close(); }
                }));
                Timeout.Start();
                try
                {
                    this.HostWindow.AboutMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                }
                finally { Timeout.Stop(); if (Pending.Status == DispatcherOperationStatus.Pending) Pending.Abort(); }
                if (Failure is not null) throw new InvalidOperationException("Could not verify About.", Failure);
                Check(Inspected && this.HostWindow.IsVisible && this.HostWindow.IsEnabled && FindDialog() is null,
                    "Closing About restores the main window");
            }
            this.HostWindow.HelpMenuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            FocusManager.SetFocusedElement(this, this.SearchBox);
            var MenuEscape = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(this.HostWindow), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            this.HostWindow.AboutMenuItem.RaiseEvent(MenuEscape);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(MenuEscape.Handled && !Menu.IsOpen, "Escape dismisses Help from a menu item");
            Check(ReferenceEquals(FocusManager.GetFocusedElement(this), this.FileGrid),
                "Escape restores file-panel focus after closing Help");
        }
        finally { Menu.IsOpen = false; Menu.StaysOpen = StaysOpen; }
    }
}
