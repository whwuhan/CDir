namespace CDir.Models;

internal sealed class AppSettings
{
    public string? LastPath { get; set; }
    public int ShortcutDefaultsRevision { get; set; }

    internal void UpgradeShortcutDefaults()
    {
        if (this.ShortcutDefaultsRevision >= 1) return;
        if (this.Shortcuts?.GetValueOrDefault("ToggleSidebar") == "Alt+1"
            && !this.Shortcuts.Any(Item => Item.Key != "ToggleSidebar" && Item.Value == "Alt+Q"))
            this.Shortcuts = new(this.Shortcuts, StringComparer.Ordinal) { ["ToggleSidebar"] = "Alt+Q" };
        this.ShortcutDefaultsRevision = 1;
    }

    public FileViewMode FileView { get; set; } = FileViewMode.Details;
    public int FileViewIconSize { get; set; }
    public Dictionary<string, FileViewMode>? FolderViews { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FileViewMode>? FolderViewDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int>? FolderViewIconSizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int>? FolderViewDefaultIconSizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ShowHidden { get; set; } = DefaultSettings.Current.ShowHidden;
    public bool ShowSidebar { get; set; } = DefaultSettings.Current.ShowSidebar;
    public double SidebarWidth { get; set; } = DefaultSettings.Current.SidebarWidth;
    public double SidebarScrollOffset { get; set; }
    public string? NavigationTreeKey { get; set; }
    public Dictionary<string, SidebarTreeLayout>? SidebarTreeLayouts { get; set; } = new(StringComparer.Ordinal);
    public bool ShowGridLines { get; set; } = DefaultSettings.Current.ShowGridLines;
    public bool Use12HourClock { get; set; } = DefaultSettings.Current.Use12HourClock;
    public bool ExitOnClose { get; set; } = DefaultSettings.Current.ExitOnClose;
    public bool CopyOnDrop { get; set; } = DefaultSettings.Current.CopyOnDrop;
    public bool CalculateFolderSizes { get; set; } = DefaultSettings.Current.CalculateFolderSizes;
    public bool AutoFitColumnsOnNavigation { get; set; } = DefaultSettings.Current.AutoFitColumnsOnNavigation;
    public bool ShowSidebarDriveUsage { get; set; } = DefaultSettings.Current.ShowSidebarDriveUsage;
    public bool SeparatePinnedTabs { get; set; } = DefaultSettings.Current.SeparatePinnedTabs;
    public bool ShowBookmarkPaths { get; set; } = DefaultSettings.Current.ShowBookmarkPaths;
    public List<string>? ColumnOrder { get; set; } = DefaultSettings.Current.ColumnOrder.ToList();
    public List<string>? HiddenColumns { get; set; } = DefaultSettings.Current.HiddenColumns.ToList();
    public bool BoldAllFonts { get; set; } = DefaultSettings.Current.BoldAllFonts;
    public Dictionary<string, double>? FontSizes { get; set; } = new(DefaultSettings.Current.FontSizes, StringComparer.Ordinal);
    public int TitleBarFontRevision { get; set; } = DefaultSettings.Current.TitleBarFontRevision;
    public Dictionary<string, string>? Shortcuts { get; set; } = new(DefaultSettings.Current.Shortcuts, StringComparer.Ordinal);
    public double TerminalFontSize { get; set; } = DefaultSettings.Current.TerminalFontSize;
    public string? TerminalBackgroundColor { get; set; } = DefaultSettings.Current.TerminalBackgroundColor;
    public string? TerminalForegroundColor { get; set; } = DefaultSettings.Current.TerminalForegroundColor;
    public double Width { get; set; } = DefaultSettings.Current.Width;
    public double Height { get; set; } = DefaultSettings.Current.Height;
    public bool Maximized { get; set; } = DefaultSettings.Current.Maximized;
    public string? Language { get; set; } = DefaultSettings.Current.Language;
    public bool FavoritesExpanded { get; set; } = DefaultSettings.Current.FavoritesExpanded;
    public bool DirectoryTreeExpanded { get; set; } = DefaultSettings.Current.DirectoryTreeExpanded;
    public List<SidebarCategory>? CustomCategories { get; set; } = [];
    public List<string>? SidebarOrder { get; set; } = DefaultSettings.Current.SidebarOrder.ToList();
}

internal sealed class SidebarTreeLayout
{
    public List<string>? ExpandedPaths { get; set; } = [];
    public List<string>? SelectedPaths { get; set; } = [];

    internal SidebarTreeLayout Clone() => new()
    {
        ExpandedPaths = this.ExpandedPaths?.ToList(),
        SelectedPaths = this.SelectedPaths?.ToList()
    };
}
