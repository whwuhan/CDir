using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CDir.Models;

namespace CDir.UI;

internal sealed partial class PreferencesWindow
{
    internal ListBoxItem TerminalThemeCategory { get; private set; } = null!;
    internal FrameworkElement TerminalThemePanel { get; private set; } = null!;
    internal ComboBox TerminalFontSizePicker { get; private set; } = null!;
    internal TextBox TerminalBackgroundEditor { get; private set; } = null!;
    internal TextBlock TerminalColorError { get; private set; } = null!;
    internal Border TerminalThemePreview { get; private set; } = null!;
    internal TextBlock TerminalPreviewText { get; private set; } = null!;
    internal List<Button> TerminalColorButtons { get; } = [];
    internal TextBox TerminalForegroundEditor { get; private set; } = null!;
    internal CheckBox TerminalAutomaticForeground { get; private set; } = null!;
    internal TextBlock TerminalForegroundError { get; private set; } = null!;
    internal List<Button> TerminalForegroundButtons { get; } = [];

    private void BuildTerminalTheme(double Size, string Background, Func<double, bool> OnSizeChanged,
        Func<string, bool> OnBackgroundChanged, Func<bool> IsUpdating, string? Foreground,
        Func<string?, bool> OnForegroundChanged)
    {
        var Panel = new StackPanel { Visibility = Visibility.Collapsed };
        this.TerminalThemePanel = Panel;
        var Heading = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) };
        Heading.SetResourceReference(TextBlock.TextProperty, "TextTerminalTheme");
        Heading.SetResourceReference(FontSizeProperty, "UIPreferencesHeadingSize");
        Panel.Children.Add(Heading);
        void Label(string Key, Control Target)
        {
            var Label = new Label { Target = Target, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 8) };
            Label.SetResourceReference(ContentProperty, "Text" + Key);
            Target.SetResourceReference(AutomationProperties.NameProperty, "Text" + Key);
            Panel.Children.Add(Label);
            Panel.Children.Add(Target);
        }
        this.TerminalFontSizePicker = new ComboBox
        {
            Name = "TerminalFontSizePicker", Width = 120, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 20),
            ItemsSource = Enumerable.Range(8, 41).Select(Value => (double)Value).ToArray(), SelectedItem = Size
        };
        this.TerminalFontSizePicker.SetResourceReference(MinHeightProperty, "UIPreferencesControlHeight");
        Label("TerminalFontSize", this.TerminalFontSizePicker);
        this.TerminalBackgroundEditor = new TextBox
        {
            Name = "TerminalBackgroundEditor", Text = Background, MaxLength = 7, Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 0, 8)
        };
        Label("TerminalBackgroundColor", this.TerminalBackgroundEditor);
        this.TerminalBackgroundEditor.SetResourceReference(ToolTipProperty, "TextTerminalColorHint");
        this.TerminalColorError = new TextBlock { Visibility = Visibility.Collapsed, Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        this.TerminalColorError.SetResourceReference(TextBlock.TextProperty, "TextTerminalColorInvalid");
        AutomationProperties.SetLiveSetting(this.TerminalColorError, AutomationLiveSetting.Polite);
        Panel.Children.Add(this.TerminalColorError);
        var Swatches = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var Hex in new[] { "#111827", "#000000", "#1E1E1E", "#282A36", "#002B36", "#FFFFFF", "#F3F4F6" })
        {
            ExplorerTab.TryTerminalColor(Hex, out var Color);
            var Button = new Button
            {
                Width = 44, Height = 32, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(3),
                ToolTip = Hex, Tag = Hex,
                Content = new Border { Background = new SolidColorBrush(Color), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Width = 32, Height = 20 }
            };
            AutomationProperties.SetName(Button, Hex);
            Button.Click += (_, _) => this.TerminalBackgroundEditor.Text = Hex;
            this.TerminalColorButtons.Add(Button);
            Swatches.Children.Add(Button);
        }
        Panel.Children.Add(Swatches);
        var Hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20), Foreground = (Brush)this.FindResource("Muted") };
        Hint.SetResourceReference(TextBlock.TextProperty, "TextTerminalColorHint");
        Hint.SetResourceReference(FontSizeProperty, "UIPreferencesHintSize");
        Panel.Children.Add(Hint);
        this.TerminalForegroundEditor = new TextBox
        {
            Name = "TerminalForegroundEditor", Text = Foreground ?? "#E5E7EB", MaxLength = 7, Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 8)
        };
        Label("TerminalForegroundColor", this.TerminalForegroundEditor);
        this.TerminalAutomaticForeground = new CheckBox
        {
            IsChecked = Foreground is null, Margin = new Thickness(0, 0, 0, 10), VerticalContentAlignment = VerticalAlignment.Center
        };
        this.TerminalAutomaticForeground.SetResourceReference(ContentProperty, "TextTerminalAutomaticForeground");
        Panel.Children.Add(this.TerminalAutomaticForeground);
        this.TerminalForegroundError = new TextBlock { Visibility = Visibility.Collapsed, Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        this.TerminalForegroundError.SetResourceReference(TextBlock.TextProperty, "TextTerminalColorInvalid");
        AutomationProperties.SetLiveSetting(this.TerminalForegroundError, AutomationLiveSetting.Polite);
        Panel.Children.Add(this.TerminalForegroundError);
        var TextSwatches = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        foreach (var Hex in new[] { "#E5E7EB", "#FFFFFF", "#000000", "#A8D5BA", "#FFD166", "#66D9EF" })
        {
            ExplorerTab.TryTerminalColor(Hex, out var Color);
            var Button = new Button
            {
                Width = 44, Height = 32, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(3), ToolTip = Hex, Tag = Hex,
                Content = new Border { Background = new SolidColorBrush(Color), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Width = 32, Height = 20 }
            };
            AutomationProperties.SetName(Button, Hex);
            Button.Click += (_, _) =>
            {
                this.TerminalAutomaticForeground.IsChecked = false;
                this.TerminalForegroundEditor.Text = Hex;
            };
            this.TerminalForegroundButtons.Add(Button);
            TextSwatches.Children.Add(Button);
        }
        Panel.Children.Add(TextSwatches);
        var PreviewLabel = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        PreviewLabel.SetResourceReference(TextBlock.TextProperty, "TextTerminalPreview");
        Panel.Children.Add(PreviewLabel);
        this.TerminalPreviewText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = Size, Text = "PS C:\\> Get-ChildItem\nCDir", TextWrapping = TextWrapping.Wrap };
        this.TerminalThemePreview = new Border { Padding = new Thickness(16), MinHeight = 100, Child = this.TerminalPreviewText };
        Panel.Children.Add(this.TerminalThemePreview);
        void UpdatePreview()
        {
            if (ExplorerTab.TryTerminalColor(this.TerminalBackgroundEditor.Text, out var Color))
            {
                this.TerminalThemePreview.Background = new SolidColorBrush(Color);
                if (this.TerminalAutomaticForeground.IsChecked == true)
                {
                    var Automatic = ExplorerTab.TerminalForeground(Color);
                    this.TerminalForegroundEditor.Text = Automatic.ToString()[3..].Insert(0, "#");
                    this.TerminalPreviewText.Foreground = new SolidColorBrush(Automatic);
                }
                else if (ExplorerTab.TryTerminalColor(this.TerminalForegroundEditor.Text, out var TextColor))
                    this.TerminalPreviewText.Foreground = new SolidColorBrush(TextColor);
            }
            if (this.TerminalFontSizePicker.SelectedItem is double Value) this.TerminalPreviewText.FontSize = Value;
        }
        this.TerminalFontSizePicker.SelectionChanged += (_, _) =>
        {
            if (!IsUpdating() && this.TerminalFontSizePicker.SelectedItem is double Value) OnSizeChanged(Value);
            UpdatePreview();
        };
        this.TerminalBackgroundEditor.TextChanged += (_, _) =>
        {
            var Valid = ExplorerTab.TryTerminalColor(this.TerminalBackgroundEditor.Text, out _);
            this.TerminalColorError.Visibility = Valid ? Visibility.Collapsed : Visibility.Visible;
            if (Valid && !IsUpdating()) OnBackgroundChanged(this.TerminalBackgroundEditor.Text);
            UpdatePreview();
        };
        this.TerminalForegroundEditor.TextChanged += (_, _) =>
        {
            var Valid = ExplorerTab.TryTerminalColor(this.TerminalForegroundEditor.Text, out _);
            this.TerminalForegroundError.Visibility = Valid || this.TerminalAutomaticForeground.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            if (Valid && !IsUpdating() && this.TerminalAutomaticForeground.IsChecked != true)
                OnForegroundChanged(this.TerminalForegroundEditor.Text);
            UpdatePreview();
        };
        void AutomaticChanged(object Sender, RoutedEventArgs E)
        {
            this.TerminalForegroundEditor.IsEnabled = this.TerminalAutomaticForeground.IsChecked != true;
            if (!IsUpdating()) OnForegroundChanged(this.TerminalAutomaticForeground.IsChecked == true ? null : this.TerminalForegroundEditor.Text);
            UpdatePreview();
        }
        this.TerminalAutomaticForeground.Checked += AutomaticChanged;
        this.TerminalAutomaticForeground.Unchecked += AutomaticChanged;
        this.TerminalForegroundEditor.IsEnabled = Foreground is not null;
        UpdatePreview();
    }
}
