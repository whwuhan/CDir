using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunAddressFolderDragSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialCategories = this.CustomCategories.ToArray();
        var InitialOrder = this.SidebarSections.Select(this.SidebarSectionKey).ToArray();
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirAddressDrag-" + FixtureId);
        var Current = Directory.CreateDirectory(Path.Combine(Fixture, "Current 中文 With Spaces")).FullName;
        var Marker = Path.Combine(Fixture, ".CDirAddressDragOwner");
        File.WriteAllText(Marker, FixtureId);
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Address folder-drag UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        try
        {
            this.RestoreSidebarCategories([]);
            this.TryAddSidebarCategory("Address Favorites");
            var Category = this.CustomCategories.Single();
            this.TryMoveSidebarSection(Category, 0);
            Check(await this.NavigateAsync(Current), "Address drag opens its owned Unicode and spaced fixture");
            this.SidebarScroll.ScrollToTop();
            this.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var Position = FindVisualChild<ToggleButton>(this.SidebarSectionElement(Category)!)!
                .TranslatePoint(new Point(8, 8), this.SidebarPanel);
            var History = this.History.ToArray();
            var HistoryIndex = this.HistoryIndex;
            var Display = this.AddressBox.Text;
            Check(this.PrepareAddressFolderDrag(this.AddressIcon, 1, new Point())
                && SamePath(this.AddressFolderDragCandidate!.Folder.FullPath, Current),
                "The address folder icon prepares the complete current directory for a category shortcut");
            var Payload = new SidebarFolderAddPayload(this, this.AddressFolderDragCandidate!.Folder);
            var Data = new DataObject();
            Data.SetData(SidebarFolderAddDragFormat, Payload);
            Check(!this.CompleteSidebarDrop(Data, Position) && Category.Folders.Count == 0,
                "Inactive address drag data cannot add category folders");
            this.ActiveSidebarFolderAddDrag = Payload;
            Check(this.UpdateSidebarDragFeedback(Data, Position) == DragDropEffects.Link && this.CompleteSidebarDrop(Data, Position)
                && SamePath(Category.Folders.Single().FullPath, Current),
                "Dropping the address folder onto a category adds the current directory as a shortcut");
            Check(this.UpdateSidebarDragFeedback(Data, Position) == DragDropEffects.None
                && !this.CompleteSidebarDrop(Data, Position) && Category.Folders.Count == 1,
                "Repeated address folder drops do not duplicate category entries");
            this.ActiveSidebarFolderAddDrag = null;
            var Segment = this.AddressBreadcrumbs.Items.Cast<AddressSegment>().Single(Item => SamePath(Item.FullPath, Fixture));
            var Breadcrumb = FindVisualChild<Button>(this.AddressBreadcrumbs.ItemContainerGenerator.ContainerFromItem(Segment))!;
            Check(this.PrepareAddressFolderDrag(FindVisualChild<TextBlock>(Breadcrumb) ?? (DependencyObject)Breadcrumb, 1, new Point())
                && SamePath(this.AddressFolderDragCandidate!.Folder.FullPath, Fixture),
                "An ancestor breadcrumb prepares its own directory without navigating there");
            Payload = new SidebarFolderAddPayload(this, this.AddressFolderDragCandidate!.Folder);
            Data = new DataObject();
            Data.SetData(SidebarFolderAddDragFormat, Payload);
            this.ActiveSidebarFolderAddDrag = Payload;
            this.AddressFolderDragActive = true;
            Breadcrumb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(this.AddressNavigationDelay is null && this.CompleteSidebarDrop(Data, Position)
                && Category.Folders.Select(Folder => Folder.FullPath).SequenceEqual(new[] { Current, Fixture }),
                "An address ancestor drag adds its folder and suppresses the breadcrumb click navigation");
            this.AddressFolderDragActive = false;
            this.ActiveSidebarFolderAddDrag = null;
            Check(!this.CompleteSidebarDrop(Data, Position) && Category.Folders.Count == 2,
                "A cancelled or finished address drag cannot add another shortcut");
            Check(!this.PrepareAddressFolderDrag(this.AddressGoButton, 1, new Point())
                && !this.PrepareAddressFolderDrag(this.AddressBox, 1, new Point())
                && !this.PrepareAddressFolderDrag(this.AddressIcon, 2, new Point()),
                "Address actions text editing and double-click editing do not prepare folder drags");
            this.OperationBusy = true;
            try { Check(!this.PrepareAddressFolderDrag(this.AddressIcon, 1, new Point()), "Busy file operations block address folder drags"); }
            finally { this.OperationBusy = false; }
            this.BeginAddressEdit();
            Check(!this.PrepareAddressFolderDrag(this.AddressIcon, 1, new Point()), "Editing the path prevents accidental address folder drags");
            this.EndAddressEdit();
            Check(SamePath(this.CurrentPath, Current) && this.AddressBox.Text == Display && this.History.SequenceEqual(History)
                && this.HistoryIndex == HistoryIndex && SamePath(this.AddressSegments[^1].FullPath, Current) && Directory.Exists(Current),
                "Address drops preserve the displayed path current location history and original directory");
            var SettingsFile = Path.Combine(Fixture, "Settings.json");
            Check(this.SaveSettings(SettingsFile), "Address category shortcuts save to owned settings");
            var Saved = ReadSettings(SettingsFile);
            this.RestoreSidebarCategories(Saved.CustomCategories, Saved.SidebarOrder);
            Check(this.CustomCategories.Single().Folders.Select(Folder => Folder.FullPath).SequenceEqual(new[] { Current, Fixture }),
                "Address folder and ancestor shortcuts survive settings restoration");
        }
        finally
        {
            this.AddressFolderDragCandidate = null;
            this.AddressFolderDragActive = false;
            this.ActiveSidebarFolderAddDrag = null;
            this.ClearSidebarDropFeedback();
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.RestoreSidebarCategories(InitialCategories, InitialOrder);
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != FixtureId
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned address-drag fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
