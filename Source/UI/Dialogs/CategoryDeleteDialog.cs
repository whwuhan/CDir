using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CDir.Localization;

namespace CDir.UI.Dialogs;

internal sealed class CategoryDeleteDialog : DialogWindow
{
    internal TextBlock Question { get; }
    internal Button ConfirmButton { get; }
    internal Button CancelButton { get; }

    public CategoryDeleteDialog(Window Owner, string CategoryName)
    {
        this.Owner = Owner;
        this.Title = LanguageService.Text("DeleteCategory");
        this.FontFamily = Owner.FontFamily;
        this.Language = Owner.Language;
        this.Width = 460;
        this.SizeToContent = SizeToContent.Height;
        this.ResizeMode = ResizeMode.NoResize;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.ShowInTaskbar = false;
        var Panel = new StackPanel { Margin = new Thickness(26) };
        Panel.Children.Add(new TextBlock { Text = this.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16), TextWrapping = TextWrapping.Wrap });
        this.Question = new TextBlock { Text = LanguageService.Text("DeleteCategoryQuestion", CategoryName), FontSize = 14, TextWrapping = TextWrapping.Wrap };
        var Body = new StackPanel();
        Body.Children.Add(this.Question);
        Body.Children.Add(new TextBlock { Text = LanguageService.Text("DeleteCategoryHint"), Foreground = (Brush)this.FindResource("Muted"), FontSize = 12,
            Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap });
        Panel.Children.Add(new ScrollViewer { Content = Body, MaxHeight = Math.Max(100, SystemParameters.WorkArea.Height - 240),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 22) });
        var Buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        this.CancelButton = new Button { Content = LanguageService.Text("CommonCancel"), IsCancel = true, IsDefault = true, MinWidth = 80,
            Margin = new Thickness(0, 0, 8, 0), Style = (Style)FindResource("PrimaryButton") };
        this.ConfirmButton = new Button { Content = LanguageService.Text("CommonConfirm"), MinWidth = 80 };
        this.CancelButton.Click += (_, _) => this.DialogResult = false;
        this.ConfirmButton.Click += (_, _) => this.DialogResult = true;
        Buttons.Children.Add(this.CancelButton);
        Buttons.Children.Add(this.ConfirmButton);
        Panel.Children.Add(Buttons);
        this.Content = Panel;
    }
}
