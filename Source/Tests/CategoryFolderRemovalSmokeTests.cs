using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunCategoryFolderRemovalSmokeChecksAsync(List<string> Passed)
    {
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        var InitialPath = this.CurrentPath;
        var InitialFavorites = this.FavoritesSection.IsExpanded;
        var InitialTree = this.DirectoryTreeSection.IsExpanded;
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirDragRemoval-" + FixtureId);
        var FolderPath = Directory.CreateDirectory(Path.Combine(Fixture, "Folder 中文 With Spaces")).FullName;
        var Marker = Path.Combine(Fixture, ".CDirDragRemovalOwner");
        File.WriteAllText(Marker, FixtureId);
        var Entry = new FileEntry { FullPath = FolderPath, IsDirectory = true };
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Category-folder drag removal UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        try
        {
            this.RestoreSidebarCategories([]);
            await this.NavigateAsync(FolderPath);
            this.FavoritesSection.IsExpanded = false;
            this.DirectoryTreeSection.IsExpanded = false;
            this.TryAddSidebarCategory("Remove From Here");
            this.TryAddSidebarCategory("Keep Here");
            var Work = this.CustomCategories[0];
            var Other = this.CustomCategories[1];
            this.AddFoldersToCategory(Work, [Entry]);
            this.AddFoldersToCategory(Other, [Entry]);
            this.SidebarScroll.ScrollToTop();
            await Settle();
            var Folder = Work.Folders.Single();
            var Payload = new CategoryFolderDragPayload(this, Work, Folder);
            this.ActiveCategoryFolderDrag = Payload;
            var MainPanelPoint = this.FileGrid.TranslatePoint(new Point(20, 20), this.SidebarPanel);
            var HeaderPoint = new Point(20, -20);
            var StatusPoint = new Point(20, this.SidebarPanel.ActualHeight + 1);
            var OutsideWindowPoint = new Point(-this.ActualWidth, -this.ActualHeight);
            var OutsidePoints = new[] { MainPanelPoint, HeaderPoint, StatusPoint, OutsideWindowPoint };
            foreach (var Point in OutsidePoints)
            {
                var Moving = this.QueryCategoryFolderDrag(false, DragDropKeyStates.LeftMouseButton, Point);
                Check(!Moving.RemoveOnRelease && Moving.Action == DragAction.Continue
                    && Work.Folders.Contains(Folder), "Moving outside the sidebar keeps the shortcut until the mouse is released");
            }
            var Escaped = this.QueryCategoryFolderDrag(true, DragDropKeyStates.None, MainPanelPoint);
            Check(!Escaped.RemoveOnRelease && Escaped.Action == DragAction.Cancel
                && !this.FinishCategoryFolderDrag(Payload, false) && Work.Folders.Contains(Folder),
                "Escape cancels an outside drag without removing the shortcut");
            var RightClick = this.QueryCategoryFolderDrag(false, DragDropKeyStates.LeftMouseButton | DragDropKeyStates.RightMouseButton, MainPanelPoint);
            Check(!RightClick.RemoveOnRelease && RightClick.Action == DragAction.Cancel,
                "Mouse-button cancellation does not schedule shortcut removal");
            var UnknownPosition = this.QueryCategoryFolderDrag(false, DragDropKeyStates.None, null);
            Check(!UnknownPosition.RemoveOnRelease && UnknownPosition.Action == DragAction.Cancel
                && Work.Folders.Contains(Folder), "An unavailable cursor position leaves the shortcut unchanged");
            foreach (var Point in new[] { new Point(0, 0), new Point(this.SidebarPanel.ActualWidth / 2, this.SidebarPanel.ActualHeight / 2),
                new Point(this.SidebarPanel.ActualWidth, this.SidebarPanel.ActualHeight) })
            {
                var ReleasedInside = this.QueryCategoryFolderDrag(false, DragDropKeyStates.None, Point);
                Check(!ReleasedInside.RemoveOnRelease && ReleasedInside.Action == DragAction.Drop
                    && Work.Folders.Contains(Folder), "Releasing anywhere within the sidebar retains normal drop and reorder behavior");
            }
            this.ActiveCategoryFolderDrag = null;
            Check(!this.FinishCategoryFolderDrag(Payload, true) && Work.Folders.Contains(Folder),
                "Inactive or cancelled drag sessions cannot remove category shortcuts");
            this.ActiveCategoryFolderDrag = Payload;
            Check(!this.FinishCategoryFolderDrag(new CategoryFolderDragPayload(this, Work, Folder), true)
                && Work.Folders.Contains(Folder), "An unrelated drag payload cannot remove the active shortcut");

            foreach (var Point in OutsidePoints)
            {
                this.AddFoldersToCategory(Work, [Entry]);
                Folder = Work.Folders.Single();
                Payload = new CategoryFolderDragPayload(this, Work, Folder);
                this.ActiveCategoryFolderDrag = Payload;
                var ReleasedOutside = this.QueryCategoryFolderDrag(false, DragDropKeyStates.None, Point);
                var RemoveOnRelease = ReleasedOutside.RemoveOnRelease;
                Check(RemoveOnRelease && ReleasedOutside.Action == DragAction.Cancel && Work.Folders.Contains(Folder),
                    "An outside release cancels the native drop and defers removal until the drag loop exits");
                Check(this.FinishCategoryFolderDrag(Payload, RemoveOnRelease) && Work.Folders.Count == 0
                    && Other.Folders.Count == 1 && Directory.Exists(FolderPath) && SamePath(this.CurrentPath, FolderPath),
                    "An outside release removes only the source category shortcut and preserves the directory and current location");
                Check(!this.FinishCategoryFolderDrag(Payload, RemoveOnRelease), "Finishing the same drag twice cannot remove any other shortcut");
                this.ActiveCategoryFolderDrag = null;
            }
            await Settle();
            var EmptyText = ((StackPanel)this.SidebarSectionElement(Work)!.Content).Children.OfType<TextBlock>().Single();
            Check(EmptyText.IsVisible, "Removing a category's last shortcut restores its empty-state hint");
            var SettingsFile = Path.Combine(Fixture, "Settings.json");
            Check(this.SaveSettings(SettingsFile), "Drag-out removal saves to owned settings");
            var Saved = ReadSettings(SettingsFile);
            this.RestoreSidebarCategories(Saved.CustomCategories, Saved.SidebarOrder);
            Check(this.CustomCategories[0].Folders.Count == 0 && this.CustomCategories[1].Folders.Count == 1,
                "Restart restoration retains drag-out removal and the same folder in other categories");
        }
        finally
        {
            this.ActiveCategoryFolderDrag = null;
            this.CategoryFolderDragCandidate = null;
            this.ClearSidebarDropFeedback();
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.RestoreSidebarCategories(InitialCategories, InitialOrder);
            this.FavoritesSection.IsExpanded = InitialFavorites;
            this.DirectoryTreeSection.IsExpanded = InitialTree;
            this.SidebarScroll.ScrollToTop();
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != FixtureId
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned drag-removal fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
