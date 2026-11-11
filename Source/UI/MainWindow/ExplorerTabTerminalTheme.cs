using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private double GetTerminalFontSize() => double.IsFinite(this.Settings.TerminalFontSize)
        ? Math.Clamp(Math.Round(this.Settings.TerminalFontSize), 8, 48) : DefaultSettings.Current.TerminalFontSize;

    internal static bool TryTerminalColor(string? Text, out Color Color)
    {
        Color = default;
        var Value = Text?.Trim();
        if (Value is not { Length: 7 } || Value[0] != '#'
            || !uint.TryParse(Value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var Rgb)) return false;
        Color = System.Windows.Media.Color.FromRgb((byte)(Rgb >> 16), (byte)(Rgb >> 8), (byte)Rgb);
        return true;
    }

    private string GetTerminalBackgroundColor() => TryTerminalColor(this.Settings.TerminalBackgroundColor, out var Color)
        ? Color.ToString()[3..].Insert(0, "#") : DefaultSettings.Current.TerminalBackgroundColor;

    internal static Color TerminalForeground(Color Background) =>
        (Background.R * 0.299 + Background.G * 0.587 + Background.B * 0.114) >= 150
            ? Color.FromRgb(0x20, 0x29, 0x38) : Color.FromRgb(0xE5, 0xE7, 0xEB);

    private string? GetTerminalForegroundColor() => TryTerminalColor(this.Settings.TerminalForegroundColor, out var Color)
        ? Color.ToString()[3..].Insert(0, "#") : null;

    private void UpdateTerminalStatus()
    {
        if (this.TerminalFontStatusText is null) return;
        this.TerminalFontStatusText.Text = LanguageService.Text("TerminalFontStatus", this.GetTerminalFontSize());
        this.TerminalBackgroundStatusText.Text = LanguageService.Text("TerminalBackgroundStatus", this.GetTerminalBackgroundColor());
        this.TerminalFontHint.Text = LanguageService.Text("TerminalFontHint",
            this.ShortcutBindings["TerminalFontIncrease"] is { Length: > 0 } Increase ? Increase : "—",
            this.ShortcutBindings["TerminalFontDecrease"] is { Length: > 0 } Decrease ? Decrease : "—");
    }

    private void ApplyTerminalFontWeight()
    {
        if (this.TerminalSurface is not { } Surface) return;
        if (Surface.Session?.Decoder is VirtualTerminal.TerminalDecoder Decoder)
            Decoder.Options.FontFallback = this.Settings.BoldAllFonts
                ? CDir.Controls.EmbeddedTerminalControl.BoldUnicodeFontFallback
                : CDir.Controls.EmbeddedTerminalControl.UnicodeFontFallback;
        // The terminal renderer resolves font faces by family name rather than Control.FontWeight.
        var Family = this.Settings.BoldAllFonts ? "Consolas Bold" : "Consolas";
        if (Surface.FontFamily.Source != Family) Surface.FontFamily = new FontFamily(Family);
    }

    private void ApplyTerminalTheme()
    {
        this.Settings.TerminalFontSize = this.GetTerminalFontSize();
        this.Settings.TerminalBackgroundColor = this.GetTerminalBackgroundColor();
        this.Settings.TerminalForegroundColor = this.GetTerminalForegroundColor();
        this.UpdateTerminalStatus();
        this.ApplyTerminalFontWeight();
        if (this.TerminalSurface is not { } Surface) return;
        TryTerminalColor(this.Settings.TerminalBackgroundColor, out var Background);
        var Foreground = TryTerminalColor(this.Settings.TerminalForegroundColor, out var Custom) ? Custom : TerminalForeground(Background);
        var ChromeForeground = TerminalForeground(Background);
        Surface.FontSize = this.Settings.TerminalFontSize;
        Surface.Background = new SolidColorBrush(Background);
        Surface.ScreenBackground = Background;
        Surface.Foreground = new SolidColorBrush(Foreground);
        Surface.ScreenForeground = Foreground;
        this.TerminalPanel.Background = Surface.Background;
        this.TerminalPanel.BorderBrush = Surface.Background;
        this.TerminalHost.Background = Surface.Background;
        var ChromeBrush = new SolidColorBrush(ChromeForeground);
        this.TerminalHeader.Resources["TerminalHeaderForeground"] = ChromeBrush;
        this.TerminalFontStatusText.Foreground = ChromeBrush;
        this.TerminalBackgroundStatusText.Foreground = ChromeBrush;
        this.TerminalFontHint.Foreground = ChromeBrush;
        this.TerminalHeader.Resources["TerminalHeaderHoverBackground"] = this.FindResource("SurfaceHover");
    }

    private bool ChangeTerminalFontSize(double Size)
    {
        if (this.WindowClosed || !double.IsFinite(Size) || Size < 8 || Size > 48) return false;
        this.Settings.TerminalFontSize = Math.Round(Size);
        this.ApplyTerminalTheme();
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
        return true;
    }

    private bool ChangeTerminalBackgroundColor(string Color)
    {
        if (this.WindowClosed || !TryTerminalColor(Color, out _)) return false;
        this.Settings.TerminalBackgroundColor = Color;
        this.ApplyTerminalTheme();
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
        return true;
    }

    private bool ChangeTerminalForegroundColor(string? Color)
    {
        if (this.WindowClosed || (Color is not null && !TryTerminalColor(Color, out _))) return false;
        this.Settings.TerminalForegroundColor = Color;
        this.ApplyTerminalTheme();
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
        return true;
    }

    private bool HandleTerminalFontGesture(string Gesture, int Steps = 1, bool RequireFocus = true)
    {
        if (this.TerminalSurface is not { IsVisible: true } Surface
            || RequireFocus && !Surface.IsFocused && !Surface.IsKeyboardFocusWithin) return false;
        var Shortcut = KeyboardShortcut.All.FirstOrDefault(Item => Item.TerminalOnly && this.ShortcutBindings[Item.Id] == Gesture);
        if (Shortcut is null) return false;
        var Delta = Shortcut.Id.Contains("Increase", StringComparison.Ordinal) ? 1 : -1;
        var Size = Shortcut.Id == "TerminalFontReset" ? DefaultSettings.Current.TerminalFontSize
            : Math.Clamp(this.GetTerminalFontSize() + Delta * Steps, 8, 48);
        if (Size != this.GetTerminalFontSize()) this.ChangeTerminalFontSize(Size);
        return true;
    }

    internal bool IsMouseOverTerminal => this.TerminalPanel.IsVisible && this.TerminalPanel.IsMouseOver;

    internal bool TerminalThemeMouseWheel(int Delta, ModifierKeys Modifiers, bool PointerInside) => PointerInside && Delta != 0
        && this.HandleTerminalFontGesture(KeyboardShortcut.FormatWheel(Delta, Modifiers), Math.Max(1, Math.Abs(Delta / 120)), RequireFocus: false);
}
