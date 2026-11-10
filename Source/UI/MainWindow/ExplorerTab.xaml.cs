using CDir.UI.Dialogs;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab : UserControl, INotifyPropertyChanged
{
    private readonly FileSystemService Files = new();
    private readonly ObservableCollection<FolderNode> Roots = [];
    private readonly List<string> History = [];
    private readonly DispatcherTimer RefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private readonly string SettingsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CDir", "Settings.json");
    private ICollectionView? View;
    private IReadOnlyList<FileEntry> Entries = [];
    private int HistoryIndex = -1;
    private int NavigationVersion;
    private string CurrentPath = "";
    private string? NavigatingPath;
    private bool Loading;
    private bool OperationBusy;
    private bool SyncingTree;
    private bool SuppressTreeSelectionScroll;
    private bool WindowClosed;
    private FileSystemWatcher? Watcher;
    private bool FolderRefreshPending;
    private string SortProperty = nameof(FileEntry.Name);
    private ListSortDirection SortDirection = ListSortDirection.Ascending;
    private AppSettings Settings = new();
    private Action<IDataObject> WriteClipboardFiles = Data => Clipboard.SetDataObject(Data, true);
    private Func<IDataObject?> ReadClipboardFiles = Clipboard.GetDataObject;
    private Action ClearClipboardFiles = Clipboard.Clear;
    private Func<uint> ReadClipboardVersion = GetClipboardSequenceNumber;
    private static (uint Version, string Error)? FailedFileClipboard;

    internal ExplorerTab(MainWindow HostWindow, string? LayoutSettingsFile = null, string? InitialPath = null, AppSettings? InitialSettings = null, bool RunStartupChecks = false, FolderNode[]? InitialRoots = null, bool RefreshInheritedRoots = true)
    {
        this.HostWindow = HostWindow;
        if (ShellLocations.IsRecycleBin(InitialPath) && HostWindow.ActiveTab is { } Previous)
        {
            this.RecycleBinViewTarget = Previous.RecycleBinViewTarget;
            this.ReadRecycleEntries = Previous.ReadRecycleEntries;
        }
        this.Files.OperationHistory = SharedFileHistory;
        this.InitialPath = InitialPath;
        this.RunStartupChecks = RunStartupChecks;
        this.InheritedSettings = InitialSettings;
        this.InitialRoots = InitialRoots;
        this.RefreshInheritedRoots = RefreshInheritedRoots;
        if (LayoutSettingsFile is not null && (!AppMain.SmokeTest || !AppMain.IsInsideSmokeScope(LayoutSettingsFile)))
            throw new InvalidOperationException("Layout checks require an owned test settings file.");
        this.LayoutProbeSettingsFile = LayoutSettingsFile;
        AppMain.RecordSmokeProgress("Initializing language resources");
        this.InitializeLanguage();
        this.ApplyUIFonts();
        AppMain.RecordSmokeProgress("Loading window XAML");
        InitializeComponent();
        this.PreviewMouseDown += (_, _) => this.StartupInteraction |= this.IsInitialContentReady;
        this.PreviewMouseWheel += (_, _) => this.StartupInteraction |= this.IsInitialContentReady;
        this.PreviewKeyDown += (_, _) => this.StartupInteraction |= this.IsInitialContentReady;
        this.ShowBookmarkPaths = this.Settings.ShowBookmarkPaths;
        this.ShowSidebarDriveUsage = this.Settings.ShowSidebarDriveUsage;
        CutClipboardChanged += this.RefreshCutFeedback;
        this.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler(this.MainPanelPointerReleased), handledEventsToo: true);
        this.InitializeDriveUsage();
        this.RestoreSidebarWidth(this.Settings.SidebarWidth);
        this.ApplyShortcutHints();
        this.ApplyUIFonts();
        this.RestoreColumnLayout(this.Settings.ColumnOrder, this.Settings.HiddenColumns);
        AppMain.RecordSmokeProgress("Initializing window state");
        this.InitializeVersionControl();
        this.InitializeSidebarCategories();
        this.FolderTree.ItemsSource = this.Roots;
        this.RefreshTimer.Tick += async (_, _) =>
        {
            this.RefreshTimer.Stop();
            if (this.CurrentSearch.IsActive) { this.FolderRefreshPending = true; return; }
            if (this.WindowClosed) return;
            if (this.Loading || this.OperationBusy) { this.RefreshTimer.Start(); return; }
            await this.RefreshAsync();
        };
        this.ApplyGridLines();
        this.FavoritesSection.IsExpanded = this.Settings.FavoritesExpanded;
        this.DirectoryTreeSection.IsExpanded = this.Settings.DirectoryTreeExpanded;
        AppMain.RecordSmokeProgress("Window construction completed");
    }

    private async void WindowLoaded(object Sender, RoutedEventArgs E)
    {
        if (this.PageLoaded) return;
        this.PageLoaded = true;
        await this.HostWindow.InitialPageReadGate.WaitAsync();
        try
        {
        if (this.WindowClosed) { this.InitialLayoutReady.TrySetResult(); return; }
        AppMain.RecordSmokeProgress("Loading initial test directory");
        var Home = AppMain.SmokeTest ? AppMain.SmokeRoot! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Redirected known folders can also be on an unavailable network device.
        var Favorites = Task.Run(this.ReadFavorites);
        if (this.InitialRoots is { Length: > 0 })
            foreach (var Root in this.InitialRoots) this.Roots.Add(Root);
        else if (AppMain.SmokeTest) this.Roots.Add(new FolderNode("Test Files", AppMain.SmokeRoot!));
        // Disk labels/readiness can block for seconds on disconnected devices.
        // The current folder does not depend on discovering every drive first.
        var Initial = this.InitialPath ?? (this.LayoutProbeSettingsFile is null ? AppMain.StartupPath : null) ?? this.Settings.LastPath ?? Home;
        var TreeKey = this.Settings.NavigationTreeKey;
        if (TreeKey is null || !this.SidebarSections.Any(Section => this.SidebarSectionKey(Section) == TreeKey)) TreeKey = "folder-tree";
        var TreeLayouts = this.Settings.SidebarTreeLayouts;
        var SidebarOffset = this.Settings.SidebarScrollOffset;
        if (!await this.NavigateAsync(Initial, RevealTree: false, SyncNavigationTree: false, TreeKey: TreeKey)
            && !AppMain.SmokeTest && !string.Equals(Initial, Home, StringComparison.OrdinalIgnoreCase))
            await this.NavigateAsync(Home, RevealTree: false, SyncNavigationTree: false, TreeKey: TreeKey);
        this.InitialNavigationVersion = this.NavigationVersion;
        this.IsInitialContentReady = true;
        this.HostWindow.TabReady(this);
        if (this.IsTabSelected) this.RestoreTabFocus();
        // Paint and service input before optional device and sidebar restoration.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        var LoadedFavorites = await Favorites;
        if (this.WindowClosed) { this.InitialLayoutReady.TrySetResult(); return; }
        this.FavoritesList.ItemsSource = LoadedFavorites;
        if (!AppMain.SmokeTest && this.InitialRoots is not { Length: > 0 }) await this.RefreshRootsAsync();
        if (this.InitialNavigationVersion == this.NavigationVersion && !this.WindowClosed)
        {
            if (TreeLayouts is null && !this.IsVirtualLocation)
                await this.SyncNavigationTreeAsync(TreeKey, this.CurrentPath, this.InitialNavigationVersion, Reveal: false);
            else await this.RestoreSidebarTreeLayoutsAsync(TreeLayouts, this.InitialNavigationVersion);
        }
        this.RestoringWindowLayout = false;
        this.UpdateLayout();
        if (this.InitialNavigationVersion == this.NavigationVersion && double.IsFinite(SidebarOffset) && SidebarOffset >= 0)
            this.SidebarScroll.ScrollToVerticalOffset(SidebarOffset);
        this.HostWindow.TabReady(this);
        this.InitialLayoutReady.TrySetResult();
        if (this.IsTabSelected && !this.StartupInteraction) this.RestoreTabFocus();
        this.QueueSidebarDriveUsageRefresh();
        // New tabs can use known drive metadata immediately; discover device changes afterward.
        if (this.RefreshInheritedRoots && this.InitialRoots is { Length: > 0 } && !AppMain.SmokeTest) _ = this.RefreshRootsAsync();
        }
        finally { this.HostWindow.InitialPageReadGate.Release(); }
        if (!this.RunStartupChecks || this.LayoutProbeSettingsFile is not null) return;
        if (AppMain.ScreenshotPath is not null || AppMain.SmokeTest)
        {
            try
            {
                AppMain.RecordSmokeProgress("Waiting for initial layout");
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var Checks = AppMain.SmokeTest ? await this.RunSmokeChecksAsync() : new List<string>();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(500);
                await this.RefreshVersionStatusesAsync();
                if (AppMain.ScreenshotPath is not null)
                {
                    UpdateLayout();
                    var Bitmap = new RenderTargetBitmap((int)this.HostWindow.ActualWidth, (int)this.HostWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    Bitmap.Render(this.HostWindow);
                    var Encoder = new PngBitmapEncoder();
                    Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(AppMain.ScreenshotPath)!);
                    using (var Stream = System.IO.File.Create(AppMain.ScreenshotPath)) Encoder.Save(Stream);
                    using var ReportProcess = System.Diagnostics.Process.GetCurrentProcess();
                    System.IO.File.WriteAllText(AppMain.ScreenshotPath + ".json", JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["passed"] = Checks, ["groups"] = this.CompletedSmokeGroups, ["skipped_groups"] = this.SkippedSmokeGroups,
                        ["requested_group"] = AppMain.SmokeGroup, ["path"] = this.CurrentPath, ["entries"] = this.Entries.Count,
                        ["release_version"] = typeof(AppMain).Assembly.GetName().Version?.ToString(3) ?? "",
                        ["captured_at"] = DateTimeOffset.Now.ToString("O"),
                        ["width"] = this.HostWindow.ActualWidth, ["height"] = this.HostWindow.ActualHeight,
                        ["scope_root"] = AppMain.SmokeRoot ?? "", ["desktop"] = "current",
                        ["render_mode"] = RenderOptions.ProcessRenderMode.ToString(), ["priority"] = ReportProcess.PriorityClass.ToString()
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    if (System.IO.File.Exists(AppMain.ScreenshotPath + ".error.txt")) System.IO.File.Delete(AppMain.ScreenshotPath + ".error.txt");
                }
                AppMain.RecordSmokeProgress("Checks and report completed");
                if (AppMain.SmokeTest) Application.Current.Shutdown(0);
            }
            catch (System.Exception Ex)
            {
                if (AppMain.ScreenshotPath is not null) System.IO.File.WriteAllText(AppMain.ScreenshotPath + ".error.txt", Ex.ToString());
                if (AppMain.SmokeTest) Application.Current.Shutdown(1);
                else this.ShowNotice(Ex.Message, true);
            }
        }
    }

    private static string GetDownloadsPath(string Home)
    {
        // Downloads can be redirected independently from the profile (for example to another disk).
        try
        {
            using var Key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (Key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string Path)
                return Environment.ExpandEnvironmentVariables(Path);
        }
        catch (System.Exception Ex) when (Ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        return System.IO.Path.Combine(Home, "Downloads");
    }

    private async Task RefreshRootsAsync()
    {
        if (AppMain.SmokeTest) return;
        try
        {
            var Drives = await Task.Run(this.Files.GetRoots);
            if (this.WindowClosed) return;
            var Paths = Drives.Select(X => X.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var Old in this.Roots.Where(X => !X.FullPath.StartsWith(@"\\") && !Paths.Contains(X.FullPath)).ToList()) this.Roots.Remove(Old);
            foreach (var Drive in Drives)
                if (!this.Roots.Any(X => SamePath(X.FullPath, Drive.FullPath))) this.Roots.Add(new(Drive));
            this.QueueSidebarDriveUsageRefresh();
        }
        catch (System.Exception Ex) { this.ShowNotice(LanguageService.Text("ReadDrivesError", Ex.Message), true); }
    }

    private async Task<bool> NavigateAsync(string Raw, int? HistoryIndex = null, bool RecordHistory = true, bool PreserveFilter = false, bool RevealTree = true, bool SyncNavigationTree = true, string TreeKey = "folder-tree")
    {
        if (RecordHistory && !HistoryIndex.HasValue) ++this.BrowseHistoryEpoch;
        this.CancelInlineRename();
        this.DismissFileDragFeedback();
        if (ShellLocations.IsRecycleBin(Raw)) return await this.NavigateRecycleBinAsync(HistoryIndex, RecordHistory);
        if (ShellLocations.IsComputer(Raw)) TreeKey = "quick-access";
        // A background refresh must not cancel a user's pending breadcrumb click.
        if (!PreserveFilter || RecordHistory || HistoryIndex.HasValue)
        {
            this.CancelAddressNavigation();
            this.CancelSidebarFolderClick();
        }
        if (this.WindowClosed) return false;
        var Version = ++this.NavigationVersion;
        this.CancelFolderSizes();
        this.RecycleReadCancellation?.Cancel();
        this.NavigatingTreeKey = TreeKey;
        this.NavigatingPath = null;
        this.CancelSearch();
        var SearchSelection = PreserveFilter ? this.SelectedEntries().Select(Entry => Entry.FullPath).ToArray() : [];
        var OldPath = this.CurrentPath;
        string? SelectPath = null;
        var ShowHidden = this.Settings.ShowHidden;
        this.Loading = true;
        this.UpdateActions();
        try
        {
            var Computer = ShellLocations.IsComputer(Raw);
            var ComputerRoots = Computer ? this.Roots.ToArray() : [];
            var Normalized = Computer ? ShellLocations.Computer : this.Files.NormalizePath(Raw, string.IsNullOrEmpty(this.CurrentPath) ? null : this.FileSystemWorkingDirectory);
            this.NavigatingPath = Normalized;
            if (AppMain.SmokeTest && !Computer && !AppMain.IsInsideSmokeScope(Normalized))
                throw new UnauthorizedAccessException("UI checks may only navigate inside their owned test scope.");
            var Result = await Task.Run(() =>
            {
                if (Computer)
                {
                    var Drives = AppMain.SmokeTest ? ComputerRoots.Select(Node => new DriveEntry(Node.Name, Node.FullPath, "")) : this.Files.GetRoots();
                    return (Path: ShellLocations.Computer, Entries: (IReadOnlyList<FileEntry>)Drives.Select(Drive => new FileEntry
                    { Name = Drive.Name, FullPath = Drive.FullPath, IsDirectory = true, IsDrive = true, TypeKey = "DriveLocal" }).ToArray());
                }
                var Path = Normalized;
                if (System.IO.File.Exists(Path)) { SelectPath = Path; Path = System.IO.Path.GetDirectoryName(Path)!; }
                return (Path: Path, Entries: this.Files.GetEntries(Path, ShowHidden));
            });
            if (Version != this.NavigationVersion || this.WindowClosed) return false;
            this.CurrentPath = Result.Path;
            this.CloseRecycleBinView();
            this.NotifyTabHeader();
            this.QueueDriveUsageRefresh(this.CurrentPath);
            this.AddressIcon.IsDrive = !Computer && SamePath(this.CurrentPath, System.IO.Path.GetPathRoot(this.CurrentPath)!);
            this.AddressIcon.Path = this.CurrentPath;
            this.AddressIcon.VersionStatus = VersionStatus.None;
            this.FolderEntries = Result.Entries;
            this.FolderRefreshPending = false;
            if (HistoryIndex.HasValue) this.HistoryIndex = HistoryIndex.Value;
            else if (RecordHistory && (this.HistoryIndex < 0 || !SamePath(this.History[this.HistoryIndex], this.CurrentPath)))
            {
                this.History.RemoveRange(this.HistoryIndex + 1, this.History.Count - this.HistoryIndex - 1);
                this.HistoryTreeKeys.RemoveRange(this.HistoryIndex + 1, this.HistoryTreeKeys.Count - this.HistoryIndex - 1);
                this.History.Add(this.CurrentPath);
                this.HistoryTreeKeys.Add(TreeKey);
                this.HistoryIndex = this.History.Count - 1;
            }
            this.UseNavigationTree(TreeKey);
            this.UpdateAddressPath(PreserveEditing: this.AddressEditing && !RecordHistory && PreserveFilter && SamePath(OldPath, this.CurrentPath));
            if (!PreserveFilter) this.SearchBox.Text = "";
            this.ShowEntries(this.FolderEntries, SearchSelection);
            // Fit newly opened folders, while watcher/manual refreshes keep personal widths.
            if (!Computer && !PreserveFilter && this.Settings.AutoFitColumnsOnNavigation)
                await this.FitVisibleColumnsAsync(Version);
            if (Version != this.NavigationVersion || this.WindowClosed) return false;
            if (SelectPath is not null) this.SelectPath(SelectPath);
            this.ConfigureWatcher();
            if (Computer) this.SelectSidebarNode(null);
            else if (SyncNavigationTree)
                await this.SyncNavigationTreeAsync(TreeKey, this.CurrentPath, Version,
                    Reveal: RevealTree && (RecordHistory || HistoryIndex.HasValue || this.FileGrid.IsKeyboardFocusWithin));
            if (Version != this.NavigationVersion || this.WindowClosed) return false;
            this.ScheduleVersionRefresh();
            this.QueueSearch(Debounce: false, Selection: SelectPath is null ? SearchSelection : [SelectPath]);
            return true;
        }
        catch (System.Exception Ex)
        {
            if (Version == this.NavigationVersion && !this.WindowClosed)
            {
                if (!this.AddressEditing) this.AddressBox.Text = OldPath;
                this.ShowNotice(LanguageService.Text("OpenLocationError", FriendlyError(Ex)), true);
                if (string.IsNullOrEmpty(OldPath)) { this.EmptyState.Visibility = Visibility.Visible; this.EmptyTitle.Text = LanguageService.Text("LocationUnavailable"); this.EmptyDescription.Text = LanguageService.Text("LocationUnavailableHint"); }
            }
            return false;
        }
        finally { if (Version == this.NavigationVersion) { this.NavigatingPath = null; this.NavigatingTreeKey = null; this.SyncingTree = false; this.Loading = false; this.UpdateActions(); this.UpdateStickySidebar(); } }
    }

    private async Task EnsureChildrenAsync(FolderNode Node)
    {
        if (ShellLocations.IsVirtual(Node.FullPath)) return;
        if (Node.IsPlaceholder || Node.IsLoaded) return;
        if (Node.LoadTask is not null) { await Node.LoadTask; return; }
        var ShowHidden = this.Settings.ShowHidden;
        var LoadVersion = Node.LoadVersion;
        async Task LoadAsync()
        {
            try
            {
                var Children = await Task.Run(() => this.Files.GetDirectories(Node.FullPath, ShowHidden));
                if (LoadVersion != Node.LoadVersion || this.WindowClosed) return;
                Node.Children.Clear();
                var Slice = Stopwatch.StartNew();
                foreach (var Child in Children)
                {
                    if (LoadVersion != Node.LoadVersion || this.WindowClosed) return;
                    Node.Children.Add(new FolderNode(Child.Name, Child.FullPath));
                    if (Slice.ElapsedMilliseconds < 8) continue;
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    Slice.Restart();
                }
                Node.IsLoaded = true;
                this.ScheduleVersionRefresh();
            }
            catch (System.Exception Ex)
            {
                if (LoadVersion != Node.LoadVersion || this.WindowClosed) return;
                Node.Children.Clear();
                Node.Children.Add(new FolderNode("", "", NameKey: "TreeAccessError"));
                this.ShowNotice(LanguageService.Text("ExpandFolderError", FriendlyError(Ex)), true);
            }
        }
        Node.LoadTask = LoadAsync();
        try { await Node.LoadTask; }
        finally { if (LoadVersion == Node.LoadVersion) Node.LoadTask = null; }
    }

    private async Task SyncTreeAsync(string Path, int Version, bool Reveal)
    {
        this.SyncingTree = true;
        try
        {
            if (Reveal) this.DirectoryTreeSection.IsExpanded = true;
            var RootPath = AppMain.SmokeTest ? AppMain.SmokeRoot! : System.IO.Path.GetPathRoot(Path)!;
            var Node = this.Roots.FirstOrDefault(X => SamePath(X.FullPath, RootPath));
            if (Node is null)
            {
                Node = new FolderNode(AppMain.SmokeTest ? "Test Files" : RootPath.TrimEnd('\\'), RootPath, !AppMain.SmokeTest);
                this.Roots.Add(Node);
            }
            var Branch = new List<FolderNode> { Node };
            var Relative = System.IO.Path.GetRelativePath(RootPath, Path);
            if (Relative != ".")
                foreach (var Part in Relative.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    await this.EnsureChildrenAsync(Node);
                    if (Version != this.NavigationVersion || this.WindowClosed) return;
                    Node.IsExpanded = true;
                    var Next = Node.Children.FirstOrDefault(X => string.Equals(X.Name, Part, StringComparison.OrdinalIgnoreCase));
                    if (Next is null)
                    {
                        // Hidden paths remain navigable even while hidden items are filtered out of the tree.
                        Next = new FolderNode(Part, System.IO.Path.Combine(Node.FullPath, Part));
                        Node.Children.Add(Next);
                    }
                    Node = Next;
                    Branch.Add(Node);
                }
            if (Reveal)
            {
                await this.EnsureChildrenAsync(Node);
                if (Version != this.NavigationVersion || this.WindowClosed) return;
                Node.IsExpanded = true;
            }
            // Preserve the selected node instead of clearing and reselecting it on every
            // navigation. Mouse navigation also keeps the user's sidebar viewport intact.
            var WasSuppressed = this.SuppressTreeSelectionScroll;
            var WasSelected = Node.IsSelected;
            if (Version != this.NavigationVersion || this.NavigationTreeKey != "folder-tree" || this.WindowClosed) return;
            this.SuppressTreeSelectionScroll = WasSuppressed || !Reveal;
            try
            {
                this.SelectSidebarNode(Node);
            }
            finally { this.SuppressTreeSelectionScroll = WasSuppressed; }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            // Address/history navigation must still reveal an already selected folder
            // after the user has manually scrolled it out of view.
            if (Reveal && WasSelected && Version == this.NavigationVersion && !this.WindowClosed)
            {
                ItemsControl Owner = this.FolderTree;
                TreeViewItem? Item = null;
                foreach (var Part in Branch)
                {
                    Item = Owner.ItemContainerGenerator.ContainerFromItem(Part) as TreeViewItem;
                    if (Item is null) break;
                    Owner = Item;
                }
                if (Item is not null) this.QueueTreeItemIntoView(Item);
            }
        }
        finally { if (Version == this.NavigationVersion) this.SyncingTree = false; }
    }

    private static void ClearTreeSelection(FolderNode Node, FolderNode? Except) { if (!ReferenceEquals(Node, Except)) Node.IsSelected = false; foreach (var Child in Node.Children) ClearTreeSelection(Child, Except); }
    private static bool SamePath(string A, string B) => string.Equals(System.IO.Path.TrimEndingDirectorySeparator(A), System.IO.Path.TrimEndingDirectorySeparator(B), StringComparison.OrdinalIgnoreCase);
    private async void TreeItemExpanded(object Sender, RoutedEventArgs E) { if (E.OriginalSource is TreeViewItem { DataContext: FolderNode Node }) { E.Handled = true; await this.EnsureChildrenAsync(Node); } }
    private void TreeItemSelected(object Sender, RoutedEventArgs E)
    {
        if (E.OriginalSource is not TreeViewItem Item) return;
        E.Handled = true;
        if (this.SuppressTreeSelectionScroll) return;
        this.QueueTreeItemIntoView(Item);
    }
    private void QueueTreeItemIntoView(TreeViewItem Item)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (this.WindowClosed || !Item.IsVisible || Item.DataContext is not FolderNode { IsSelected: true } Node
                || !SamePath(Node.FullPath, this.CurrentPath)) return;
            Item.BringIntoView(new Rect(0, 0, Math.Min(180, Item.ActualWidth), 24));
            if (FindAncestor<TreeView>(Item) is { } Tree) FindVisualChild<ScrollViewer>(Tree)?.ScrollToHorizontalOffset(0);
        }, DispatcherPriority.Loaded);
    }
    private static TItem? FindVisualChild<TItem>(DependencyObject Parent) where TItem : DependencyObject
    {
        for (var I = 0; I < VisualTreeHelper.GetChildrenCount(Parent); I++)
        {
            var Child = VisualTreeHelper.GetChild(Parent, I);
            if (Child is TItem Found) return Found;
            if (FindVisualChild<TItem>(Child) is { } Descendant) return Descendant;
        }
        return null;
    }
    private async void FolderTreeSelectedItemChanged(object Sender, RoutedPropertyChangedEventArgs<object> E)
    {
        if (this.SyncingTree || E.NewValue is not FolderNode { IsPlaceholder: false } Node
            || IsCollapsedAncestorSelection(E.OldValue, Node)) return;
        await this.NavigateFromTreeAsync(Node, "folder-tree");
    }
    private async void FavoriteClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (Sender is Button { DataContext: FavoriteLocation { IsVirtual: true } Location })
        {
            await this.NavigateAsync(Location.FullPath, TreeKey: "quick-access");
            return;
        }
        if (Sender is Button Button) await this.OpenSidebarFolderClickAsync(() => this.OpenBookmarkFolderAsync(Button, Navigate: true), Button);
    }
    private async void GoClick(object Sender, RoutedEventArgs E)
    {
        if (await this.NavigateAsync(this.AddressBox.Text)) this.FileGrid.Focus();
        else if (this.AddressEditing) this.AddressBox.Focus();
    }
    private async void AddressBoxKeyDown(object Sender, KeyEventArgs E)
    {
        if (E.Key == System.Windows.Input.Key.Enter) { E.Handled = true; if (await this.NavigateAsync(this.AddressBox.Text)) this.FileGrid.Focus(); }
        else if (E.Key == System.Windows.Input.Key.Escape) { this.CancelAddressEdit(); E.Handled = true; }
    }
    private async void BackClick(object Sender, RoutedEventArgs E) => await this.BackAsync();
    private readonly SemaphoreSlim BrowseHistoryGate = new(1);
    private int BrowseHistoryEpoch;
    internal async Task NavigateBrowseHistoryAsync(bool Forward)
    {
        var Epoch = this.BrowseHistoryEpoch;
        this.CancelAddressNavigation();
        this.CancelSidebarFolderClick();
        await this.BrowseHistoryGate.WaitAsync();
        try
        {
            if (this.WindowClosed || Epoch != this.BrowseHistoryEpoch) return;
            var Index = this.HistoryIndex + (Forward ? 1 : -1);
            if (Index >= 0 && Index < this.History.Count)
                await this.NavigateAsync(this.History[Index], Index, TreeKey: this.HistoryTreeKeys[Index]);
        }
        finally { this.BrowseHistoryGate.Release(); }
    }
    private Task BackAsync() => this.NavigateBrowseHistoryAsync(false);
    private async void ForwardClick(object Sender, RoutedEventArgs E) => await this.ForwardAsync();
    private Task ForwardAsync() => this.NavigateBrowseHistoryAsync(true);
    private async void UpClick(object Sender, RoutedEventArgs E) => await this.UpAsync();
    private async Task UpAsync() { if (!this.IsVirtualLocation && !string.IsNullOrEmpty(this.CurrentPath) && this.Files.GetParent(this.CurrentPath) is { } Parent) await this.NavigateAsync(Parent, TreeKey: this.NavigationTreeKey); }
    private async void RefreshClick(object Sender, RoutedEventArgs E) { this.InvalidateVersionStatuses(); ShellIconService.InvalidateCache(); if (this.NavigationTreeKey == "folder-tree") await this.RefreshRootsAsync(); await this.RefreshAsync(true); }
    private async Task RefreshAsync(bool RefreshTree = false)
    {
        if (string.IsNullOrEmpty(this.CurrentPath) || this.WindowClosed) return;
        if (this.IsComputer) { await this.NavigateAsync(this.CurrentPath, RecordHistory: false, PreserveFilter: true, TreeKey: this.NavigationTreeKey); return; }
        if (this.IsRecycleBin) { await this.RefreshRecycleBinAsync(); return; }
        if (!RefreshTree) { await this.RefreshCurrentFolderAsync(false); return; }
        var Selected = this.SelectedEntries().Select(X => X.FullPath).ToArray();
        if (RefreshTree)
        {
            ++this.TreeExpansionVersion;
            var WasSyncing = this.SyncingTree;
            this.SyncingTree = true;
            try
            {
                var Roots = this.NavigationTreeKey == "folder-tree" ? this.Roots.ToArray()
                    : this.NavigationSection(this.NavigationTreeKey) is { } Section ? this.CategoryFolderRoots(Section) : [];
                foreach (var Root in Roots) ResetNode(Root);
            }
            finally { this.SyncingTree = WasSyncing; }
        }
        if (await this.NavigateAsync(this.CurrentPath, RecordHistory: false, PreserveFilter: true, RevealTree: false,
            SyncNavigationTree: RefreshTree, TreeKey: this.NavigationTreeKey))
            foreach (var Path in Selected) this.SelectPath(Path, Append: true);
    }
    private static void InvalidateLoads(FolderNode Node) { Node.LoadVersion++; Node.LoadTask = null; foreach (var Child in Node.Children) InvalidateLoads(Child); }
    private static void ResetNode(FolderNode Node) { InvalidateLoads(Node); Node.IsLoaded = false; Node.IsExpanded = false; Node.Children.Clear(); Node.Children.Add(new FolderNode("", "", NameKey: "TreeExpandHint")); }
    private void ClearSearchClick(object Sender, RoutedEventArgs E) => this.SearchBox.Clear();
    private void ApplySort()
    {
        if (this.View is null) return;
        using (this.View.DeferRefresh())
        {
            this.View.SortDescriptions.Clear();
            this.View.SortDescriptions.Add(new(nameof(FileEntry.IsDirectory), ListSortDirection.Descending));
            this.View.SortDescriptions.Add(new(this.SortProperty, this.SortDirection));
            if (this.SortProperty != nameof(FileEntry.Name)) this.View.SortDescriptions.Add(new(nameof(FileEntry.Name), ListSortDirection.Ascending));
        }
    }
    private void FileGridSorting(object Sender, DataGridSortingEventArgs E)
    {
        E.Handled = true;
        this.SortProperty = E.Column.SortMemberPath;
        this.SortDirection = E.Column.SortDirection == ListSortDirection.Descending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        foreach (var Column in this.FileGrid.Columns) Column.SortDirection = null;
        E.Column.SortDirection = this.SortDirection;
        this.ApplySort();
    }
    private void UpdateCounts()
    {
        if (this.StatusText is null || this.View is null) return;
        var VisibleEntries = this.View.Cast<FileEntry>().ToArray();
        var Visible = VisibleEntries.Length;
        var Filtered = this.CurrentSearch.IsActive;
        var CountText = Filtered ? LanguageService.Text(this.Searching ? "SearchProgressCount" : "SearchResultsCount", Visible) : LanguageService.Text("ItemsCount", Visible);
        var Selected = this.FileGrid.SelectedItems.Count;
        this.StatusText.Text = CountText + " = " + LanguageService.Text("FolderFileCount", VisibleEntries.Count(X => X.IsDirectory), VisibleEntries.Count(X => !X.IsDirectory));
        this.SelectionStatusText.Text = Selected > 0 ? LanguageService.Text("SelectedCount", Selected) : "";
        this.SelectionStatusText.Visibility = Selected > 0 ? Visibility.Visible : Visibility.Collapsed;
        this.StatusText.Foreground = (Brush)FindResource("Muted");
        this.EmptyState.Visibility = Visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        this.EmptyTitle.Text = LanguageService.Text(this.Searching ? "SearchRunning" : Filtered ? "NoResults" : "EmptyFolder");
        this.EmptyDescription.Text = LanguageService.Text(this.Searching ? "SearchScopeHint" : Filtered ? "NoResultsHint" : "EmptyFolderHint");
        if (this.Loading && this.IsRecycleBin && Visible == 0)
        {
            this.EmptyTitle.Text = LanguageService.Text("Opening");
            this.EmptyDescription.Text = "";
        }
        if (this.CurrentSearch.ErrorKey is { } SearchError)
        {
            this.StatusText.Text = LanguageService.Text(SearchError);
            this.EmptyTitle.Text = LanguageService.Text("SearchPatternError");
            this.EmptyDescription.Text = LanguageService.Text(SearchError);
        }
    }
    private FileEntry[] SelectedEntries() => this.FileGrid.SelectedItems.Cast<FileEntry>().ToArray();
    private void FileGridPreviewKeyDown(object Sender, KeyEventArgs E)
    {
        if (!E.Handled && E.Key == Key.Escape && this.MarqueeOrigin is not null)
        {
            this.EndMarqueeSelection(Cancel: true);
            E.Handled = true;
            return;
        }
        if (E.Handled || E.Key != Key.Escape || E.KeyboardDevice.Modifiers != ModifierKeys.None
            || this.FileGrid.SelectedItems.Count == 0) return;
        this.ClearFileSelection();
        E.Handled = true;
    }

    private void FileGridSelectionChanged(object Sender, SelectionChangedEventArgs E)
    {
        if (!this.ApplyingSearchSelection) this.PendingSearchSelection.Clear();
        if (this.ApplyingMarqueeSelection) return;
        this.UpdateActions();
        this.UpdateCounts();
    }
    private void UpdateActions()
    {
        if (this.BackButton is null) return;
        var HasPath = !string.IsNullOrEmpty(this.CurrentPath);
        this.BackButton.IsEnabled = this.HistoryIndex > 0 && (!this.Loading || this.IsRecycleBin);
        this.ForwardButton.IsEnabled = this.HistoryIndex >= 0 && this.HistoryIndex < this.History.Count - 1 && (!this.Loading || this.IsRecycleBin);
        this.UpButton.IsEnabled = HasPath && !this.IsVirtualLocation && this.Files.GetParent(this.CurrentPath) is not null && !this.Loading;
        this.LoadingBar.Visibility = this.Loading || this.OperationBusy || this.Searching ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(this.HostWindow.ActiveTab, this) && this.HostWindow.PreferencesMenuItem is not null)
            this.HostWindow.PreferencesMenuItem.IsEnabled = !this.Loading && !this.OperationBusy;
    }
    private void SelectPath(string Path, bool Append = false)
    {
        var Entry = this.Entries.FirstOrDefault(X => SamePath(X.FullPath, Path));
        if (!Append) this.PendingSearchSelection.Clear();
        if (Entry is null || this.View?.Contains(Entry) != true)
        {
            if (this.Searching) this.PendingSearchSelection.Add(Path);
            return;
        }
        this.PendingSearchSelection.Remove(Path);
        this.ApplyingSearchSelection = true;
        try
        {
            if (!Append) this.FileGrid.SelectedItems.Clear();
            if (!this.FileGrid.SelectedItems.Contains(Entry)) this.FileGrid.SelectedItems.Add(Entry);
            this.FileGrid.ScrollIntoView(Entry);
        }
        finally { this.ApplyingSearchSelection = false; }
    }
    private async Task OpenSelectedAsync()
    {
        if (this.FileGrid.SelectedItem is not FileEntry Entry || this.Loading) return;
        if (this.IsRecycleBin) { await this.ExecuteRecycleSelectionAsync("properties"); return; }
        if (Entry.IsDirectory) await this.NavigateAsync(Entry.FullPath, TreeKey: this.NavigationTreeKey);
        else { try { SharedFileHistory.Audit("Open", [Entry.FullPath], "Invoked"); this.Files.OpenPath(Entry.FullPath, this.FileSystemWorkingDirectory); } catch (System.Exception Ex) { this.ShowNotice(LanguageService.Text("OpenFileError", FriendlyError(Ex)), true); } }
    }
    private async void OpenClick(object Sender, RoutedEventArgs E) => await this.OpenSelectedAsync();
    private async void FileGridMouseDoubleClick(object Sender, MouseButtonEventArgs E)
    {
        if (FindAncestor<TextBoxBase>(E.OriginalSource as DependencyObject) is not null) return;
        if (E.ChangedButton != MouseButton.Left || FindAncestor<ButtonBase>(E.OriginalSource as DependencyObject) is not null
            || FindAncestor<DataGridRow>(E.OriginalSource as DependencyObject) is null) return;
        E.Handled = true;
        await this.OpenSelectedAsync();
    }
    private void FileGridRightClick(object Sender, MouseButtonEventArgs E)
    {
        this.PendingFileContextEntries = null;
        if (FindAncestor<DataGridColumnHeader>(E.OriginalSource as DependencyObject) is not null) return;
        var Entries = this.FileContextEntries(E.OriginalSource as DependencyObject);
        this.PendingFileContextEntries = Entries;
        this.PendingFileContextNavigation = this.NavigationVersion;
        if (Entries.Length == 0) this.ClearFileSelection();
        else if (!this.FileGrid.SelectedItems.Contains(Entries[0]))
        { this.FileGrid.SelectedItems.Clear(); this.FileGrid.SelectedItem = Entries[0]; }
    }
    private static TItem? FindAncestor<TItem>(DependencyObject? Item) where TItem : DependencyObject
    {
        while (Item is not null) { if (Item is TItem Found) return Found; Item = VisualTreeHelper.GetParent(Item); }
        return null;
    }
    private void CopyCurrentPathClick(object Sender, RoutedEventArgs E) => this.CopyText(this.CurrentPath, LanguageService.Text("CurrentPathCopied"));
    private void CopySelectedPathsClick(object Sender, RoutedEventArgs E) => this.CopyText(string.Join(Environment.NewLine, this.SelectedEntries().Select(X => X.FullPath)), LanguageService.Text("SelectedPathsCopied"));
    private void CopyText(string Text, string Message) { if (string.IsNullOrEmpty(Text)) return; try { this.WriteClipboardText(Text); this.ShowNotice(Message); } catch (System.Exception Ex) { this.ShowNotice(LanguageService.Text("ClipboardError", Ex.Message), true); } }
    private void CopyClick(object Sender, RoutedEventArgs E) => this.CopySelection(false);
    private void CutClick(object Sender, RoutedEventArgs E) => this.CopySelection(true);
    private void CopySelection(bool Cut)
    {
        if (this.IsComputer && Cut) return;
        if (this.IsRecycleBin) { if (!Cut) _ = this.ExecuteRecycleSelectionAsync("copy"); return; }
        this.CopyFilePathsToClipboard(this.SelectedEntries().Select(Entry => Entry.FullPath).ToArray(), Cut);
    }
    private void CopyFilePathsToClipboard(string[] FilePaths, bool Cut)
    {
        // Publishing selected paths does not mutate files. In particular, a pending
        // navigation or operation must not silently keep the previous clipboard.
        if (FilePaths.Length == 0) return;
        try
        {
            var Paths = new StringCollection();
            Paths.AddRange(FilePaths);
            var Data = new DataObject();
            Data.SetFileDropList(Paths);
            Data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(Cut ? 2 : 1)));
            var CutId = Guid.NewGuid().ToString("N");
            if (Cut) Data.SetData(CutClipboardFormat, CutId);
            this.WriteClipboardFiles(Data);
            FailedFileClipboard = null;
            SetCutClipboard(Cut ? new(CutId, FilePaths.ToHashSet(StringComparer.OrdinalIgnoreCase),
                this.ReadClipboardVersion(), this.ReadClipboardVersion, this.ReadClipboardFiles, this.ClearClipboardFiles) : null);
            SharedFileHistory.Audit(Cut ? "Cut" : "CopyToClipboard", FilePaths);
            this.ShowNotice(LanguageService.Text(Cut ? "CutItemsNotice" : "CopiedItemsNotice", FilePaths.Length));
        }
        catch (System.Exception Ex)
        {
            FailedFileClipboard = (this.ReadClipboardVersion(), Ex.Message);
            this.ShowNotice(LanguageService.Text("ClipboardError", Ex.Message), true);
        }
    }
    private async void PasteClick(object Sender, RoutedEventArgs E) => await this.PasteAsync();
    private async Task PasteAsync()
    {
        if (this.OperationBusy || this.Loading || this.IsVirtualLocation || this.CurrentPath.Length == 0) return;
        try
        {
            if (FailedFileClipboard is { } Failed && this.ReadClipboardVersion() == Failed.Version)
            {
                this.ShowNotice(LanguageService.Text("ClipboardError", Failed.Error), true);
                return;
            }
            var Data = this.ReadClipboardFiles();
            var ClipboardVersion = this.ReadClipboardVersion();
            if (Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } Paths) { this.ShowNotice(LanguageService.Text("ClipboardEmpty")); return; }
            var Effect = Data.GetData("Preferred DropEffect");
            var EffectBytes = Effect is MemoryStream Stream ? Stream.ToArray() : Effect as byte[];
            var Move = EffectBytes is { Length: >= 4 } Bytes && (BitConverter.ToInt32(Bytes) & 2) != 0;
            var Destination = this.CurrentPath;
            var Success = await this.TransferFilesAsync(Paths, Destination, Move);
            if (Move && this.ReadClipboardVersion() == ClipboardVersion)
            {
                var Remaining = await Task.Run(() => Paths.Where(Path => File.Exists(Path) || Directory.Exists(Path)).ToArray());
                if (this.ReadClipboardVersion() == ClipboardVersion)
                {
                    if (Remaining.Length == 0) { this.ClearClipboardFiles(); SetCutClipboard(null); }
                    else if (Remaining.Length != Paths.Length)
                    {
                        var Notice = this.StatusText.Text;
                        this.CopyFilePathsToClipboard(Remaining, Cut: true);
                        this.ShowNotice(Notice, !Success);
                    }
                }
            }
        }
        catch (System.Exception Ex) { this.ShowNotice(LanguageService.Text("PasteError", FriendlyError(Ex)), true); }
    }
    private async void NewFolderClick(object Sender, RoutedEventArgs E) => await this.NewFolderAsync();
    private async Task NewFolderAsync()
    {
        if (this.OperationBusy || this.Loading || this.IsVirtualLocation || this.CurrentPath.Length == 0) return;
        this.CancelInlineRename();
        var Parent = this.CurrentPath;
        var Navigation = this.NavigationVersion;
        string? NewPath = null;
        if (await this.RunOperationAsync(LanguageService.Text("CreatingFolder"), () => NewPath = this.CreateDefaultFolder(Parent), LanguageService.Text("FolderCreated"), RequiresSta: true)
            && NewPath is not null && Navigation == this.NavigationVersion) this.BeginFileRename(NewPath);
    }
    private async void RenameClick(object Sender, RoutedEventArgs E) => await this.RenameAsync();
    private Task RenameAsync()
    {
        if (!this.IsVirtualLocation && !this.OperationBusy && !this.Loading && this.SelectedEntries() is { Length: 1 } Entries)
            this.BeginFileRename(Entries[0].FullPath);
        return Task.CompletedTask;
    }
    private async void DeleteClick(object Sender, RoutedEventArgs E) => await this.DeleteAsync();
    private async Task DeleteAsync()
    {
        if (this.IsComputer) return;
        if (this.IsRecycleBin) { await this.ExecuteRecycleSelectionAsync("delete"); return; }
        if (this.OperationBusy || this.Loading) return;
        var Entries = this.SelectedEntries();
        if (Entries.Length == 0) return;
        await this.DeleteShellSelectionAsync(Entries.Select(Entry => Entry.FullPath).ToArray(), Permanently: false);
    }
    private long OperationVersion;
    private async Task<bool> RunOperationAsync(string Progress, System.Action Action, string Success, bool RequiresSta = false)
    {
        if (this.OperationBusy) return false;
        var Version = ++this.OperationVersion;
        ++this.FolderRefreshVersion;
        this.RefreshTimer.Stop();
        this.OperationBusy = true;
        this.UpdateActions();
        this.ShowNotice(Progress);
        System.Exception? Failure = null;
        try
        {
            if (RequiresSta)
            {
                var Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                System.Threading.Thread Thread = new System.Threading.Thread(() => { try { Action(); Completion.SetResult(); } catch (System.Exception Ex) { Completion.SetException(Ex); } }) { IsBackground = true };
                Thread.SetApartmentState(ApartmentState.STA);
                Thread.Start();
                await Completion.Task;
            }
            else await Task.Run(Action);
        }
        catch (System.Exception Ex)
        {
            Failure = Ex is COMException ShellError && ShellContextMenu.IsShellCancellation(ShellError.HResult)
                ? new OperationCanceledException("The Windows operation was cancelled.", Ex) : Ex;
            try { SharedFileHistory.Audit("FileOperation", [], Failure is OperationCanceledException ? "Cancelled" : "Failed", Ex.Message); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        finally { this.OperationBusy = false; this.UpdateActions(); }
        void ShowResult()
        {
            if (Version != this.OperationVersion || this.OperationBusy) return;
            if (Failure is OperationCanceledException) this.ShowNotice(LanguageService.Text("OperationCancelled"));
            else if (Failure is not null) this.ShowNotice(LanguageService.Text("OperationIncomplete", FriendlyError(Failure)), true);
            else this.ShowNotice(Success);
        }
        // End progress before any slow folder/tree refresh, including after Cancel.
        ShowResult();
        try
        {
            if (this.Loading) this.ScheduleRefresh();
            else await this.RefreshCurrentFolderAsync(true);
            foreach (var Tab in this.HostWindow.Tabs.Where(Tab => !ReferenceEquals(Tab, this)).ToArray())
                if (Tab.Loading || Tab.OperationBusy) Tab.ScheduleRefresh();
                else await Tab.RefreshCurrentFolderAsync(false);
        }
        finally { ShowResult(); }
        return Failure is null;
    }
    private void TerminalClick(object Sender, RoutedEventArgs E) { try { this.Files.OpenTerminal(this.CurrentPath); } catch (System.Exception Ex) { this.ShowNotice(FriendlyError(Ex), true); } }
    private void ExplorerClick(object Sender, RoutedEventArgs E)
    {
        try { var Info = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true }; Info.ArgumentList.Add(this.CurrentPath); Process.Start(Info); }
        catch (System.Exception Ex) { this.ShowNotice(FriendlyError(Ex), true); }
    }
    private void HelpClick(object Sender, RoutedEventArgs E) => MessageBox.Show(this.HostWindow,
        this.ShortcutHelpBody(), LanguageService.Text("HelpTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
    private void ShowNotice(string Text, bool Error = false) { if (this.WindowClosed) return; this.StatusText.Text = Text; this.StatusText.Foreground = Error ? new SolidColorBrush(Color.FromRgb(176, 81, 61)) : (Brush)this.FindResource("Muted"); }
    private static string FriendlyError(System.Exception Exception) => Exception switch
    {
        UnauthorizedAccessException => LanguageService.Text("AccessDenied"),
        DirectoryNotFoundException => LanguageService.Text("DirectoryMissing"),
        FileNotFoundException => LanguageService.Text("FileMissing"),
        PathTooLongException => LanguageService.Text("PathTooLong"),
        _ => Exception.Message
    };
    internal async void WindowPreviewKeyDown(object Sender, KeyEventArgs E)
    {
        if (this.HandleInlineRenameKey(E)) return;
        System.Windows.Input.Key Key = E.Key == System.Windows.Input.Key.System ? E.SystemKey : E.Key;
        if (Key == Key.Escape)
        {
            this.DismissFileDragFeedback();
            var InFiles = E.OriginalSource is DependencyObject Origin
                ? FindAncestor<DataGrid>(Origin) == this.FileGrid : this.FileGrid.IsKeyboardFocusWithin;
            var Editing = this.AddressEditing;
            var Tips = this.SearchTips.Visibility == Visibility.Visible;
            if (Editing) this.CancelAddressEdit();
            this.DismissSearchTips();
            var CancelledCut = !this.OperationBusy && this.CancelCutClipboard();
            if (this.MarqueeOrigin is not null) this.EndMarqueeSelection(Cancel: true);
            else if (InFiles && !Editing && !Tips && !CancelledCut) this.ClearFileSelection();
            this.FocusMainFilePanel();
            E.Handled = true;
            return;
        }
        var Modifiers = E.KeyboardDevice.Modifiers;
        if (Key == Key.C && Modifiers == ModifierKeys.Control && this.TryCopySidebarFolderPath(E.OriginalSource as DependencyObject))
        {
            E.Handled = true;
            return;
        }
        var Shortcut = KeyboardShortcut.Match(this.ShortcutBindings, Key, Modifiers);
        if ((Shortcut?.Id is "NewWindow" or "ToggleSidebar" or "Preferences") && E.IsRepeat) { E.Handled = true; return; }
        if (Shortcut?.TerminalOnly == true)
        {
            E.Handled = this.HandleTerminalFontGesture(KeyboardShortcut.Format(Key, Modifiers));
            return;
        }
        var InputSource = E.OriginalSource as DependencyObject;
        var InTerminal = InputSource is null ? this.TerminalSurface?.IsKeyboardFocusWithin == true
            : FindAncestor<CDir.Controls.EmbeddedTerminalControl>(InputSource) is { } Terminal && ReferenceEquals(Terminal, this.TerminalSurface);
        if (InTerminal && Shortcut?.Id is not ("ToggleTerminal" or "ToggleSidebar" or "Search" or "NewWindow" or "SelectAll" or "Preferences")) return;
        var InText = InputSource is null ? Keyboard.FocusedElement is TextBoxBase : FindAncestor<TextBoxBase>(InputSource) is not null;
        var InFileList = InputSource is null ? this.FileGrid.IsKeyboardFocusWithin : FindAncestor<DataGrid>(InputSource) == this.FileGrid;
        if (!CanHandleShortcut(Shortcut, Key, Modifiers, InText, InFileList)) return;
        E.Handled = true;
        await this.HandleShortcutAsync(Key, Modifiers, InText, InFileList);
    }
    private void ConfigureWatcher()
    {
        this.Watcher?.Dispose();
        this.Watcher = null;
        if (this.IsVirtualLocation) return;
        try
        {
            this.Watcher = new FileSystemWatcher(this.CurrentPath) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, IncludeSubdirectories = false };
            FileSystemEventHandler Changed = (_, _) => this.ScheduleRefresh();
            this.Watcher.Changed += Changed; this.Watcher.Created += Changed; this.Watcher.Deleted += Changed;
            this.Watcher.Renamed += (_, _) => this.ScheduleRefresh();
            this.Watcher.Error += (_, _) => this.ScheduleRefresh();
            this.Watcher.EnableRaisingEvents = true;
        }
        catch (System.Exception Ex) when (Ex is IOException or UnauthorizedAccessException or ArgumentException) { /* F5 still refreshes unavailable watcher locations. */ }
    }
    private void ScheduleRefresh()
    {
        if (this.WindowClosed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (this.WindowClosed) return;
            // Search results are a snapshot. Background writes must not restart a full recursive scan.
            if (this.CurrentSearch.IsActive) { this.FolderRefreshPending = true; return; }
            this.RefreshTimer.Stop();
            this.RefreshTimer.Start();
        });
    }
    internal async void PreferencesClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        await this.OpenPreferencesAsync();
    }

    private async Task OpenPreferencesAsync()
    {
        this.HostWindow.FileMenu.IsOpen = false;
        if (this.Loading || this.OperationBusy) return;
        new PreferencesWindow(this.HostWindow, Code => this.ChangeLanguage(Code), this.Settings.ShowGridLines,
            Value => this.ChangeSharedUI(() => this.ChangeGridLines(Value)),
            this.Settings.ShowHidden, this.ChangeShowHiddenAsync, this.Settings.Use12HourClock, this.ChangeTimeFormat,
            this.GetColumnPreferences(), (Key, Value) => this.TryChangeSharedUI(() => this.ChangeColumnVisibility(Key, Value)),
            this.GetFontOptions(), (Key, Value) => this.TryChangeSharedUI(() => this.ChangeUIFontSize(Key, Value)),
            this.RestoreDefaultPreferencesAsync, this.ShortcutBindings, this.ChangeShortcut,
            this.GetTerminalFontSize(), this.GetTerminalBackgroundColor(), this.ChangeTerminalFontSize, this.ChangeTerminalBackgroundColor,
            this.GetTerminalForegroundColor(), this.ChangeTerminalForegroundColor,
            this.Settings.ExitOnClose, this.ChangeExitOnClose, this.Settings.CopyOnDrop, this.ChangeCopyOnDrop,
            this.Settings.CalculateFolderSizes, this.ChangeCalculateFolderSizes,
            this.Settings.ShowBookmarkPaths, Value => this.ChangeSharedUI(() => this.ChangeShowBookmarkPaths(Value)),
            this.Settings.ShowSidebarDriveUsage, Value => this.ChangeSharedUI(() => this.ChangeShowSidebarDriveUsage(Value)),
            this.Settings.AutoFitColumnsOnNavigation, Value => this.ChangeSharedUI(() => this.ChangeAutoFitColumnsOnNavigation(Value)),
            this.Settings.SeparatePinnedTabs, Value => this.ChangeSharedUI(() => this.ChangeSeparatePinnedTabs(Value)),
            this.ReadStartOnLogin(), this.ChangeStartOnLogin).ShowDialog();
        await this.HostWindow.SynchronizePreferencesAsync(this);
    }

    internal void DisposePage()
    {
        if (this.WindowClosed) return;
        this.EndMarqueeSelection();
        this.WindowClosed = true;
        this.CloseRecycleBinView();
        CutClipboardChanged -= this.RefreshCutFeedback;
        this.ClearFileTransferFeedback();
        this.ReleaseTerminalSession();
        this.TerminalSurface?.Dispose();
        this.CancelAddressNavigation();
        this.CancelSidebarFolderClick();
        this.CancelSearch();
        this.NavigationVersion++;
        this.CancelFolderSizes();
        this.Watcher?.Dispose();
        this.RefreshTimer.Stop();
        this.DriveUsageTimer.Stop();
        this.StopVersionControl();
    }

    [DllImport("user32.dll", EntryPoint = "GetClipboardSequenceNumber")]
    private static extern uint GetClipboardSequenceNumber();
}
