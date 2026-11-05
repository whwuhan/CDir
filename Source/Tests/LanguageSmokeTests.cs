using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunLanguageSmokeChecksAsync(List<string> Passed)
    {
        if (!AppMain.SmokeTest) return;
        var InitialLanguage = LanguageService.CurrentLanguage;
        var InitialPath = this.CurrentPath;
        var InitialSearch = this.SearchBox.Text;
        var InitialHidden = this.Settings.ShowHidden;
        var InitialFavorites = this.FavoritesSection.IsExpanded;
        var InitialTree = this.DirectoryTreeSection.IsExpanded;
        var InitialSort = this.SortProperty;
        var InitialDirection = this.SortDirection;
        var InitialColumnDirections = this.FileGrid.Columns.Select(Column => Column.SortDirection).ToArray();
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CDirLanguageUI" + FixtureId));
        var BrowserPath = System.IO.Path.Combine(Fixture, "LanguagePreview");
        var Marker = System.IO.Path.Combine(Fixture, ".CDirLanguageOwner");
        System.IO.Directory.CreateDirectory(BrowserPath);
        System.IO.File.WriteAllText(Marker, FixtureId);

        void Check(bool Condition, string Name, string? Detail = null)
        {
            if (!Condition) throw new InvalidOperationException("Language UI smoke test failed: " + Name + (Detail is null ? "" : "\n" + Detail));
            Passed.Add(Name);
        }

        try
        {
            var DefaultProperties = System.Text.Json.JsonSerializer.SerializeToElement(DefaultSettings.Current)
                .EnumerateObject().ToDictionary(Property => Property.Name, Property => Property.Value.GetRawText());
            var FreshProperties = System.Text.Json.JsonSerializer.SerializeToElement(new AppSettings())
                .EnumerateObject().ToDictionary(Property => Property.Name, Property => Property.Value.GetRawText());
            Check(DefaultProperties.All(Property => FreshProperties.TryGetValue(Property.Key, out var Value) && Value == Property.Value),
                "Every default preference initializes the matching user setting");
            using (var DefaultStream = typeof(DefaultSettings).Assembly.GetManifestResourceStream("CDir.Config.DefaultSettings.json")!)
            using (var DefaultDocument = System.Text.Json.JsonDocument.Parse(DefaultStream))
            {
                var Root = DefaultDocument.RootElement;
                Check(new[] { "CustomCategories", "LastPath", "NavigationTreeKey", "SidebarTreeLayouts", "SidebarScrollOffset" }
                    .All(Key => !Root.TryGetProperty(Key, out _)),
                    "Bundled defaults exclude user categories, paths, and navigation state");
            }
            var EmptySettings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!;
            Check(EmptySettings.CustomCategories is { Count: 0 } && EmptySettings.LastPath is null
                && EmptySettings.NavigationTreeKey is null && EmptySettings.SidebarTreeLayouts is { Count: 0 }
                && EmptySettings.SidebarScrollOffset == 0
                && EmptySettings.SidebarOrder!.SequenceEqual(new[] { "quick-access", "folder-tree" })
                && this.CustomCategories.Count == 0,
                "Fresh and legacy settings without categories start with only built-in sidebar sections");
            var DefaultSnapshot = System.Text.Json.JsonSerializer.Serialize(DefaultSettings.Current);
            var DetachedSettings = new AppSettings();
            DetachedSettings.SidebarTreeLayouts!["custom:test"] = new SidebarTreeLayout { ExpandedPaths = [BrowserPath] };
            DetachedSettings.CustomCategories!.Add(new SidebarCategory { Name = "Independent test category" });
            DetachedSettings.Shortcuts!["Search"] = "Alt+F";
            Check(System.Text.Json.JsonSerializer.Serialize(DefaultSettings.Current) == DefaultSnapshot
                && new AppSettings().CustomCategories is { Count: 0 }
                && new AppSettings().SidebarTreeLayouts is { Count: 0 },
                "User categories, sidebar trees, and shortcuts cannot mutate defaults or another settings instance");
            var IndependentSettings = new AppSettings();
            IndependentSettings.FontSizes!["Menus"] = 24;
            IndependentSettings.HiddenColumns!.Add(nameof(FileEntry.Size));
            IndependentSettings.ColumnOrder!.Reverse();
            var FreshSettings = new AppSettings();
            Check(FreshSettings.FontSizes!["Menus"] == 13 && FreshSettings.HiddenColumns!.Count == 0
                && FreshSettings.ColumnOrder!.SequenceEqual(ColumnKeys),
                "Changing user settings cannot mutate the shared JSON default configuration");
            Check(InitialLanguage == "en-US" && this.Settings.Language == "en-US"
                && this.HostWindow.Title == LanguageService.Text("WindowTitle") && this.HostWindow.FileMenuButton.Content?.ToString() == "File",
                "fresh startup defaults to English");
            Check(this.DirectoryTreeSection.Header?.ToString() == "My Computer"
                && this.FavoritesSection.Header?.ToString() == "Quick Access",
                "English sidebar displays My Computer and Quick Access");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(BrowserPath, "Documents"));
            var AlphaPath = System.IO.Path.Combine(BrowserPath, "alpha.txt");
            System.IO.File.WriteAllText(AlphaPath, "alpha");
            System.IO.File.WriteAllText(System.IO.Path.Combine(BrowserPath, "beta.txt"), "longer beta document");
            this.Settings.ShowHidden = false;
            if (!await this.NavigateAsync(BrowserPath)) throw new InvalidOperationException("Cannot open language smoke fixture.");
            this.SearchBox.Text = ".txt";
            await this.WaitForSearchSmokeAsync();
            this.SelectPath(AlphaPath);
            var SizeColumn = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Size));
            SizeColumn.SortDirection = System.ComponentModel.ListSortDirection.Ascending;
            this.FileGridSorting(this.FileGrid, new DataGridSortingEventArgs(SizeColumn));
            var ExpectedSort = this.SortProperty;
            var ExpectedDirection = this.SortDirection;
            var ExpectedHistory = this.History.ToArray();
            var ExpectedHistoryIndex = this.HistoryIndex;

            foreach (var LanguageOption in LanguageService.SupportedLanguages)
            {
                var Code = LanguageOption.Code;
                await this.RunPreferencesSmokeChecksAsync(Passed, Code);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Check(LanguageService.CurrentLanguage == Code && this.Settings.Language == Code,
                    Code + " picker switches language without restarting");
                Check(this.HostWindow.FileMenuButton.Content?.ToString() == LanguageService.Text("FileMenu")
                    && this.HostWindow.HelpMenuButton.Content?.ToString() == LanguageService.Text("HelpMenu")
                    && this.HostWindow.HelpMenuButton.IsVisible && ReferenceEquals(this.HostWindow.HelpMenuButton.ContextMenu, this.HostWindow.HelpMenu)
                    && this.HostWindow.HelpMenuButton.FontSize == this.HostWindow.FileMenuButton.FontSize
                    && this.HostWindow.HelpMenuButton.TranslatePoint(new Point(), this.HostWindow).X
                        >= this.HostWindow.FileMenuButton.TranslatePoint(new Point(), this.HostWindow).X + this.HostWindow.FileMenuButton.ActualWidth,
                    Code + " file menu label updates");
                var FileMenuInvoker = (System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(this.HostWindow.FileMenuButton)
                    .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);
                FileMenuInvoker.Invoke();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Check(this.HostWindow.FileMenu.IsOpen && this.HostWindow.FileMenu.IsVisible && this.HostWindow.FileMenu.Items.Count == 7
                    && ReferenceEquals(this.HostWindow.FileMenu.Items[0], this.HostWindow.UndoMenuItem)
                    && ReferenceEquals(this.HostWindow.FileMenu.Items[1], this.HostWindow.RedoMenuItem)
                    && ReferenceEquals(this.HostWindow.FileMenu.Items[2], this.HostWindow.FileMenuSeparator)
                    && ReferenceEquals(this.HostWindow.FileMenu.Items[3], this.HostWindow.PreferencesMenuItem)
                    && this.HostWindow.FileMenuSeparator.IsVisible && this.HostWindow.FileMenuSeparator.ActualHeight >= 1
                    && ReferenceEquals(this.HostWindow.FileMenu.Items[4], this.HostWindow.FileAboutMenuItem)
                    && this.HostWindow.FileAboutMenuItem.Header?.ToString() == LanguageService.Text("About")
                    && ReferenceEquals(this.HostWindow.FileMenu.Items[5], this.HostWindow.FileMenuExitSeparator)
                    && ReferenceEquals(this.HostWindow.FileMenu.Items[6], this.HostWindow.ExitMenuItem)
                    && this.HostWindow.ExitMenuItem.IsVisible && this.HostWindow.ExitMenuItem.IsEnabled
                    && this.HostWindow.ExitMenuItem.Header?.ToString() == LanguageService.Text("TrayExit")
                    && this.HostWindow.PreferencesMenuItem.Header?.ToString() == LanguageService.Text("Preferences") && this.HostWindow.PreferencesMenuItem.IsVisible
                    && this.HostWindow.PreferencesMenuItem.ActualWidth > 0 && this.HostWindow.PreferencesMenuItem.ActualHeight > 0
                    && this.HostWindow.FileMenu.ActualWidth >= this.HostWindow.PreferencesMenuItem.ActualWidth
                    && this.HostWindow.FileMenu.ActualWidth >= (double)Application.Current.Resources["UIMenuMinimumWidth"]
                    && this.HostWindow.FileMenu.ActualHeight >= this.HostWindow.PreferencesMenuItem.ActualHeight,
                    Code + " file menu click opens the Preferences dropdown");
                foreach (var Separator in new[] { this.HostWindow.FileMenuSeparator, this.HostWindow.FileMenuExitSeparator })
                {
                    var Line = VisualTreeHelper.GetChild(Separator, 0) as Border;
                    var ItemLeft = this.HostWindow.PreferencesMenuItem.TranslatePoint(new Point(), this.HostWindow.FileMenu).X;
                    Check(Separator.IsVisible && Line is not null && Line.ActualHeight == 1
                        && Line.TranslatePoint(new Point(), this.HostWindow.FileMenu).X <= ItemLeft + 2.1
                        && Line.ActualWidth >= this.HostWindow.PreferencesMenuItem.ActualWidth - 4.1
                        && Separator.ActualHeight <= 5,
                        Code + " file menu separator spans the menu without an icon gutter: " + Separator.Name);
                }
                if (AppMain.ScreenshotPath is not null)
                {
                    var Menu = this.HostWindow.FileMenu;
                    var MenuBitmap = new RenderTargetBitmap((int)Math.Ceiling(Menu.ActualWidth), (int)Math.Ceiling(Menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    MenuBitmap.Render(Menu);
                    var MenuEncoder = new PngBitmapEncoder();
                    MenuEncoder.Frames.Add(BitmapFrame.Create(MenuBitmap));
                    using var MenuStream = File.Create(Path.Combine(Path.GetDirectoryName(AppMain.ScreenshotPath)!, "FileMenu-" + Code + ".png"));
                    MenuEncoder.Save(MenuStream);
                }
                // Raising KeyDown alone does not generate the preview event used by real keyboard input.
                System.Windows.Input.FocusManager.SetFocusedElement(this, this.SearchBox);
                var MenuEscape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(this.HostWindow.PreferencesMenuItem), 0, System.Windows.Input.Key.Escape)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                this.HostWindow.PreferencesMenuItem.RaiseEvent(MenuEscape);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Check(MenuEscape.Handled && !this.HostWindow.FileMenu.IsOpen, Code + " Escape dismisses the file dropdown from a menu item");
                Check(ReferenceEquals(System.Windows.Input.FocusManager.GetFocusedElement(this), this.FileGrid),
                    Code + " Escape restores file-panel focus after closing the file dropdown");
                await this.RunAboutSmokeChecksAsync(Passed);
                await this.RunPreferencesSmokeChecksAsync(Passed);
                Check(this.HostWindow.Title == LanguageService.Text("WindowTitle")
                    && Application.Current.Resources["TextWindowTitle"]?.ToString() == LanguageService.Text("WindowTitle")
                    && this.FileGrid.Columns.Select(Column => Column.Header?.ToString()).SequenceEqual(
                        new[] { "NameColumn", "ModifiedColumn", "TypeColumn", "SizeColumn", "AbsolutePathColumn" }.Select(Key => LanguageService.Text(Key)))
                    && this.StatusText.Text.StartsWith(LanguageService.Text("SearchResultsCount", 2), StringComparison.Ordinal)
                    && this.SelectionStatusText.IsVisible && this.SelectionStatusText.Text == LanguageService.Text("SelectedCount", 1),
                    Code + " window and column resources update",
                    System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["Title"] = this.HostWindow.Title,
                        ["ExpectedTitle"] = LanguageService.Text("WindowTitle"),
                        ["ApplicationResource"] = Application.Current.Resources["TextWindowTitle"]?.ToString(),
                        ["Header"] = this.FileGrid.Columns[0].Header?.ToString(),
                        ["ExpectedHeader"] = LanguageService.Text("NameColumn"),
                        ["AllHeaders"] = this.FileGrid.Columns.Select(Column => Column.Header?.ToString()).ToArray(),
                        ["AllExpectedHeaders"] = new[] { "NameColumn", "ModifiedColumn", "TypeColumn", "SizeColumn", "AbsolutePathColumn" }.Select(Key => LanguageService.Text(Key)).ToArray(),
                        ["ExpectedStatusStartsWith"] = LanguageService.Text("SearchResultsCount", 2),
                        ["Status"] = this.StatusText.Text,
                        ["SelectionStatus"] = this.SelectionStatusText.Text,
                        ["ExpectedSelectionStatus"] = LanguageService.Text("SelectedCount", 1)
                    }));
                Check(this.FavoritesList.Items.Cast<FavoriteLocation>().First().Name == LanguageService.Text("FavoriteHome")
                    && this.Entries.Single(Entry => SamePath(Entry.FullPath, AlphaPath)).TypeName == LanguageService.Text("TypeText"),
                    Code + " favorites and cached file types update");
                Check(SamePath(this.CurrentPath, BrowserPath) && SamePath(this.AddressBox.Text, BrowserPath)
                    && this.SearchBox.Text == ".txt" && this.View!.Cast<FileEntry>().Count() == 2
                    && this.SelectedEntries() is { Length: 1 } Selected && SamePath(Selected[0].FullPath, AlphaPath)
                    && this.SortProperty == ExpectedSort && this.SortDirection == ExpectedDirection
                    && this.Settings.ShowHidden == false && this.History.SequenceEqual(ExpectedHistory)
                    && this.HistoryIndex == ExpectedHistoryIndex
                    && this.FolderTree.SelectedItem is FolderNode Node && SamePath(Node.FullPath, BrowserPath),
                    Code + " language switch preserves navigation filter selection and sorting");

                this.FileGrid.SelectedItems.Clear();
                Check(this.FileGrid.ContextMenu.Items.Count == 0,
                    Code + " does not inject application commands into the Windows Shell menu");
                this.SelectPath(AlphaPath);

                var CreatedName = "Folder-" + Code;
                await this.AcceptSmokeNameDialogAsync(this.NewFolderAsync, LanguageService.Text("NewFolder"), CreatedName);
                Check(System.IO.Directory.Exists(System.IO.Path.Combine(BrowserPath, CreatedName)),
                    Code + " localized name dialog confirms creation");
                this.SelectPath(AlphaPath);
                var CancelledName = "cancelled-" + Code + ".txt";
                await this.AcceptSmokeNameDialogAsync(this.RenameAsync, LanguageService.Text("Rename"), CancelledName, ConfirmDialog: false);
                Check(System.IO.File.Exists(AlphaPath) && !System.IO.File.Exists(System.IO.Path.Combine(BrowserPath, CancelledName)),
                    Code + " localized name dialog cancellation preserves file");

                var SettingsFile = System.IO.Path.Combine(Fixture, "Settings", Code + ".json");
                this.FavoritesSection.IsExpanded = Code is "zh-CN" or "en-US";
                this.DirectoryTreeSection.IsExpanded = Code is "ja-JP" or "en-US";
                var Saved = this.SaveSettings(SettingsFile);
                var Restored = Saved ? ReadSettings(SettingsFile) : null;
                Check(Restored is not null && Restored.Language == Code && Restored.ShowHidden == false
                    && Restored.LastPath is not null && SamePath(Restored.LastPath, BrowserPath)
                    && Restored.FavoritesExpanded == this.FavoritesSection.IsExpanded
                    && Restored.DirectoryTreeExpanded == this.DirectoryTreeSection.IsExpanded
                    && this.FavoritesSection.Header?.ToString() == LanguageService.Text("Favorites")
                    && this.DirectoryTreeSection.Header?.ToString() == LanguageService.Text("DirectoryTree"),
                    Code + " real settings save and read preserve language location and sidebar states");
                this.FavoritesSection.IsExpanded = true;
                this.DirectoryTreeSection.IsExpanded = true;

                this.SearchBox.Text = "__CDirLanguageNoMatch__";
                await this.WaitForSearchSmokeAsync();
                Check(this.EmptyState.Visibility == Visibility.Visible
                    && this.EmptyTitle.Text == LanguageService.Text("NoResults")
                    && this.EmptyDescription.Text == LanguageService.Text("NoResultsHint"),
                    Code + " empty filter state uses the active language");
                this.SearchBox.Text = ".txt";
                await this.WaitForSearchSmokeAsync();
                this.SelectPath(AlphaPath);

                if (AppMain.ScreenshotPath is not null)
                {
                    this.SearchBox.Clear();
                    this.SelectPath(AlphaPath);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    this.CaptureLanguagePreview(Code);
                    this.SearchBox.Text = ".txt";
                    this.SelectPath(AlphaPath);
                }
            }

            var LegacyFile = System.IO.Path.Combine(Fixture, "Settings", "Legacy.json");
            System.IO.File.WriteAllText(LegacyFile, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["LastPath"] = BrowserPath, ["ShowHidden"] = true, ["Width"] = 1000, ["Height"] = 700
            }));
            var Legacy = ReadSettings(LegacyFile);
            Check(Legacy.Language == "en-US" && Legacy.ShowHidden && Legacy.Width == 1000
                && Legacy.FavoritesExpanded && Legacy.DirectoryTreeExpanded,
                "legacy settings retain defaults for language and expanded sidebar sections");

            var PreviousSettings = System.IO.Path.Combine(Fixture, "Migration", "Previous", "Settings.json");
            var CurrentSettings = System.IO.Path.Combine(Fixture, "Migration", "Current", "Settings.json");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PreviousSettings)!);
            System.IO.File.WriteAllText(PreviousSettings,
                "{\"Language\":\"zh-CN\",\"ShowHidden\":true,\"CustomCategories\":[{\"Id\":\"migrated\",\"Name\":\"工作\",\"IsExpanded\":false}],\"SidebarOrder\":[\"custom:migrated\",\"quick-access\",\"folder-tree\"]}");
            var Migrated = ReadSettingsWithMigration(CurrentSettings, PreviousSettings);
            Check(Migrated.Language == "zh-CN" && Migrated.ShowHidden
                && Migrated.CustomCategories is { Count: 1 } Categories && Categories[0].Name == "工作" && !Categories[0].IsExpanded
                && Migrated.SidebarOrder!.SequenceEqual(new[] { "custom:migrated", "quick-access", "folder-tree" })
                && System.IO.File.ReadAllText(CurrentSettings) == System.IO.File.ReadAllText(PreviousSettings),
                "renamed application migrates previous settings including language custom categories and sidebar order");
            System.IO.File.WriteAllText(CurrentSettings, "{\"Language\":\"ja-JP\"}");
            Check(ReadSettingsWithMigration(CurrentSettings, PreviousSettings).Language == "ja-JP"
                && System.IO.File.Exists(PreviousSettings), "existing CDir settings take priority and previous settings remain intact");
            var OlderSettings = System.IO.Path.Combine(Fixture, "Migration", "OlderFallback", "Settings.json");
            Check(ReadSettingsWithMigration(OlderSettings, PreviousSettings + ".missing", PreviousSettings).Language == "zh-CN"
                && System.IO.File.Exists(OlderSettings), "settings migration skips missing earlier locations");
            Check(ReadSettingsWithMigration(System.IO.Path.Combine(Fixture, "Migration", "Fresh", "Settings.json"), PreviousSettings + ".missing").Language == "en-US",
                "a fresh CDir installation still defaults to English");
            await this.RunPreferencesSmokeChecksAsync(Passed, RestoreDefaults: true);
            var DefaultsFile = System.IO.Path.Combine(Fixture, "Settings", "RestoredDefaults.json");
            Check(this.SaveSettings(DefaultsFile), "Restored defaults save to an owned settings file");
            var Defaults = ReadSettings(DefaultsFile);
            Check(Defaults.Language == "en-US" && Defaults.ShowGridLines && !Defaults.ShowHidden && !Defaults.Use12HourClock
                && Defaults.HiddenColumns is { Count: 0 } && Defaults.ColumnOrder!.SequenceEqual(ColumnKeys)
                && FontPanels.All(Panel => Defaults.FontSizes![Panel.Key] == Panel.DefaultSize)
                && Defaults.FontSizes!["Headers"] == 13 && Defaults.FontSizes["StatusBar"] == 13
                && Defaults.FontSizes["Sidebar"] == 13 && Defaults.FontSizes["Menus"] == 13,
                "Restored defaults survive serialization with the current preferences as their baseline");
        }
        finally
        {
            this.HostWindow.FileMenu.IsOpen = false;
            this.HostWindow.HelpMenu.IsOpen = false;
            this.FavoritesSection.IsExpanded = InitialFavorites;
            this.DirectoryTreeSection.IsExpanded = InitialTree;
            this.ChangeLanguage(InitialLanguage, Persist: false);
            this.Settings.ShowHidden = InitialHidden;
            this.SortProperty = InitialSort;
            this.SortDirection = InitialDirection;
            for (var I = 0; I < this.FileGrid.Columns.Count; I++) this.FileGrid.Columns[I].SortDirection = InitialColumnDirections[I];
            try
            {
                if (!this.WindowClosed && !string.IsNullOrEmpty(InitialPath))
                {
                    await this.NavigateAsync(InitialPath, RecordHistory: false);
                    this.SearchBox.Text = InitialSearch;
                }
            }
            finally
            {
                var TempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                if (!Fixture.StartsWith(TempRoot, StringComparison.OrdinalIgnoreCase)
                    || System.IO.Path.GetFileName(Fixture) != "CDirLanguageUI" + FixtureId
                    || !System.IO.File.Exists(Marker) || System.IO.File.ReadAllText(Marker) != FixtureId
                    || (System.IO.File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Refusing cleanup outside the owned language smoke fixture.");
                System.IO.Directory.Delete(Fixture, true);
            }
        }
    }

    private void CaptureLanguagePreview(string Code)
    {
        if (!AppMain.SmokeTest || AppMain.ScreenshotPath is null) return;
        var Name = Code switch
        {
            "zh-CN" => "LanguageChinese.png",
            "ja-JP" => "LanguageJapanese.png",
            "ko-KR" => "LanguageKorean.png",
            "en-US" => "LanguageEnglish.png",
            _ => throw new ArgumentOutOfRangeException(nameof(Code))
        };
        var DirectoryPath = System.IO.Path.GetDirectoryName(AppMain.ScreenshotPath)!;
        System.IO.Directory.CreateDirectory(DirectoryPath);
        this.UpdateLayout();
        var Bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        Bitmap.Render(this);
        var Encoder = new PngBitmapEncoder();
        Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
        using var Stream = System.IO.File.Create(System.IO.Path.Combine(DirectoryPath, Name));
        Encoder.Save(Stream);
    }
}
