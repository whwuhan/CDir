using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunQuickAccessSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialTreeKey = this.NavigationTreeKey;
        var InitialHistory = this.History.ToArray();
        var InitialTreeKeys = this.HistoryTreeKeys.ToArray();
        var InitialIndex = this.HistoryIndex;
        var Expanded = this.FavoritesSection.IsExpanded;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Quick Access check failed: " + Name);
            Passed.Add(Name);
        }
        try
        {
            this.UpdateFavorites();
            this.FavoritesSection.IsExpanded = true;
            this.UpdateLayout();
            var Items = this.FavoritesList.Items.Cast<FavoriteLocation>().ToArray();
            Check(Items.Select(Item => Item.Name).SequenceEqual(new[] { "FavoriteHome", "DirectoryTree", "RecycleBin" }.Select(Key => LanguageService.Text(Key))),
                "Quick Access preserves the fixed shortcut order when optional folders are unavailable");
            var Computer = Items.Single(Item => ShellLocations.IsComputer(Item.FullPath));
            var Container = this.FavoritesList.ItemContainerGenerator.ContainerFromItem(Computer);
            var Button = FindVisualChild<Button>(Container)!;
            Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var Deadline = Environment.TickCount64 + 10000;
            while (this.Loading && Environment.TickCount64 < Deadline) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(this.IsComputer && this.TabTitle == LanguageService.Text("DirectoryTree") && !this.UpButton.IsEnabled
                && this.AddressSegments.Single().Name == LanguageService.Text("DirectoryTree"),
                "The rendered My Computer shortcut opens an in-tab drive overview with a localized caption and address");
            Check(this.Entries.Count > 0 && this.Entries.All(Entry => Entry.IsDrive && Entry.IsDirectory && AppMain.IsInsideSmokeScope(Entry.FullPath))
                && this.Entries.Select(Entry => Entry.FullPath).Order().SequenceEqual(this.Roots.Select(Node => Node.FullPath).Order()),
                "My Computer uses only owned test roots during UI checks");
            Check(this.SidebarBookmarkRoots().All(Node => !ShellLocations.IsVirtual(Node.FullPath))
                && this.SidebarFolderAddFromSource(Button, 1) is null,
                "Virtual Quick Access shortcuts cannot be expanded as disk folders or dragged into filesystem categories");
            var HistoryCount = this.History.Count;
            await this.RefreshAsync(true);
            Check(this.IsComputer && this.History.Count == HistoryCount && !this.Loading,
                "Refreshing My Computer preserves its location and history");
            var Drive = this.Entries.First();
            this.SearchBox.Text = Drive.Name;
            await this.SearchTask;
            Check(this.View!.Cast<FileEntry>().Any(Entry => Entry.FullPath == Drive.FullPath) && !this.Searching,
                "Searching My Computer filters drive names without recursively scanning disks");
            this.SearchBox.Clear();
            this.SelectPath(Drive.FullPath);
            await this.NewFolderAsync();
            await this.RenameAsync();
            await this.DeleteAsync();
            Check(!this.OperationBusy && this.InlineRenameTarget is null && this.IsComputer,
                "Folder creation, rename and deletion do not treat the computer overview as a writable directory");
            await this.OpenSelectedAsync();
            Check(SamePath(this.CurrentPath, Drive.FullPath), "Opening a drive in My Computer navigates within the current tab");
            await this.BackAsync();
            Check(this.IsComputer, "Back returns from a drive to My Computer");
            await this.ForwardAsync();
            Check(SamePath(this.CurrentPath, Drive.FullPath), "Forward reopens the drive after visiting My Computer");
            await this.NavigateAsync(ShellLocations.Computer, TreeKey: "quick-access");
            var Other = this.HostWindow.AddTab();
            try
            {
                await Other.WaitUntilReadyAsync();
                Check(Other.IsComputer && Other.Entries.Count > 0 && this.IsComputer,
                    "New tabs preserve My Computer as an independent virtual location");
            }
            finally { this.HostWindow.CloseTab(Other); this.HostWindow.SelectTab(this); }
        }
        finally
        {
            this.SearchBox.Clear();
            await this.NavigateAsync(InitialPath, RecordHistory: false, TreeKey: InitialTreeKey);
            this.History.Clear(); this.History.AddRange(InitialHistory);
            this.HistoryTreeKeys.Clear(); this.HistoryTreeKeys.AddRange(InitialTreeKeys);
            this.HistoryIndex = InitialIndex;
            this.FavoritesSection.IsExpanded = Expanded;
            this.UpdateActions();
        }
    }
}
