using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using CDir.Localization;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private void InitializeLanguage()
    {
        if (this.InheritedSettings is not null) this.Settings = this.InheritedSettings;
        else if (this.LayoutProbeSettingsFile is not null) this.Settings = ReadSettings(this.LayoutProbeSettingsFile);
        else if (AppMain.SmokeTest)
        {
            // Personal startup locations in bundled defaults must never escape the owned test root.
            this.Settings.LastPath = AppMain.StartupPath ?? AppMain.SmokeRoot;
            this.Settings.CustomCategories = [];
            this.Settings.SidebarOrder = [];
            this.Settings.SidebarTreeLayouts = null;
            this.Settings.NavigationTreeKey = null;
            this.Settings.SidebarScrollOffset = 0;
        }
        else if (!AppMain.SmokeTest)
        {
            try
            {
                var LocalSettingsRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                this.Settings = ReadSettingsWithMigration(this.SettingsPath,
                    Path.Combine(LocalSettingsRoot, "DocReader", "Settings.json"),
                    Path.Combine(LocalSettingsRoot, "ClearPath", "Settings.json"));
            }
            catch (Exception Ex) when (Ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        if (AppMain.SmokeTest)
        {
            this.Settings.CustomCategories = this.Settings.CustomCategories?
                .Where(Category => Category.Folders.Count == 0 || Category.Folders.Any(Folder => AppMain.IsInsideSmokeScope(Folder.FullPath)))
                .Select(Category =>
                {
                    var Copy = Category.Clone();
                    foreach (var Folder in Copy.Folders.Where(Folder => !AppMain.IsInsideSmokeScope(Folder.FullPath)).ToArray()) Copy.Folders.Remove(Folder);
                    return Copy;
                }).ToList();
            if (this.Settings.SidebarTreeLayouts is { } Layouts)
                foreach (var Layout in Layouts.Values)
                {
                    Layout.ExpandedPaths = Layout.ExpandedPaths?.Where(AppMain.IsInsideSmokeScope).ToList();
                    Layout.SelectedPaths = Layout.SelectedPaths?.Where(AppMain.IsInsideSmokeScope).ToList();
                }
        }
        LanguageService.SetLanguage(this.Settings.Language);
        this.HostWindow.RegisterFolderViews(this.Settings);
        this.Settings.UpgradeShortcutDefaults();
        this.ShortcutBindings = KeyboardShortcut.Resolve(this.Settings.Shortcuts);
        this.ApplyLanguageResources();
    }

    private void ApplyLanguageResources()
    {
        foreach (var Entry in LanguageService.GetTexts()) SetSharedResource("Text" + Entry.Key, Entry.Value);
        var FontSource = LanguageService.CurrentLanguage switch
        {
            "ja-JP" => "Yu Gothic UI, Meiryo, Segoe UI, Microsoft YaHei UI, Malgun Gothic",
            "ko-KR" => "Malgun Gothic, Segoe UI, Microsoft YaHei UI, Yu Gothic UI",
            "en-US" => "Segoe UI, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic",
            _ => "Microsoft YaHei UI, Segoe UI, Yu Gothic UI, Malgun Gothic"
        };
        if (Application.Current.Resources["InterfaceFont"] is not FontFamily Font || Font.Source != FontSource)
            SetSharedResource("InterfaceFont", new FontFamily(FontSource));
        this.Language = XmlLanguage.GetLanguage(LanguageService.CurrentLanguage);
        // DataGrid columns and closed context menus do not participate in the window's resource invalidation.
        UpdateMenuHeaders(this.HostWindow.FileMenu);
        UpdateMenuHeaders(this.HostWindow.HelpMenu);
        UpdateMenuHeaders(this.TerminalSurface?.ContextMenu);
        if (this.FileGrid is not null)
        {
            foreach (var Column in this.FileGrid.Columns)
            {
                var Key = Column.SortMemberPath switch
                {
                    nameof(FileEntry.Name) => "NameColumn",
                    nameof(FileEntry.Modified) => "ModifiedColumn",
                    nameof(FileEntry.TypeName) => "TypeColumn",
                    nameof(FileEntry.Size) => "SizeColumn",
                    nameof(FileEntry.FullPath) => "AbsolutePathColumn",
                    nameof(FileEntry.OriginalLocation) => "OriginalLocationColumn",
                    nameof(FileEntry.DateDeleted) => "DateDeletedColumn",
                    _ => null
                };
                if (Key is not null) Column.Header = LanguageService.Text(Key);
            }
            UpdateMenuHeaders(this.FileGrid.ContextMenu);
            this.ApplyShortcutHints();
        }
    }

    private static void UpdateMenuHeaders(ContextMenu? Menu)
    {
        if (Menu is null) return;
        foreach (var Item in Menu.Items.OfType<MenuItem>())
            if (Item.Tag is string Key) Item.Header = LanguageService.Text(Key == "Delete" ? "RecycleMenu" : Key);
    }

    private void ChangeLanguage(string Code, bool Persist = true)
    {
        if (this.WindowClosed) return;
        var Selected = this.SelectedEntries();
        LanguageService.SetLanguage(Code);
        this.Settings.Language = LanguageService.CurrentLanguage;
        this.ApplyLanguageResources();
        this.UpdateFavorites();
        foreach (var Entry in this.Entries) Entry.RefreshLanguage();
        foreach (var Root in this.Roots) Root.RefreshLanguage();
        foreach (var Folder in this.CustomCategories.SelectMany(Category => Category.Folders)) Folder.Node.RefreshLanguage();
        if (this.View is not null)
        {
            this.View.Culture = LanguageService.Culture;
            if (this.FileGroupProperty.Length > 0) this.ApplySort();
            else this.View.Refresh();
            FindVisualChild<CDir.Controls.FileTilePanel>(this.FileTiles)?.InvalidateMeasure();
            this.FileGrid.SelectedItems.Clear();
            foreach (var Entry in Selected)
                if (this.View.Contains(Entry)) this.FileGrid.SelectedItems.Add(Entry);
        }
        this.UpdateCounts();
        if (this.IsVirtualLocation) { this.UpdateAddressPath(); this.NotifyTabHeader(); }
        this.UpdateDriveUsageStatus();
        this.UpdateTerminalStatus();
        if (Persist && !this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }

    private void UpdateFavorites()
    {
        // Paths are discovered once on the startup worker. Relabeling must not
        // probe redirected/offline known folders or discard their loaded trees.
        var Favorites = this.FavoritesList.Items.OfType<FavoriteLocation>().ToArray();
        foreach (var Favorite in Favorites) Favorite.Node.RefreshLanguage();
        this.FavoritesList.ItemsSource = Favorites.Select(Favorite => Favorite.NameKey is { } Key
            ? Favorite with { Name = LanguageService.Text(Key) } : Favorite).ToArray();
    }

    private Func<FavoriteLocation[]>? ReadFavoritesForTests;

    private FavoriteLocation[] ReadFavorites()
    {
        if (AppMain.SmokeTest && this.ReadFavoritesForTests is { } Read) return Read();
        var Home = AppMain.SmokeTest ? AppMain.StartupPath ?? AppMain.SmokeRoot! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return AppMain.SmokeTest ? CreateQuickAccess(Home, "", "", "", "")
            : CreateQuickAccess(Home, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), GetDownloadsPath(Home),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
    }

    private static FavoriteLocation[] CreateQuickAccess(string Home, string Desktop, string Documents, string Downloads, string Pictures)
    {
        var Favorites = new List<FavoriteLocation> { new(LanguageService.Text("FavoriteHome"), Home, "FavoriteHome") };
        void AddFavorite(string Key, string FolderPath)
        {
            if (Directory.Exists(FolderPath)) Favorites.Add(new(LanguageService.Text(Key), FolderPath, Key));
        }
        AddFavorite("FavoriteDesktop", Desktop);
        AddFavorite("FavoriteDocuments", Documents);
        AddFavorite("FavoriteDownloads", Downloads);
        AddFavorite("FavoritePictures", Pictures);
        Favorites.Add(new(LanguageService.Text("DirectoryTree"), ShellLocations.Computer, "DirectoryTree"));
        Favorites.Add(new(LanguageService.Text("RecycleBin"), ShellLocations.RecycleBin, "RecycleBin"));
        return Favorites.ToArray();
    }

    private static AppSettings ReadSettings(string SettingsFile)
    {
        using var Document = JsonDocument.Parse(DurableJsonStore.ReadText(SettingsFile));
        var Restored = Document.RootElement.Deserialize<AppSettings>() ?? new();
        // Apply the compact title once when upgrading existing settings. Later title
        // font choices remain independent of the sidebar and survive restarts.
        if (Document.RootElement.ValueKind == JsonValueKind.Object
            && !Document.RootElement.TryGetProperty(nameof(AppSettings.TitleBarFontRevision), out _)
            && Restored.FontSizes is { } Fonts)
            Fonts["TitleBar"] = Fonts.GetValueOrDefault("Sidebar", DefaultSettings.Current.FontSizes["Sidebar"]);
        return Restored;
    }

    private static AppSettings ReadSettingsWithMigration(string SettingsFile, params string[] LegacyFiles)
    {
        if (File.Exists(SettingsFile) || File.Exists(SettingsFile + ".bak")) return ReadSettings(SettingsFile);
        var LegacyFile = LegacyFiles.FirstOrDefault(File.Exists);
        if (LegacyFile is null) return new();
        var Restored = ReadSettings(LegacyFile);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        try { File.Copy(LegacyFile, SettingsFile, overwrite: false); }
        catch (IOException) when (File.Exists(SettingsFile)) { return ReadSettings(SettingsFile); }
        return Restored;
    }

    internal AppSettings CaptureSaveSettings()
    {
        var Bounds = this.HostWindow.CapturePersistenceBounds();
        return new AppSettings
        {
            LastPath = string.IsNullOrEmpty(this.CurrentPath) ? this.Settings.LastPath : this.CurrentPath,
            FileView = this.Settings.FileView,
            FileViewIconSize = this.Settings.FileViewIconSize,
            FolderViews = this.HostWindow.CaptureFolderViews(),
            FolderViewDefaults = this.HostWindow.CaptureFolderViewDefaults(),
            FolderViewIconSizes = this.HostWindow.CaptureFolderIconSizes(),
            FolderViewDefaultIconSizes = this.HostWindow.CaptureFolderDefaultIconSizes(),
            ShowHidden = this.Settings.ShowHidden,
            ShowSidebar = this.Settings.ShowSidebar,
            SidebarWidth = this.Settings.ShowSidebar ? this.SidebarColumn.Width.Value : this.ExpandedSidebarWidth.Value,
            SidebarScrollOffset = this.SidebarScroll.VerticalOffset,
            NavigationTreeKey = this.NavigationTreeKey,
            SidebarTreeLayouts = this.CaptureSidebarTreeLayouts(),
            ShowGridLines = this.Settings.ShowGridLines,
            Use12HourClock = this.Settings.Use12HourClock,
            ExitOnClose = this.Settings.ExitOnClose,
            CopyOnDrop = this.Settings.CopyOnDrop,
            CalculateFolderSizes = this.Settings.CalculateFolderSizes,
            ShowBookmarkPaths = this.Settings.ShowBookmarkPaths,
            SeparatePinnedTabs = this.Settings.SeparatePinnedTabs,
            ShowSidebarDriveUsage = this.Settings.ShowSidebarDriveUsage,
            AutoFitColumnsOnNavigation = this.Settings.AutoFitColumnsOnNavigation,
            ColumnOrder = this.FileGrid.Columns.OrderBy(Column => Column.DisplayIndex).Select(Column => Column.SortMemberPath).ToList(),
            HiddenColumns = this.Settings.HiddenColumns?.ToList() ?? [],
            BoldAllFonts = this.Settings.BoldAllFonts,
            FontSizes = this.Settings.FontSizes is null ? null : new(this.Settings.FontSizes, StringComparer.Ordinal),
            TitleBarFontRevision = this.Settings.TitleBarFontRevision,
            ShortcutDefaultsRevision = this.Settings.ShortcutDefaultsRevision,
            Shortcuts = this.Settings.Shortcuts is null ? null : new(this.Settings.Shortcuts, StringComparer.Ordinal),
            TerminalFontSize = this.Settings.TerminalFontSize,
            TerminalBackgroundColor = this.Settings.TerminalBackgroundColor,
            TerminalForegroundColor = this.Settings.TerminalForegroundColor,
            FavoritesExpanded = this.FavoritesSection.IsExpanded,
            DirectoryTreeExpanded = this.DirectoryTreeSection.IsExpanded,
            Width = Bounds.Width,
            Height = Bounds.Height,
            Maximized = this.HostWindow.PersistenceMaximized,
            Language = LanguageService.CurrentLanguage,
            CustomCategories = this.CustomCategories.Select(Category => Category.Clone()).ToList(),
            SidebarOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToList()
        };
    }

    private bool SaveSettings(string? SettingsFile = null)
    {
        if (SettingsFile is null && (this.RestoringWindowLayout || AppMain.SmokeTest && this.LayoutProbeSettingsFile is null)) return true;
        try
        {
            var Destination = SettingsFile ?? this.LayoutProbeSettingsFile ?? this.SettingsPath;
            this.Settings = this.CaptureSaveSettings();
            if (!this.HostWindow.CanPersistSharedState) return true;
            var Json = JsonSerializer.Serialize(this.Settings);
            DurableJsonStore.Write(Destination, Json);
            this.HostWindow.RecordSharedWrite(Destination, Json);
            this.HostWindow.PersistWorkspaceNow();
            return true;
        }
        catch (Exception Ex) when (Ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
}
