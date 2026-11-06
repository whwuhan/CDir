using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI.Dialogs;

internal sealed class ResetPreferencesDialog : DialogWindow
{
    internal RadioButton CurrentCategoryOption { get; }
    internal RadioButton AllPreferencesOption { get; }
    internal Button RestoreButton { get; }
    internal Button CancelButton { get; }
    internal PreferenceResetScope SelectedScope => this.AllPreferencesOption.IsChecked == true ? PreferenceResetScope.All : this.CurrentScope;
    private readonly PreferenceResetScope CurrentScope;

    internal ResetPreferencesDialog(Window Owner, PreferenceResetScope CurrentScope, string CategoryName)
    {
        this.Owner = Owner;
        this.FontFamily = Owner.FontFamily;
        this.Language = Owner.Language;
        this.CurrentScope = CurrentScope;
        this.Title = LanguageService.Text("RestoreDefaults");
        this.Width = 520;
        this.MaxWidth = SystemParameters.WorkArea.Width;
        this.SizeToContent = SizeToContent.Height;
        this.ResizeMode = ResizeMode.NoResize;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.ShowInTaskbar = false;
        this.ShowActivated = !AppMain.SmokeTest;
        this.SetResourceReference(FontSizeProperty, "UIFontPreferences");
        var Panel = new StackPanel { Margin = new Thickness(24) };
        Panel.Children.Add(new TextBlock
        {
            Text = LanguageService.Text("ResetScopeQuestion"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        });
        this.CurrentCategoryOption = new RadioButton
        {
            Content = new TextBlock { Text = LanguageService.Text("ResetScopeCurrent", CategoryName), TextWrapping = TextWrapping.Wrap },
            IsChecked = true, GroupName = "ResetScope", Margin = new Thickness(0, 0, 0, 12), VerticalContentAlignment = VerticalAlignment.Center
        };
        this.AllPreferencesOption = new RadioButton
        {
            Content = new TextBlock { Text = LanguageService.Text("ResetScopeAll"), TextWrapping = TextWrapping.Wrap },
            GroupName = "ResetScope", Margin = new Thickness(0, 0, 0, 22), VerticalContentAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(this.CurrentCategoryOption, LanguageService.Text("ResetScopeCurrent", CategoryName));
        System.Windows.Automation.AutomationProperties.SetName(this.AllPreferencesOption, LanguageService.Text("ResetScopeAll"));
        Panel.Children.Add(this.CurrentCategoryOption);
        Panel.Children.Add(this.AllPreferencesOption);
        var Buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        this.CancelButton = new Button { Content = LanguageService.Text("CommonCancel"), IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        this.RestoreButton = new Button { Content = LanguageService.Text("ResetScopeApply"), IsDefault = true, MinWidth = 80, Style = (Style)this.FindResource("PrimaryButton") };
        this.CancelButton.Click += (_, _) => this.DialogResult = false;
        this.RestoreButton.Click += (_, _) => this.DialogResult = true;
        Buttons.Children.Add(this.CancelButton);
        Buttons.Children.Add(this.RestoreButton);
        Panel.Children.Add(Buttons);
        this.Content = new ScrollViewer
        {
            Content = Panel, MaxHeight = Math.Max(200, SystemParameters.WorkArea.Height - 80),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }
}
