using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunPinnedTabSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Pinned tab check failed: " + Name);
            Passed.Add(Name);
        }
        var SettingsFile = Path.Combine(AppMain.SmokeRoot!, "PinnedTabs.json");
        MainWindow? Probe = null;
        Button PinButton(ExplorerTab Page)
        {
            Probe!.UpdateLayout();
            var Header = Probe.Headers.Single(Header => ReferenceEquals(Header.Page, Page));
            var Items = Probe.PinnedHeaders.Contains(Header) ? Probe.PinnedTabHeaders : Probe.TabHeaders;
            var Container = (ContentPresenter)Items.ItemContainerGenerator.ContainerFromItem(Header);
            var Surface = FindVisualChild<Border>(Container)!;
            var Buttons = ((Grid)Surface.Child).Children.OfType<Button>().ToArray();
            Check(Buttons.Length == 3 && Buttons[1].Name == "PinTabButton" && Buttons[2].Name == "CloseTabButton"
                && Buttons[1].TranslatePoint(new Point(), Surface).X > Buttons[0].TranslatePoint(new Point(), Surface).X
                && Buttons[1].TranslatePoint(new Point(), Surface).X < Buttons[2].TranslatePoint(new Point(), Surface).X,
                "The clickable pin sits between the tab label and Close");
            return Buttons[1];
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
        void CheckPinContrast(ExplorerTab Page, string Prefix)
        {
            var Button = PinButton(Page);
            var HoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            var PressedKey = (DependencyPropertyKey)typeof(System.Windows.Controls.Primitives.ButtonBase).GetField("IsPressedPropertyKey",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            var OriginalHover = Button.IsMouseOver;
            var OriginalPressed = Button.IsPressed;
            try
            {
                // Exercise only this owned control's visual state, without moving the desktop pointer.
                foreach (var Pressed in new[] { false, true })
                {
                    Button.SetValue(HoverKey, !Pressed);
                    Button.SetValue(PressedKey, Pressed);
                    Probe!.UpdateLayout();
                    var Surface = (Border)Button.Template.FindName("Surface", Button);
                    var Foreground = ((SolidColorBrush)Button.Foreground).Color;
                    var Background = ((SolidColorBrush)Surface.Background).Color;
                    static double Luminance(Color Color)
                    {
                        static double Linear(byte Channel) => Channel / 255d <= 0.04045 ? Channel / 255d / 12.92 : Math.Pow((Channel / 255d + 0.055) / 1.055, 2.4);
                        return 0.2126 * Linear(Color.R) + 0.7152 * Linear(Color.G) + 0.0722 * Linear(Color.B);
                    }
                    var Light = Math.Max(Luminance(Foreground), Luminance(Background));
                    var Dark = Math.Min(Luminance(Foreground), Luminance(Background));
                    Check(Button.Content?.ToString() == "\uE77A"
                        && Equals(Button.Foreground, Probe.FindResource("HighlightText"))
                        && Equals(Surface.Background, Probe.FindResource(Pressed ? "SurfacePressed" : "SurfaceHover"))
                        && (Light + 0.05) / (Dark + 0.05) >= 3,
                        Prefix + (Pressed ? " pressed" : " hover") + " keeps the pinned glyph visible with sufficient contrast");
                    Capture(Prefix + (Pressed ? "Pressed" : "Hover"));
                }
            }
            finally
            {
                Button.SetValue(HoverKey, OriginalHover);
                Button.SetValue(PressedKey, OriginalPressed);
            }
            Probe!.UpdateLayout();
        }
        try
        {
            Check(!new AppSettings().SeparatePinnedTabs
                && !JsonSerializer.Deserialize<AppSettings>("{}")!.SeparatePinnedTabs,
                "New and legacy settings keep pinned tabs on the original row by default");
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = this.CurrentPath }));
            Probe = new MainWindow(SettingsFile, RestoreWorkspace: true)
                { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show();
            await Probe.ActiveTab!.WaitUntilReadyAsync();
            await Probe.WorkspaceRestored;
            var A = Probe.ActiveTab!;
            A.SetTabAlias("First");
            var B = Probe.AddTab(); await B.WaitUntilReadyAsync(); B.SetTabAlias("Second");
            var C = Probe.AddTab(); await C.WaitUntilReadyAsync(); C.SetTabAlias("Third");
            PinButton(B).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(B.IsTabPinned && Probe.Tabs.SequenceEqual(new[] { B, A, C }) && ReferenceEquals(Probe.ActiveTab, C),
                "Pinning an inactive tab moves it first and preserves the active workspace");
            Check(PinButton(B).ToolTip?.ToString() == LanguageService.Text("UnpinTab"),
                "A pinned tab offers the localized Unpin action");
            CheckPinContrast(B, "PinnedTabCombined");
            var Input = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Alt), PresentationSource.FromVisual(Probe), 0, Key.W)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Probe.RaiseEvent(Input);
            Check(Input.Handled && C.IsTabPinned && Probe.Tabs.SequenceEqual(new[] { B, C, A }),
                "Alt+W pins the current tab at the end of the pinned group");
            await C.NavigateAsync(Path.Combine(this.CurrentPath, "child"));
            Check(C.IsTabPinned, "Navigating preserves the tab's pinned state");
            PinButton(B).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!B.IsTabPinned && Probe.Tabs.SequenceEqual(new[] { C, B, A }),
                "Unpinning inserts the tab before the first ordinary tab");
            PinButton(B).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Capture("PinnedTabsCombined");
            await C.RunPreferencesSmokeChecksAsync(Passed, InspectShortcuts: Dialog =>
            {
                Dialog.Categories.SelectedItem = Dialog.UICategory;
                Dialog.UpdateLayout();
                Check(Dialog.SeparatePinnedTabsCheck.IsVisible && Dialog.SeparatePinnedTabsCheck.IsChecked == false
                    && Dialog.SeparatePinnedTabsCheck.Content?.ToString() == LanguageService.Text("SeparatePinnedTabs"),
                    "Preferences UI exposes the separate pinned row as an unchecked localized option");
                Dialog.SeparatePinnedTabsCheck.IsChecked = true;
                Probe.UpdateLayout();
                Check(Probe.PinnedTabScroll.IsVisible && Probe.PinnedHeaders.Select(Header => Header.Page).SequenceEqual(new[] { C, B })
                    && Probe.VisibleHeaders.Single().Page == A
                    && Probe.PinnedTabScroll.TranslatePoint(new Point(), Probe).Y < Probe.TabScroll.TranslatePoint(new Point(), Probe).Y,
                    "The preference immediately puts pinned tabs above the original row");
                Dialog.Categories.SelectedItem = Dialog.ShortcutsCategory;
                Check(Dialog.SetShortcut("ToggleTabPin", "Ctrl+Shift+P")
                    && C.TryGetTabCommand(Key.P, ModifierKeys.Control | ModifierKeys.Shift, out var Command) && Command == "ToggleTabPin"
                    && !C.TryGetTabCommand(Key.W, ModifierKeys.Alt, out _),
                    "The pin command can be rebound through Preferences");
                return Task.CompletedTask;
            });
            Check(Probe.Tabs.All(Page => Page.Settings.SeparatePinnedTabs)
                && Probe.Tabs.All(Page => Page.ShortcutBindings["ToggleTabPin"] == "Ctrl+Shift+P"),
                "The row preference and custom pin shortcut synchronize across tabs");
            PinButton(C);
            CheckPinContrast(C, "PinnedTabSeparate");
            Capture("PinnedTabsSeparate");
            Probe.SelectTab(A);
            Check(Probe.PersistWorkspaceNow(), "Pinned tabs can be saved while the window stays open");
            var Saved = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(SettingsFile + ".workspace.json"))!;
            Check(Saved.Tabs.Select(Tab => Tab.IsPinned).SequenceEqual(new[] { true, true, false })
                && Saved.ActiveTab == 2 && Saved.Tabs.All(Tab => Tab.Settings.SeparatePinnedTabs),
                "Workspace snapshots retain pin order, active tab and the row preference");
            Probe.RequestExit();
            Probe = new MainWindow(SettingsFile, RestoreWorkspace: true)
                { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show();
            await Probe.ActiveTab!.WaitUntilReadyAsync();
            await Probe.WorkspaceRestored;
            Check(Probe.Tabs.Select(Page => Page.TabAlias).SequenceEqual(new[] { "Third", "Second", "First" })
                && Probe.Tabs.Select(Page => Page.IsTabPinned).SequenceEqual(new[] { true, true, false })
                && Probe.ActiveTab!.TabAlias == "First" && Probe.PinnedHeaders.Count == 2 && Probe.VisibleHeaders.Count == 1,
                "Reopening restores both rows and selects the same workspace");
            foreach (var Page in Probe.Tabs.Where(Page => Page.IsTabPinned).ToArray())
                PinButton(Page).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!Probe.PinnedTabScroll.IsVisible && Probe.TabHeaders.Items.Count == 3,
                "The separate row collapses when the last pinned tab is unpinned");
            Probe.SetTabPinned(Probe.ActiveTab!, true);
            Check(Probe.CloseTab(Probe.ActiveTab!) && Probe.Tabs.Count == 2 && Probe.PinnedHeaders.Count == 0,
                "A pinned tab still closes normally and removes its row when empty");
            Check(KeyboardShortcut.Resolve(new Dictionary<string, string> { ["Search"] = "Alt+W" }) is var Migrated
                && Migrated["Search"] == "Alt+W" && Migrated["ToggleTabPin"] == "",
                "An existing custom Alt+W binding is preserved when loading older settings");
        }
        finally { Probe?.RequestExit(); }
    }
}
