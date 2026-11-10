using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunPreferencesShortcutSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Preferences shortcut check failed: " + Name);
            Passed.Add(Name);
        }
        Check(KeyboardShortcut.Resolve(new Dictionary<string, string> { ["Search"] = "Alt+S" }) is var Legacy
            && Legacy["Search"] == "Alt+S" && Legacy["Preferences"] == "",
            "Adding Preferences preserves an existing personal Alt+S assignment");
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Preferences shortcut")).FullName;
        var SettingsFile = Path.Combine(Root, "Settings.json");
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ExitOnClose = true }));
        var Probe = new MainWindow(SettingsFile) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            Probe.Show(); var Page = Probe.ActiveTab!; await Page.WaitUntilReadyAsync();
            async Task OpenFrom(UIElement Target, Key Key, ModifierKeys Modifiers, Action<PreferencesWindow> Inspect)
            {
                Exception? Failure = null; var Opened = false;
                PreferencesWindow? Dialog() => Probe.OwnedWindows.OfType<PreferencesWindow>().FirstOrDefault();
                var Timeout = new DispatcherTimer(DispatcherPriority.Send, Dispatcher) { Interval = TimeSpan.FromSeconds(30) };
                Timeout.Tick += (_, _) => { Timeout.Stop(); Failure = new TimeoutException("Preferences did not finish opening"); Dialog()?.Close(); };
                _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    var Window = Dialog();
                    try
                    {
                        Check(Window is { IsVisible: true } && ReferenceEquals(Window.Owner, Probe)
                            && System.Windows.Interop.ComponentDispatcher.IsThreadModal, "The configured Preferences key opens the owned modal window");
                        Opened = true; Inspect(Window!);
                    }
                    catch (Exception Error) { Failure = Error; }
                    finally { Window?.Close(); }
                }));
                var Input = new KeyEventArgs(new SmokeKeyboardDevice(Modifiers), PresentationSource.FromVisual(Target), 0, Key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                Timeout.Start();
                try { Target.RaiseEvent(Input); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); }
                finally { Timeout.Stop(); }
                if (Failure is not null) throw Failure;
                Check(Opened && Input.Handled, "Preferences dispatch works through the normal WPF key route");
            }
            Probe.FileMenuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Probe.PreferencesMenuItem.ApplyTemplate();
            Check(Probe.PreferencesMenuItem.InputGestureText == "Alt+S"
                && Probe.PreferencesMenuItem.Template.FindName("ShortcutHint", Probe.PreferencesMenuItem) is TextBlock { Text: "Alt+S" }
                && Probe.PreferencesMenuItem.Padding.Top <= 3,
                "The compact File menu renders Alt+S beside Preferences");
            await OpenFrom(Page.SearchBox, Key.S, ModifierKeys.Alt, Dialog =>
            {
                Check(!Probe.FileMenu.IsOpen, "Opening Preferences with a key dismisses the File menu");
                Dialog.Categories.SelectedItem = Dialog.ShortcutsCategory; Dialog.UpdateLayout();
                Check(Dialog.ShortcutEditors["Preferences"].Text == "Alt+S", "Preferences appears as an editable keyboard shortcut");
                Dialog.ShortcutEditors["Preferences"].RaiseEvent(new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control | ModifierKeys.Alt),
                    PresentationSource.FromVisual(Dialog), 0, Key.P) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Check(Page.PreferencesGesture == "Ctrl+Alt+P" && Probe.PreferencesMenuItem.InputGestureText == "Ctrl+Alt+P",
                    "Recording a custom Preferences key immediately updates dispatch and the File menu");
            });
            Check(KeyboardShortcut.Match(Page.ShortcutBindings, Key.S, ModifierKeys.Alt) is null
                && KeyboardShortcut.Resolve(DurableJsonStore.Read<AppSettings>(SettingsFile)!.Shortcuts)["Preferences"] == "Ctrl+Alt+P",
                "The old key is inactive and the custom Preferences key is saved");
            await OpenFrom(Page.FileGrid, Key.P, ModifierKeys.Control | ModifierKeys.Alt, Dialog =>
            {
                Dialog.Categories.SelectedItem = Dialog.ShortcutsCategory;
                Check(Dialog.ShortcutEditors["Preferences"].Text == "Ctrl+Alt+P", "Reopening Preferences retains its custom key");
                Dialog.ShortcutClearButtons["Preferences"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Page.PreferencesGesture == "" && Probe.PreferencesMenuItem.InputGestureText == "",
                    "Clearing the Preferences key removes both dispatch and menu hint");
                Dialog.ShortcutResetButtons["Preferences"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Page.PreferencesGesture == "Alt+S" && Probe.PreferencesMenuItem.InputGestureText == "Alt+S",
                    "Resetting the Preferences key restores Alt+S and its menu hint");
            });
            await OpenFrom(Page.FileGrid, Key.S, ModifierKeys.Alt, _ => { });
        }
        finally { Probe.RequestExit(); }
    }
}
