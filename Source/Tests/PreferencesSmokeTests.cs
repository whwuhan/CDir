using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunPreferencesSmokeChecksAsync(List<string> Passed, string? LanguageCode = null, bool? ShowHidden = null,
        bool? Use12HourClock = null, IReadOnlyDictionary<string, bool>? VisibleColumns = null, IReadOnlyDictionary<string, double>? FontSizes = null,
        bool RestoreDefaults = false, Func<PreferencesWindow, Task>? InspectShortcuts = null, bool? ExitOnClose = null, bool? CopyOnDrop = null,
        bool? CalculateFolderSizes = null)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Preferences UI smoke test failed: " + Name);
            Passed.Add(Name);
        }

        void CheckCategoryLayout(PreferencesWindow Dialog)
        {
            Dialog.UpdateLayout();
            var Viewer = FindVisualChild<ScrollViewer>(Dialog.Categories);
            Check(Viewer is not null && Viewer.ComputedHorizontalScrollBarVisibility != Visibility.Visible
                && Viewer.ScrollableWidth < 0.5,
                "Preferences categories fit without horizontal scrolling");
            foreach (var Category in Dialog.Categories.Items.Cast<ListBoxItem>())
            {
                var Text = FindVisualChild<TextBlock>(Category);
                var NaturalText = new FormattedText(Category.Content.ToString()!,
                    System.Globalization.CultureInfo.GetCultureInfo(Dialog.Language.IetfLanguageTag),
                    Category.FlowDirection, new Typeface(Category.FontFamily, Category.FontStyle, Category.FontWeight, Category.FontStretch),
                    Category.FontSize, Category.Foreground, VisualTreeHelper.GetDpi(Category).PixelsPerDip);
                var Left = Category.TranslatePoint(new Point(), Dialog.Categories).X;
                Check(Text is not null && Text.ActualWidth + 0.5 >= NaturalText.WidthIncludingTrailingWhitespace
                    && Left >= 0 && Left + Category.ActualWidth <= Dialog.Categories.ActualWidth + 0.5,
                    "Preferences category text is fully visible: " + Category.Content);
            }
        }

        PreferencesWindow? FindDialog() => this.HostWindow.OwnedWindows.OfType<PreferencesWindow>().FirstOrDefault();
        Check(this.FindName("LanguagePicker") is null, "Language picker is absent from the main title bar");
        Check(this.FindName("ShowHiddenCheck") is null, "Hidden items checkbox is absent from the main file panel");
        Check(Math.Abs(this.FileGrid.TranslatePoint(new Point(), this.MainPanelContent).Y) < 0.1,
            "File list fills the former hidden-items toolbar space");
        await this.WaitForSearchSmokeAsync();
        Exception? Failure = null;
        var Inspected = false;
        // Idle-priority desktop checks can be delayed by the user's foreground
        // work. This is a deadline, not a delay on successful dialog checks.
        var Timeout = new DispatcherTimer(DispatcherPriority.Send, Dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        Timeout.Tick += (_, _) =>
        {
            Timeout.Stop();
            Failure = new TimeoutException("Timed out waiting for the Preferences window. Last check: " + Passed.LastOrDefault());
            FindDialog()?.Close();
        };

        // The owned smoke window stays inactive. Keep unrelated desktop mouse
        // activity from dismissing this popup while its layout is inspected.
        var MenuStaysOpen = this.HostWindow.FileMenu.StaysOpen;
        this.HostWindow.FileMenu.StaysOpen = true;
        // Fixture file changes can briefly refresh the browser and disable settings.
        var ReadyTimer = System.Diagnostics.Stopwatch.StartNew();
        while ((!this.HostWindow.PreferencesMenuItem.IsEnabled || this.Loading || this.OperationBusy)
            && ReadyTimer.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20);
        this.HostWindow.FileMenuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (!this.HostWindow.FileMenu.IsOpen)
        {
            // A previously shown owned fixture can finish its popup-close dispatch
            // during the first layout. Reopen once after that dispatch has drained.
            this.HostWindow.FileMenuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        Check(this.HostWindow.FileMenu.IsOpen && this.HostWindow.PreferencesMenuItem.IsVisible && this.HostWindow.PreferencesMenuItem.IsEnabled
            && this.HostWindow.PreferencesMenuItem.Header?.ToString() == LanguageService.Text("Preferences"),
            $"File dropdown exposes the localized Preferences action (open={this.HostWindow.FileMenu.IsOpen}, visible={this.HostWindow.PreferencesMenuItem.IsVisible}, enabled={this.HostWindow.PreferencesMenuItem.IsEnabled}, header={this.HostWindow.PreferencesMenuItem.Header}, loading={this.Loading})");
        this.HostWindow.FileMenu.StaysOpen = MenuStaysOpen;

        var Pending = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(async () =>
        {
            var Dialog = FindDialog();
            try
            {
                Check(Dialog is { IsVisible: true } && Dialog.Title == LanguageService.Text("Preferences")
                    && System.Windows.Interop.ComponentDispatcher.IsThreadModal && !this.HostWindow.FileMenu.IsOpen,
                    "Preferences click opens a localized owned modal settings window and closes the menu");
                CheckSquareWindowCorners(Dialog!);
                Check(Dialog!.Categories.IsVisible && Dialog.Categories.Items.Count == 4
                    && ReferenceEquals(Dialog.Categories.Items[2], Dialog.TerminalThemeCategory)
                    && ReferenceEquals(Dialog.Categories.SelectedItem, Dialog.GeneralCategory)
                    && Dialog.GeneralPanel.IsVisible && !Dialog.UIPanel.IsVisible,
                    "Preferences opens with a left category sidebar and the General page selected");
                Check(Dialog!.LanguagePicker.IsVisible && Dialog.LanguagePicker.IsEnabled
                    && Dialog.LanguagePicker.SelectedValue?.ToString() == LanguageService.CurrentLanguage
                    && Dialog.LanguagePicker.Items.Cast<LanguageOption>().SequenceEqual(LanguageService.SupportedLanguages),
                    "Preferences language picker shows all four languages and remembers the current selection");
                Dialog.UpdateLayout();
                CheckCategoryLayout(Dialog);
                var LanguageToggle = FindVisualChild<System.Windows.Controls.Primitives.ToggleButton>(Dialog.LanguagePicker);
                Check(LanguageToggle is not null && FindVisualChild<TextBlock>(LanguageToggle)?.Text
                    == LanguageService.SupportedLanguages.Single(Option => Option.Code == LanguageService.CurrentLanguage).NativeName,
                    "The language dropdown renders the selected language name rather than the data object's string representation");
                Check(Dialog.TimeFormatPicker.IsVisible && Dialog.TimeFormatPicker.IsEnabled
                    && Dialog.TimeFormatPicker.Items.Count == 2
                    && Dialog.TimeFormatPicker.SelectedItem is ComboBoxItem { Tag: bool InitialClock }
                    && InitialClock == this.Settings.Use12HourClock,
                    "Preferences clock picker offers 12-hour and 24-hour time and remembers the current selection");
                if (Use12HourClock.HasValue)
                {
                    Dialog.TimeFormatPicker.SelectedIndex = Use12HourClock.Value ? 1 : 0;
                    Check(this.Settings.Use12HourClock == Use12HourClock.Value
                        && this.Entries.All(Entry => Entry.Use12HourClock == Use12HourClock.Value),
                        "Clock picker immediately updates the cached file dates and application settings");
                }
                Check(Dialog.ShowHiddenCheck.IsVisible && Dialog.ShowHiddenCheck.IsEnabled
                    && Dialog.ShowHiddenCheck.IsChecked == this.Settings.ShowHidden
                    && Dialog.ShowHiddenCheck.Content?.ToString() == LanguageService.Text("HiddenItems")
                    && Dialog.ShowHiddenCheck.ToolTip?.ToString() == LanguageService.Text("HiddenItemsTooltip"),
                    "Preferences shows the localized hidden-items checkbox and retains the existing setting");
                if (ShowHidden.HasValue && ShowHidden.Value != this.Settings.ShowHidden)
                {
                    var HiddenToggle = (System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(Dialog.ShowHiddenCheck)
                        .GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
                    HiddenToggle.Toggle();
                    while (!Dialog.ShowHiddenCheck.IsEnabled && Failure is null) await Task.Delay(20);
                    Check(Failure is null && this.Settings.ShowHidden == ShowHidden.Value
                        && Dialog.ShowHiddenCheck.IsChecked == ShowHidden.Value && !this.Loading,
                        "Preferences hidden-items checkbox immediately refreshes the browser and updates settings");
                }
                Check(Dialog.ExitOnCloseCheck.IsVisible && Dialog.ExitOnCloseCheck.IsEnabled
                    && Dialog.ExitOnCloseCheck.IsChecked == this.Settings.ExitOnClose
                    && Dialog.ExitOnCloseCheck.Content?.ToString() == LanguageService.Text("ExitOnClose"),
                    "General exposes the localized exit-on-close checkbox with its saved value");
                if (ExitOnClose.HasValue)
                {
                    Dialog.ExitOnCloseCheck.IsChecked = ExitOnClose.Value;
                    Check(this.Settings.ExitOnClose == ExitOnClose.Value, "Exit-on-close changes apply immediately");
                }
                Check(Dialog.CopyOnDropCheck.IsVisible && Dialog.CopyOnDropCheck.IsEnabled
                    && Dialog.CopyOnDropCheck.IsChecked == this.Settings.CopyOnDrop
                    && Dialog.CopyOnDropCheck.Content?.ToString() == LanguageService.Text("CopyOnDrop"),
                    "General exposes the drag-copy checkbox with the saved mode");
                if (CopyOnDrop.HasValue)
                {
                    Dialog.CopyOnDropCheck.IsChecked = CopyOnDrop.Value;
                    Check(this.Settings.CopyOnDrop == CopyOnDrop.Value, "The drag-copy checkbox updates the transfer mode immediately");
                }
                Check(Dialog.CalculateFolderSizesCheck.IsVisible && Dialog.CalculateFolderSizesCheck.IsEnabled
                    && Dialog.CalculateFolderSizesCheck.IsChecked == this.Settings.CalculateFolderSizes
                    && Dialog.CalculateFolderSizesCheck.Content?.ToString() == LanguageService.Text("CalculateFolderSizes")
                    && Dialog.CalculateFolderSizesCheck.ToolTip?.ToString() == LanguageService.Text("CalculateFolderSizesTooltip"),
                    "General exposes the localized background folder-size checkbox with its saved value");
                if (CalculateFolderSizes.HasValue)
                {
                    Dialog.CalculateFolderSizesCheck.IsChecked = CalculateFolderSizes.Value;
                    Check(this.Settings.CalculateFolderSizes == CalculateFolderSizes.Value,
                        "The folder-size checkbox applies immediately");
                }
                Check(!Dialog.ShowBookmarkPathsCheck.IsVisible && !Dialog.ShowSidebarDriveUsageCheck.IsVisible && !Dialog.AutoFitColumnsOnNavigationCheck.IsVisible,
                    "Folder paths and drive usage are absent from General");
                Dialog.Categories.SelectedItem = Dialog.UICategory;
                Dialog.UpdateLayout();
                Check(Dialog.ShowSidebarDriveUsageCheck.IsVisible && Dialog.ShowSidebarDriveUsageCheck.IsEnabled
                    && Dialog.ShowSidebarDriveUsageCheck.IsChecked == this.Settings.ShowSidebarDriveUsage
                    && Dialog.ShowSidebarDriveUsageCheck.Content?.ToString() == LanguageService.Text("ShowSidebarDriveUsage"),
                    "UI exposes the localized drive usage choice with its saved value");
                Check(Dialog.AutoFitColumnsOnNavigationCheck.IsVisible && Dialog.AutoFitColumnsOnNavigationCheck.IsEnabled
                    && Dialog.AutoFitColumnsOnNavigationCheck.IsChecked == this.Settings.AutoFitColumnsOnNavigation
                    && Dialog.AutoFitColumnsOnNavigationCheck.Content?.ToString() == LanguageService.Text("AutoFitColumnsOnNavigation")
                    && Dialog.AutoFitColumnsOnNavigationCheck.ToolTip?.ToString() == LanguageService.Text("AutoFitColumnsOnNavigationTooltip"),
                    "UI exposes the localized automatic column-fitting checkbox with its saved value");
                var InitialFit = this.Settings.AutoFitColumnsOnNavigation;
                Dialog.AutoFitColumnsOnNavigationCheck.IsChecked = !InitialFit;
                Check(this.Settings.AutoFitColumnsOnNavigation == !InitialFit
                    && this.CaptureSaveSettings().AutoFitColumnsOnNavigation == !InitialFit,
                    "Automatic column fitting updates its persisted preference immediately");
                Dialog.AutoFitColumnsOnNavigationCheck.IsChecked = InitialFit;
                var InitialUsage = this.Settings.ShowSidebarDriveUsage;
                Dialog.ShowSidebarDriveUsageCheck.IsChecked = !InitialUsage;
                Check(this.Settings.ShowSidebarDriveUsage == !InitialUsage && this.ShowSidebarDriveUsage == !InitialUsage
                    && this.CaptureSaveSettings().ShowSidebarDriveUsage == !InitialUsage,
                    "The drive usage checkbox updates the sidebar and persisted preference immediately");
                Dialog.ShowSidebarDriveUsageCheck.IsChecked = InitialUsage;
                Check(Dialog.ShowBookmarkPathsCheck.IsVisible && Dialog.ShowBookmarkPathsCheck.IsEnabled
                    && Dialog.ShowBookmarkPathsCheck.IsChecked == this.Settings.ShowBookmarkPaths
                    && Dialog.ShowBookmarkPathsCheck.Content?.ToString() == LanguageService.Text("ShowBookmarkPaths"),
                    "UI exposes the saved folder-path checkbox");
                var InitialPaths = this.Settings.ShowBookmarkPaths;
                Dialog.ShowBookmarkPathsCheck.IsChecked = !InitialPaths;
                Check(this.Settings.ShowBookmarkPaths == !InitialPaths && this.ShowBookmarkPaths == !InitialPaths
                    && this.CaptureSaveSettings().ShowBookmarkPaths == !InitialPaths,
                    "The bookmark-path checkbox updates the sidebar and saved settings immediately");
                Dialog.ShowBookmarkPathsCheck.IsChecked = InitialPaths;
                Dialog.Categories.SelectedItem = Dialog.TerminalThemeCategory;
                Check(Dialog.StartOnLoginCheck.IsChecked == this.ReadStartOnLogin()
                    && Dialog.StartOnLoginCheck.Content?.ToString() == LanguageService.Text("StartOnLogin")
                    && Dialog.StartOnLoginCheck.ToolTip?.ToString() == LanguageService.Text("StartOnLoginTooltip"),
                    "General shows the saved Windows startup choice with localized text");
                Dialog.UpdateLayout();
                Check(Dialog.TerminalThemePanel.IsVisible && !Dialog.GeneralPanel.IsVisible && !Dialog.UIPanel.IsVisible
                    && Equals(Dialog.TerminalFontSizePicker.SelectedItem, this.GetTerminalFontSize())
                    && Dialog.TerminalBackgroundEditor.Text == this.GetTerminalBackgroundColor(),
                    "Terminal Theme has its own page after UI with the current font size and background color");
                Check(Dialog.TerminalFontSizePicker.Items.Cast<double>().SequenceEqual(Enumerable.Range(8, 41).Select(Value => (double)Value))
                    && Dialog.TerminalColorButtons.Count == 7 && Dialog.TerminalThemePreview.Background is SolidColorBrush,
                    "Terminal Theme offers bounded font sizes color swatches custom color input and a live preview");
                Dialog.Categories.SelectedItem = Dialog.UICategory;
                Dialog.UpdateLayout();
                Check(Dialog.UIPanel.IsVisible && !Dialog.GeneralPanel.IsVisible && !Dialog.LanguagePicker.IsVisible,
                    "UI category opens its own page for grid lines and column checkboxes");
                Check(Dialog.DirectoryTreeAndTabBarHeading.IsVisible
                    && Dialog.DirectoryTreeAndTabBarHeading.Text == LanguageService.Text("DirectoryTreeAndTabBarUI")
                    && Dialog.DirectoryTreeAndTabBarHeading.FontWeight == (this.Settings.BoldAllFonts ? FontWeights.Bold : FontWeights.SemiBold)
                    && Dialog.DirectoryTreeAndTabBarHeading.TranslatePoint(new Point(0, Dialog.DirectoryTreeAndTabBarHeading.ActualHeight), Dialog.UIPanel).Y
                        <= Dialog.GridLinesCheck.TranslatePoint(new Point(), Dialog.UIPanel).Y,
                    "The localized directory tree and tab bar heading separates font sizes from UI options");
                Check(Dialog.ColumnChecks.Count == this.FileGrid.Columns.Count
                    && this.GetColumnPreferences().All(Option => Dialog.ColumnChecks[Option.Key].IsVisible
                        && Dialog.ColumnChecks[Option.Key].IsChecked == Option.Visible
                        && Dialog.ColumnChecks[Option.Key].Content?.ToString() == LanguageService.Text(Option.ResourceKey)),
                    "UI page exposes localized checkboxes for every column and reflects the saved visibility");
                Check(Dialog.FontSizePickers.Count == 9 && this.GetFontOptions().All(Option =>
                    Dialog.FontSizePickers[Option.Key].SelectedItem is double Size && Size == Option.Size
                    && Dialog.FontSizeLabels[Option.Key].Text == LanguageService.Text(Option.ResourceKey)),
                    "UI page shows nine localized panel font controls with saved sizes");
                if (FontSizes is not null)
                    foreach (var Choice in FontSizes)
                    {
                        Dialog.FontSizePickers[Choice.Key].SelectedItem = Choice.Value;
                        Check(this.Settings.FontSizes![Choice.Key] == Choice.Value,
                            "Panel font picker applies and saves its new size: " + Choice.Key);
                    }
                Dialog.UpdateLayout();
                if (VisibleColumns is not null)
                    foreach (var Choice in VisibleColumns)
                    {
                        var ColumnCheck = Dialog.ColumnChecks[Choice.Key];
                        if (ColumnCheck.IsChecked != Choice.Value)
                        {
                            var ColumnToggle = (System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(ColumnCheck)
                                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
                            ColumnToggle.Toggle();
                        }
                        Check(ColumnCheck.IsChecked == Choice.Value
                            && (this.Settings.HiddenColumns?.Contains(Choice.Key) != true) == Choice.Value,
                            "Column checkbox immediately updates and saves its selected visibility: " + Choice.Key);
                    }
                Check(Dialog.FontSize == this.Settings.FontSizes!["Preferences"],
                    "Preferences font changes resize the open settings window immediately");
                CheckCategoryLayout(Dialog);
                var LastBrowsingColumn = Dialog.ColumnChecks.Where(Entry => Entry.Key != nameof(Models.FileEntry.FullPath) && Entry.Value.IsChecked == true).ToArray();
                Check(LastBrowsingColumn.Length > 0 && (LastBrowsingColumn.Length > 1 || !LastBrowsingColumn[0].Value.IsEnabled),
                    "UI keeps at least one browsing column visible");
                var InitialGridLines = this.Settings.ShowGridLines;
                Check(Dialog.GridLinesCheck.IsVisible && Dialog.GridLinesCheck.IsChecked == InitialGridLines
                    && Dialog.GridLinesCheck.Content?.ToString() == LanguageService.Text("ShowGridLines"),
                    "Preferences exposes a localized grid-lines checkbox with the current saved value");
                var Toggle = (System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(Dialog.GridLinesCheck)
                    .GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
                Toggle.Toggle();
                Check(this.Settings.ShowGridLines == !InitialGridLines
                    && this.FileGridLines.Visibility == (!InitialGridLines ? Visibility.Visible : Visibility.Collapsed),
                    "Grid-lines checkbox immediately updates the file list and application settings");
                Toggle.Toggle();
                Check(this.Settings.ShowGridLines == InitialGridLines
                    && this.FileGridLines.Visibility == (InitialGridLines ? Visibility.Visible : Visibility.Collapsed),
                    "Grid-lines checkbox can restore its previous state without restarting");
                Dialog.Categories.SelectedItem = Dialog.GeneralCategory;
                Dialog.UpdateLayout();
                var SidebarWidthBeforeLanguageChange = Dialog.Categories.ActualWidth;
                var PanelLeftBeforeLanguageChange = Dialog.GeneralPanel.TranslatePoint(new Point(), Dialog).X;
                var CategoryBoundsBeforeLanguageChange = Dialog.Categories.Items.Cast<ListBoxItem>()
                    .Select(Item => (Top: Item.TranslatePoint(new Point(), Dialog).Y, Height: Item.ActualHeight)).ToArray();
                if (LanguageCode is not null) Dialog.LanguagePicker.SelectedValue = LanguageCode;
                Dialog.UpdateLayout();
                Check(Math.Abs(Dialog.Categories.ActualWidth - SidebarWidthBeforeLanguageChange) < 0.1
                    && Math.Abs(Dialog.GeneralPanel.TranslatePoint(new Point(), Dialog).X - PanelLeftBeforeLanguageChange) < 0.1
                    && ReferenceEquals(Dialog.Categories.SelectedItem, Dialog.GeneralCategory)
                    && Dialog.Categories.Items.Cast<ListBoxItem>().Select((Item, Index) =>
                        Math.Abs(Item.TranslatePoint(new Point(), Dialog).Y - CategoryBoundsBeforeLanguageChange[Index].Top) < 0.1
                        && Math.Abs(Item.ActualHeight - CategoryBoundsBeforeLanguageChange[Index].Height) < 0.1).All(Stable => Stable),
                    "Switching language preserves the Preferences sidebar width, category positions, and content alignment");
                var OriginalWidth = Dialog.Width;
                Dialog.Width = Dialog.MinWidth;
                CheckCategoryLayout(Dialog);
                Dialog.Width = OriginalWidth;
                Dialog.UpdateLayout();
                Check(Dialog.Title == LanguageService.Text("Preferences")
                    && Dialog.LanguageLabel.Content?.ToString() == LanguageService.Text("Language")
                    && Dialog.GridLinesCheck.Content?.ToString() == LanguageService.Text("ShowGridLines")
                    && Dialog.ShowHiddenCheck.Content?.ToString() == LanguageService.Text("HiddenItems")
                    && Dialog.ShowHiddenCheck.ToolTip?.ToString() == LanguageService.Text("HiddenItemsTooltip")
                    && Dialog.ExitOnCloseCheck.Content?.ToString() == LanguageService.Text("ExitOnClose")
                    && Dialog.ExitOnCloseCheck.ToolTip?.ToString() == LanguageService.Text("ExitOnCloseTooltip")
                    && Dialog.CopyOnDropCheck.Content?.ToString() == LanguageService.Text("CopyOnDrop")
                    && Dialog.CopyOnDropCheck.ToolTip?.ToString() == LanguageService.Text("CopyOnDropTooltip")
                    && Dialog.CalculateFolderSizesCheck.Content?.ToString() == LanguageService.Text("CalculateFolderSizes")
                    && Dialog.CalculateFolderSizesCheck.ToolTip?.ToString() == LanguageService.Text("CalculateFolderSizesTooltip")
                    && Dialog.ShowBookmarkPathsCheck.Content?.ToString() == LanguageService.Text("ShowBookmarkPaths")
                    && Dialog.ShowBookmarkPathsCheck.ToolTip?.ToString() == LanguageService.Text("ShowBookmarkPathsTooltip")
                    && Dialog.StartOnLoginCheck.Content?.ToString() == LanguageService.Text("StartOnLogin")
                    && Dialog.StartOnLoginCheck.ToolTip?.ToString() == LanguageService.Text("StartOnLoginTooltip")
                    && Dialog.TimeFormatLabel.Content?.ToString() == LanguageService.Text("TimeFormat")
                    && Dialog.TimeFormatPicker.Items.Cast<ComboBoxItem>().Select(Item => Item.Content?.ToString())
                        .SequenceEqual(new[] { LanguageService.Text("Clock24Hour"), LanguageService.Text("Clock12Hour") })
                    && Dialog.LanguagePicker.SelectedValue?.ToString() == LanguageService.CurrentLanguage
                    && this.Settings.Language == LanguageService.CurrentLanguage
                    && (LanguageCode is null || LanguageService.CurrentLanguage == LanguageCode),
                    "Preferences language selection immediately updates the window label title and application settings");
                Check(Dialog.GeneralCategory.Content?.ToString() == LanguageService.Text("GeneralCategory")
                    && Dialog.TerminalThemeCategory.Content?.ToString() == LanguageService.Text("TerminalTheme")
                    && Dialog.UICategory.Content?.ToString() == LanguageService.Text("UICategory")
                    && this.GetColumnPreferences().All(Option => Dialog.ColumnChecks[Option.Key].Content?.ToString() == LanguageService.Text(Option.ResourceKey))
                    && this.GetFontOptions().All(Option => Dialog.FontSizeLabels[Option.Key].Text == LanguageService.Text(Option.ResourceKey)),
                    "Preferences categories and column choices update when the interface language changes");
                Check(Dialog.RestoreDefaultsButton.IsVisible && Dialog.RestoreDefaultsButton.IsEnabled
                    && Dialog.RestoreDefaultsButton.Content?.ToString() == LanguageService.Text("RestoreDefaults")
                    && Dialog.RestoreDefaultsButton.ToolTip?.ToString() == LanguageService.Text("RestoreDefaultsTooltip"),
                    "Preferences exposes a localized restore-defaults button on every page");
                if (RestoreDefaults)
                {
                    var CurrentPath = this.CurrentPath;
                    var CurrentWidth = this.HostWindow.Width;
                    var CurrentHeight = this.HostWindow.Height;
                    var Categories = this.CustomCategories.Select(Category => (Category.Id, Category.Name)).ToArray();
                    Dialog.LanguagePicker.SelectedValue = "zh-CN";
                    Dialog.TimeFormatPicker.SelectedIndex = 1;
                    Dialog.ShowHiddenCheck.IsChecked = true;
                    Dialog.ExitOnCloseCheck.IsChecked = true;
                    Dialog.CopyOnDropCheck.IsChecked = false;
                    Dialog.CalculateFolderSizesCheck.IsChecked = true;
                    Dialog.ShowBookmarkPathsCheck.IsChecked = true;
                    Dialog.ShowSidebarDriveUsageCheck.IsChecked = true;
                    Dialog.AutoFitColumnsOnNavigationCheck.IsChecked = true;
                    Dialog.StartOnLoginCheck.IsChecked = true;
                    while (!Dialog.ShowHiddenCheck.IsEnabled && Failure is null) await Task.Delay(20);
                    Check(Dialog.ShowHiddenCheck.IsEnabled && this.Settings.ShowHidden, "Reset fixture enables hidden items through Preferences");
                    Dialog.Categories.SelectedItem = Dialog.UICategory;
                    Dialog.GridLinesCheck.IsChecked = true;
                    foreach (var Picker in Dialog.FontSizePickers.Values) Picker.SelectedItem = 18.0;
                    Dialog.TerminalFontSizePicker.SelectedItem = 21.0;
                    Dialog.TerminalBackgroundEditor.Text = "#FFFFFF";
                    Dialog.ColumnChecks[nameof(Models.FileEntry.Name)].IsChecked = false;
                    this.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(Models.FileEntry.Size)).DisplayIndex = 0;
                    for (var Attempt = 0; Attempt < 2; Attempt++)
                    {
                        await this.RestorePreferencesSmokeAsync(Dialog);
                        while (!Dialog.RestoreDefaultsButton.IsEnabled && Failure is null) await Task.Delay(20);
                        Dialog.UpdateLayout();
                        Check(Failure is null && Dialog.IsVisible && Dialog.RestoreDefaultsButton.IsEnabled
                            && LanguageService.CurrentLanguage == "en-US" && Dialog.LanguagePicker.SelectedValue?.ToString() == "en-US"
                            && Dialog.TimeFormatPicker.SelectedIndex == 0 && !this.Settings.Use12HourClock
                            && Dialog.ShowHiddenCheck.IsChecked == false && !this.Settings.ShowHidden
                            && Dialog.ExitOnCloseCheck.IsChecked == false && !this.Settings.ExitOnClose
                            && Dialog.CopyOnDropCheck.IsChecked == true && this.Settings.CopyOnDrop
                            && Dialog.CalculateFolderSizesCheck.IsChecked == false && !this.Settings.CalculateFolderSizes
                            && Dialog.ShowBookmarkPathsCheck.IsChecked == false && !this.Settings.ShowBookmarkPaths && !this.ShowBookmarkPaths
                            && Dialog.ShowSidebarDriveUsageCheck.IsChecked == false && !this.Settings.ShowSidebarDriveUsage && !this.ShowSidebarDriveUsage
                            && Dialog.AutoFitColumnsOnNavigationCheck.IsChecked == false && !this.Settings.AutoFitColumnsOnNavigation
                            && Dialog.StartOnLoginCheck.IsChecked == false && !this.ReadStartOnLogin()
                            && Dialog.GridLinesCheck.IsChecked == false && !this.Settings.ShowGridLines
                            && this.Settings.TerminalFontSize == Models.DefaultSettings.Current.TerminalFontSize
                            && this.Settings.TerminalBackgroundColor == Models.DefaultSettings.Current.TerminalBackgroundColor
                            && Equals(Dialog.TerminalFontSizePicker.SelectedItem, Models.DefaultSettings.Current.TerminalFontSize)
                            && Dialog.TerminalBackgroundEditor.Text == Models.DefaultSettings.Current.TerminalBackgroundColor
                            && this.FileGridLines.Visibility == Visibility.Collapsed
                            && this.HostWindow.PreferencesMenuItem.FontSize == 13 && this.HostWindow.FileMenu.FontSize == 13
                            && this.GetFontOptions().All(Option => Option.Size == Option.DefaultSize
                                && Dialog.FontSizePickers[Option.Key].SelectedItem is double Size && Size == Option.DefaultSize)
                            && Dialog.ColumnChecks.Values.All(Check => Check.IsChecked == true && Check.IsEnabled)
                            && this.Settings.HiddenColumns!.Count == 0
                            && this.FileGrid.Columns.OrderBy(Column => Column.DisplayIndex).Select(Column => Column.SortMemberPath).SequenceEqual(ColumnKeys)
                            && this.Entries.All(Entry => !Entry.Use12HourClock),
                            "Restore Defaults immediately resets all Preferences and remains safe to repeat: " + Attempt);
                        Check(this.CurrentPath == CurrentPath && this.HostWindow.Width == CurrentWidth && this.HostWindow.Height == CurrentHeight
                            && this.CustomCategories.Select(Category => (Category.Id, Category.Name)).SequenceEqual(Categories),
                            "Restore Defaults preserves the current folder, window size, and custom categories");
                    }
                    Dialog.Categories.SelectedItem = Dialog.GeneralCategory;
                    Check(Dialog.RestoreDefaultsButton.IsVisible && Dialog.RestoreDefaultsButton.Content?.ToString() == "Restore Default Settings",
                        "Restore Defaults stays visible on General and switches its own label back to English");
                }
                if (InspectShortcuts is not null) await InspectShortcuts(Dialog);
                if (AppMain.ScreenshotPath is not null)
                {
                    Dialog.Categories.SelectedItem = Dialog.UICategory;
                    Dialog.UpdateLayout();
                    var UIBitmap = new RenderTargetBitmap((int)Dialog.ActualWidth, (int)Dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    UIBitmap.Render(Dialog);
                    var UIEncoder = new PngBitmapEncoder();
                    UIEncoder.Frames.Add(BitmapFrame.Create(UIBitmap));
                    using (var UIStream = System.IO.File.Create(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AppMain.ScreenshotPath)!,
                        "PreferencesUI-" + LanguageService.CurrentLanguage + ".png"))) UIEncoder.Save(UIStream);
                    Dialog.Categories.SelectedItem = Dialog.GeneralCategory;
                    Dialog.UpdateLayout();
                    var Bitmap = new RenderTargetBitmap((int)Dialog.ActualWidth, (int)Dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    Bitmap.Render(Dialog);
                    var Encoder = new PngBitmapEncoder();
                    Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                    using var Stream = System.IO.File.Create(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AppMain.ScreenshotPath)!,
                        "Preferences-" + LanguageService.CurrentLanguage + ".png"));
                    Encoder.Save(Stream);
                }
                UIElement EscapeSource = Dialog.LanguagePicker;
                if (InspectShortcuts is not null)
                {
                    Dialog.Categories.SelectedItem = Dialog.ShortcutsCategory;
                    Dialog.UpdateLayout();
                    EscapeSource = Dialog.ShortcutEditors["Search"];
                }
                var Escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Dialog), 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                EscapeSource.RaiseEvent(Escape);
                Check(Escape.Handled && !Dialog.IsVisible,
                    "Escape closes Preferences from " + (InspectShortcuts is null ? "a settings picker" : "a shortcut editor"));
                Inspected = true;
            }
            catch (Exception Ex) { Failure = Ex; }
            finally { Timeout.Stop(); Dialog?.Close(); }
        }));

        Timeout.Start();
        try { this.HostWindow.PreferencesMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
        finally
        {
            Timeout.Stop();
            if (Pending.Status == DispatcherOperationStatus.Pending) Pending.Abort();
            FindDialog()?.Close();
            this.HostWindow.FileMenu.IsOpen = false;
        }
        if (Failure is not null) throw new InvalidOperationException("Could not verify the Preferences window.", Failure);
        Check(Inspected && !System.Windows.Interop.ComponentDispatcher.IsThreadModal && this.IsEnabled && FindDialog() is null,
            "Closing Preferences restores the main window");
    }
}
