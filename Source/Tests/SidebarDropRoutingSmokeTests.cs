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
        DragEventArgs Route(UIElement Element, RoutedEvent Event, IDataObject Data)
        {
            var Input = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [Data, DragDropKeyStates.None, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, Element, new Point(8, 8)], culture: null)!;
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
        }
        finally
        {
            this.ActiveMainPanelFolderDrag = null;
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
