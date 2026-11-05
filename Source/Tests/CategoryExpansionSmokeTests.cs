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
    private async Task RunCategoryExpansionSmokeChecksAsync(List<string> Passed)
    {
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        var InitialRoots = this.Roots.ToArray();
        var InitialFavoritesSource = this.FavoritesList.ItemsSource;
        var InitialFavorites = this.FavoritesSection.IsExpanded;
        var InitialTree = this.DirectoryTreeSection.IsExpanded;
        var InitialHidden = this.Settings.ShowHidden;
        var InitialLanguage = LanguageService.CurrentLanguage;
        var InitialPath = this.CurrentPath;
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirCategoryExpansion-" + FixtureId);
        var Alpha = Directory.CreateDirectory(Path.Combine(Fixture, "Alpha 中文")).FullName;
        var Deep = Directory.CreateDirectory(Path.Combine(Alpha, "Child", "Deep")).FullName;
        var Beta = Directory.CreateDirectory(Path.Combine(Fixture, "Beta With Spaces", "Other Child")).Parent!.FullName;
        var Hidden = Directory.CreateDirectory(Path.Combine(Alpha, "Hidden Child")).FullName;
        File.SetAttributes(Hidden, FileAttributes.Directory | FileAttributes.Hidden);
        var Marker = Path.Combine(Fixture, ".CDirCategoryExpansionOwner");
        File.WriteAllText(Marker, FixtureId);
        ContextMenu? ActiveMenu = null;
        var MenuStayOpen = new Dictionary<ContextMenu, bool>();
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Category expansion UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        async Task<ContextMenu> OpenMenu(Expander Section)
        {
            if (ActiveMenu is not null) { ActiveMenu.IsOpen = false; await Settle(); }
            ActiveMenu = Section.ContextMenu;
            // Programmatic checks must not capture or depend on the user's desktop mouse input.
            MenuStayOpen.TryAdd(ActiveMenu, ActiveMenu.StaysOpen);
            ActiveMenu.StaysOpen = true;
            ActiveMenu.PlacementTarget = Section;
            ActiveMenu.Placement = PlacementMode.Bottom;
            ActiveMenu.IsOpen = true;
            await Settle();
            Check(ActiveMenu.IsOpen && ActiveMenu.IsVisible && !ActiveMenu.Items.OfType<MenuItem>().Any(Item => Item.Tag?.ToString() == "AddCategory"),
                $"Category context menus open visibly without Add Category (open={ActiveMenu.IsOpen}, visible={ActiveMenu.IsVisible}, section={Section.Header}, path={this.CurrentPath}, last={Passed.LastOrDefault()}, items={string.Join(',', ActiveMenu.Items.OfType<MenuItem>().Select(Item => Item.Tag))})");
            return ActiveMenu;
        }
        static void Invoke(ContextMenu Menu, string Key) => Menu.Items.OfType<MenuItem>().Single(Item => Item.Tag?.ToString() == Key)
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        static Button HeaderButton(Expander Section, string Name) => (Button)Section.Template.FindName(Name, Section);
        static void InvokeButton(Button Button) => Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static IEnumerable<FolderNode> Nodes(IEnumerable<FolderNode> Roots)
        {
            var Pending = new Stack<FolderNode>(Roots);
            while (Pending.TryPop(out var Node))
            {
                if (Node.IsPlaceholder) continue;
                yield return Node;
                foreach (var Child in Node.Children) Pending.Push(Child);
            }
        }
        void Screenshot(string Name, Visual Visual, double Width, double Height)
        {
            if (AppMain.ScreenshotPath is not { } Output) return;
            var Bitmap = new RenderTargetBitmap((int)Math.Ceiling(Width), (int)Math.Ceiling(Height), 96, 96, PixelFormats.Pbgra32);
            Bitmap.Render(Visual);
            var Encoder = new PngBitmapEncoder();
            Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
            using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, Name + ".png"));
            Encoder.Save(Stream);
        }
        try
        {
            this.RestoreSidebarCategories([]);
            this.TryAddSidebarCategory("Projects_工作");
            this.TryAddSidebarCategory("Unrelated");
            var Category = this.CustomCategories[0];
            var Other = this.CustomCategories[1];
            var Entries = new[] { Alpha, Beta }.Select(FullPath => new FileEntry { FullPath = FullPath, IsDirectory = true }).ToArray();
            this.AddFoldersToCategory(Category, Entries);
            this.AddFoldersToCategory(Other, Entries.Take(1));
            this.FavoritesList.ItemsSource = new[] { new FavoriteLocation("Alpha", Alpha), new FavoriteLocation("Beta", Beta) };
            this.SyncingTree = true;
            try { this.Roots.Clear(); this.Roots.Add(new FolderNode("Owned Drive", Fixture)); }
            finally { this.SyncingTree = false; }
            this.Settings.ShowHidden = false;
            this.FavoritesSection.IsExpanded = true;
            this.DirectoryTreeSection.IsExpanded = true;
            this.SidebarScroll.ScrollToTop();
            await Settle();
            var CustomSection = this.SidebarSectionElement(Category)!;
            foreach (var Section in new[] { this.DirectoryTreeSection, this.FavoritesSection, CustomSection })
            {
                var Roots = this.CategoryFolderRoots(Section);
                var Menu = await OpenMenu(Section);
                Check(Menu.Items.OfType<MenuItem>().Where(Item => Item.Tag is "ExpandCategoryFolders" or "CollapseCategoryFolders").All(Item => Item.IsEnabled),
                    "Nonempty built-in and custom categories enable both folder actions");
                Invoke(Menu, "ExpandCategoryFolders");
                await this.TreeExpansionTask;
                await Settle();
                Check(Section.IsExpanded && Nodes(Roots).All(Node => Node.IsLoaded && Node.IsExpanded)
                    && Nodes(Roots).Any(Node => SamePath(Node.FullPath, Deep)) && SamePath(this.CurrentPath, InitialPath),
                    "Category expansion recursively loads every folder without navigating");
                Check(!Other.Folders[0].Node.IsExpanded, "Category folder expansion leaves unrelated categories unchanged");
                Menu = await OpenMenu(Section);
                Invoke(Menu, "CollapseCategoryFolders");
                await Settle();
                Check(Section.IsExpanded && Nodes(Roots).All(Node => !Node.IsExpanded) && SamePath(this.CurrentPath, InitialPath),
                    "Collapse All Folders keeps the category expanded and preserves the current directory");
                var ExpandButton = HeaderButton(Section, "ExpandFoldersButton");
                var CollapseButton = HeaderButton(Section, "CollapseFoldersButton");
                var ExpandPosition = ExpandButton.TranslatePoint(new Point(), Section);
                var CollapsePosition = CollapseButton.TranslatePoint(new Point(), Section);
                Check(ExpandButton.IsVisible && CollapseButton.IsVisible
                    && ExpandButton.Content is Image { Source: DrawingImage } && CollapseButton.Content is Image { Source: DrawingImage }
                    && ExpandPosition.X + ExpandButton.ActualWidth <= CollapsePosition.X
                    && CollapsePosition.X + CollapseButton.ActualWidth < Section.ActualWidth,
                    "Every built-in and custom category displays separate vector expand and collapse buttons in that order");
                foreach (var Button in new[] { ExpandButton, CollapseButton })
                {
                    var Hit = Button.InputHitTest(new Point(Button.ActualWidth / 2, Button.ActualHeight / 2)) as DependencyObject;
                    Check(ReferenceEquals(FindAncestor<Button>(Hit), Button)
                        && !this.PrepareSidebarDragSource(Hit, 1, new Point())
                        && this.SidebarDragCandidate is null && this.SidebarFolderAddDragCandidate is null && this.CategoryFolderDragCandidate is null,
                        "Category action buttons receive their own clicks without initiating a category or folder drag");
                }
                Section.IsExpanded = false;
                await Settle();
                InvokeButton(ExpandButton);
                await this.TreeExpansionTask;
                await Settle();
                Check(Section.IsExpanded && Nodes(Roots).All(Node => Node.IsLoaded && Node.IsExpanded)
                    && SamePath(this.CurrentPath, InitialPath) && !Other.Folders[0].Node.IsExpanded,
                    "The header expand button opens a collapsed category and recursively expands only its folders without navigating");
                InvokeButton(CollapseButton);
                await Settle();
                Check(Section.IsExpanded && Nodes(Roots).All(Node => !Node.IsExpanded) && SamePath(this.CurrentPath, InitialPath),
                    "The header collapse button collapses every descendant while keeping its category expanded and current directory unchanged");
                InvokeButton(ExpandButton);
                InvokeButton(CollapseButton);
                await this.TreeExpansionTask;
                Check(Section.IsExpanded && Nodes(Roots).All(Node => !Node.IsExpanded),
                    "The header collapse button cancels an in-flight expansion");
            }
            Screenshot("CategoryHeaderButtons", this, this.ActualWidth, this.ActualHeight);

            var CategoryMenu = await OpenMenu(CustomSection);
            Invoke(CategoryMenu, "ExpandCategoryFolders");
            await this.TreeExpansionTask;
            await Settle();
            var List = FindVisualChild<ItemsControl>(CustomSection)!;
            var Row = List.ItemContainerGenerator.ContainerFromIndex(0);
            var ChildTree = FindVisualChild<TreeView>(Row)!;
            Check(ChildTree.IsVisible && ChildTree.Items.Count > 0,
                "Expanded custom-category shortcuts render their child directories as a tree");
            var ChildItem = (TreeViewItem)ChildTree.ItemContainerGenerator.ContainerFromIndex(0);
            var ChildHeader = (FrameworkElement)ChildItem.Template.FindName("PART_Header", ChildItem);
            Check(this.SidebarFolderAddFromSource(ChildHeader, 1)?.Folder.FullPath == ((FolderNode)ChildItem.DataContext).FullPath,
                "A child directory in a category remains draggable into another category");
            var ClickedChild = (FolderNode)ChildItem.DataContext;
            ClickedChild.IsExpanded = false;
            await this.CompleteSidebarFolderClick(this.SidebarFolderAddFromSource(ChildHeader, 1), ChildHeader);
            Check(SamePath(this.CurrentPath, ClickedChild.FullPath) && !ClickedChild.IsExpanded,
                "Single-clicking a collapsed category child navigates without changing expansion");
            await this.OpenSidebarTreeFolderAsync(ChildItem, Navigate: false);
            await Settle();
            Check(SamePath(this.CurrentPath, ClickedChild.FullPath) && ClickedChild.IsExpanded,
                "An expansion-only action opens the current category child without navigating");
            await this.OpenSidebarTreeFolderAsync(ChildItem, Navigate: false);
            await Settle();
            Check(SamePath(this.CurrentPath, ClickedChild.FullPath) && !ClickedChild.IsExpanded && CustomSection.IsExpanded,
                "An expansion-only action closes the current category child without changing the main panel");
            await this.OpenSidebarTreeFolderAsync(ChildItem, Navigate: false);
            await Settle();
            var Selected = Nodes([Category.Folders[0].Node]).Single(Node => SamePath(Node.FullPath, Deep));
            Selected.IsSelected = true;
            var Deadline = DateTime.UtcNow.AddSeconds(10);
            while ((this.Loading || !SamePath(this.CurrentPath, Deep)) && DateTime.UtcNow < Deadline) await Task.Delay(20);
            Check(!this.Loading && SamePath(this.CurrentPath, Deep), "Selecting a child directory in a custom category opens it");
            CategoryMenu = await OpenMenu(CustomSection);
            CategoryMenu.IsOpen = false;
            InvokeButton(HeaderButton(CustomSection, "CollapseFoldersButton"));
            await Settle();
            Check(CustomSection.IsExpanded && Category.IsExpanded && !ChildTree.IsVisible && SamePath(this.CurrentPath, Deep),
                "Collapsing a category's selected child hides its branches without collapsing the category or navigating");
            Invoke(CategoryMenu, "ExpandCategoryFolders");
            Invoke(CategoryMenu, "CollapseCategoryFolders");
            await this.TreeExpansionTask;
            Check(CustomSection.IsExpanded && Nodes(this.CategoryFolderRoots(CustomSection)).All(Node => !Node.IsExpanded),
                "Collapse All cancels an in-flight category expansion while retaining the category header and roots");

            await this.RefreshAsync(true);
            CategoryMenu = await OpenMenu(CustomSection);
            Invoke(CategoryMenu, "ExpandCategoryFolders");
            await this.TreeExpansionTask;
            Check(!Nodes(this.CategoryFolderRoots(CustomSection)).Any(Node => SamePath(Node.FullPath, Hidden)),
                "Bookmark trees follow the current hidden-folder filter");
            this.Settings.ShowHidden = true;
            await this.RefreshAsync(true);
            CategoryMenu = await OpenMenu(CustomSection);
            Invoke(CategoryMenu, "ExpandCategoryFolders");
            await this.TreeExpansionTask;
            await Settle();
            Check(Nodes(this.CategoryFolderRoots(CustomSection)).Any(Node => SamePath(Node.FullPath, Hidden)),
                "Refreshing after changing hidden-folder settings rebuilds bookmark descendants");
            this.FavoritesSection.IsExpanded = false;
            this.DirectoryTreeSection.IsExpanded = false;
            this.SidebarScroll.ScrollToTop();
            await Settle();
            Screenshot("ExpandedCategoryFolders", this, this.ActualWidth, this.ActualHeight);
            CategoryMenu = await OpenMenu(CustomSection);
            Screenshot("CategoryFolderMenu", CategoryMenu, CategoryMenu.ActualWidth, CategoryMenu.ActualHeight);
            foreach (var Language in LanguageService.SupportedLanguages)
            {
                this.ChangeLanguage(Language.Code, Persist: false);
                CategoryMenu = await OpenMenu(CustomSection);
                Check(CategoryMenu.Items.OfType<MenuItem>().All(Item => Item.Header?.ToString() == LanguageService.Text((string)Item.Tag))
                    && Category.Name == "Projects_工作", Language.Code + " category folder commands localize while category names stay unchanged");
                foreach (var Section in new[] { this.DirectoryTreeSection, this.FavoritesSection, CustomSection })
                    Check(HeaderButton(Section, "ExpandFoldersButton").ToolTip?.ToString() == LanguageService.Text("ExpandCategoryFoldersTooltip")
                        && HeaderButton(Section, "CollapseFoldersButton").ToolTip?.ToString() == LanguageService.Text("CollapseCategoryFoldersTooltip")
                        && System.Windows.Automation.AutomationProperties.GetName(HeaderButton(Section, "ExpandFoldersButton")) == LanguageService.Text("ExpandCategoryFolders")
                        && System.Windows.Automation.AutomationProperties.GetName(HeaderButton(Section, "CollapseFoldersButton")) == LanguageService.Text("CollapseCategoryFolders"),
                        Language.Code + " category header button tooltips and accessible names update with the language");
            }
            this.TryAddSidebarCategory("Empty");
            await Settle();
            var EmptyMenu = await OpenMenu(this.SidebarSectionElement(this.CustomCategories[^1])!);
            Check(EmptyMenu.Items.OfType<MenuItem>().Where(Item => Item.Tag is "ExpandCategoryFolders" or "CollapseCategoryFolders").All(Item => !Item.IsEnabled),
                "An empty category disables both folder expansion actions");
            EmptyMenu.IsOpen = false;
            var EmptySection = this.SidebarSectionElement(this.CustomCategories[^1])!;
            InvokeButton(HeaderButton(EmptySection, "ExpandFoldersButton"));
            await this.TreeExpansionTask;
            InvokeButton(HeaderButton(EmptySection, "CollapseFoldersButton"));
            Check(EmptySection.IsExpanded && this.CategoryFolderRoots(EmptySection).Length == 0,
                "Header folder actions safely preserve an expanded empty category");
            var SettingsFile = Path.Combine(Fixture, "Settings.json");
            Check(this.SaveSettings(SettingsFile) && !File.ReadAllText(SettingsFile).Contains("\"Node\"")
                && !File.ReadAllText(SettingsFile).Contains("\"Children\""), "Category settings save only shortcut paths and do not serialize loaded directory trees");
        }
        finally
        {
            ++this.TreeExpansionVersion;
            await this.TreeExpansionTask;
            if (ActiveMenu is not null) ActiveMenu.IsOpen = false;
            foreach (var Entry in MenuStayOpen) Entry.Key.StaysOpen = Entry.Value;
            this.Settings.ShowHidden = InitialHidden;
            this.ChangeLanguage(InitialLanguage, Persist: false);
            this.FavoritesList.ItemsSource = InitialFavoritesSource;
            this.SyncingTree = true;
            try { this.Roots.Clear(); foreach (var Root in InitialRoots) this.Roots.Add(Root); }
            finally { this.SyncingTree = false; }
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.RestoreSidebarCategories(InitialCategories, InitialOrder);
            this.FavoritesSection.IsExpanded = InitialFavorites;
            this.DirectoryTreeSection.IsExpanded = InitialTree;
            this.SidebarScroll.ScrollToTop();
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != FixtureId
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned category expansion fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
