using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Markup;
using System.Windows.Media;
using CDir.Localization;
using CDir.Models;
using CDir.UI.Dialogs;

namespace CDir.UI;

internal sealed partial class PreferencesWindow : DialogWindow
{
    private ColumnDefinition? CategoryColumn;
    internal ComboBox LanguagePicker { get; }
    internal Label LanguageLabel { get; }
    internal CheckBox GridLinesCheck { get; }
    internal CheckBox ShowHiddenCheck { get; }
    internal CheckBox ExitOnCloseCheck { get; }
    internal CheckBox CopyOnDropCheck { get; }
    internal CheckBox CalculateFolderSizesCheck { get; }
    internal CheckBox ShowBookmarkPathsCheck { get; }
    internal CheckBox ShowSidebarDriveUsageCheck { get; }
    internal CheckBox SeparatePinnedTabsCheck { get; }
    internal CheckBox StartOnLoginCheck { get; }
    internal ComboBox TimeFormatPicker { get; }
    internal Label TimeFormatLabel { get; }
    internal ListBox Categories { get; }
    internal ListBoxItem GeneralCategory { get; }
    internal ListBoxItem UICategory { get; }
    internal FrameworkElement GeneralPanel { get; }
    internal FrameworkElement UIPanel { get; }
    internal IReadOnlyDictionary<string, CheckBox> ColumnChecks { get; }
    internal IReadOnlyDictionary<string, ComboBox> FontSizePickers { get; }
    internal IReadOnlyDictionary<string, TextBlock> FontSizeLabels { get; }
    internal Button RestoreDefaultsButton { get; }

    public PreferencesWindow(Window Owner, Action<string> OnLanguageChanged, bool ShowGridLines, Action<bool> OnGridLinesChanged,
        bool ShowHidden, Func<bool, Task> OnShowHiddenChanged, bool Use12HourClock, Action<bool> OnTimeFormatChanged,
        IReadOnlyList<ColumnPreference> Columns, Func<string, bool, bool> OnColumnVisibilityChanged,
        IReadOnlyList<UIFontOption> Fonts, Func<string, double, bool> OnFontSizeChanged, Func<PreferenceResetScope, Task> OnRestoreDefaults,
        IReadOnlyDictionary<string, string> Shortcuts, Func<string, string, bool> OnShortcutChanged,
        double TerminalFontSize, string TerminalBackgroundColor, Func<double, bool> OnTerminalFontSizeChanged,
        Func<string, bool> OnTerminalBackgroundChanged, string? TerminalForegroundColor, Func<string?, bool> OnTerminalForegroundChanged,
        bool ExitOnClose, Action<bool> OnExitOnCloseChanged, bool CopyOnDrop, Action<bool> OnCopyOnDropChanged,
        bool CalculateFolderSizes, Action<bool> OnCalculateFolderSizesChanged,
        bool ShowBookmarkPaths, Action<bool> OnShowBookmarkPathsChanged,
        bool ShowSidebarDriveUsage, Action<bool> OnShowSidebarDriveUsageChanged,
        bool SeparatePinnedTabs, Action<bool> OnSeparatePinnedTabsChanged,
        bool StartOnLogin, Func<bool, bool> OnStartOnLoginChanged)
    {
        var UpdatingPreferences = false;
        this.RestoreDefaultsButton = new Button
        {
            Name = "RestoreDefaultsButton",
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(16, 12, 16, 12),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        this.Owner = Owner;
        this.SetResourceReference(TitleProperty, "TextPreferences");
        this.Width = 880;
        this.Height = 640;
        this.MinWidth = 800;
        this.MinHeight = 320;
        this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.ShowInTaskbar = false;
        this.ShowActivated = !AppMain.SmokeTest;
        this.Language = XmlLanguage.GetLanguage(LanguageService.CurrentLanguage);
        this.SetResourceReference(FontSizeProperty, "UIFontPreferences");

        this.LanguagePicker = new ComboBox
        {
            Name = "LanguagePicker",
            ItemsSource = LanguageService.SupportedLanguages,
            DisplayMemberPath = nameof(LanguageOption.NativeName),
            SelectedValuePath = nameof(LanguageOption.Code),
            SelectedValue = LanguageService.CurrentLanguage,
            Width = 240,
            Height = 32,
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = (Brush)this.FindResource("SurfaceInner"),
            BorderBrush = (Brush)this.FindResource("Line")
        };
        this.LanguagePicker.SetResourceReference(AutomationProperties.NameProperty, "TextLanguage");
        this.LanguagePicker.SetResourceReference(HeightProperty, "UIPreferencesControlHeight");
        this.LanguageLabel = new Label
        {
            Target = this.LanguagePicker,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 24, 0)
        };
        this.LanguageLabel.SetResourceReference(ContentControl.ContentProperty, "TextLanguage");
        var Panel = new Grid { VerticalAlignment = VerticalAlignment.Top };
        Panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(this.LanguagePicker, 1);
        Panel.Children.Add(this.LanguageLabel);
        Panel.Children.Add(this.LanguagePicker);
        this.TimeFormatPicker = new ComboBox
        {
            Name = "TimeFormatPicker",
            Width = 240,
            Height = 32,
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush)this.FindResource("SurfaceInner"),
            BorderBrush = (Brush)this.FindResource("Line")
        };
        var Clock24 = new ComboBoxItem { Tag = false };
        Clock24.SetResourceReference(ContentControl.ContentProperty, "TextClock24Hour");
        var Clock12 = new ComboBoxItem { Tag = true };
        Clock12.SetResourceReference(ContentControl.ContentProperty, "TextClock12Hour");
        this.TimeFormatPicker.Items.Add(Clock24);
        this.TimeFormatPicker.Items.Add(Clock12);
        this.TimeFormatPicker.SelectedIndex = Use12HourClock ? 1 : 0;
        this.TimeFormatPicker.SetResourceReference(AutomationProperties.NameProperty, "TextTimeFormat");
        this.TimeFormatPicker.SetResourceReference(HeightProperty, "UIPreferencesControlHeight");
        this.TimeFormatLabel = new Label
        {
            Target = this.TimeFormatPicker,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 16, 24, 0)
        };
        this.TimeFormatLabel.SetResourceReference(ContentControl.ContentProperty, "TextTimeFormat");
        Grid.SetRow(this.TimeFormatLabel, 1);
        Grid.SetRow(this.TimeFormatPicker, 1);
        Grid.SetColumn(this.TimeFormatPicker, 1);
        Panel.Children.Add(this.TimeFormatLabel);
        Panel.Children.Add(this.TimeFormatPicker);
        this.TimeFormatPicker.SelectionChanged += (_, _) =>
        {
            if (!UpdatingPreferences && this.TimeFormatPicker.SelectedItem is ComboBoxItem { Tag: bool Selected }) OnTimeFormatChanged(Selected);
        };
        this.GridLinesCheck = new CheckBox
        {
            Name = "GridLinesCheck",
            IsChecked = ShowGridLines,
            Margin = new Thickness(0, 24, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        this.GridLinesCheck.SetResourceReference(ContentControl.ContentProperty, "TextShowGridLines");
        this.GridLinesCheck.SetResourceReference(AutomationProperties.NameProperty, "TextShowGridLines");
        this.GridLinesCheck.Checked += (_, _) => { if (!UpdatingPreferences) OnGridLinesChanged(true); };
        this.GridLinesCheck.Unchecked += (_, _) => { if (!UpdatingPreferences) OnGridLinesChanged(false); };
        this.ShowHiddenCheck = new CheckBox
        {
            Name = "ShowHiddenCheck",
            IsChecked = ShowHidden,
            Margin = new Thickness(0, 16, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        this.ShowHiddenCheck.SetResourceReference(ContentControl.ContentProperty, "TextHiddenItems");
        this.ShowHiddenCheck.SetResourceReference(ToolTipProperty, "TextHiddenItemsTooltip");
        this.ShowHiddenCheck.SetResourceReference(AutomationProperties.NameProperty, "TextHiddenItems");
        Grid.SetRow(this.ShowHiddenCheck, 2);
        Grid.SetColumnSpan(this.ShowHiddenCheck, 2);
        Panel.Children.Add(this.ShowHiddenCheck);
        async void HiddenItemsChanged(object Sender, RoutedEventArgs E)
        {
            if (UpdatingPreferences) return;
            this.RestoreDefaultsButton.IsEnabled = false;
            this.ShowHiddenCheck.IsEnabled = false;
            try { await OnShowHiddenChanged(this.ShowHiddenCheck.IsChecked == true); }
            finally { this.ShowHiddenCheck.IsEnabled = true; this.RestoreDefaultsButton.IsEnabled = true; }
        }
        this.ShowHiddenCheck.Checked += HiddenItemsChanged;
        this.ShowHiddenCheck.Unchecked += HiddenItemsChanged;
        this.ExitOnCloseCheck = new CheckBox
        {
            Name = "ExitOnCloseCheck",
            IsChecked = ExitOnClose,
            Margin = new Thickness(0, 16, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        this.ExitOnCloseCheck.SetResourceReference(ContentControl.ContentProperty, "TextExitOnClose");
        this.ExitOnCloseCheck.SetResourceReference(AutomationProperties.NameProperty, "TextExitOnClose");
        this.ExitOnCloseCheck.SetResourceReference(ToolTipProperty, "TextExitOnCloseTooltip");
        this.ExitOnCloseCheck.Checked += (_, _) => { if (!UpdatingPreferences) OnExitOnCloseChanged(true); };
        this.ExitOnCloseCheck.Unchecked += (_, _) => { if (!UpdatingPreferences) OnExitOnCloseChanged(false); };
        Grid.SetRow(this.ExitOnCloseCheck, 3);
        Grid.SetColumnSpan(this.ExitOnCloseCheck, 2);
        Panel.Children.Add(this.ExitOnCloseCheck);
        Panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        this.CopyOnDropCheck = new CheckBox
        {
            Name = "CopyOnDropCheck", IsChecked = CopyOnDrop,
            Margin = new Thickness(0, 16, 0, 0), VerticalContentAlignment = VerticalAlignment.Center
        };
        this.CopyOnDropCheck.SetResourceReference(ContentControl.ContentProperty, "TextCopyOnDrop");
        this.CopyOnDropCheck.SetResourceReference(AutomationProperties.NameProperty, "TextCopyOnDrop");
        this.CopyOnDropCheck.SetResourceReference(ToolTipProperty, "TextCopyOnDropTooltip");
        this.CopyOnDropCheck.Checked += (_, _) => { if (!UpdatingPreferences) OnCopyOnDropChanged(true); };
        this.CopyOnDropCheck.Unchecked += (_, _) => { if (!UpdatingPreferences) OnCopyOnDropChanged(false); };
        Grid.SetRow(this.CopyOnDropCheck, 4);
        Grid.SetColumnSpan(this.CopyOnDropCheck, 2);
        Panel.Children.Add(this.CopyOnDropCheck);
        Panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        this.CalculateFolderSizesCheck = new CheckBox
        {
            Name = "CalculateFolderSizesCheck", IsChecked = CalculateFolderSizes,
            Margin = new Thickness(0, 16, 0, 0), VerticalContentAlignment = VerticalAlignment.Center
        };
        this.CalculateFolderSizesCheck.SetResourceReference(ContentControl.ContentProperty, "TextCalculateFolderSizes");
        this.CalculateFolderSizesCheck.SetResourceReference(AutomationProperties.NameProperty, "TextCalculateFolderSizes");
        this.CalculateFolderSizesCheck.SetResourceReference(ToolTipProperty, "TextCalculateFolderSizesTooltip");
        this.CalculateFolderSizesCheck.Checked += (_, _) => { if (!UpdatingPreferences) OnCalculateFolderSizesChanged(true); };
        this.CalculateFolderSizesCheck.Unchecked += (_, _) => { if (!UpdatingPreferences) OnCalculateFolderSizesChanged(false); };
        Grid.SetRow(this.CalculateFolderSizesCheck, 5);
        Grid.SetColumnSpan(this.CalculateFolderSizesCheck, 2);
        Panel.Children.Add(this.CalculateFolderSizesCheck);
        Panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        this.StartOnLoginCheck = new CheckBox
        {
            Name = "StartOnLoginCheck", IsChecked = StartOnLogin,
            Margin = new Thickness(0, 16, 0, 0), VerticalContentAlignment = VerticalAlignment.Center
        };
        this.StartOnLoginCheck.SetResourceReference(ContentControl.ContentProperty, "TextStartOnLogin");
        this.StartOnLoginCheck.SetResourceReference(AutomationProperties.NameProperty, "TextStartOnLogin");
        this.StartOnLoginCheck.SetResourceReference(ToolTipProperty, "TextStartOnLoginTooltip");
        void StartupChanged(object Sender, RoutedEventArgs E)
        {
            if (UpdatingPreferences) return;
            var Enabled = this.StartOnLoginCheck.IsChecked == true;
            if (OnStartOnLoginChanged(Enabled)) return;
            UpdatingPreferences = true;
            try { this.StartOnLoginCheck.IsChecked = !Enabled; }
            finally { UpdatingPreferences = false; }
        }
        this.StartOnLoginCheck.Checked += StartupChanged;
        this.StartOnLoginCheck.Unchecked += StartupChanged;
        Grid.SetRow(this.StartOnLoginCheck, 6);
        Grid.SetColumnSpan(this.StartOnLoginCheck, 2);
        Panel.Children.Add(this.StartOnLoginCheck);
        Panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        this.ShowBookmarkPathsCheck = new CheckBox
        {
            Name = "ShowBookmarkPathsCheck", IsChecked = ShowBookmarkPaths,
            Margin = new Thickness(0, 16, 0, 0), VerticalContentAlignment = VerticalAlignment.Center
        };
        this.ShowBookmarkPathsCheck.SetResourceReference(ContentControl.ContentProperty, "TextShowBookmarkPaths");
        this.ShowBookmarkPathsCheck.SetResourceReference(AutomationProperties.NameProperty, "TextShowBookmarkPaths");
        this.ShowBookmarkPathsCheck.SetResourceReference(ToolTipProperty, "TextShowBookmarkPathsTooltip");
        this.ShowBookmarkPathsCheck.Checked += (_, _) => { if (!UpdatingPreferences) OnShowBookmarkPathsChanged(true); };
        this.ShowBookmarkPathsCheck.Unchecked += (_, _) => { if (!UpdatingPreferences) OnShowBookmarkPathsChanged(false); };
        this.ShowSidebarDriveUsageCheck = new CheckBox
        {
            Name = "ShowSidebarDriveUsageCheck", IsChecked = ShowSidebarDriveUsage,
            Margin = new Thickness(0, 0, 0, 12), VerticalContentAlignment = VerticalAlignment.Center
        };
        this.ShowSidebarDriveUsageCheck.SetResourceReference(ContentControl.ContentProperty, "TextShowSidebarDriveUsage");
        this.ShowSidebarDriveUsageCheck.SetResourceReference(AutomationProperties.NameProperty, "TextShowSidebarDriveUsage");
        this.ShowSidebarDriveUsageCheck.SetResourceReference(ToolTipProperty, "TextShowSidebarDriveUsageTooltip");
        this.ShowSidebarDriveUsageCheck.Checked += (_, _) => { if (!UpdatingPreferences) OnShowSidebarDriveUsageChanged(true); };
        this.ShowSidebarDriveUsageCheck.Unchecked += (_, _) => { if (!UpdatingPreferences) OnShowSidebarDriveUsageChanged(false); };
        this.GeneralPanel = Panel;
        var UI = new StackPanel { Visibility = Visibility.Collapsed };
        var UIHeading = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) };
        UIHeading.SetResourceReference(TextBlock.TextProperty, "TextUICategory");
        UIHeading.SetResourceReference(TextBlock.FontSizeProperty, "UIPreferencesHeadingSize");
        UI.Children.Add(UIHeading);
        var FontsHeading = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        FontsHeading.SetResourceReference(TextBlock.TextProperty, "TextPanelFontSizes");
        UI.Children.Add(FontsHeading);
        var FontGrid = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        FontGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FontGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var Pickers = new Dictionary<string, ComboBox>(StringComparer.Ordinal);
        var Labels = new Dictionary<string, TextBlock>(StringComparer.Ordinal);
        this.FontSizePickers = Pickers;
        this.FontSizeLabels = Labels;
        for (var Index = 0; Index < Fonts.Count; Index++)
        {
            var Option = Fonts[Index];
            if (Index % 2 == 0) FontGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var FontRow = new Grid { Margin = new Thickness(0, 0, Index % 2 == 0 ? 20 : 0, 8) };
            FontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var Label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
            Label.SetResourceReference(TextBlock.TextProperty, "Text" + Option.ResourceKey);
            var Picker = new ComboBox
            {
                ItemsSource = Enumerable.Range(8, 17).Select(Size => (double)Size).ToArray(),
                SelectedItem = Option.Size,
                Width = 70,
                MinHeight = 28,
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = Option.Key
            };
            Picker.SetResourceReference(AutomationProperties.NameProperty, "Text" + Option.ResourceKey);
            Grid.SetColumn(Picker, 1);
            FontRow.Children.Add(Label);
            FontRow.Children.Add(Picker);
            Grid.SetRow(FontRow, Index / 2);
            Grid.SetColumn(FontRow, Index % 2);
            FontGrid.Children.Add(FontRow);
            Pickers.Add(Option.Key, Picker);
            Labels.Add(Option.Key, Label);
            Picker.SelectionChanged += (_, _) =>
            {
                if (!UpdatingPreferences && Picker.SelectedItem is double Size) OnFontSizeChanged(Option.Key, Size);
            };
        }
        UI.Children.Add(FontGrid);
        this.GridLinesCheck.Margin = new Thickness(0, 0, 0, 12);
        UI.Children.Add(this.GridLinesCheck);
        this.ShowBookmarkPathsCheck.Margin = new Thickness(0, 0, 0, 12);
        UI.Children.Add(this.ShowBookmarkPathsCheck);
        UI.Children.Add(this.ShowSidebarDriveUsageCheck);
        this.SeparatePinnedTabsCheck = new CheckBox
        {
            Name = "SeparatePinnedTabsCheck", IsChecked = SeparatePinnedTabs,
            Margin = new Thickness(0, 0, 0, 12), VerticalContentAlignment = VerticalAlignment.Center
        };
        this.SeparatePinnedTabsCheck.SetResourceReference(ContentControl.ContentProperty, "TextSeparatePinnedTabs");
        this.SeparatePinnedTabsCheck.SetResourceReference(AutomationProperties.NameProperty, "TextSeparatePinnedTabs");
        this.SeparatePinnedTabsCheck.Checked += (_, _) => { if (!UpdatingPreferences) OnSeparatePinnedTabsChanged(true); };
        this.SeparatePinnedTabsCheck.Unchecked += (_, _) => { if (!UpdatingPreferences) OnSeparatePinnedTabsChanged(false); };
        UI.Children.Add(this.SeparatePinnedTabsCheck);
        var ColumnsHeading = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        ColumnsHeading.SetResourceReference(TextBlock.TextProperty, "TextVisibleColumns");
        UI.Children.Add(ColumnsHeading);
        var Checks = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        this.ColumnChecks = Checks;
        var UpdatingChecks = false;
        void UpdateColumnChecks()
        {
            var BrowsingCount = Columns.Count(Option => !Option.SearchOnly && Checks[Option.Key].IsChecked == true);
            foreach (var Option in Columns)
                Checks[Option.Key].IsEnabled = Option.SearchOnly || Checks[Option.Key].IsChecked != true || BrowsingCount > 1;
        }
        foreach (var Option in Columns)
        {
            var Check = new CheckBox
            {
                IsChecked = Option.Visible,
                Margin = new Thickness(0, 0, 0, 12),
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = Option.Key
            };
            Check.SetResourceReference(ContentControl.ContentProperty, "Text" + Option.ResourceKey);
            Check.SetResourceReference(AutomationProperties.NameProperty, "Text" + Option.ResourceKey);
            if (Option.SearchOnly) Check.SetResourceReference(ToolTipProperty, "TextSearchColumnHint");
            Checks.Add(Option.Key, Check);
            UI.Children.Add(Check);
            void ColumnChanged(object Sender, RoutedEventArgs E)
            {
                if (UpdatingChecks || UpdatingPreferences) return;
                var Visible = Check.IsChecked == true;
                if (!OnColumnVisibilityChanged(Option.Key, Visible))
                {
                    UpdatingChecks = true;
                    Check.IsChecked = !Visible;
                    UpdatingChecks = false;
                }
                UpdateColumnChecks();
            }
            Check.Checked += ColumnChanged;
            Check.Unchecked += ColumnChanged;
        }
        UpdateColumnChecks();
        var ColumnsHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)this.FindResource("Muted"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        ColumnsHint.SetResourceReference(TextBlock.TextProperty, "TextColumnsHint");
        ColumnsHint.SetResourceReference(TextBlock.FontSizeProperty, "UIPreferencesHintSize");
        UI.Children.Add(ColumnsHint);
        this.UIPanel = UI;
        this.BuildShortcuts(Shortcuts, OnShortcutChanged);
        this.BuildTerminalTheme(TerminalFontSize, TerminalBackgroundColor, OnTerminalFontSizeChanged, OnTerminalBackgroundChanged,
            () => UpdatingPreferences, TerminalForegroundColor, OnTerminalForegroundChanged);

        this.Categories = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(12, 20, 12, 12) };
        ScrollViewer.SetHorizontalScrollBarVisibility(this.Categories, ScrollBarVisibility.Disabled);
        this.Categories.SetResourceReference(AutomationProperties.NameProperty, "TextPreferenceCategories");
        this.GeneralCategory = new ListBoxItem { Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 4) };
        this.GeneralCategory.SetResourceReference(ContentControl.ContentProperty, "TextGeneralCategory");
        this.UICategory = new ListBoxItem { Padding = new Thickness(12, 10, 12, 10) };
        this.UICategory.SetResourceReference(ContentControl.ContentProperty, "TextUICategory");
        this.TerminalThemeCategory = new ListBoxItem { Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 4, 0, 0) };
        this.TerminalThemeCategory.SetResourceReference(ContentControl.ContentProperty, "TextTerminalTheme");
        this.ShortcutsCategory = new ListBoxItem { Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 4, 0, 0) };
        this.ShortcutsCategory.SetResourceReference(ContentControl.ContentProperty, "TextShortcutsCategory");
        this.Categories.Items.Add(this.GeneralCategory);
        this.Categories.Items.Add(this.UICategory);
        this.Categories.Items.Add(this.TerminalThemeCategory);
        this.Categories.Items.Add(this.ShortcutsCategory);
        var Sidebar = new Border
        {
            Background = (Brush)this.FindResource("SurfaceMiddle"),
            BorderBrush = (Brush)this.FindResource("Line"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = this.Categories
        };
        var ContentPanels = new Grid { Margin = new Thickness(24, 28, 24, 28), VerticalAlignment = VerticalAlignment.Top, Background = (Brush)this.FindResource("SurfaceInner") };
        ContentPanels.Children.Add(this.GeneralPanel);
        ContentPanels.Children.Add(this.UIPanel);
        ContentPanels.Children.Add(this.TerminalThemePanel);
        ContentPanels.Children.Add(this.ShortcutsPanel);
        var Layout = new Grid();
        Layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        this.CategoryColumn = new ColumnDefinition { MinWidth = 148 };
        Layout.ColumnDefinitions.Add(this.CategoryColumn);
        Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        this.UpdateCategoryLayout();
        Layout.Children.Add(Sidebar);
        var Scroll = new ScrollViewer { Content = ContentPanels, Background = (Brush)this.FindResource("SurfaceInner"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(Scroll, 1);
        Layout.Children.Add(Scroll);
        this.RestoreDefaultsButton.SetResourceReference(ContentControl.ContentProperty, "TextRestoreDefaults");
        this.RestoreDefaultsButton.SetResourceReference(AutomationProperties.NameProperty, "TextRestoreDefaults");
        this.RestoreDefaultsButton.SetResourceReference(ToolTipProperty, "TextRestoreDefaultsTooltip");
        var Footer = new Border
        {
            BorderBrush = (Brush)this.FindResource("Line"), BorderThickness = new Thickness(0, 1, 0, 0),
            Background = (Brush)this.FindResource("SurfaceOuter"),
            Child = this.RestoreDefaultsButton
        };
        Grid.SetRow(Footer, 1);
        Grid.SetColumnSpan(Footer, 2);
        Layout.Children.Add(Footer);
        this.RestoreDefaultsButton.Click += async (_, _) =>
        {
            if (UpdatingPreferences) return;
            var CurrentScope = ReferenceEquals(this.Categories.SelectedItem, this.UICategory) ? PreferenceResetScope.UI
                : ReferenceEquals(this.Categories.SelectedItem, this.TerminalThemeCategory) ? PreferenceResetScope.TerminalTheme
                : ReferenceEquals(this.Categories.SelectedItem, this.ShortcutsCategory) ? PreferenceResetScope.Shortcuts
                : PreferenceResetScope.General;
            var CategoryName = ((ListBoxItem)this.Categories.SelectedItem).Content.ToString()!;
            var Choice = new ResetPreferencesDialog(this, CurrentScope, CategoryName);
            if (Choice.ShowDialog() != true) return;
            var Scope = Choice.SelectedScope;
            UpdatingPreferences = true;
            Layout.IsEnabled = false;
            try
            {
                if ((Scope is PreferenceResetScope.All or PreferenceResetScope.General) && !OnStartOnLoginChanged(false)) return;
                await OnRestoreDefaults(Scope);
                var Defaults = DefaultSettings.Current;
                if (Scope is PreferenceResetScope.All or PreferenceResetScope.General)
                {
                    this.LanguagePicker.SelectedValue = Defaults.Language;
                    this.Language = XmlLanguage.GetLanguage(LanguageService.CurrentLanguage);
                    this.TimeFormatPicker.SelectedIndex = Defaults.Use12HourClock ? 1 : 0;
                    this.ShowHiddenCheck.IsChecked = Defaults.ShowHidden;
                    this.ExitOnCloseCheck.IsChecked = Defaults.ExitOnClose;
                    this.CopyOnDropCheck.IsChecked = Defaults.CopyOnDrop;
                    this.CalculateFolderSizesCheck.IsChecked = Defaults.CalculateFolderSizes;
                    this.StartOnLoginCheck.IsChecked = false;
                }
                if (Scope is PreferenceResetScope.All or PreferenceResetScope.UI)
                {
                    this.ShowBookmarkPathsCheck.IsChecked = Defaults.ShowBookmarkPaths;
                    this.ShowSidebarDriveUsageCheck.IsChecked = Defaults.ShowSidebarDriveUsage;
                    this.SeparatePinnedTabsCheck.IsChecked = Defaults.SeparatePinnedTabs;
                    this.GridLinesCheck.IsChecked = Defaults.ShowGridLines;
                    foreach (var Option in Fonts) Pickers[Option.Key].SelectedItem = Option.DefaultSize;
                    foreach (var Check in Checks) Check.Value.IsChecked = !Defaults.HiddenColumns.Contains(Check.Key);
                    UpdateColumnChecks();
                }
                if (Scope is PreferenceResetScope.All or PreferenceResetScope.TerminalTheme)
                {
                    this.TerminalFontSizePicker.SelectedItem = Defaults.TerminalFontSize;
                    this.TerminalBackgroundEditor.Text = Defaults.TerminalBackgroundColor;
                    this.TerminalAutomaticForeground.IsChecked = Defaults.TerminalForegroundColor is null;
                    this.TerminalForegroundEditor.Text = Defaults.TerminalForegroundColor ?? "#E5E7EB";
                }
                if (Scope is PreferenceResetScope.All or PreferenceResetScope.Shortcuts)
                {
                    this.PreferenceShortcuts = KeyboardShortcut.Resolve(null);
                    this.ShowShortcutError(null);
                }
                this.RefreshShortcuts();
            }
            finally { UpdatingPreferences = false; Layout.IsEnabled = true; }
        };
        this.Content = Layout;
        this.Categories.SelectionChanged += (_, _) =>
        {
            var ShowUI = ReferenceEquals(this.Categories.SelectedItem, this.UICategory);
            var ShowShortcuts = ReferenceEquals(this.Categories.SelectedItem, this.ShortcutsCategory);
            var ShowTerminal = ReferenceEquals(this.Categories.SelectedItem, this.TerminalThemeCategory);
            this.GeneralPanel.Visibility = ShowUI || ShowShortcuts || ShowTerminal ? Visibility.Collapsed : Visibility.Visible;
            this.UIPanel.Visibility = ShowUI ? Visibility.Visible : Visibility.Collapsed;
            this.ShortcutsPanel.Visibility = ShowShortcuts ? Visibility.Visible : Visibility.Collapsed;
            this.TerminalThemePanel.Visibility = ShowTerminal ? Visibility.Visible : Visibility.Collapsed;
            Scroll.ScrollToTop();
        };
        this.Categories.SelectedItem = this.GeneralCategory;

        this.LanguagePicker.SelectionChanged += (_, _) =>
        {
            if (UpdatingPreferences || this.LanguagePicker.SelectedValue is not string Code || Code == LanguageService.CurrentLanguage) return;
            OnLanguageChanged(Code);
            this.Language = XmlLanguage.GetLanguage(LanguageService.CurrentLanguage);
            this.ShowShortcutError(null);
            this.RefreshShortcuts();
        };
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs E)
    {
        base.OnPropertyChanged(E);
        if (E.Property == FontSizeProperty || E.Property == FontStyleProperty
            || E.Property == FontWeightProperty || E.Property == FontStretchProperty)
            this.UpdateCategoryLayout();
    }

    private void UpdateCategoryLayout()
    {
        if (this.CategoryColumn is null) return;
        // Reserve the largest label in every supported language so translation never moves the divider.
        var TextWidth = 0d;
        var TextHeight = 0d;
        string[] Keys = ["GeneralCategory", "UICategory", "TerminalTheme", "ShortcutsCategory"];
        string[] Families = ["Segoe UI", "Microsoft YaHei UI", "Yu Gothic UI", "Malgun Gothic"];
        foreach (var Language in LanguageService.SupportedLanguages)
        {
            var Texts = LanguageService.GetTexts(Language.Code);
            foreach (var Family in Families)
                foreach (var Key in Keys)
                {
                    var Text = new FormattedText(Texts[Key], System.Globalization.CultureInfo.GetCultureInfo(Language.Code),
                        this.FlowDirection, new Typeface(new FontFamily(Family), this.FontStyle, this.FontWeight, this.FontStretch),
                        this.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    TextWidth = Math.Max(TextWidth, Text.WidthIncludingTrailingWhitespace);
                    TextHeight = Math.Max(TextHeight, Text.Height);
                }
        }
        this.CategoryColumn.Width = new GridLength(Math.Max(148, Math.Ceiling(TextWidth + 52)));
        foreach (var Category in this.Categories.Items.OfType<ListBoxItem>())
            Category.Height = Math.Ceiling(TextHeight + Category.Padding.Top + Category.Padding.Bottom + 2);
    }
}
