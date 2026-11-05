using CDir.Models;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunSidebarFolderAddSmokeChecksAsync(List<string> Passed)
    {
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        var InitialFavoritesSource = this.FavoritesList.ItemsSource;
        var InitialFavorites = this.FavoritesSection.IsExpanded;
        var InitialTree = this.DirectoryTreeSection.IsExpanded;
        var InitialPath = this.CurrentPath;
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirSidebarAdd-" + FixtureId);
        var Alpha = Directory.CreateDirectory(Path.Combine(Fixture, "Alpha 中文")).FullName;
        var Beta = Directory.CreateDirectory(Path.Combine(Fixture, "Beta With Spaces")).FullName;
        var Marker = Path.Combine(Fixture, ".CDirSidebarAddOwner");
        File.WriteAllText(Marker, FixtureId);
        var Root = new FolderNode("Drag Fixture", Fixture) { IsLoaded = true };
        Root.Children.Clear();
        var AlphaNode = new FolderNode("Alpha 中文", Alpha);
        var BetaNode = new FolderNode("Beta With Spaces", Beta);
        Root.Children.Add(AlphaNode);
        Root.Children.Add(BetaNode);
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Sidebar folder-add UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        Point Target(object Category) => FindVisualChild<ToggleButton>(this.SidebarSectionElement(Category)!)!
            .TranslatePoint(new Point(8, 8), this.SidebarPanel);
        IDataObject Data(SidebarFolderAddPayload Payload)
        {
            var Result = new DataObject();
            Result.SetData(SidebarFolderAddDragFormat, Payload);
            return Result;
        }
        try
        {
            this.RestoreSidebarCategories([]);
            this.TryAddSidebarCategory("Tree Favorites");
            this.TryAddSidebarCategory("Another Category");
            var Work = this.CustomCategories[0];
            var Other = this.CustomCategories[1];
            this.TryMoveSidebarSection(Work, 0);
            this.TryMoveSidebarSection(Other, 1);
            this.FavoritesList.ItemsSource = new[] { new FavoriteLocation("Quick Beta", Beta) };
            this.FavoritesSection.IsExpanded = true;
            this.DirectoryTreeSection.IsExpanded = true;
            this.Roots.Add(Root);
            Root.IsExpanded = true;
            this.SidebarScroll.ScrollToTop();
            await Settle();
            var RootItem = (TreeViewItem)this.FolderTree.ItemContainerGenerator.ContainerFromItem(Root);
            var AlphaItem = (TreeViewItem)RootItem.ItemContainerGenerator.ContainerFromItem(AlphaNode);
            var AlphaHeader = (FrameworkElement)AlphaItem.Template.FindName("PART_Header", AlphaItem);
            var Arrow = (ToggleButton)RootItem.Template.FindName("Expander", RootItem);
            var History = this.History.ToArray();
            Check(this.PrepareSidebarDragSource(AlphaHeader, 1, new Point(20, 20))
                && this.SidebarFolderAddDragCandidate is { } && this.SidebarDragCandidate is null && this.CategoryFolderDragCandidate is null
                && SamePath(this.CurrentPath, InitialPath) && !AlphaNode.IsSelected,
                "Pressing a tree folder prepares shortcut addition without navigating or starting a category reorder");
            var TreeSource = this.SidebarFolderAddDragCandidate!;
            Check(SamePath(TreeSource.Folder.FullPath, Alpha) && ReferenceEquals(TreeSource.Element, AlphaItem),
                "The directory-tree drag uses the actual pressed folder");
            Check(!this.PrepareSidebarDragSource(Arrow, 1, new Point()) && this.SidebarFolderAddDragCandidate is null
                && this.SidebarDragCandidate is null, "Tree expand arrows do not initiate folder or category drags");
            ((IToggleProvider)new ToggleButtonAutomationPeer(Arrow).GetPattern(PatternInterface.Toggle)).Toggle();
            await Settle();
            Check(!Root.IsExpanded, "Tree expansion arrows keep their ordinary collapse behavior");
            ((IToggleProvider)new ToggleButtonAutomationPeer(Arrow).GetPattern(PatternInterface.Toggle)).Toggle();
            await Settle();
            Check(Root.IsExpanded && this.PrepareSidebarDragSource(AlphaHeader, 2, new Point())
                && this.SidebarFolderAddDragCandidate is not null, "All ordinary tree presses use the same drag preparation regardless of click count");
            Check(this.SidebarFolderAddFromSource(new TreeViewItem { DataContext = AlphaNode }, 1) is null
                && this.SidebarFolderAddFromSource(FindVisualChild<ToggleButton>(this.DirectoryTreeSection), 1) is null,
                "Unrelated trees and section headers cannot masquerade as directory-tree folder sources");

            var TreePayload = new SidebarFolderAddPayload(this, TreeSource.Folder);
            var TreeData = Data(TreePayload);
            Check(this.SidebarFolderAddition(TreeData) is null && !this.CompleteSidebarDrop(TreeData, Target(Work)),
                "Inactive tree-folder drag data cannot add a shortcut");
            this.ActiveSidebarFolderAddDrag = TreePayload;
            var Forged = Data(new SidebarFolderAddPayload(this, TreeSource.Folder));
            Check(this.SidebarFolderAddition(Forged) is null, "Forged sidebar drag sessions are rejected");
            Check(this.UpdateSidebarDragFeedback(TreeData, Target(Work)) == DragDropEffects.Link
                && this.SidebarCategoryDropHighlight.IsVisible && this.CompleteSidebarDrop(TreeData, Target(Work))
                && Work.Folders.Count == 1 && SamePath(Work.Folders[0].FullPath, Alpha),
                "Dragging a My Computer folder to a category highlights the target and adds a shortcut");
            Check(this.UpdateSidebarDragFeedback(TreeData, Target(Work)) == DragDropEffects.None
                && !this.CompleteSidebarDrop(TreeData, Target(Work)) && Work.Folders.Count == 1,
                "Repeated directory-tree drops do not duplicate category entries");
            Check(!this.CompleteSidebarDrop(TreeData, Target(this.FavoritesSection))
                && !this.CompleteSidebarDrop(TreeData, Target(this.DirectoryTreeSection))
                && !this.CompleteSidebarDrop(TreeData, new Point(-1, 10)),
                "Built-in sections and positions outside the sidebar reject tree shortcut additions");
            this.ActiveSidebarFolderAddDrag = null;
            Check(SamePath(this.CurrentPath, InitialPath) && this.History.SequenceEqual(History) && Root.Children.Contains(AlphaNode)
                && Directory.Exists(Alpha), "Directory-tree additions preserve the source node current location history and actual folder");

            var QuickContainer = this.FavoritesList.ItemContainerGenerator.ContainerFromIndex(0);
            var QuickButton = FindVisualChild<Button>(QuickContainer)!;
            var QuickSource = this.SidebarFolderAddFromSource(FindVisualChild<TextBlock>(QuickButton), 1)!;
            Check(QuickSource is not null && SamePath(QuickSource.Folder.FullPath, Beta)
                && !this.PrepareSidebarDragSource(QuickButton, 1, new Point()), "Quick Access folders prepare additions while retaining ordinary button clicks");
            var QuickPayload = new SidebarFolderAddPayload(this, QuickSource!.Folder);
            var QuickData = Data(QuickPayload);
            this.ActiveSidebarFolderAddDrag = QuickPayload;
            Check(this.UpdateSidebarDragFeedback(QuickData, Target(Work)) == DragDropEffects.Link && this.CompleteSidebarDrop(QuickData, Target(Work))
                && Work.Folders.Select(Folder => Folder.FullPath).SequenceEqual(new[] { Alpha, Beta })
                && this.FavoritesList.Items.Count == 1, "Quick Access folders append to a custom category and retain their original shortcut");
            this.ActiveSidebarFolderAddDrag = null;
            Check(!this.CompleteSidebarDrop(QuickData, Target(Other)) && Other.Folders.Count == 0,
                "Cancelling a Quick Access drag leaves categories unchanged");

            await Settle();
            this.SidebarSectionElement(Other)!.IsExpanded = false;
            await Settle();
            var SourceFolder = Work.Folders[0];
            var CategoryPayload = new CategoryFolderDragPayload(this, Work, SourceFolder);
            var CategoryData = new DataObject();
            CategoryData.SetData(CategoryFolderDragFormat, CategoryPayload);
            this.ActiveCategoryFolderDrag = CategoryPayload;
            Check(this.UpdateSidebarDragFeedback(CategoryData, Target(Other)) == DragDropEffects.Link
                && this.CompleteSidebarDrop(CategoryData, Target(Other)) && Work.Folders.Count == 2
                && ReferenceEquals(Work.Folders[0], SourceFolder) && Other.Folders.Count == 1,
                "Dragging between custom categories adds the shortcut and preserves the source membership and order");
            await Settle();
            Check(Other.IsExpanded && this.SidebarSectionElement(Other)!.IsExpanded
                && this.UpdateSidebarDragFeedback(CategoryData, Target(Other)) == DragDropEffects.None
                && !this.CompleteSidebarDrop(CategoryData, Target(Other)) && Other.Folders.Count == 1,
                "Cross-category additions expand a collapsed target and suppress duplicate drops");
            var WorkList = FindVisualChild<ItemsControl>(this.SidebarSectionElement(Work)!)!;
            var LastRow = (FrameworkElement)WorkList.ItemContainerGenerator.ContainerFromIndex(1);
            var AfterLast = LastRow.TranslatePoint(new Point(8, LastRow.ActualHeight - 1), this.SidebarPanel);
            Check(this.UpdateSidebarDragFeedback(CategoryData, AfterLast) == DragDropEffects.Move
                && this.CompleteSidebarDrop(CategoryData, AfterLast) && ReferenceEquals(Work.Folders[^1], SourceFolder),
                "The same drag source continues to reorder within its original category");
            Check(!this.FinishCategoryFolderDrag(CategoryPayload, false) && Work.Folders.Count == 2
                && Other.Folders.Count == 1, "Finishing an in-sidebar category copy retains both memberships");
            this.ActiveCategoryFolderDrag = null;
            this.ActiveSidebarFolderAddDrag = TreePayload;
            this.OperationBusy = true;
            try { Check(this.SidebarFolderAddFromSource(AlphaHeader, 1) is null && this.SidebarFolderAddition(TreeData) is null,
                "Busy operations block new sidebar folder-add drags"); }
            finally { this.OperationBusy = false; }
            this.ActiveSidebarFolderAddDrag = null;

            await this.CompleteSidebarFolderClick(TreeSource, AlphaHeader);
            var Deadline = DateTime.UtcNow.AddSeconds(10);
            while ((this.Loading || !SamePath(this.CurrentPath, Alpha)) && DateTime.UtcNow < Deadline) await Task.Delay(20);
            Check(!this.Loading && SamePath(this.CurrentPath, Alpha), "Releasing a tree folder without dragging still opens the directory");
            var SettingsFile = Path.Combine(Fixture, "Settings.json");
            Check(this.SaveSettings(SettingsFile), "Sidebar drag additions save to owned settings");
            var Saved = ReadSettings(SettingsFile);
            this.RestoreSidebarCategories(Saved.CustomCategories, Saved.SidebarOrder);
            Check(this.CustomCategories[0].Folders.Select(Folder => Folder.FullPath).SequenceEqual(new[] { Beta, Alpha })
                && this.CustomCategories[1].Folders.Single().FullPath == Alpha && Directory.Exists(Alpha) && Directory.Exists(Beta),
                "Restart restoration preserves tree Quick Access and cross-category additions without moving directories");
        }
        finally
        {
            this.ActiveSidebarFolderAddDrag = null;
            this.SidebarFolderAddDragCandidate = null;
            this.ActiveCategoryFolderDrag = null;
            this.SidebarDragCandidate = null;
            this.CategoryFolderDragCandidate = null;
            this.ClearSidebarDropFeedback();
            this.Roots.Remove(Root);
            this.FavoritesList.ItemsSource = InitialFavoritesSource;
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.RestoreSidebarCategories(InitialCategories, InitialOrder);
            this.FavoritesSection.IsExpanded = InitialFavorites;
            this.DirectoryTreeSection.IsExpanded = InitialTree;
            this.SidebarScroll.ScrollToTop();
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != FixtureId
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned sidebar-add fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
