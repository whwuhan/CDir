using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunSidebarCopySmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        var InitialFavorites = this.FavoritesList.ItemsSource;
        var InitialFavoritesExpanded = this.FavoritesSection.IsExpanded;
        var ClipboardWriter = this.WriteClipboardText;
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Sidebar copy 中文 " + Guid.NewGuid().ToString("N"))).FullName;
        var ChildPath = Directory.CreateDirectory(Path.Combine(Fixture, "Child With Spaces")).FullName;
        var Copied = new List<string>();
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Sidebar copy UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        bool Copy(UIElement Target)
        {
            var Input = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control), PresentationSource.FromVisual(Target), 0, Key.C)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Target.RaiseEvent(Input);
            return Input.Handled;
        }
        try
        {
            this.WriteClipboardText = Copied.Add;
            await this.NavigateAsync(Fixture);
            this.SelectPath(ChildPath);
            var Version = this.NavigationVersion;
            Check(Copy(this.FolderTree) && Copied.SequenceEqual(new[] { Fixture }) && this.NavigationVersion == Version,
                "Ctrl+C in My Computer copies the selected directory's absolute path without copying the file-list selection or navigating");
            await this.NavigateAsync(ChildPath);
            Check(Copy(this.FolderTree) && Copied[^1] == ChildPath,
                "Ctrl+C copies a nested directory path with Unicode and spaces intact");

            this.RestoreSidebarCategories([]);
            this.TryAddSidebarCategory("Copy paths");
            var Category = this.CustomCategories.Single();
            this.AddFoldersToCategory(Category, [new FileEntry { FullPath = Fixture, IsDirectory = true }]);
            await Settle();
            var Section = this.SidebarSectionElement(Category)!;
            var List = FindVisualChild<ItemsControl>(Section)!;
            var Container = List.ItemContainerGenerator.ContainerFromIndex(0);
            var Button = FindVisualChild<Button>(Container)!;
            Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await this.SidebarClickTask;
            await Settle();
            Check(Copy(Button) && Copied[^1] == Fixture,
                "Ctrl+C on a selected category root copies that root's absolute path");
            Category.Folders[0].Node.IsExpanded = false;
            await this.OpenCategoryFolderAsync(Button, Navigate: false);
            var Child = Category.Folders[0].Node.Children.Single(Node => SamePath(Node.FullPath, ChildPath));
            await this.NavigateFromTreeAsync(Child, this.TreeKeyForSection(Section));
            await Settle();
            var Tree = FindVisualChild<TreeView>(Container)!;
            var Item = (TreeViewItem)Tree.ItemContainerGenerator.ContainerFromItem(Child);
            Check(Copy(Item) && Copied[^1] == ChildPath,
                "Ctrl+C on a nested category tree item copies the selected child rather than its category root");

            var Favorite = new FavoriteLocation("Copy fixture", Fixture);
            this.FavoritesList.ItemsSource = new[] { Favorite };
            this.FavoritesSection.IsExpanded = true;
            await Settle();
            var FavoriteButton = FindVisualChild<Button>(this.FavoritesList.ItemContainerGenerator.ContainerFromIndex(0))!;
            FavoriteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await this.SidebarClickTask;
            await Settle();
            Check(Copy(FavoriteButton) && Copied[^1] == Fixture,
                "Ctrl+C on a selected Quick Access root copies its absolute directory path");

            this.SelectPath(ChildPath);
            var Count = Copied.Count;
            Check(Copy(Button) && Copied.Count == Count,
                "An unselected sidebar node never copies a stale selection from the file list");
            Check(!Copy(this.SearchBox) && Copied.Count == Count,
                "Ctrl+C in an editable field remains native text copy");
            this.WriteClipboardText = _ => throw new System.Runtime.InteropServices.ExternalException("Owned clipboard failure");
            Check(Copy(FavoriteButton) && !string.IsNullOrEmpty(this.StatusText.Text),
                "A clipboard failure is reported without breaking the sidebar shortcut route");
        }
        finally
        {
            this.WriteClipboardText = ClipboardWriter;
            this.FavoritesList.ItemsSource = InitialFavorites;
            this.FavoritesSection.IsExpanded = InitialFavoritesExpanded;
            this.RestoreSidebarCategories(InitialCategories, InitialOrder);
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned sidebar copy fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
