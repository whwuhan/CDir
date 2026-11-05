using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using CDir.Localization;

namespace CDir.UI.Dialogs;

internal sealed class AboutWindow : DialogWindow
{
    internal const string RepositoryUrl = "https://github.com/whwuhan/CDir";
    internal TextBlock AuthorText { get; }
    internal TextBlock RepositoryText { get; }
    internal Hyperlink RepositoryLink { get; }
    internal Button CloseButton { get; }
    internal Action<ProcessStartInfo> OpenLink { get; set; } = Info => Process.Start(Info);

    internal AboutWindow(Window Owner)
    {
        this.Owner = Owner;
        this.SetResourceReference(TitleProperty, "TextAbout");
        this.SetResourceReference(FontSizeProperty, "UIFontPreferences");
        this.Language = XmlLanguage.GetLanguage(LanguageService.CurrentLanguage);
        this.Width = 480;
        this.SizeToContent = SizeToContent.Height;
        this.ResizeMode = ResizeMode.NoResize;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.ShowInTaskbar = false;
        this.ShowActivated = !AppMain.SmokeTest;

        var Panel = new StackPanel { Margin = new Thickness(22) };
        this.AuthorText = new TextBlock
        {
            Text = LanguageService.Text("AboutAuthor", LanguageService.CurrentLanguage == "zh-CN" ? "吴寒" : "chesterwu"),
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap
        };
        Panel.Children.Add(this.AuthorText);
        this.RepositoryLink = new Hyperlink(new Run(RepositoryUrl)) { NavigateUri = new Uri(RepositoryUrl) };
        this.RepositoryLink.RequestNavigate += (_, E) =>
        {
            E.Handled = true;
            try { this.OpenLink(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true }); }
            catch (Exception Ex)
            {
                MessageBox.Show(this, LanguageService.Text("OpenLocationError", Ex.Message), this.Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        this.RepositoryText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        this.RepositoryText.Inlines.Add(new Run(LanguageService.Text("AboutGitHub") + " "));
        this.RepositoryText.Inlines.Add(this.RepositoryLink);
        Panel.Children.Add(this.RepositoryText);
        this.CloseButton = new Button
        {
            MinWidth = 80,
            Margin = new Thickness(0, 20, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsCancel = true,
            IsDefault = true
        };
        this.CloseButton.SetResourceReference(ContentControl.ContentProperty, "TextClose");
        this.CloseButton.Click += (_, _) => this.Close();
        Panel.Children.Add(this.CloseButton);
        this.Content = Panel;
    }
}
