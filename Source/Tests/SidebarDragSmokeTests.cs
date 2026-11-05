using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunSidebarDragSmokeChecksAsync(List<string> Passed)
    {
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        var InitialFavorites = this.FavoritesSection.IsExpanded;
        var InitialTree = this.DirectoryTreeSection.IsExpanded;
        var InitialPath = this.CurrentPath;
        var InitialSelectedNode = this.FolderTree.SelectedItem;
        var SettingsFile = Path.Combine(Path.GetTempPath(), "CDirSidebarOrder-" + Guid.NewGuid().ToString("N") + ".json");
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Sidebar drag UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        ToggleButton Header(object Section) => FindVisualChild<ToggleButton>(this.SidebarSectionElement(Section)!)!;
        Point Above(object Section) => Header(Section).TranslatePoint(new Point(8, 1), this.SidebarPanel);
        string[] Keys() => this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        try
        {
            this.RestoreSidebarCategories([]);
            this.FavoritesSection.IsExpanded = false;
            this.DirectoryTreeSection.IsExpanded = false;
            this.TryAddSidebarCategory("First");
            this.TryAddSidebarCategory("Second");
            var First = this.CustomCategories[0];
            var Second = this.CustomCategories[1];
            this.SidebarScroll.ScrollToTop();
            await Settle();
            Check(ReferenceEquals(this.SidebarSectionFromHeader(Header(this.FavoritesSection)), this.FavoritesSection)
                && ReferenceEquals(this.SidebarSectionFromHeader(Header(this.DirectoryTreeSection)), this.DirectoryTreeSection)
                && ReferenceEquals(this.SidebarSectionFromHeader(Header(First)), First)
                && this.SidebarSectionFromHeader(this.FolderTree) is null && this.SidebarSectionFromHeader(this.FavoritesList) is null,
                "Only category headings start a reorder including both built-in sections");

            var FirstIndex = this.SidebarSections.IndexOf(First);
            Check(this.SidebarInsertionIndex(Above(First).Y) == FirstIndex
                && this.SidebarInsertionIndex(Above(First).Y + Header(First).ActualHeight - 2) == FirstIndex + 1,
                "Upper and lower header halves resolve to before and after insertion positions");
            var Payload = new SidebarDragPayload(this, First);
            var Data = new DataObject();
            Data.SetData(SidebarDragFormat, Payload);
            Check(this.SidebarDraggedSection(Data) is null, "Inactive drag data cannot change sidebar order");
            this.ActiveSidebarDrag = Payload;
            var Forged = new DataObject();
            Forged.SetData(SidebarDragFormat, new SidebarDragPayload(this, First));
            Check(this.SidebarDraggedSection(Forged) is null && this.SidebarDraggedSection(new DataObject(DataFormats.FileDrop, new[] { InitialPath })) is null,
                "Unrelated drag sessions and filesystem drops are rejected");
            this.ShowSidebarDropMarker(0);
            Check(this.SidebarDropMarker.IsVisible && this.SidebarDropMarker.Margin.Top >= 0,
                "Reordering displays an insertion marker in the sidebar");
            Check(this.CompleteSidebarDrop(Data, Above(this.FavoritesSection)) && ReferenceEquals(this.SidebarSections[0], First)
                && this.SidebarDropMarker.Visibility == Visibility.Collapsed,
                "A category drop moves before Quick access and clears the insertion marker");
            this.ActiveSidebarDrag = null;
            await Settle();
            Check(this.TryMoveSidebarSection(this.DirectoryTreeSection, 0), "Folder tree can move to the first position");
            Check(this.TryMoveSidebarSection(this.FavoritesSection, this.SidebarSections.Count), "Quick access can move to the last position");
            Check(this.TryMoveSidebarSection(Second, 1) && this.CustomCategories[0] == Second && this.CustomCategories[1] == First,
                "Custom categories move upward and their saved relative order follows the sidebar");
            await Settle();
            var HeaderPositions = this.SidebarSections.Select(Section => Header(Section).TranslatePoint(new Point(), this.SidebarPanel).Y).ToArray();
            Check(HeaderPositions.Zip(HeaderPositions.Skip(1), (Before, After) => Before < After).All(InOrder => InOrder),
                "Visible category headings follow the reordered collection from top to bottom");
            Check(Keys().SequenceEqual(new[] { "folder-tree", this.SidebarSectionKey(Second), this.SidebarSectionKey(First), "quick-access" })
                && !this.FavoritesSection.IsExpanded && !this.DirectoryTreeSection.IsExpanded && First.IsExpanded
                && SamePath(this.CurrentPath, InitialPath) && ReferenceEquals(this.FolderTree.SelectedItem, InitialSelectedNode),
                "Reordering preserves section expansion navigation and the selected directory node");
            if (AppMain.ScreenshotPath is { } Screenshot)
            {
                this.ShowSidebarDropMarker(1);
                this.UpdateLayout();
                var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(this);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Screenshot)!, "SidebarReordered.png"));
                Encoder.Save(Stream);
                this.SidebarDropMarker.Visibility = Visibility.Collapsed;
            }
            var StableOrder = Keys();
            Check(!this.TryMoveSidebarSection(First, this.SidebarSections.IndexOf(First))
                && !this.TryMoveSidebarSection(First, -1) && !this.TryMoveSidebarSection(new SidebarCategory(), 0)
                && Keys().SequenceEqual(StableOrder), "No-op and invalid moves do not change category order");
            this.ActiveSidebarDrag = Payload;
            Check(!this.CompleteSidebarDrop(Data, new Point(-1, 10)) && Keys().SequenceEqual(StableOrder),
                "Drops outside the sidebar leave the order unchanged");
            this.ActiveSidebarDrag = null;

            this.TryAddSidebarCategory("Third");
            Check(this.SidebarSections[^1] is SidebarCategory { Name: "Third" }, "New categories still append below the manually reordered sections");
            Check(this.SaveSettings(SettingsFile), "Reordered categories save to an owned settings file");
            var Saved = ReadSettings(SettingsFile);
            var SavedOrder = Keys();
            this.RestoreSidebarCategories(Saved.CustomCategories, Saved.SidebarOrder);
            await Settle();
            Check(Keys().SequenceEqual(SavedOrder) && Saved.SidebarOrder!.SequenceEqual(SavedOrder)
                && this.CustomCategories.Select(Category => Category.Name).SequenceEqual(new[] { "Second", "First", "Third" })
                && SamePath(this.CurrentPath, InitialPath) && ReferenceEquals(this.FolderTree.SelectedItem, InitialSelectedNode),
                "Startup restores the full order of custom and built-in categories");
            var Renamed = this.CustomCategories.Single(Category => Category.Name == "First");
            var StableId = Renamed.Id;
            Check(this.TryRenameSidebarCategory(Renamed, "Renamed") && Renamed.Id == StableId && Keys().SequenceEqual(SavedOrder),
                "Renaming a reordered category preserves its stable identity and position");
            await this.RespondSmokeCategoryDeleteDialogAsync(() =>
            {
                this.DeleteCategoryClick(new MenuItem { DataContext = Renamed }, new RoutedEventArgs(MenuItem.ClickEvent));
                return Task.CompletedTask;
            }, Renamed.Name);
            Check(!Keys().Contains("custom:" + StableId) && Keys().Contains("quick-access") && Keys().Contains("folder-tree"),
                "Deleting a reordered custom category retains both built-in sections");

            File.WriteAllText(SettingsFile, "{\"CustomCategories\":[{\"Name\":\"Legacy\"}]}");
            var Legacy = ReadSettings(SettingsFile);
            this.RestoreSidebarCategories(Legacy.CustomCategories, Legacy.SidebarOrder);
            Check(Keys()[0] == "quick-access" && Keys()[1] == "folder-tree" && this.CustomCategories[0].Name == "Legacy"
                && !string.IsNullOrWhiteSpace(this.CustomCategories[0].Id), "Older settings without order or IDs retain the original sidebar layout");
            this.RestoreSidebarCategories(Legacy.CustomCategories, new[] { "unknown", "folder-tree", "folder-tree" });
            Check(this.SidebarSections.Count == 3 && Keys().Distinct().Count() == 3 && Keys().Contains("quick-access"),
                "Incomplete or repeated saved order entries cannot hide or duplicate built-in categories");
        }
        finally
        {
            this.ActiveSidebarDrag = null;
            this.SidebarDragCandidate = null;
            this.SidebarDropMarker.Visibility = Visibility.Collapsed;
            this.RestoreSidebarCategories(InitialCategories, InitialOrder);
            this.FavoritesSection.IsExpanded = InitialFavorites;
            this.DirectoryTreeSection.IsExpanded = InitialTree;
            this.SidebarScroll.ScrollToTop();
            File.Delete(SettingsFile);
        }
    }
}
