using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunCustomCategorySmokeChecksAsync(List<string> Passed)
    {
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialLanguage = LanguageService.CurrentLanguage;
        var InitialPath = this.CurrentPath;
        var SettingsFile = Path.Combine(Path.GetTempPath(), "CDirCategories-" + Guid.NewGuid().ToString("N") + ".json");
        ContextMenu? ActiveMenu = null;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Custom category UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        Task AddDialog()
        {
            Action(this.SidebarPanel.ContextMenu, "AddCategory").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            return Task.CompletedTask;
        }
        static MenuItem Action(ContextMenu Menu, string Key) => Menu.Items.OfType<MenuItem>().Single(Item => Item.Tag?.ToString() == Key);
        async Task<ContextMenu> OpenMenu(FrameworkElement Target)
        {
            if (ActiveMenu is not null)
            {
                ActiveMenu.IsOpen = false;
                await Settle();
            }
            ActiveMenu = Target.ContextMenu ?? this.SidebarPanel.ContextMenu;
            ActiveMenu.PlacementTarget = Target;
            ActiveMenu.Placement = PlacementMode.Bottom;
            ActiveMenu.IsOpen = true;
            await Settle();
            Check(ActiveMenu.IsOpen && ActiveMenu.IsVisible, "Sidebar context menu opens visibly for " + (Target is Expander Section ? Section.Header : Target.Name));
            return ActiveMenu;
        }
        Task RenameDialog(ContextMenu Menu)
        {
            Action(Menu, "RenameCategory").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            return Task.CompletedTask;
        }
        DependencyObject Container(SidebarCategory Category) => this.SidebarSectionsList.ItemContainerGenerator.ContainerFromItem(Category)
            ?? throw new InvalidOperationException("Custom category is not rendered.");
        try
        {
            this.RestoreSidebarCategories([]);
            var BackgroundMenu = await OpenMenu(this.SidebarPanel);
            Check(BackgroundMenu.Items.OfType<MenuItem>().Select(Item => Item.Tag?.ToString()).SequenceEqual(new[] { "AddCategory" })
                && this.FindName("AddCategoryButton") is null, "Sidebar exposes Add category through its context menu without an inline button");
            await this.AcceptSmokeNameDialogAsync(AddDialog, LanguageService.Text("AddCategory"), "  Work 工作  ");
            await Settle();
            Check(this.CustomCategories is { Count: 1 } && this.CustomCategories[0].Name == "Work 工作",
                "Add category dialog creates a trimmed Unicode category");
            var Work = this.CustomCategories[0];
            var Section = FindVisualChild<Expander>(Container(Work))!;
            Check(Section.IsVisible && Section.Header?.ToString() == Work.Name && Section.IsExpanded,
                "Custom category renders as an independent expanded sidebar section");
            Check(Section.TranslatePoint(new Point(), this).Y >= this.DirectoryTreeSection.TranslatePoint(new Point(), this).Y + this.DirectoryTreeSection.ActualHeight,
                "New custom categories appear below Folder tree");
            Check(FindVisualChild<StackPanel>(Section)!.Children.OfType<Button>().Select(Button => Button.Name)
                .SequenceEqual(new[] { "ExpandFoldersButton", "CollapseFoldersButton" }),
                "Custom category header offers only folder expansion buttons and no inline delete button");
            await OpenMenu(this.SidebarPanel);
            await this.AcceptSmokeNameDialogAsync(AddDialog, LanguageService.Text("AddCategory"), "Cancelled", ConfirmDialog: false);
            Check(this.CustomCategories.Count == 1, "Cancelling category creation leaves the sidebar unchanged");
            Check(!this.TryAddSidebarCategory("work 工作") && !this.TryAddSidebarCategory("   ")
                && !this.TryAddSidebarCategory(LanguageService.Text("Favorites")) && !this.TryAddSidebarCategory(LanguageService.Text("DirectoryTree"))
                && !this.TryAddSidebarCategory("my computer") && !this.TryAddSidebarCategory("quick access")
                && this.CustomCategories.Count == 1, "Duplicate blank and built-in category names are rejected");

            var Toggle = FindVisualChild<ToggleButton>(Section)!;
            ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(Toggle)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
            await Settle();
            Check(!Section.IsExpanded && !Work.IsExpanded, "Custom category collapse updates its saved expansion state");
            this.TryAddSidebarCategory("Projects");
            await Settle();
            var ProjectsSection = FindVisualChild<Expander>(Container(this.CustomCategories[^1]))!;
            Check(ProjectsSection.TranslatePoint(new Point(), this).Y >= Section.TranslatePoint(new Point(), this).Y + Section.ActualHeight,
                "Each new category is appended below existing custom categories");
            var CategoryMenu = await OpenMenu(Section);
            Check(CategoryMenu.Items.OfType<MenuItem>().Select(Item => Item.Tag?.ToString())
                .SequenceEqual(new[] { "ExpandCategoryFolders", "CollapseCategoryFolders", "RenameCategory", "DeleteCategory" })
                && ReferenceEquals(Action(CategoryMenu, "RenameCategory").DataContext, Work),
                "Custom category context menu offers folder expansion rename and delete without category creation");
            await this.AcceptSmokeNameDialogAsync(() => RenameDialog(CategoryMenu), LanguageService.Text("RenameCategory"), "  Renamed 工作  ");
            await Settle();
            Check(Work.Name == "Renamed 工作" && Section.Header?.ToString() == Work.Name && !Work.IsExpanded
                && ReferenceEquals(this.CustomCategories[0], Work), "Rename dialog updates the visible category while preserving its order and expansion state");
            CategoryMenu = await OpenMenu(Section);
            await this.AcceptSmokeNameDialogAsync(() => RenameDialog(CategoryMenu), LanguageService.Text("RenameCategory"), "Cancelled", ConfirmDialog: false);
            Check(Work.Name == "Renamed 工作", "Cancelling rename preserves the category name");
            CategoryMenu = await OpenMenu(Section);
            await this.AcceptSmokeNameDialogAsync(() => RenameDialog(CategoryMenu), LanguageService.Text("RenameCategory"), "Projects");
            Check(Work.Name == "Renamed 工作" && !this.TryRenameSidebarCategory(Work, " ")
                && !this.TryRenameSidebarCategory(Work, LanguageService.Text("Favorites")),
                "Rename rejects duplicate blank and reserved names without changing the category");
            Check(this.SaveSettings(SettingsFile), "Custom categories save to an owned settings file");
            var Restored = ReadSettings(SettingsFile);
            Check(Restored.CustomCategories is { Count: 2 } && Restored.CustomCategories[0].Name == "Renamed 工作"
                && !Restored.CustomCategories[0].IsExpanded && Restored.CustomCategories[1].IsExpanded,
                "Settings retain custom category order names and independent expansion states");
            this.RestoreSidebarCategories(Restored.CustomCategories);
            await Settle();
            Check(this.CustomCategories.Count == 2 && !FindVisualChild<Expander>(Container(this.CustomCategories[0]))!.IsExpanded,
                "Startup restoration rebuilds custom category sections with their saved state");
            Check(SamePath(this.CurrentPath, InitialPath) && this.FolderTree.SelectedItem is FolderNode Node && SamePath(Node.FullPath, InitialPath),
                "Restoring category sections preserves the current directory and tree selection");

            foreach (var Language in LanguageService.SupportedLanguages)
            {
                this.ChangeLanguage(Language.Code, Persist: false);
                await Settle();
                BackgroundMenu = await OpenMenu(this.SidebarPanel);
                CategoryMenu = await OpenMenu(FindVisualChild<Expander>(Container(this.CustomCategories[0]))!);
                Check(Action(BackgroundMenu, "AddCategory").Header?.ToString() == LanguageService.Text("AddCategory")
                    && CategoryMenu.Items.OfType<MenuItem>().Count() == 4 && !CategoryMenu.Items.OfType<MenuItem>().Any(Item => Item.Tag?.ToString() == "AddCategory")
                    && CategoryMenu.Items.OfType<MenuItem>().All(Item => Item.Header?.ToString() == LanguageService.Text((string)Item.Tag))
                    && this.CustomCategories[0].Name == "Renamed 工作",
                    Language.Code + " sidebar context menus localize while user names stay unchanged");
                await this.RespondSmokeCategoryDeleteDialogAsync(() =>
                {
                    Action(CategoryMenu, "DeleteCategory").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    return Task.CompletedTask;
                }, "Renamed 工作", Confirm: false, Inspect: Dialog => Check(this.CustomCategories.Count == 2,
                    Language.Code + " category remains present while its deletion confirmation is open"));
                Check(this.CustomCategories.Count == 2 && this.CustomCategories[0].Name == "Renamed 工作",
                    Language.Code + " cancelling localized category deletion preserves the category");
            }

            this.DeleteCategoryClick(this.FavoritesSection, new RoutedEventArgs(Button.ClickEvent));
            this.DeleteCategoryClick(this.DirectoryTreeSection, new RoutedEventArgs(Button.ClickEvent));
            this.RenameCategoryClick(this.FavoritesSection, new RoutedEventArgs(MenuItem.ClickEvent));
            this.RenameCategoryClick(this.DirectoryTreeSection, new RoutedEventArgs(MenuItem.ClickEvent));
            foreach (var BuiltIn in new[] { this.FavoritesSection, this.DirectoryTreeSection })
            {
                var BuiltInMenu = await OpenMenu(BuiltIn);
                Check(BuiltInMenu.Items.OfType<MenuItem>().Select(Item => Item.Tag?.ToString()).SequenceEqual(new[] { "ExpandCategoryFolders", "CollapseCategoryFolders" }),
                    "Built-in section context menu offers folder expansion without category creation");
            }
            Check(this.CustomCategories.Count == 2 && this.FavoritesSection.IsVisible && this.DirectoryTreeSection.IsVisible,
                "Quick access and Folder tree resist deletion and rename");
            CategoryMenu = await OpenMenu(FindVisualChild<Expander>(Container(this.CustomCategories[0]))!);
            var Deleting = this.CustomCategories[0];
            this.AddFoldersToCategory(Deleting, [new FileEntry { FullPath = InitialPath, IsDirectory = true }]);
            this.AddFoldersToCategory(this.CustomCategories[1], [new FileEntry { FullPath = InitialPath, IsDirectory = true }]);
            Check(this.SaveSettings(SettingsFile), "Populated categories save before delete confirmation");
            var BeforeDelete = File.ReadAllText(SettingsFile);
            await this.RespondSmokeCategoryDeleteDialogAsync(() =>
            {
                Action(CategoryMenu, "DeleteCategory").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                return Task.CompletedTask;
            }, Deleting.Name, Confirm: false, Inspect: Dialog => Check(this.CustomCategories.Contains(Deleting) && Deleting.Folders.Count == 1,
                "A populated category and its folder shortcuts remain intact until deletion is confirmed"), Escape: true);
            Check(this.CustomCategories.Contains(Deleting) && Deleting.Folders.Count == 1 && !Deleting.IsExpanded
                && File.ReadAllText(SettingsFile) == BeforeDelete, "Escape cancels deletion and retains category contents order expansion and saved settings");
            CategoryMenu = await OpenMenu(FindVisualChild<Expander>(Container(Deleting))!);
            await this.RespondSmokeCategoryDeleteDialogAsync(() =>
            {
                Action(CategoryMenu, "DeleteCategory").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                return Task.CompletedTask;
            }, Deleting.Name, Confirm: false, CloseWindow: true);
            Check(this.CustomCategories.Contains(Deleting) && Deleting.Folders.Count == 1,
                "Closing the confirmation window preserves the category and its shortcuts");
            CategoryMenu = await OpenMenu(FindVisualChild<Expander>(Container(Deleting))!);
            await this.RespondSmokeCategoryDeleteDialogAsync(() =>
            {
                Action(CategoryMenu, "DeleteCategory").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                return Task.CompletedTask;
            }, Deleting.Name);
            await Settle();
            Check(this.CustomCategories.Count == 1 && this.CustomCategories[0].Name == "Projects"
                && this.FavoritesSection.IsVisible && this.DirectoryTreeSection.IsVisible,
                "Delete category context action removes only its clicked custom section");
            Check(Directory.Exists(InitialPath) && this.CustomCategories[0].Folders.Count == 1,
                "Confirmed category deletion preserves original folders and shortcuts in other categories");
            Check(this.SaveSettings(SettingsFile) && ReadSettings(SettingsFile).CustomCategories is { Count: 1 } Remaining
                && Remaining[0].Name == "Projects", "Deleted categories stay removed in saved settings");

            for (var Index = 0; Index < 20; Index++) this.TryAddSidebarCategory("Group " + Index);
            await Settle();
            var LastSection = FindVisualChild<Expander>(Container(this.CustomCategories[^1]))!;
            LastSection.BringIntoView();
            await Settle();
            Check(this.SidebarPanel.ContextMenu is not null && this.FolderTree.ActualHeight > 0
                && this.SidebarScroll.ScrollableHeight > 0 && this.SidebarScroll.VerticalOffset > 0,
                "Scrolling reaches newly appended categories at the bottom of a long sidebar");
            if (ActiveMenu is not null) ActiveMenu.IsOpen = false;
            this.RestoreSidebarCategories(Restored.CustomCategories);
            this.ChangeLanguage(InitialLanguage, Persist: false);
            this.SidebarScroll.ScrollToTop();
            await Settle();
            if (AppMain.ScreenshotPath is { } Screenshot)
            {
                var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(this);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Screenshot)!, "CustomCategories.png"));
                Encoder.Save(Stream);
            }
        }
        finally
        {
            if (ActiveMenu is not null) ActiveMenu.IsOpen = false;
            this.RestoreSidebarCategories(InitialCategories);
            this.ChangeLanguage(InitialLanguage, Persist: false);
            File.Delete(SettingsFile);
        }
    }
}
