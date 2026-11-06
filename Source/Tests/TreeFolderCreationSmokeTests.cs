using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunTreeFolderCreationSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        var InitialTreeExpanded = this.DirectoryTreeSection.IsExpanded;
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Tree-" + FixtureId[..8])).FullName;
        var Marker = Path.Combine(Fixture, ".CDirTreeCreateOwner");
        File.WriteAllText(Marker, FixtureId);
        var Current = Directory.CreateDirectory(Path.Combine(Fixture, "Current")).FullName;
        var Parent = Directory.CreateDirectory(Path.Combine(Fixture, "Target 中文 With Spaces")).FullName;
        var Node = new FolderNode("Target 中文 With Spaces", Parent);
        ContextMenu? Menu = null;
        var MenuStayOpen = new Dictionary<ContextMenu, bool>();
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Tree folder-creation UI smoke test failed: " + Name + "; previous=" + Passed.LastOrDefault());
            Passed.Add(Name);
        }
        async Task Settle()
        {
            this.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        async Task<ContextMenu> OpenMenu(FrameworkElement Target)
        {
            if (Menu is not null) { Menu.IsOpen = false; await Settle(); }
            Menu = Target.ContextMenu ?? throw new InvalidOperationException("Tree context menu is missing.");
            MenuStayOpen.TryAdd(Menu, Menu.StaysOpen);
            Menu.StaysOpen = true; // Programmatic UI checks must not capture the user's pointer.
            Menu.PlacementTarget = Target;
            Menu.Placement = PlacementMode.Bottom;
            await Settle();
            Menu.IsOpen = true;
            await Settle();
            Check(Menu.IsOpen && Menu.Items[1] is MenuItem { Tag: "CollapseAllSubfolders" }
                && Menu.Items[2] is Separator && Menu.Items[3] is MenuItem { Tag: "NewFolder" } Action
                && Action.Header?.ToString() == LanguageService.Text("NewFolder"),
                "A directory menu places localized New Folder after a separator below Collapse All Subfolders"
                + $" (open={Menu.IsOpen}, context={Menu.DataContext?.GetType().Name}, target={Target.DataContext?.GetType().Name}, items={string.Join(',', Menu.Items.OfType<MenuItem>().Select(Item => Item.Tag))})");
            var ItemLeft = Menu.Items.OfType<MenuItem>().First().TranslatePoint(new Point(), Menu).X;
            var ItemWidth = Menu.Items.OfType<MenuItem>().Max(Item => Item.ActualWidth);
            foreach (var Separator in Menu.Items.OfType<Separator>())
            {
                var Left = Separator.TranslatePoint(new Point(), Menu).X;
                Check(Math.Abs(Left - ItemLeft - 2) <= 1 && Separator.ActualWidth >= ItemWidth - 5
                    && Separator.Margin.Left == 2 && Separator.Margin.Right == 2,
                    "Directory and category separators span the compact menu without the default Windows icon gutter"
                    + $" (left={Left}, item={ItemLeft}, width={Separator.ActualWidth}/{ItemWidth})");
            }
            if (AppMain.ScreenshotPath is { } Screenshot)
            {
                var Image = new RenderTargetBitmap((int)Math.Ceiling(Menu.ActualWidth), (int)Math.Ceiling(Menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                Image.Render(Menu);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Image));
                using var Output = File.Create(Path.Combine(Path.GetDirectoryName(Screenshot)!,
                    Menu.Items.OfType<MenuItem>().Any(Item => Item.Tag is "RemoveFromCategory") ? "CategoryFolderMenu.png" : "TreeNewFolderMenu.png"));
                Encoder.Save(Output);
            }
            return Menu;
        }
        Task InvokeCreate()
        {
            Menu!.Items.OfType<MenuItem>().Single(Item => Item.Tag is "NewFolder")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            return this.TreeFolderCreationTask;
        }
        try
        {
            File.WriteAllText(Path.Combine(Parent, LanguageService.Text("NewFolder")), "Existing file");
            Directory.CreateDirectory(Path.Combine(Parent, LanguageService.Text("NumberedNewFolder", 2)));
            File.WriteAllText(Path.Combine(Current, "Keep.txt"), "Keep the current selection");
            Check(await this.NavigateAsync(Current), "Tree creation opens a separate owned current directory");
            this.Roots.Add(Node);
            this.DirectoryTreeSection.IsExpanded = true;
            await Settle();
            var TreeItem = (TreeViewItem)this.FolderTree.ItemContainerGenerator.ContainerFromItem(Node);
            var History = this.History.ToArray();
            var Navigation = this.NavigationVersion;
            var Address = this.AddressBox.Text;
            var Selected = this.Entries.Single(Entry => Entry.Name == "Keep.txt");
            this.FileGrid.SelectedItem = Selected;
            await OpenMenu(TreeItem);
            var CreatedName = "Created 中文 With Spaces";
            var ExpectedSuggestion = LanguageService.Text("NumberedNewFolder", 3);
            string? Suggested = null;
            await InvokeCreate();
            Suggested = this.InlineRenameBox?.Text;
            await this.AcceptSmokeInlineRenameAsync(() => Task.CompletedTask, CreatedName);
            var Created = Path.Combine(Parent, CreatedName);
            Check(Suggested == ExpectedSuggestion, "The name suggestion skips both existing files and folders in the clicked directory");
            Check(Directory.Exists(Created) && !Directory.Exists(Path.Combine(Current, CreatedName))
                && Node.IsLoaded && Node.IsExpanded && Node.Children.Any(Child => SamePath(Child.FullPath, Created)),
                "The real tree command creates inside the clicked directory and expands the previously unloaded parent");
            Check(SamePath(this.CurrentPath, Current) && this.AddressBox.Text == Address && this.History.SequenceEqual(History)
                && this.NavigationVersion == Navigation && this.FileGrid.SelectedItems.Contains(Selected),
                "Creating under another tree folder preserves the displayed path history navigation and selection");
            await OpenMenu(TreeItem);
            await this.AcceptSmokeInlineRenameAsync(InvokeCreate, "Cancelled", Confirm: false);
            Check(!Directory.Exists(Path.Combine(Parent, "Cancelled")), "Escape cancels naming and keeps the new tree folder under its default name");
            await OpenMenu(TreeItem);
            await this.AcceptSmokeInlineRenameAsync(InvokeCreate, CreatedName);
            Check(Directory.Exists(Created) && !this.OperationBusy && Node.Children.Count(Child => SamePath(Child.FullPath, Created)) == 1,
                "A conflicting name reports failure without replacing the existing folder or duplicating its tree node");
            this.OperationBusy = true;
            try
            {
                await OpenMenu(TreeItem);
                Check(!Menu!.Items.OfType<MenuItem>().Single(Item => Item.Tag is "NewFolder").IsEnabled,
                    "The tree New Folder action is disabled while another operation is busy");
                await this.NewTreeFolderAsync(Node);
            }
            finally { this.OperationBusy = false; }
            await this.NewTreeFolderAsync(new FolderNode("", ""));
            await this.NewTreeFolderAsync(null);
            Check(!Application.Current.Windows.OfType<CDir.UI.Dialogs.NameDialog>().Any(), "Busy missing and placeholder targets do not open a name dialog");

            this.RestoreSidebarCategories([]);
            this.TryAddSidebarCategory("Creation Favorites");
            var Category = this.CustomCategories.Single();
            this.TryMoveSidebarSection(Category, 0);
            this.AddFoldersToCategory(Category, [new FileEntry { FullPath = Parent, IsDirectory = true }]);
            this.SidebarScroll.ScrollToTop();
            await Settle();
            var List = FindVisualChild<ItemsControl>(this.SidebarSectionElement(Category)!)!;
            var Container = List.ItemContainerGenerator.ContainerFromIndex(0);
            var CategoryButton = FindVisualChild<Button>(Container)!;
            await OpenMenu(CategoryButton);
            await this.AcceptSmokeInlineRenameAsync(InvokeCreate, "From Category");
            var CategoryRoot = Category.Folders.Single().Node;
            Check(Directory.Exists(Path.Combine(Parent, "From Category")) && Category.Folders.Count == 1
                && CategoryRoot.IsExpanded && CategoryRoot.Children.Any(Child => Child.Name == "From Category")
                && SamePath(this.CurrentPath, Current), "Category root commands create inside the real directory without adding category shortcuts or navigating");
            await Settle();
            var ChildTree = FindVisualChild<TreeView>(Container)!;
            var NestedNode = CategoryRoot.Children.Single(Child => Child.Name == "From Category");
            var NestedItem = (TreeViewItem)ChildTree.ItemContainerGenerator.ContainerFromItem(NestedNode);
            await OpenMenu(NestedItem);
            await this.AcceptSmokeInlineRenameAsync(InvokeCreate, "Nested Child");
            Check(Directory.Exists(Path.Combine(Parent, "From Category", "Nested Child"))
                && NestedNode.Children.Any(Child => Child.Name == "Nested Child") && SamePath(this.CurrentPath, Current),
                "Nested category tree folders create children under their own path");

            Check(await this.NavigateAsync(Parent), "Tree creation can also target the current main-panel directory");
            await OpenMenu(TreeItem);
            await this.AcceptSmokeInlineRenameAsync(InvokeCreate, "In Current");
            Check(this.FileGrid.SelectedItem is FileEntry Entry && SamePath(Entry.FullPath, Path.Combine(Parent, "In Current")),
                "Creating in the current directory refreshes the main panel and selects the new folder");
        }
        finally
        {
            if (Menu is not null) Menu.IsOpen = false;
            foreach (var Item in MenuStayOpen) { Item.Key.IsOpen = false; Item.Key.StaysOpen = Item.Value; }
            this.Roots.Remove(Node);
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.RestoreSidebarCategories(InitialCategories, InitialOrder);
            this.DirectoryTreeSection.IsExpanded = InitialTreeExpanded;
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != FixtureId
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned tree-creation fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
