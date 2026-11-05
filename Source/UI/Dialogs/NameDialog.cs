using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CDir.Localization;

namespace CDir.UI.Dialogs;

internal sealed class NameDialog : DialogWindow
{
    private readonly TextBox NameInput;
    public string EnteredName => this.NameInput.Text;
    public NameDialog(Window Owner, string Title, string Initial, bool SelectStem = false, string HintKey = "DialogNameHint", bool AllowEmpty = false)
    {
        this.Owner = Owner;
        this.Title = Title;
        this.Width = 430;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var Panel = new StackPanel { Margin = new Thickness(26) };
        Panel.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        Panel.Children.Add(new TextBlock { Text = LanguageService.Text(HintKey), Foreground = (Brush)this.FindResource("Muted"), FontSize = 12, Margin = new Thickness(0, 0, 0, 18), TextWrapping = TextWrapping.Wrap });
        this.NameInput = new TextBox { Text = Initial, MinWidth = 340, Margin = new Thickness(0, 0, 0, 22) };
        Panel.Children.Add(this.NameInput);
        var Buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        System.Windows.Controls.Button Cancel = new System.Windows.Controls.Button { Content = LanguageService.Text("CommonCancel"), IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        System.Windows.Controls.Button Confirm = new System.Windows.Controls.Button { Content = LanguageService.Text("CommonConfirm"), IsDefault = true, MinWidth = 80, Style = (Style)FindResource("PrimaryButton") };
        Confirm.Click += (_, _) => { if (AllowEmpty || !string.IsNullOrWhiteSpace(this.EnteredName)) DialogResult = true; };
        Buttons.Children.Add(Cancel);
        Buttons.Children.Add(Confirm);
        Panel.Children.Add(Buttons);
        Content = Panel;
        Loaded += (_, _) =>
        {
            this.NameInput.Focus();
            var Length = SelectStem ? System.IO.Path.GetFileNameWithoutExtension(Initial).Length : Initial.Length;
            this.NameInput.Select(0, Length);
        };
    }
}
