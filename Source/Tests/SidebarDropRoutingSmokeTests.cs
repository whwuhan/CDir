using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    // A foreign process may advertise CDir's private formats without being able
    // to marshal their objects. The receiver must use its standard file paths.
    private sealed class ExternalSidebarFileData(string[] Paths) : IDataObject
    {
        private readonly IDataObject Files = new DataObject(DataFormats.FileDrop, Paths);
        private static void CheckFormat(string Format)
        {
            if (Format.StartsWith("CDir.", StringComparison.Ordinal))
                throw new InvalidOperationException("A foreign private drag format was queried.");
        }
        public object GetData(string Format, bool AutoConvert) { CheckFormat(Format); return this.Files.GetData(Format, AutoConvert); }
        public object GetData(string Format) => this.GetData(Format, true);
        public object GetData(Type Format) => this.GetData(Format.FullName!);
        public bool GetDataPresent(string Format, bool AutoConvert) { CheckFormat(Format); return this.Files.GetDataPresent(Format, AutoConvert); }
        public bool GetDataPresent(string Format) => this.GetDataPresent(Format, true);
        public bool GetDataPresent(Type Format) => this.GetDataPresent(Format.FullName!);
        public string[] GetFormats(bool AutoConvert) => this.Files.GetFormats(AutoConvert);
        public string[] GetFormats() => this.Files.GetFormats();
        public void SetData(string Format, object Data, bool AutoConvert) => this.Files.SetData(Format, Data, AutoConvert);
        public void SetData(string Format, object Data) => this.Files.SetData(Format, Data);
        public void SetData(Type Format, object Data) => this.Files.SetData(Format, Data);
        public void SetData(object Data) => this.Files.SetData(Data);
    }

    private async Task RunSidebarDropRoutingSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        var InitialCopy = this.Settings.CopyOnDrop;
        var InitialFavorites = this.FavoritesSection.IsExpanded;
        var InitialTree = this.DirectoryTreeSection.IsExpanded;
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "SidebarDrop-" + Guid.NewGuid().ToString("N"))).FullName;
        var Source = Directory.CreateDirectory(Path.Combine(Fixture, "Source")).FullName;
        var Target = Directory.CreateDirectory(Path.Combine(Fixture, "Concrete Target")).FullName;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Sidebar drop routing check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); this.UpdateLayout(); }
        ToggleButton Header(SidebarCategory Category) => FindVisualChild<ToggleButton>(this.SidebarSectionElement(Category)!)!;
        ItemsControl List(SidebarCategory Category) => FindVisualChild<ItemsControl>(this.SidebarSectionElement(Category)!)!;
        Button FolderButton(SidebarCategory Category) => FindVisualChild<Button>(List(Category).ItemContainerGenerator.ContainerFromIndex(0))!;
        DataObject FolderData(string Folder)
        {
            var Entry = new FileEntry { FullPath = Folder, Name = Path.GetFileName(Folder), IsDirectory = true };
            var Payload = new MainPanelFolderDragPayload(this, [Entry]);
            this.ActiveMainPanelFolderDrag = Payload;
            var Data = CreateFileTransferData([Entry]);
            Data.SetData(MainPanelFolderDragFormat, Payload);
            return Data;
        }
        DragEventArgs Route(UIElement Element, RoutedEvent Event, IDataObject Data,
            DragDropEffects Allowed = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link)
        {
            var Input = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [Data, DragDropKeyStates.None, Allowed, Element, new Point(8, 8)], culture: null)!;
            Input.RoutedEvent = Event;
            Element.RaiseEvent(Input);
            return Input;
        }
        try
        {
            this.RestoreSidebarCategories([]);
            this.TryAddSidebarCategory("Drop Destinations");
            this.TryAddSidebarCategory("Add Shortcuts Here");
            var Work = this.CustomCategories[0];
            var Archive = this.CustomCategories[1];
            this.TryMoveSidebarSection(Work, 0);
            this.TryMoveSidebarSection(Archive, 1);
            this.AddFoldersToCategory(Work, [new FileEntry { FullPath = Target, IsDirectory = true }]);
            this.FavoritesSection.IsExpanded = false;
            this.DirectoryTreeSection.IsExpanded = false;
            await this.NavigateAsync(Source, RecordHistory: false);
            this.SidebarScroll.ScrollToTop();
            await Settle();
            foreach (var Copy in new[] { false, true })
            {
                this.ChangeCopyOnDrop(Copy);
                var Name = Copy ? "Copy Folder" : "Move Folder";
                var Folder = Directory.CreateDirectory(Path.Combine(Source, Name, "Nested")).Parent!.FullName;
                File.WriteAllText(Path.Combine(Folder, "Nested", "Contents.txt"), Name);
                var Data = FolderData(Folder);
                var Effect = Copy ? DragDropEffects.Copy : DragDropEffects.Move;
                var Over = Route(FolderButton(Work), DragDrop.PreviewDragOverEvent, Data);
                Check(Over.Handled && Over.Effects == Effect && this.FileDropHint.IsVisible
                    && this.FileDropHint.Text.Contains(Target)
                    && this.SidebarCategoryDropHighlight.Width == FolderButton(Work).ActualWidth,
                    "An internal folder drag over a concrete category folder previews a filesystem " + Effect);
                var HeaderOver = Route(Header(Archive), DragDrop.PreviewDragOverEvent, Data);
                Check(HeaderOver.Effects == DragDropEffects.Link && this.FileDropHint.Visibility == Visibility.Collapsed,
                    "Moving from a concrete folder to a category heading switches to shortcut addition and clears the transfer hint");
                var BackOver = Route(FolderButton(Work), DragDrop.PreviewDragOverEvent, Data);
                Check(BackOver.Effects == Effect && this.FileDropHint.IsVisible,
                    "Moving from a category heading back to its concrete folder restores the configured transfer action");
                var Drop = Route(FolderButton(Work), DragDrop.PreviewDropEvent, Data);
                Check(Drop.Handled && Drop.Effects == Effect && Directory.Exists(Folder) == Copy
                    && File.ReadAllText(Path.Combine(Target, Name, "Nested", "Contents.txt")) == Name
                    && Work.Folders.Count == 1 && Archive.Folders.Count == 0 && SamePath(this.CurrentPath, Source)
                    && this.FileDropHint.Visibility == Visibility.Collapsed && this.SidebarCategoryDropHighlight.Visibility == Visibility.Collapsed,
                    "Dropping an internal folder on a concrete directory completes " + Effect + " and never adds a category entry");
                await Settle();
            }
            var Linked = Directory.CreateDirectory(Path.Combine(Source, "Linked Folder")).FullName;
            var LinkData = FolderData(Linked);
            var HeaderDrop = Route(Header(Archive), DragDrop.PreviewDropEvent, LinkData);
            Check(HeaderDrop.Handled && HeaderDrop.Effects == DragDropEffects.Link && Archive.Folders.Count == 1
                && SamePath(Archive.Folders[0].FullPath, Linked) && Directory.Exists(Linked),
                "Dropping on a category title adds the folder shortcut without copying or moving its source");
            await Settle();
            var BlankFolder = Directory.CreateDirectory(Path.Combine(Source, "Blank Area Folder")).FullName;
            var BlankData = FolderData(BlankFolder);
            // The list itself is the blank area, with no folder button in the event ancestry.
            var BlankDrop = Route(List(Archive), DragDrop.PreviewDropEvent, BlankData);
            Check(BlankDrop.Handled && BlankDrop.Effects == DragDropEffects.Link && Archive.Folders.Count == 2
                && Archive.Folders.Any(Folder => SamePath(Folder.FullPath, BlankFolder)) && Directory.Exists(BlankFolder),
                "Dropping on another category's blank content area adds membership while preserving the folder");
            await Settle();
            var InvalidData = FolderData(Target);
            var Invalid = Route(FolderButton(Work), DragDrop.PreviewDragOverEvent, InvalidData);
            var InvalidDrop = Route(FolderButton(Work), DragDrop.PreviewDropEvent, InvalidData);
            Check(Invalid.Effects == DragDropEffects.None && InvalidDrop.Effects == DragDropEffects.None
                && Work.Folders.Count == 1 && this.SidebarCategoryDropHighlight.Visibility == Visibility.Collapsed,
                "An invalid transfer onto a concrete folder is rejected without falling back to category addition");
            this.ActiveMainPanelFolderDrag = null;
            var NativeFolder = Directory.CreateDirectory(Path.Combine(Source, "External Folder")).FullName;
            var NativeData = CreateFileTransferData([new FileEntry { FullPath = NativeFolder, IsDirectory = true }]);
            var NativeDrop = Route(FolderButton(Work), DragDrop.PreviewDropEvent, NativeData);
            Check(NativeDrop.Effects == DragDropEffects.Copy && Directory.Exists(NativeFolder)
                && Directory.Exists(Path.Combine(Target, "External Folder")) && Work.Folders.Count == 1,
                "Standard Windows folder-drop data continues to copy into a concrete sidebar directory");
            await Settle();
            foreach (var Copy in new[] { false, true })
            {
                this.ChangeCopyOnDrop(Copy);
                var Mode = Copy ? "Copy" : "Move";
                var External = Directory.CreateDirectory(Path.Combine(Source, "External Category " + Mode + " 中文")).FullName;
                var FilePath = Path.Combine(Source, "External File " + Mode + ".txt");
                File.WriteAllText(FilePath, Mode);
                var ExternalData = new DataObject(DataFormats.FileDrop, new[] { External, FilePath, External + Path.DirectorySeparatorChar });
                var Count = Archive.Folders.Count;
                this.SidebarSectionElement(Archive)!.IsExpanded = false;
                await Settle();
                var ExternalOver = Route(Header(Archive), DragDrop.PreviewDragOverEvent, ExternalData);
                Check(ExternalOver.Handled && ExternalOver.Effects == DragDropEffects.Link
                    && this.SidebarCategoryDropHighlight.IsVisible && this.FileDropHint.Visibility == Visibility.Collapsed,
                    "External directories preview category membership with " + Mode + " enabled");
                var ExternalDrop = Route(Header(Archive), DragDrop.PreviewDropEvent, ExternalData);
                Check(ExternalDrop.Effects == DragDropEffects.Link && Archive.Folders.Count == Count + 1
                    && Archive.Folders.Count(Folder => SamePath(Folder.FullPath, External)) == 1
                    && Directory.Exists(External) && File.ReadAllText(FilePath) == Mode
                    && this.SidebarSectionElement(Archive)!.IsExpanded && SamePath(this.CurrentPath, Source),
                    "External mixed drops add unique directory shortcuts, skip files and expand the category without transferring sources (" + Mode + ")");
                await Settle();
                Check(Route(Header(Archive), DragDrop.PreviewDropEvent, ExternalData).Effects == DragDropEffects.None
                    && Archive.Folders.Count == Count + 1,
                    "Repeated external category drops do not duplicate entries (" + Mode + ")");
                var FileData = new DataObject(DataFormats.FileDrop, new[] { FilePath });
                Check(Route(Header(Archive), DragDrop.PreviewDropEvent, FileData).Effects == DragDropEffects.None
                    && File.Exists(FilePath) && Archive.Folders.Count == Count + 1,
                    "Files alone cannot become directory shortcuts (" + Mode + ")");
                var FileDrop = Route(FolderButton(Work), DragDrop.PreviewDropEvent, FileData);
                Check(FileDrop.Effects == (Copy ? DragDropEffects.Copy : DragDropEffects.Move)
                    && File.Exists(FilePath) == Copy && File.ReadAllText(Path.Combine(Target, Path.GetFileName(FilePath))) == Mode,
                    "External files dropped on concrete folders obey the copy setting (" + Mode + ")");
                await Settle();
                var DirectoryData = new DataObject(DataFormats.FileDrop, new[] { External });
                var DirectoryDrop = Route(FolderButton(Work), DragDrop.PreviewDropEvent, DirectoryData);
                Check(DirectoryDrop.Effects == (Copy ? DragDropEffects.Copy : DragDropEffects.Move)
                    && Directory.Exists(External) == Copy && Directory.Exists(Path.Combine(Target, Path.GetFileName(External)))
                    && Work.Folders.Count == 1,
                    "External directories dropped on concrete folders obey the copy setting (" + Mode + ")");
                await Settle();
            }
            var CopyOnly = Directory.CreateDirectory(Path.Combine(Source, "Copy-only source")).FullName;
            var CopyOnlyData = new DataObject(DataFormats.FileDrop, new[] { CopyOnly });
            Check(Route(Header(Archive), DragDrop.PreviewDragOverEvent, CopyOnlyData, DragDropEffects.Copy).Effects == DragDropEffects.Copy
                && Route(Header(Archive), DragDrop.PreviewDropEvent, CopyOnlyData, DragDropEffects.Copy).Effects == DragDropEffects.Copy
                && Archive.Folders.Any(Folder => SamePath(Folder.FullPath, CopyOnly)) && Directory.Exists(CopyOnly),
                "Sources that allow only copy can add category shortcuts without removing their directories");
            await Settle();
            var MoveOnly = Directory.CreateDirectory(Path.Combine(Source, "Move-only source")).FullName;
            var MoveOnlyData = new DataObject(DataFormats.FileDrop, new[] { MoveOnly });
            Check(Route(Header(Archive), DragDrop.PreviewDragOverEvent, MoveOnlyData, DragDropEffects.Move).Effects == DragDropEffects.None
                && Route(Header(Archive), DragDrop.PreviewDropEvent, MoveOnlyData, DragDropEffects.Move).Effects == DragDropEffects.None
                && !Archive.Folders.Any(Folder => SamePath(Folder.FullPath, MoveOnly)) && Directory.Exists(MoveOnly),
                "Move-only external sources are rejected for shortcut addition so the source cannot be deleted");
            var RelativeData = new DataObject(DataFormats.FileDrop, new[] { "relative", CopyOnly });
            Check(Route(Header(Archive), DragDrop.PreviewDropEvent, RelativeData).Effects == DragDropEffects.None,
                "Malformed external path lists cannot partially add category shortcuts");
            var ForeignFolder = Directory.CreateDirectory(Path.Combine(Source, "Other CDir process 中文")).FullName;
            var ForeignData = new ExternalSidebarFileData([ForeignFolder]);
            Check(Route(Header(Archive), DragDrop.PreviewDragOverEvent, ForeignData).Effects == DragDropEffects.Link
                && Route(Header(Archive), DragDrop.PreviewDropEvent, ForeignData).Effects == DragDropEffects.Link
                && Archive.Folders.Any(Folder => SamePath(Folder.FullPath, ForeignFolder)) && Directory.Exists(ForeignFolder),
                "Another CDir process adds category shortcuts without querying its private drag objects");
            await Settle();
            foreach (var Copy in new[] { false, true })
            {
                this.ChangeCopyOnDrop(Copy);
                var Folder = Directory.CreateDirectory(Path.Combine(Source, "Sidebar Source " + Copy)).FullName;
                var Entry = new FileEntry { FullPath = Folder, IsDirectory = true };
                var Payload = new SidebarFolderAddPayload(this, Entry);
                var Data = CreateFileTransferData([Entry]);
                Data.SetData(SidebarFolderAddDragFormat, Payload);
                this.ActiveSidebarFolderAddDrag = Payload;
                var Drop = Route(FolderButton(Work), DragDrop.PreviewDropEvent, Data);
                Check(Drop.Effects == (Copy ? DragDropEffects.Copy : DragDropEffects.Move) && Directory.Exists(Folder) == Copy
                    && Directory.Exists(Path.Combine(Target, Path.GetFileName(Folder))) && Work.Folders.Count == 1,
                    "Directory-tree, Quick Access and address-folder payloads transfer into concrete folders (copy=" + Copy + ")");
                this.ActiveSidebarFolderAddDrag = null;
                await Settle();
            }
            var CountBeforeBusy = Archive.Folders.Count;
            this.OperationBusy = true;
            try
            {
                Check(Route(Header(Archive), DragDrop.PreviewDropEvent, MoveOnlyData).Effects == DragDropEffects.None
                    && Archive.Folders.Count == CountBeforeBusy, "Busy windows reject external shortcut drops");
            }
            finally { this.OperationBusy = false; }
        }
        finally
        {
            this.ActiveMainPanelFolderDrag = null;
            this.ActiveSidebarFolderAddDrag = null;
            this.ClearFileTransferFeedback();
            this.ClearSidebarDropFeedback();
            this.ChangeCopyOnDrop(InitialCopy);
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.RestoreSidebarCategories(InitialCategories, InitialOrder);
            this.FavoritesSection.IsExpanded = InitialFavorites;
            this.DirectoryTreeSection.IsExpanded = InitialTree;
            this.SidebarScroll.ScrollToTop();
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned sidebar-drop fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
