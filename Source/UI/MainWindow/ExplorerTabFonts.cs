using System.Windows;
using System.Windows.Controls;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private static void SetSharedResource(string Key, object Value)
    {
        var Resources = Application.Current.Resources;
        if (!Equals(Resources[Key], Value)) Resources[Key] = Value;
    }

    private static readonly (string Key, string ResourceKey, double DefaultSize)[] FontPanels =
    [
        ("FileList", "FontFileList", DefaultSettings.Current.FontSizes["FileList"]),
        ("Headers", "FontHeaders", DefaultSettings.Current.FontSizes["Headers"]),
        ("Sidebar", "FontSidebar", DefaultSettings.Current.FontSizes["Sidebar"]),
        ("Navigation", "FontNavigation", DefaultSettings.Current.FontSizes["Navigation"]),
        ("Menus", "FontMenus", DefaultSettings.Current.FontSizes["Menus"]),
        ("Tabs", "FontTabs", DefaultSettings.Current.FontSizes["Tabs"]),
        ("StatusBar", "FontStatusBar", DefaultSettings.Current.FontSizes["StatusBar"]),
        ("TitleBar", "FontTitleBar", DefaultSettings.Current.FontSizes["TitleBar"]),
        ("Preferences", "Preferences", DefaultSettings.Current.FontSizes["Preferences"])
    ];

    private IReadOnlyList<UIFontOption> GetFontOptions() => FontPanels.Select(Panel => new UIFontOption(
        Panel.Key, Panel.ResourceKey, Panel.DefaultSize, this.Settings.FontSizes?.GetValueOrDefault(Panel.Key, Panel.DefaultSize) ?? Panel.DefaultSize)).ToArray();

    private void ApplyUIFonts()
    {
        SetSharedResource("UIFontWeight", this.Settings.BoldAllFonts ? FontWeights.Bold : FontWeights.Normal);
        SetSharedResource("UIHeadingFontWeight", this.Settings.BoldAllFonts ? FontWeights.Bold : FontWeights.SemiBold);
        var Values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var Panel in FontPanels)
        {
            var Saved = this.Settings.FontSizes?.GetValueOrDefault(Panel.Key, Panel.DefaultSize) ?? Panel.DefaultSize;
            var Size = double.IsFinite(Saved) ? Math.Clamp(Math.Round(Saved), 8, 24) : Panel.DefaultSize;
            Values[Panel.Key] = Size;
            SetSharedResource("UIFont" + Panel.Key, Size);
        }
        this.Settings.FontSizes = Values;
        void Resource(string Key, double Value) => SetSharedResource(Key, Value);
        void RowResource(string Key, double Value) => SetSharedResource(Key, new GridLength(Value));
        Resource("UITabHeight", Math.Max(24, Math.Ceiling(Values["Tabs"] * 1.35 + 6)));
        Resource("UISidebarSectionFontSize", Math.Max(8, Values["Sidebar"] - 1));
        Resource("UISidebarFavoriteFontSize", Values["Sidebar"] + 1);
        Resource("UIFileRowHeight", Math.Max(22, Math.Ceiling(Values["FileList"] * 1.35 + 4)));
        Resource("UIEmptyTitleFontSize", Values["FileList"] + 3);
        Resource("UIEmptyHintFontSize", Math.Max(8, Values["FileList"] - 1));
        var NavigationHeight = Math.Max(28, Math.Ceiling(Values["Navigation"] * 1.35 + 10));
        Resource("UINavigationControlHeight", NavigationHeight);
        Resource("UINavigationTextHeight", NavigationHeight - 4);
        Resource("UINavigationButtonHeight", NavigationHeight - 2);
        RowResource("UINavigationRowHeight", NavigationHeight + 12);
        Resource("UINavigationHintFontSize", Math.Max(8, Values["Navigation"] - 1));
        var TitleHeight = Math.Max(30, Math.Ceiling(Values["TitleBar"] * 1.35 + 6));
        Resource("UITitleControlHeight", TitleHeight);
        RowResource("UITitleRowHeight", TitleHeight);
        RowResource("UIMenuRowHeight", Math.Max(24, Math.Ceiling(Values["Menus"] * 1.35 + 4)));
        Resource("UIMenuMinimumWidth", Math.Max(180, Math.Ceiling(Values["Menus"] * 12 + 24)));
        RowResource("UIStatusRowHeight", Math.Max(34, Math.Ceiling(Values["StatusBar"] * 1.35 + 12)));
        Resource("UIPreferencesHeadingSize", Values["Preferences"] + 5);
        Resource("UIPreferencesHintSize", Math.Max(8, Values["Preferences"] - 1));
        Resource("UIPreferencesControlHeight", Math.Max(32, Math.Ceiling(Values["Preferences"] * 1.35 + 8)));
        if (this.FileGrid is not null)
            foreach (var Column in this.FileGrid.Columns.OfType<DataGridTextColumn>()) Column.FontSize = Values["FileList"];
        // Closed popup menus can miss application resource invalidation while detached.
        ApplyMenuFont(this.HostWindow.FileMenu);
        ApplyMenuFont(this.HostWindow.HelpMenu);
        ApplyMenuFont(this.FileGrid?.ContextMenu);
        if (this.AddressDisplay is not null) this.QueueAddressLayout();
        if (this.FileTiles is not null && !this.IsDetailsView) this.ApplyFileView();
        this.ApplyTerminalFontWeight();
    }

    private static void ApplyMenuFont(ItemsControl? Menu)
    {
        if (Menu is null) return;
        Menu.SetResourceReference(Control.FontSizeProperty, "UIFontMenus");
        Menu.SetResourceReference(Control.FontWeightProperty, "UIFontWeight");
        foreach (var Item in Menu.Items.OfType<MenuItem>()) ApplyMenuFont(Item);
    }

    private void ChangeBoldAllFonts(bool Bold)
    {
        if (this.WindowClosed) return;
        this.Settings.BoldAllFonts = Bold;
        this.ApplyUIFonts();
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }

    private bool ChangeUIFontSize(string Key, double Size)
    {
        if (this.WindowClosed || !FontPanels.Any(Panel => Panel.Key == Key) || !double.IsFinite(Size) || Size < 8 || Size > 24) return false;
        this.Settings.FontSizes ??= new(StringComparer.Ordinal);
        this.Settings.FontSizes[Key] = Size;
        this.ApplyUIFonts();
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
        return true;
    }
}
