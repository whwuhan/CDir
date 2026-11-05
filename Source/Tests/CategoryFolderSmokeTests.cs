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
    private async Task RunCategoryFolderSmokeChecksAsync(List<string> Passed)
    {
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        var InitialPath = this.CurrentPath;
        var InitialLanguage = LanguageService.CurrentLanguage;
        var InitialFavorites = this.FavoritesSection.IsExpanded;
        var InitialTree = this.DirectoryTreeSection.IsExpanded;
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirCategoryFolders-" + FixtureId);
        Directory.CreateDirectory(Fixture);
        var Marker = Path.Combine(Fixture, ".CDirCategoryFoldersOwner");
        File.WriteAllText(Marker, FixtureId);
        var Paths = new[] { "Alpha 中文", "Beta With Spaces", "Gamma" }.Select(Name => Directory.CreateDirectory(Path.Combine(Fixture, Name)).FullName).ToArray();
        var ScrollFixtureCount = Math.Max(24, (int)Math.Ceiling(this.SidebarScroll.ActualHeight / 12) + 12);
        var MorePaths = Enumerable.Range(0, ScrollFixtureCount).Select(Index => Directory.CreateDirectory(Path.Combine(Fixture, $"More-{Index:00}")).FullName).ToArray();
        var SettingsFile = Path.Combine(Fixture, "Settings.json");
        ContextMenu? ActiveMenu = null;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Category folder UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        static void Invoke(MenuItem Item) => Item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ItemsControl List(SidebarCategory Category) => FindVisualChild<ItemsControl>(this.SidebarSectionElement(Category)!)!;
        Button RowButton(SidebarCategory Category, CategoryFolder Folder) => FindVisualChild<Button>(List(Category).ItemContainerGenerator.ContainerFromItem(Folder))!;
        Point RowPoint(SidebarCategory Category, CategoryFolder Folder, bool Below = false)
        {
            var Row = RowButton(Category, Folder);
            return Row.TranslatePoint(new Point(8, Below ? Row.ActualHeight - 1 : 1), this.SidebarPanel);
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
            await this.NavigateAsync(Fixture);
            this.FavoritesSection.IsExpanded = false;
            this.DirectoryTreeSection.IsExpanded = false;
            await this.AcceptSmokeNameDialogAsync(() => { Invoke(this.SidebarPanel.ContextMenu.Items.OfType<MenuItem>().Single()); return Task.CompletedTask; },
                LanguageService.Text("AddCategory"), "Work_工作");
            await Settle();
            var Work = this.CustomCategories.Single();
            Check(this.FileGrid.ContextMenu.Items.Count == 0, "The file list exposes no custom category or application menu entries");
            var SelectedFolders = Paths.Select(Path => this.Entries.Single(Entry => SamePath(Entry.FullPath, Path))).ToArray();
            Check(this.AddFoldersToCategory(Work, SelectedFolders) && Work.Folders.Select(Folder => Folder.FullPath).SequenceEqual(Paths),
                "Category folder storage preserves the supplied folder order");
            Check(!this.AddFoldersToCategory(Work, SelectedFolders) && Work.Folders.Count == 3,
                "The same folder cannot be added twice within one category");
            this.TryAddSidebarCategory("Archive");
            var Archive = this.CustomCategories[^1];
            Check(this.AddFoldersToCategory(Archive, [SelectedFolders[0]]) && Archive.Folders.Count == 1 && Work.Folders.Count == 3,
                "A folder can independently belong to multiple categories");
            await Settle();
            await this.AcceptSmokeNameDialogAsync(() => { Invoke(this.SidebarPanel.ContextMenu.Items.OfType<MenuItem>().Single()); return Task.CompletedTask; },
                LanguageService.Text("AddCategory"), "Cancelled", ConfirmDialog: false);
            Check(this.CustomCategories.Count == 2 && Work.Folders.Count == 3, "Cancelling category creation adds neither a category nor a shortcut");

            var FirstFolder = Work.Folders[0];
            RowButton(Work, FirstFolder).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var Deadline = DateTime.UtcNow.AddSeconds(10);
            while ((this.Loading || !SamePath(this.CurrentPath, Paths[0])) && DateTime.UtcNow < Deadline) await Task.Delay(20);
            Check(!this.Loading && SamePath(this.CurrentPath, Paths[0]), "Clicking a categorized folder opens its original directory");
            this.DirectoryTreeSection.IsExpanded = false;
            await Settle();
            var LastFolder = Work.Folders[^1];
            var Source = FindVisualChild<TextBlock>(RowButton(Work, LastFolder))!;
            var Payload = this.CategoryFolderFromSource(Source)!;
            Check(ReferenceEquals(Payload.Folder, LastFolder) && ReferenceEquals(Payload.Category, Work)
                && this.SidebarSectionFromHeader(Source) is null
                && this.CategoryFolderFromSource(FindVisualChild<ToggleButton>(this.SidebarSectionElement(Work)!)!) is null,
                "Folder rows and category headers start distinct types of drag");
            var Data = new DataObject();
            Data.SetData(CategoryFolderDragFormat, Payload);
            Check(this.DraggedCategoryFolder(Data) is null, "Inactive folder drag data is rejected");
            this.ActiveCategoryFolderDrag = Payload;
            var Forged = new DataObject();
            Forged.SetData(CategoryFolderDragFormat, new CategoryFolderDragPayload(this, Work, LastFolder));
            Check(this.DraggedCategoryFolder(Forged) is null
                && this.UpdateSidebarDragFeedback(Forged, RowPoint(Archive, Archive.Folders[0])) == DragDropEffects.None
                && !this.CompleteSidebarDrop(Data, new Point(-1, 10)), "Forged and out-of-sidebar folder drops are rejected");
            var Target = this.CategoryFolderInsertion(Payload, RowPoint(Work, FirstFolder))!;
            this.ShowCategoryFolderDropMarker(Target);
            Check(Target.Index == 0 && this.SidebarDropMarker.IsVisible,
                "Folder reordering displays a marker at the row insertion point");
            Check(this.CompleteSidebarDrop(Data, RowPoint(Work, FirstFolder)) && ReferenceEquals(Work.Folders[0], LastFolder)
                && this.SidebarDropMarker.Visibility == Visibility.Collapsed && SamePath(this.CurrentPath, Paths[0]),
                "Dropping a folder above a sibling moves it upward without navigating");
            await Settle();
            Check(this.CompleteSidebarDrop(Data, RowPoint(Work, Work.Folders[^1], Below: true))
                && ReferenceEquals(Work.Folders[^1], LastFolder), "Dropping below the last row moves a folder to the end");
            this.ActiveCategoryFolderDrag = null;
            await Settle();
            Check(Work.Folders.Select(Folder => Folder.FullPath).SequenceEqual(Paths)
                && Archive.Folders.Count == 1 && Paths.All(Directory.Exists), "Folder reordering preserves sibling order other categories and actual directories");
            Screenshot("CategoryFolders", this, this.ActualWidth, this.ActualHeight);

            var ExtraEntries = MorePaths.Select(FullPath => new FileEntry { FullPath = FullPath, IsDirectory = true }).ToArray();
            this.AddFoldersToCategory(Work, ExtraEntries);
            await Settle();
            var LongPayload = new CategoryFolderDragPayload(this, Work, FirstFolder);
            var LongData = new DataObject();
            LongData.SetData(CategoryFolderDragFormat, LongPayload);
            this.ActiveCategoryFolderDrag = LongPayload;
            RowButton(Work, Work.Folders[^1]).BringIntoView();
            await Settle();
            Check(this.SidebarScroll.VerticalOffset > 0
                && this.CompleteSidebarDrop(LongData, RowPoint(Work, Work.Folders[^1], Below: true))
                && ReferenceEquals(Work.Folders[^1], FirstFolder), "Folder drops resolve correctly after scrolling a long category");
            this.ActiveCategoryFolderDrag = null;
            var ExpectedPaths = Work.Folders.Select(Folder => Folder.FullPath).ToArray();
            Check(!this.TryMoveCategoryFolder(Work, FirstFolder, -1)
                && !this.TryMoveCategoryFolder(Archive, FirstFolder, 0)
                && !this.TryMoveCategoryFolder(Work, FirstFolder, Work.Folders.Count), "Invalid and no-op folder moves preserve order");
            Work.IsExpanded = false;
            Check(this.SaveSettings(SettingsFile), "Category folders save to an owned settings file");
            var Saved = ReadSettings(SettingsFile);
            Check(Saved.CustomCategories![0].Folders.Select(Folder => Folder.FullPath).SequenceEqual(ExpectedPaths)
                && Saved.CustomCategories[1].Folders.Count == 1 && !Saved.CustomCategories[0].IsExpanded,
                "Settings retain category membership folder order and section expansion");
            this.RestoreSidebarCategories(Saved.CustomCategories, Saved.SidebarOrder);
            await Settle();
            Work = this.CustomCategories[0];
            Archive = this.CustomCategories[1];
            Check(Work.Folders.Select(Folder => Folder.FullPath).SequenceEqual(ExpectedPaths)
                && !this.SidebarSectionElement(Work)!.IsExpanded && SamePath(this.CurrentPath, Paths[0]),
                "Startup restores ordered shortcuts and collapsed categories without changing location");
            this.SidebarSectionElement(Work)!.IsExpanded = true;
            this.SidebarScroll.ScrollToTop();
            await Settle();
            var Removed = Work.Folders[0];
            ActiveMenu = RowButton(Work, Removed).ContextMenu;
            ActiveMenu.PlacementTarget = RowButton(Work, Removed);
            ActiveMenu.Placement = PlacementMode.Bottom;
            ActiveMenu.IsOpen = true;
            await Settle();
            var Remove = ActiveMenu.Items.OfType<MenuItem>().Single(Item => Item.Tag?.ToString() == "RemoveFromCategory");
            Check(Remove.Header?.ToString() == LanguageService.Text("RemoveFromCategory") && ReferenceEquals(Remove.DataContext, Removed),
                "A shortcut context menu offers removal from its own category");
            Invoke(Remove);
            Check(!Work.Folders.Contains(Removed) && Archive.Folders.Count == 1 && Directory.Exists(Removed.FullPath),
                "Removing a shortcut leaves the directory and other categories untouched");

            var Dirty = new SidebarCategory { Name = "Legacy", Folders = [new CategoryFolder { FullPath = Paths[0] },
                new CategoryFolder { FullPath = Paths[0].ToUpperInvariant() + Path.DirectorySeparatorChar },
                new CategoryFolder { FullPath = "relative" }, new CategoryFolder { FullPath = "" },
                new CategoryFolder { FullPath = Path.Combine(Fixture, "Missing Folder") }] };
            this.RestoreSidebarCategories([Dirty]);
            Check(this.CustomCategories[0].Folders.Count == 2 && this.CustomCategories[0].Folders[1].Name == "Missing Folder",
                "Restoration removes duplicate or invalid paths and retains unavailable folder shortcuts");
            this.RestoreSidebarCategories([new SidebarCategory { Name = "Older", Folders = null! }]);
            Check(this.CustomCategories[0].Folders.Count == 0, "Older or null folder collections restore as empty categories");
        }
        finally
        {
            if (ActiveMenu is not null) ActiveMenu.IsOpen = false;
            this.ActiveCategoryFolderDrag = null;
            this.CategoryFolderDragCandidate = null;
            this.SidebarDropMarker.Visibility = Visibility.Collapsed;
            this.ChangeLanguage(InitialLanguage, Persist: false);
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.RestoreSidebarCategories(InitialCategories, InitialOrder);
            this.FavoritesSection.IsExpanded = InitialFavorites;
            this.DirectoryTreeSection.IsExpanded = InitialTree;
            this.SidebarScroll.ScrollToTop();
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != FixtureId
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned category-folder fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
