using CDir.UI.Dialogs;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private readonly ObservableCollection<SidebarCategory> CustomCategories = [];
    private readonly ObservableCollection<object> SidebarSections = [];
    private GridLength ExpandedSidebarWidth = new(238);
    private async void RecycleBinClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (!this.OperationBusy && !this.Loading) await this.NavigateAsync(CDir.Services.ShellLocations.RecycleBin, TreeKey: "quick-access");
    }

    private void ToggleSidebarClick(object Sender, RoutedEventArgs E)
    {
        this.Settings.ShowSidebar = !this.Settings.ShowSidebar;
        this.ApplySidebarVisibility();
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }

    private void ApplySidebarVisibility()
    {
        var Visible = this.Settings.ShowSidebar;
        if (!Visible && this.SidebarColumn.Width.Value > 0)
            this.ExpandedSidebarWidth = this.SidebarColumn.Width;
        this.SidebarPanel.Visibility = Visible ? Visibility.Visible : Visibility.Collapsed;
        this.SidebarSplitter.Visibility = Visible ? Visibility.Visible : Visibility.Collapsed;
        this.SidebarToggleButton.Tag = Visible ? "Visible" : "Hidden";
        this.SidebarColumn.MinWidth = Visible ? 190 : 0;
        this.SidebarColumn.Width = Visible ? this.ExpandedSidebarWidth : new GridLength(0);
        this.UpdateSidebarMaximumWidth();
    }

    private void FolderTreeMouseWheel(object Sender, MouseWheelEventArgs E)
    {
        // The tree's horizontal ScrollViewer must not consume vertical wheel input.
        E.Handled = true;
        this.SidebarScroll.RaiseEvent(new MouseWheelEventArgs(E.MouseDevice, E.Timestamp, E.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent
        });
    }

    private bool CategoryLayoutUpdateQueued;
    private void CategoryScrollChanged(object Sender, ScrollChangedEventArgs E)
    {
        if (!ReferenceEquals(Sender, E.OriginalSource) || this.CategoryLayoutUpdateQueued) return;
        // Path toggles and selection can change the extent and clamp the offset
        // during layout. Copy the final row positions into the pinned overlay.
        this.CategoryLayoutUpdateQueued = true;
        this.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
        {
            this.CategoryLayoutUpdateQueued = false;
            this.UpdateStickySidebar();
        });
    }

    private void ContentPanelsSizeChanged(object Sender, SizeChangedEventArgs E)
        => this.UpdateSidebarMaximumWidth();

    private void UpdateSidebarMaximumWidth()
    {
        if (!this.Settings.ShowSidebar || this.ContentPanels.ActualWidth <= 0) return;
        // A fixed sidebar column otherwise keeps its width when the window shrinks.
        var Maximum = Math.Max(this.SidebarColumn.MinWidth,
            this.ContentPanels.ActualWidth - this.ContentPanels.ColumnDefinitions[1].ActualWidth - this.MainPanelColumn.MinWidth);
        if (this.HostWindow.Panes.Count > 1)
            Maximum = Math.Min(Maximum, Math.Max(this.SidebarColumn.MinWidth, this.ContentPanels.ActualWidth * .6));
        this.SidebarColumn.MaxWidth = Maximum;
        if (this.SidebarColumn.Width.IsAbsolute && this.SidebarColumn.Width.Value > Maximum)
            this.SidebarColumn.Width = new GridLength(Maximum);
    }

    private void InitializeSidebarCategories()
    {
        this.SidebarSectionsList.Items.Clear();
        this.SidebarSectionsList.ItemsSource = this.SidebarSections;
        this.RestoreSidebarCategories(this.Settings.CustomCategories, this.Settings.SidebarOrder);
    }

    private void RestoreSidebarCategories(IEnumerable<SidebarCategory>? Categories, IEnumerable<string>? Order = null)
    {
        var Restored = (Categories ?? []).Where(Category => Category is not null && !string.IsNullOrWhiteSpace(Category.Name))
            .Select(Category => Category.Clone())
            .DistinctBy(Category => Category.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        this.CustomCategories.Clear();
        var Ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var Category in Restored)
        {
            if (string.IsNullOrWhiteSpace(Category.Id) || !Ids.Add(Category.Id))
            {
                Category.Id = Guid.NewGuid().ToString("N");
                Ids.Add(Category.Id);
            }
            this.CustomCategories.Add(Category);
        }
        var AllSections = new object[] { this.FavoritesSection, this.DirectoryTreeSection }.Concat(this.CustomCategories).ToArray();
        var ByKey = AllSections.ToDictionary(this.SidebarSectionKey, StringComparer.Ordinal);
        var Keys = (Order ?? []).Where(Key => Key is not null && ByKey.ContainsKey(Key)).Distinct(StringComparer.Ordinal).ToList();
        Keys.AddRange(AllSections.Select(this.SidebarSectionKey).Where(Key => !Keys.Contains(Key, StringComparer.Ordinal)));
        var Ordered = Keys.Select(Key => ByKey[Key]).ToArray();
        this.ChangeSidebarLayout(() =>
        {
            foreach (var Old in this.SidebarSections.Where(Section => !Ordered.Contains(Section)).ToArray()) this.SidebarSections.Remove(Old);
            for (var Index = 0; Index < Ordered.Length; Index++)
            {
                var OldIndex = this.SidebarSections.IndexOf(Ordered[Index]);
                if (OldIndex < 0) this.SidebarSections.Insert(Index, Ordered[Index]);
                else if (OldIndex != Index) this.SidebarSections.Move(OldIndex, Index);
            }
        });
        this.SyncCustomCategoryOrder();
    }

    private bool TryAddSidebarCategory(string Name)
    {
        Name = Name.Trim();
        if (!this.IsSidebarCategoryNameAvailable(Name)) return false;
        var Category = new SidebarCategory { Name = Name };
        this.CustomCategories.Add(Category);
        this.SidebarSections.Add(Category);
        this.SaveSidebarCategories();
        return true;
    }

    private bool IsSidebarCategoryNameAvailable(string Name, SidebarCategory? Excluded = null)
    {
        if (Name.Length == 0) return false;
        if (this.CustomCategories.Any(Category => !ReferenceEquals(Category, Excluded) && string.Equals(Category.Name, Name, StringComparison.OrdinalIgnoreCase))
            || new[] { LanguageService.Text("Favorites"), LanguageService.Text("DirectoryTree"), "Quick Access", "My Computer", "Folder Tree" }
                .Contains(Name, StringComparer.OrdinalIgnoreCase))
        {
            this.ShowNotice(LanguageService.Text("CategoryNameUnavailable"), true);
            return false;
        }
        return true;
    }

    private bool TryRenameSidebarCategory(SidebarCategory Category, string Name)
    {
        Name = Name.Trim();
        if (!this.CustomCategories.Contains(Category) || !this.IsSidebarCategoryNameAvailable(Name, Category)) return false;
        Category.Name = Name;
        this.SaveSidebarCategories();
        return true;
    }

    private void SaveSidebarCategories()
    {
        if (!this.SaveSettings()) this.ShowNotice(LanguageService.Text("SettingsSaveError"), true);
    }

    private void AddCategoryClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        CloseSidebarMenu(Sender);
        this.PromptNewSidebarCategory();
    }

    private SidebarCategory? PromptNewSidebarCategory()
    {
        var BaseName = LanguageService.Text("NewCategory");
        var Name = BaseName;
        for (var Index = 2; this.CustomCategories.Any(Category => string.Equals(Category.Name, Name, StringComparison.OrdinalIgnoreCase)); Index++)
            Name = BaseName + " " + Index;
        var Dialog = new NameDialog(this.HostWindow, LanguageService.Text("AddCategory"), Name);
        return Dialog.ShowDialog() == true && this.TryAddSidebarCategory(Dialog.EnteredName) ? this.CustomCategories[^1] : null;
    }

    private void DeleteCategoryClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        var Category = (Sender as FrameworkElement)?.DataContext as SidebarCategory;
        CloseSidebarMenu(Sender);
        // Only custom models can be removed; built-in sections are never in this collection.
        if (Category is null || !this.CustomCategories.Contains(Category)) return;
        if (new CategoryDeleteDialog(this.HostWindow, Category.Name).ShowDialog() != true) return;
        if (this.CustomCategories.Remove(Category))
        {
            this.SidebarSections.Remove(Category);
            this.SaveSidebarCategories();
        }
    }

    private void RenameCategoryClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        var Category = (Sender as FrameworkElement)?.DataContext as SidebarCategory;
        CloseSidebarMenu(Sender);
        if (Category is null || !this.CustomCategories.Contains(Category)) return;
        var Dialog = new NameDialog(this.HostWindow, LanguageService.Text("RenameCategory"), Category.Name);
        if (Dialog.ShowDialog() == true) this.TryRenameSidebarCategory(Category, Dialog.EnteredName);
    }

    private static void CloseSidebarMenu(object Sender)
    {
        if (Sender is MenuItem Item && ItemsControl.ItemsControlFromItemContainer(Item) is ContextMenu Menu) Menu.IsOpen = false;
    }

    private void SidebarContextOpened(object Sender, RoutedEventArgs E)
    {
        if (Sender is ContextMenu Menu) UpdateMenuHeaders(Menu);
    }

    private void CustomCategoryExpansionChanged(object Sender, RoutedEventArgs E)
    {
        if (Sender is not Expander { DataContext: SidebarCategory Category } Section) return;
        Category.IsExpanded = Section.IsExpanded;
        if (this.IsLoaded) this.SaveSidebarCategories();
    }
}
