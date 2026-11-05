using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunTerminalThemeSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Terminal theme UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        var Surface = (EmbeddedTerminalControl)this.TerminalSurface!;
        var Session = this.TerminalSession;
        var Font = this.GetTerminalFontSize();
        var Background = this.GetTerminalBackgroundColor();
        var OriginalForeground = this.GetTerminalForegroundColor();
        var Bindings = new Dictionary<string, string>(this.ShortcutBindings);
        var Overrides = this.Settings.Shortcuts;
        var Language = LanguageService.CurrentLanguage;
        try
        {
            Check(DefaultSettings.Current.TerminalFontSize == 15 && DefaultSettings.Current.TerminalBackgroundColor == "#1E1E1E"
                && new AppSettings() is { TerminalFontSize: 15, TerminalBackgroundColor: "#1E1E1E" },
                "New settings and restored defaults use the requested 15-point charcoal terminal theme");
            this.ChangeTerminalFontSize(13);
            FocusManager.SetFocusedElement(this, Surface);
            Check(await this.HandleShortcutAsync(Key.OemPlus, ModifierKeys.Control, false, false) && Surface.FontSize == 14,
                "Ctrl plus increases the focused terminal font immediately");
            Check(await this.HandleShortcutAsync(Key.Add, ModifierKeys.Control, false, false) && Surface.FontSize == 15,
                "The numeric keypad plus key also increases terminal font size");
            Check(await this.HandleShortcutAsync(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift, false, false) && Surface.FontSize == 16,
                "Ctrl Shift plus supports keyboards that require Shift to type the plus symbol");
            Check(await this.HandleShortcutAsync(Key.OemMinus, ModifierKeys.Control, false, false)
                && await this.HandleShortcutAsync(Key.Subtract, ModifierKeys.Control, false, false) && Surface.FontSize == 14,
                "Both main-keyboard and numeric-keypad minus decrease terminal font size");
            Check(Surface.FontWheel!(120, ModifierKeys.Control) && Surface.FontSize == 15
                && Surface.FontWheel(-120, ModifierKeys.Control) && Surface.FontSize == 14,
                "Ctrl wheel up and down adjust the focused terminal font in opposite directions");
            Check(!Surface.FontWheel(120, ModifierKeys.None) && !Surface.FontWheel(120, ModifierKeys.Control | ModifierKeys.Shift)
                && Surface.FontSize == 14, "Unassigned wheel gestures retain ordinary scrolling without resizing text");
            FocusManager.SetFocusedElement(this, this.SearchBox);
            Check(!await this.HandleShortcutAsync(Key.OemPlus, ModifierKeys.Control, true, false)
                && !Surface.FontWheel(120, ModifierKeys.Control) && Surface.FontSize == 14,
                "Terminal font keys and wheel gestures do not act while another control has focus");
            FocusManager.SetFocusedElement(this, Surface);
            Check(this.ChangeShortcut("TerminalFontIncrease", "F7")
                && !await this.HandleShortcutAsync(Key.OemPlus, ModifierKeys.Control, false, false)
                && await this.HandleShortcutAsync(Key.F7, ModifierKeys.None, false, false) && Surface.FontSize == 15,
                "A rebound terminal font key replaces its previous binding");
            Check(!this.ChangeShortcut("TerminalFontIncrease", "Ctrl+F"), "Terminal font keys reject conflicts with existing actions");
            Check(this.ChangeShortcut("TerminalFontIncreaseWheel", "Alt+WheelUp")
                && !Surface.FontWheel(120, ModifierKeys.Control) && Surface.FontWheel(240, ModifierKeys.Alt) && Surface.FontSize == 17,
                "Rebound wheel gestures replace defaults and respect multiple wheel notches");
            Check(this.ChangeShortcut("TerminalFontIncreaseWheel", "") && !Surface.FontWheel(120, ModifierKeys.Alt),
                "Clearing a wheel binding disables that terminal zoom gesture");
            this.ChangeTerminalFontSize(48);
            Check(await this.HandleShortcutAsync(Key.F7, ModifierKeys.None, false, false) && Surface.FontSize == 48,
                "Zooming in at the maximum consumes the shortcut without exceeding the limit");
            this.ChangeTerminalFontSize(8);
            Check(await this.HandleShortcutAsync(Key.OemMinus, ModifierKeys.Control, false, false) && Surface.FontSize == 8
                && !this.ChangeTerminalFontSize(double.NaN) && !this.ChangeTerminalFontSize(49),
                "Zooming out and invalid sizes preserve the supported font bounds");
            this.ChangeTerminalFontSize(15);
            this.ChangeTerminalFontSize(22);
            Check(await this.HandleShortcutAsync(Key.D0, ModifierKeys.Control, false, false) && Surface.FontSize == 15
                && this.TerminalFontStatusText.Text == LanguageService.Text("TerminalFontStatus", 15),
                "Ctrl zero restores the default terminal font and updates the information bar");
            this.ChangeTerminalFontSize(20);
            Check(await this.HandleShortcutAsync(Key.NumPad0, ModifierKeys.Control, false, false) && Surface.FontSize == 15,
                "Ctrl numeric keypad zero also restores the default font");
            Check(this.TerminalInfoBar.IsVisible && this.TerminalFontStatusText.TranslatePoint(new Point(), this.TerminalInfoBar).X
                < this.TerminalBackgroundStatusText.TranslatePoint(new Point(), this.TerminalInfoBar).X,
                "The terminal bottom information bar shows font size before background color");
            await this.RunPreferencesSmokeChecksAsync(Passed, InspectShortcuts: async Dialog =>
            {
                Dialog.Categories.SelectedItem = Dialog.TerminalThemeCategory;
                Dialog.UpdateLayout();
                Dialog.TerminalFontSizePicker.SelectedItem = 18.0;
                Check(Surface.FontSize == 18 && this.Settings.TerminalFontSize == 18 && Dialog.TerminalPreviewText.FontSize == 18,
                    "The Terminal Theme font picker updates the existing terminal preview and settings together");
                Dialog.TerminalBackgroundEditor.Text = "#FAF9F6";
                Check(Surface.ScreenBackground == Color.FromRgb(250, 249, 246)
                    && ((SolidColorBrush)Dialog.TerminalThemePreview.Background).Color == Surface.ScreenBackground
                    && Surface.ScreenForeground == Color.FromRgb(0x20, 0x29, 0x38),
                    "A custom light background updates the live terminal and preview with readable dark text");
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Check(((SolidColorBrush)this.TerminalHeader.Background).Color == Surface.ScreenBackground
                    && ((SolidColorBrush)this.TerminalPanel.BorderBrush).Color == Surface.ScreenBackground
                    && ((SolidColorBrush)this.TerminalHeader.Resources["TerminalHeaderForeground"]).Color == Surface.ScreenForeground
                    && ((SolidColorBrush)this.TerminalExitedHint.Foreground).Color == Surface.ScreenForeground,
                    "The terminal header border labels and button colors follow a light background with readable text");
                Dialog.TerminalBackgroundEditor.Text = "#GGGGGG";
                Check(Dialog.TerminalColorError.IsVisible && this.Settings.TerminalBackgroundColor == "#FAF9F6"
                    && Surface.ScreenBackground == Color.FromRgb(250, 249, 246),
                    "Invalid color input shows feedback and preserves the last valid background");
                Dialog.TerminalColorButtons.Single(Button => (string)Button.Tag == "#002B36").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!Dialog.TerminalColorError.IsVisible && Surface.ScreenBackground == Color.FromRgb(0, 43, 54)
                    && this.Settings.TerminalBackgroundColor == "#002B36", "Color swatches apply and save a valid terminal background");
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Check(((SolidColorBrush)this.TerminalHeader.Background).Color == Color.FromRgb(0, 43, 54)
                    && ((SolidColorBrush)this.TerminalHeader.Resources["TerminalHeaderForeground"]).Color == Surface.ScreenForeground
                    && ((SolidColorBrush)this.TerminalHeader.Resources["TerminalHeaderHoverBackground"]).Color != Surface.ScreenBackground,
                    "Dark background changes also update the full terminal header and retain a visible button hover color");
                Check(this.TerminalBackgroundStatusText.Text == LanguageService.Text("TerminalBackgroundStatus", "#002B36")
                    && this.TerminalFontStatusText.Text == LanguageService.Text("TerminalFontStatus", 18),
                    "Theme changes refresh both terminal information values immediately");
                Dialog.TerminalAutomaticForeground.IsChecked = false;
                Dialog.TerminalForegroundEditor.Text = "#FFD166";
                Check(Surface.ScreenForeground == Color.FromRgb(255, 209, 102)
                    && this.Settings.TerminalForegroundColor == "#FFD166"
                    && ((SolidColorBrush)Dialog.TerminalPreviewText.Foreground).Color == Surface.ScreenForeground,
                    "Custom font color applies immediately to the terminal preview and saved theme");
                Dialog.TerminalForegroundEditor.Text = "#XX0000";
                Check(Dialog.TerminalForegroundError.IsVisible && Surface.ScreenForeground == Color.FromRgb(255, 209, 102),
                    "Invalid font color input retains the last valid text color and shows feedback");
                Dialog.TerminalForegroundButtons.Single(Button => (string)Button.Tag == "#66D9EF").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!Dialog.TerminalForegroundError.IsVisible && Surface.ScreenForeground == Color.FromRgb(102, 217, 239),
                    "Font color swatches correct invalid input and update the live terminal");
                var Saved = Path.Combine(AppMain.SmokeRoot!, "TerminalThemeSettings.json");
                Check(this.SaveSettings(Saved) && ReadSettings(Saved) is var Restored
                    && Restored.TerminalFontSize == 18 && Restored.TerminalBackgroundColor == "#002B36" && Restored.TerminalForegroundColor == "#66D9EF",
                    "Terminal font size and custom background survive saving and loading settings");
                foreach (var Choice in LanguageService.SupportedLanguages)
                {
                    Dialog.LanguagePicker.SelectedValue = Choice.Code;
                    Check(Dialog.TerminalThemeCategory.Content?.ToString() == LanguageService.Text("TerminalTheme")
                        && System.Windows.Automation.AutomationProperties.GetName(Dialog.TerminalFontSizePicker) == LanguageService.Text("TerminalFontSize")
                        && System.Windows.Automation.AutomationProperties.GetName(Dialog.TerminalBackgroundEditor) == LanguageService.Text("TerminalBackgroundColor")
                        && System.Windows.Automation.AutomationProperties.GetName(Dialog.TerminalForegroundEditor) == LanguageService.Text("TerminalForegroundColor")
                        && this.TerminalFontStatusText.Text == LanguageService.Text("TerminalFontStatus", 18)
                        && this.TerminalBackgroundStatusText.Text == LanguageService.Text("TerminalBackgroundStatus", "#002B36"),
                        Choice.Code + " terminal theme captions and accessible field names update live");
                }
                Dialog.LanguagePicker.SelectedValue = Language;
                Dialog.Categories.SelectedItem = Dialog.TerminalThemeCategory;
                Dialog.UpdateLayout();
                if (AppMain.ScreenshotPath is { } Output)
                {
                    var Bitmap = new RenderTargetBitmap((int)Dialog.ActualWidth, (int)Dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    Bitmap.Render(Dialog);
                    var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                    using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, "PreferencesTerminalTheme.png"));
                    Encoder.Save(Stream);
                }
                await Task.CompletedTask;
            });
            Check(ReferenceEquals(Session, this.TerminalSession) && !Session!.HasExited && Surface.FontSize == 18,
                "Theme changes preserve the running terminal session and its process");
            await this.RunPreferencesSmokeChecksAsync(Passed, InspectShortcuts: async Dialog =>
            {
                Dialog.Categories.SelectedItem = Dialog.TerminalThemeCategory;
                Check(Equals(Dialog.TerminalFontSizePicker.SelectedItem, 18.0) && Dialog.TerminalBackgroundEditor.Text == "#002B36"
                    && Dialog.TerminalForegroundEditor.Text == "#66D9EF" && Dialog.TerminalAutomaticForeground.IsChecked == false,
                    "Reopening Terminal Theme retains the saved font size and background");
                Dialog.RestoreDefaultsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (!Dialog.RestoreDefaultsButton.IsEnabled) await Task.Delay(20);
                Check(Surface.FontSize == DefaultSettings.Current.TerminalFontSize
                    && this.GetTerminalBackgroundColor() == DefaultSettings.Current.TerminalBackgroundColor
                    && Equals(Dialog.TerminalFontSizePicker.SelectedItem, DefaultSettings.Current.TerminalFontSize)
                    && Dialog.TerminalBackgroundEditor.Text == DefaultSettings.Current.TerminalBackgroundColor
                    && this.Settings.TerminalForegroundColor is null && Dialog.TerminalAutomaticForeground.IsChecked == true,
                    "Restore Default Settings resets the terminal and the open theme controls together");
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Check(((SolidColorBrush)this.TerminalHeader.Background).Color == Color.FromRgb(30, 30, 30) && Surface.FontSize == 15,
                    "Restoring defaults applies the requested charcoal color to both the terminal body and header");
            });
        }
        finally
        {
            this.Settings.Shortcuts = Overrides;
            this.ShortcutBindings = Bindings;
            this.ChangeLanguage(Language, Persist: false);
            this.ChangeTerminalFontSize(Font);
            this.ChangeTerminalBackgroundColor(Background);
            this.ChangeTerminalForegroundColor(OriginalForeground);
            this.ApplyShortcutHints();
            FocusManager.SetFocusedElement(this, Surface);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
    }
}
