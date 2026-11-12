using CDir.UI.Dialogs;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private readonly List<string> CompletedSmokeGroups = [];
    private readonly List<string> SkippedSmokeGroups = [];
    private KeyboardDevice? SmokeKeyboard;
    private KeyboardDevice UnmodifiedSmokeKeyboard => this.SmokeKeyboard ??= new SmokeKeyboardDevice();

    // Routed test events must not inherit modifier keys from the user's active window.
    private sealed class SmokeKeyboardDevice(ModifierKeys SimulatedModifiers = ModifierKeys.None) : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key Key) => Key switch
        {
            Key.LeftCtrl when SimulatedModifiers.HasFlag(ModifierKeys.Control) => KeyStates.Down,
            Key.LeftShift when SimulatedModifiers.HasFlag(ModifierKeys.Shift) => KeyStates.Down,
            Key.LeftAlt when SimulatedModifiers.HasFlag(ModifierKeys.Alt) => KeyStates.Down,
            _ => KeyStates.None
        };
    }

    private async Task<List<string>> RunSmokeChecksAsync()
    {
        // Layout/selection checks use settled geometry; motion has its own timed
        // checks with animations explicitly enabled on the real disclosure hosts.
        CDir.Controls.AnimatedDisclosure.SetAnimationsEnabled(this.HostWindow, false);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((Sender, _) => CDir.Controls.AnimatedDisclosure.SetAnimationsEnabled((Window)Sender, false)));
        var Passed = new List<string>();
        var Group = AppMain.SmokeGroup;
        async Task RunGroup(string Name, Func<Task> Run)
        {
            AppMain.RecordSmokeProgress("Starting group: " + Name);
            var Before = Passed.Count;
            await Run();
            if (Passed.Count > Before) this.CompletedSmokeGroups.Add(Name);
            else this.SkippedSmokeGroups.Add(Name);
            AppMain.RecordSmokeProgress("Finished group: " + Name);
        }
        if (Group is "navigation" or "all") await RunGroup("navigation", async () => { await this.RunNavigationSmokeChecksAsync(Passed); await this.RunMainPanelNavigationSmokeChecksAsync(Passed); await this.RunQuickAccessSmokeChecksAsync(Passed); });
        else
        {
            if (this.CurrentPath.Length == 0 || this.View is null || !SamePath(this.AddressBox.Text, this.CurrentPath)
                || this.FolderTree.SelectedItem is not FolderNode Node || !SamePath(Node.FullPath, this.CurrentPath))
                throw new InvalidOperationException("UI smoke test failed: initial navigation is not synchronized");
            Passed.Add("initial navigation and directory tree are synchronized");
        }
        if (Group is "tabs" or "all") await RunGroup("tabs", () => this.RunTabsSmokeChecksAsync(Passed));
        if (Group is "panes" or "all") await RunGroup("panes", async () => { await this.RunPaneSmokeChecksAsync(Passed); await this.RunPaneBalanceSmokeChecksAsync(Passed); });
        if (Group is "lifecycle" or "all") await RunGroup("lifecycle", () => this.RunLifecycleSmokeChecksAsync(Passed));
        if (Group is "selection" or "all") await RunGroup("selection", () => this.RunMarqueeSmokeChecksAsync(Passed));
        if (Group is "transfer" or "all") await RunGroup("transfer", () => this.RunFileTransferSmokeChecksAsync(Passed));
        if (Group is "sidebar" or "all") await RunGroup("sidebar", () => this.RunSidebarSmokeChecksAsync(Passed));
        if (Group is "address" or "all") await RunGroup("address", () => this.RunAddressBreadcrumbSmokeChecksAsync(Passed));
        if (Group is "operations" or "all") await RunGroup("operations", () => this.RunExtendedSmokeChecksAsync(Passed));
        if (Group is "view-zoom" or "all") await RunGroup("view-zoom", () => this.RunViewZoomSmokeAsync(Passed));
        if (Group is "grouping" or "all") await RunGroup("grouping", async () => { await this.RunGroupingMenuSmokeAsync(Passed); await this.RunGroupedGridLinesSmokeAsync(Passed); });
        if (Group is "columns" or "all") await RunGroup("columns", () => this.RunColumnsSmokeChecksAsync(Passed));
        if (Group is "fonts" or "all") await RunGroup("fonts", () => this.RunFontsSmokeChecksAsync(Passed));
        if (Group is "search" or "all") await RunGroup("search", () => this.RunSearchSmokeChecksAsync(Passed));
        if (Group is "language" or "all") await RunGroup("language", () => this.RunLanguageSmokeChecksAsync(Passed));
        if (Group is "shortcuts" or "all") await RunGroup("shortcuts", () => this.RunShortcutsSmokeChecksAsync(Passed));
        if (Group is "shell-icons" or "all") await RunGroup("shell-icons", () => this.RunShellIconSmokeChecksAsync(Passed));
        if (Group is "version-control" or "all") await RunGroup("version-control", () => this.RunVersionControlSmokeChecksAsync(Passed));
        if (Group is "terminal" or "all") await RunGroup("terminal", () => this.RunTerminalSmokeChecksAsync(Passed));
        await this.RefreshAsync(Group == "all");
        this.FileGrid.SelectedItems.Clear();
        return Passed;
    }

    private async Task RunNavigationSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name) { if (!Condition) throw new InvalidOperationException("UI smoke test failed: " + Name); Passed.Add(Name); AppMain.RecordSmokeProgress("Navigation: " + Name); }
        Check(this.CurrentPath.Length > 0 && this.View is not null, "initial folder loaded");
        Check(SamePath(this.AddressBox.Text, this.CurrentPath), "address reflects current folder");
        Check(this.FolderTree.SelectedItem is FolderNode SelectedNode && SamePath(SelectedNode.FullPath, this.CurrentPath), "directory tree follows navigation");
        var Initial = this.CurrentPath;
        var Child = this.Entries.FirstOrDefault(X => X.IsDirectory);
        if (Child is not null)
        {
            Check(await this.NavigateAsync(Child.FullPath), "enter child folder");
            await this.BackAsync(); Check(SamePath(this.CurrentPath, Initial), "back navigation");
            await this.ForwardAsync(); Check(SamePath(this.CurrentPath, Child.FullPath), "forward navigation");
            await this.UpAsync(); Check(SamePath(this.CurrentPath, Initial), "parent navigation");
        }
        this.SearchBox.Text = "__cdir_no_match_7b90__";
        Check(this.View!.IsEmpty && this.EmptyState.Visibility == Visibility.Visible, "filter and empty state");
        this.SearchBox.Clear(); Check(this.View.Cast<FileEntry>().Count() == this.Entries.Count, "clear filter");
        var File = this.Entries.FirstOrDefault(X => !X.IsDirectory);
        if (File is not null)
        {
            Check(await this.NavigateAsync('"' + File.FullPath + '"'), "quoted file path navigation");
            Check(this.FileGrid.SelectedItem is FileEntry Selected && SamePath(Selected.FullPath, File.FullPath), "file path selects file");
        }
        var Invalid = System.IO.Path.Combine(Initial, "__cdir_missing_7b90__");
        Check(!await this.NavigateAsync(Invalid) && SamePath(this.CurrentPath, Initial), "invalid path preserves current location");
        Check(await this.NavigateAsync(Initial), "recover after invalid path");
        await this.RunTreeNavigationSmokeChecksAsync(Passed);
        await this.RunDriveUsageSmokeChecksAsync(Passed);
        await this.RunNaturalNameSortSmokeChecksAsync(Passed);
    }

    private async Task RunSidebarSmokeChecksAsync(List<string> Passed)
    {
        await this.RunRecycleBinShortcutSmokeChecksAsync(Passed);
        await this.RunSidebarResizeSmokeChecksAsync(Passed);
        await this.RunCategoryExpansionSmokeChecksAsync(Passed);
        await this.RunCategoryTreeNavigationSmokeChecksAsync(Passed);
        await this.RunCategoryFolderSmokeChecksAsync(Passed);
        await this.RunCategoryFolderRemovalSmokeChecksAsync(Passed);
        await this.RunMainPanelFolderDragSmokeChecksAsync(Passed);
        await this.RunSidebarCopySmokeChecksAsync(Passed);
        await this.RunSidebarFolderAddSmokeChecksAsync(Passed);
        await this.RunAddressFolderDragSmokeChecksAsync(Passed);
        await this.RunSidebarDragSmokeChecksAsync(Passed);
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Sidebar UI smoke test failed: " + Name);
            Passed.Add(Name);
        }

        void ToggleSection(Expander Section)
        {
            var Header = FindVisualChild<System.Windows.Controls.Primitives.ToggleButton>(Section)
                ?? throw new InvalidOperationException("Sidebar section header is missing.");
            var Peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(Header);
            ((System.Windows.Automation.Provider.IToggleProvider)Peer).Toggle();
            UpdateLayout();
        }

        var InitialPath = this.CurrentPath;
        var InitialFavorites = this.FavoritesSection.IsExpanded;
        var InitialTree = this.DirectoryTreeSection.IsExpanded;
        try
        {
            UpdateLayout();
            var TreeTop = this.DirectoryTreeSection.TranslatePoint(new Point(), this).Y;
            ToggleSection(this.FavoritesSection);
            Check(!this.FavoritesSection.IsExpanded && !this.FavoritesList.IsVisible && this.FolderTree.IsVisible
                && this.DirectoryTreeSection.TranslatePoint(new Point(), this).Y < TreeTop,
                "collapsing favorites frees space for the directory tree");
            ToggleSection(this.DirectoryTreeSection);
            Check(!this.DirectoryTreeSection.IsExpanded && !this.FolderTree.IsVisible
                && FindVisualChild<System.Windows.Controls.Primitives.ToggleButton>(this.FavoritesSection)!.IsVisible
                && FindVisualChild<System.Windows.Controls.Primitives.ToggleButton>(this.DirectoryTreeSection)!.IsVisible,
                "both sidebar sections collapse independently and keep headers visible");
            await this.RefreshAsync();
            Check(!this.FavoritesSection.IsExpanded && !this.DirectoryTreeSection.IsExpanded,
                "background refresh preserves collapsed sidebar sections");
            Check(await this.NavigateAsync(InitialPath) && !this.FavoritesSection.IsExpanded && this.DirectoryTreeSection.IsExpanded,
                "explicit navigation opens the directory tree while preserving collapsed quick access");
            ToggleSection(this.FavoritesSection);
            Check(this.FavoritesList.IsVisible && this.FolderTree.IsVisible
                && this.FolderTree.SelectedItem is FolderNode Node && SamePath(Node.FullPath, InitialPath)
                && this.FolderTree.ActualHeight > 0,
                "expanding sidebar sections restores contents and the current directory selection");
            await this.RunTreeBranchSmokeChecksAsync(Passed);
            await this.RunSidebarScrollSmokeChecksAsync(Passed);
            await this.RunCategoryHorizontalScrollSmokeChecksAsync(Passed);
            await this.RunStickyAncestorSmokeChecksAsync(Passed);
            await this.RunTreeMotionSmokeChecksAsync(Passed);
            await this.RunSidebarLayoutSmokeChecksAsync(Passed);
        }
        finally
        {
            this.FavoritesSection.IsExpanded = InitialFavorites;
            this.DirectoryTreeSection.IsExpanded = InitialTree;
        }
    }

    // Invoked only by the explicit --smoke-test path. All mutations are scoped to
    // this run's owned temporary directory; clipboard and recycle bin are untouched.
    private async Task RunExtendedSmokeChecksAsync(List<string> Passed)
    {
        await this.RunFileLaunchSmokeChecksAsync(Passed);
        await this.RunFileHistorySmokeChecksAsync(Passed);
        this.RunWindowAppearanceSmokeChecks(Passed);
        await this.RunShellContextMenuSmokeChecksAsync(Passed);
        await this.RunSharedUIPreferencesSmokeChecksAsync(Passed);
        await this.RunPreferencesSmokeChecksAsync(Passed);
        await this.RunCustomCategorySmokeChecksAsync(Passed);
        await this.RunTimeFormatSmokeChecksAsync(Passed);
        await this.RunGridLinesSmokeChecksAsync(Passed);
        var InitialPath = this.CurrentPath;
        var InitialHidden = this.Settings.ShowHidden;
        var InitialSort = this.SortProperty;
        var InitialDirection = this.SortDirection;
        var InitialColumnDirections = this.FileGrid.Columns.Select(Column => Column.SortDirection).ToArray();
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UI-" + FixtureId[..8]));
        var Marker = System.IO.Path.Combine(Fixture, ".CDirUIOwner");
        System.IO.Directory.CreateDirectory(Fixture);
        System.IO.File.WriteAllText(Marker, FixtureId);
        System.IO.File.SetAttributes(Marker, FileAttributes.Hidden);

        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("UI smoke test failed: " + Name);
            Passed.Add(Name);
        }

        FolderNode CurrentTreeNode()
        {
            FolderNode? Find(IEnumerable<FolderNode> Nodes)
            {
                foreach (var Node in Nodes)
                {
                    if (SamePath(Node.FullPath, this.CurrentPath)) return Node;
                    if (Find(Node.Children) is { } Found) return Found;
                }
                return null;
            }
            return Find(this.Roots) ?? throw new InvalidOperationException("Current directory is missing from the directory tree.");
        }

        try
        {
            var Child = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Fixture, "child")).FullName;
            System.IO.File.WriteAllText(System.IO.Path.Combine(Fixture, "alpha.txt"), "a");
            System.IO.File.WriteAllText(System.IO.Path.Combine(Fixture, "zeta.txt"), "longer contents");
            var HiddenFile = System.IO.Path.Combine(Fixture, "hidden.txt");
            System.IO.File.WriteAllText(HiddenFile, "hidden");
            System.IO.File.SetAttributes(HiddenFile, FileAttributes.Hidden);
            var HiddenFolder = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Fixture, "hidden-folder")).FullName;
            System.IO.File.SetAttributes(HiddenFolder, FileAttributes.Hidden);

            this.Settings.ShowHidden = false;
            Check(await this.NavigateAsync(Fixture), "extended fixture navigation");
            Check(this.Entries.Count == 3 && !this.Entries.Any(Entry => Entry.IsHidden), "hidden items excluded by default");

            await this.AcceptSmokeInlineRenameAsync(this.NewFolderAsync, "Cancelled", Confirm: false);
            Check(Directory.Exists(Path.Combine(Fixture, LanguageService.Text("NewFolder"))) && !Directory.Exists(Path.Combine(Fixture, "Cancelled")),
                "Escape leaves the newly created folder under its default name");
            await this.AcceptSmokeInlineRenameAsync(this.NewFolderAsync, "新建 测试");
            var CreatedPath = System.IO.Path.Combine(Fixture, "新建 测试");
            Check(System.IO.Directory.Exists(CreatedPath), "inline new-folder editor renames the created directory");
            Check(this.FileGrid.SelectedItem is FileEntry Created && SamePath(Created.FullPath, CreatedPath), "new folder is selected after refresh");

            await this.AcceptSmokeInlineRenameAsync(this.RenameAsync, "Cancelled", Confirm: false);
            Check(Directory.Exists(CreatedPath) && !Directory.Exists(Path.Combine(Fixture, "Cancelled")), "Escape cancels renaming and preserves the original directory");
            await this.AcceptSmokeInlineRenameAsync(this.RenameAsync, "重命名 测试");
            var RenamedPath = System.IO.Path.Combine(Fixture, "重命名 测试");
            Check(System.IO.Directory.Exists(RenamedPath) && !System.IO.Directory.Exists(CreatedPath), "inline editor renames directory");
            Check(this.FileGrid.SelectedItem is FileEntry Renamed && SamePath(Renamed.FullPath, RenamedPath), "renamed item is selected after refresh");

            var SizeColumn = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Size));
            SizeColumn.SortDirection = null;
            this.FileGridSorting(this.FileGrid, new DataGridSortingEventArgs(SizeColumn));
            var Sorted = this.View!.Cast<FileEntry>().ToList();
            Check(Sorted.TakeWhile(Entry => Entry.IsDirectory).Count() == Sorted.Count(Entry => Entry.IsDirectory), "sorting keeps folders before files");
            Check(Sorted.Where(Entry => !Entry.IsDirectory).Select(Entry => Entry.Name).SequenceEqual(new[] { "zeta.txt", "alpha.txt" }), "size column initially sorts by descending numeric file size");

            var TreeNode = CurrentTreeNode();
            await this.EnsureChildrenAsync(TreeNode);
            Check(!TreeNode.Children.Any(Node => SamePath(Node.FullPath, HiddenFolder)), "directory tree excludes hidden folders");

            var HiddenSettingsFile = System.IO.Path.Combine(Child, "settings.json");
            await this.RunPreferencesSmokeChecksAsync(Passed, ShowHidden: true);
            TreeNode = CurrentTreeNode();
            await this.EnsureChildrenAsync(TreeNode);
            Check(this.Entries.Any(Entry => SamePath(Entry.FullPath, HiddenFile)) && TreeNode.Children.Any(Node => SamePath(Node.FullPath, HiddenFolder)), "show-hidden refresh updates list and tree");
            Check(this.SaveSettings(HiddenSettingsFile) && ReadSettings(HiddenSettingsFile).ShowHidden,
                "Hidden items enabled from Preferences persists to settings");

            await this.RunPreferencesSmokeChecksAsync(Passed, ShowHidden: false);
            TreeNode = CurrentTreeNode();
            await this.EnsureChildrenAsync(TreeNode);
            Check(!this.Entries.Any(Entry => Entry.IsHidden) && !TreeNode.Children.Any(Node => SamePath(Node.FullPath, HiddenFolder)), "hide-hidden refresh updates list and tree");
            Check(this.SaveSettings(HiddenSettingsFile) && !ReadSettings(HiddenSettingsFile).ShowHidden,
                "Hidden items disabled from Preferences persists to settings");

            Check(await this.NavigateAsync(@".\child") && SamePath(this.CurrentPath, Child), "relative child path navigation");
            Check(SamePath(this.AddressBox.Text, Child) && this.FolderTree.SelectedItem is FolderNode SelectedChild && SamePath(SelectedChild.FullPath, Child), "relative navigation synchronizes address and tree");
            Check(await this.NavigateAsync("..") && SamePath(this.CurrentPath, Fixture), "relative parent path navigation");

            var OlderNavigation = this.NavigateAsync(Child);
            var LatestNavigation = this.NavigateAsync(Fixture);
            await Task.WhenAll(OlderNavigation, LatestNavigation);
            Check(SamePath(this.CurrentPath, Fixture) && SamePath(this.AddressBox.Text, Fixture), "latest asynchronous navigation wins");
        }
        finally
        {
            this.Settings.ShowHidden = InitialHidden;
            this.SortProperty = InitialSort;
            this.SortDirection = InitialDirection;
            for (var I = 0; I < this.FileGrid.Columns.Count; I++) this.FileGrid.Columns[I].SortDirection = InitialColumnDirections[I];
            try
            {
                if (!this.WindowClosed && !string.IsNullOrEmpty(InitialPath)) await this.NavigateAsync(InitialPath, RecordHistory: false);
            }
            finally
            {
                var TempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                if (!Fixture.StartsWith(TempRoot, StringComparison.OrdinalIgnoreCase)
                    || System.IO.Path.GetFileName(Fixture) != "UI-" + FixtureId[..8]
                    || !System.IO.File.Exists(Marker) || System.IO.File.ReadAllText(Marker) != FixtureId
                    || (System.IO.File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Refusing cleanup outside the owned UI smoke fixture.");
                System.IO.Directory.Delete(Fixture, true);
            }
        }
        await this.RunInlineRenameSmokeChecksAsync(Passed);
        await this.RunOperationRefreshSmokeChecksAsync(Passed);
        await this.RunGeneratedFilesRefreshSmokeAsync(Passed);
        await this.RunShellCommandViewportSmokeAsync(Passed);
        await this.RunShellDeletionRefreshSmokeChecksAsync(Passed);
        await this.RunTreeFolderCreationSmokeChecksAsync(Passed);
    }

    private static void CloseSmokeDialogWithEscape(Window Dialog, UIElement Source)
    {
        var Escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Dialog), 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Source.RaiseEvent(Escape);
        if (!Escape.Handled || Dialog.IsVisible || Dialog.DialogResult == true)
            throw new InvalidOperationException("Escape must close the dialog without confirming its action.");
    }

    private async Task AcceptSmokeNameDialogAsync(Func<Task> Action, string ExpectedTitle, string Value, bool ConfirmDialog = true)
    {
        System.Exception? AutomationFailure = null;
        var Confirmed = false;
        NameDialog? FindDialog() => Application.Current.Windows.OfType<NameDialog>()
            .FirstOrDefault(Dialog => ReferenceEquals(Dialog.Owner, this.HostWindow));

        var Timeout = new DispatcherTimer(DispatcherPriority.Send, Dispatcher) { Interval = TimeSpan.FromSeconds(8) };
        Timeout.Tick += (_, _) =>
        {
            Timeout.Stop();
            AutomationFailure = new TimeoutException("Timed out waiting to confirm smoke-test dialog: " + ExpectedTitle);
            if (FindDialog() is { } Dialog) Dialog.DialogResult = false;
        };

        // Some actions prepare their suggested name off the UI thread before
        // opening the modal dialog. Wait for the actual dialog to appear.
        var Pending = new DispatcherTimer(DispatcherPriority.ContextIdle, Dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
        Pending.Tick += (_, _) =>
        {
            NameDialog? Dialog = null;
            try
            {
                Dialog = FindDialog();
                if (Dialog is not { IsVisible: true }) return;
                Pending.Stop();
                CheckSquareWindowCorners(Dialog);
                if (Dialog.Title != ExpectedTitle)
                    throw new InvalidOperationException("Name dialog title does not match the current language: " + Dialog.Title);
                System.Windows.Controls.Panel Panel = (System.Windows.Controls.Panel)Dialog.Content;
                Panel.Children.OfType<TextBox>().Single().Text = Value;
                var Buttons = Panel.Children.OfType<System.Windows.Controls.Panel>().SelectMany(Container => Container.Children.OfType<System.Windows.Controls.Button>()).ToArray();
                System.Windows.Controls.Button Confirm = Buttons.Single(Button => Button.IsDefault);
                System.Windows.Controls.Button Cancel = Buttons.Single(Button => Button.IsCancel);
                if (Confirm.Content?.ToString() != LanguageService.Text("CommonConfirm") || Cancel.Content?.ToString() != LanguageService.Text("CommonCancel"))
                    throw new InvalidOperationException("Name dialog buttons do not match the current language.");
                if (ConfirmDialog) Confirm.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                else CloseSmokeDialogWithEscape(Dialog, Panel.Children.OfType<TextBox>().Single());
                Confirmed = true;
                Timeout.Stop();
            }
            catch (System.Exception Ex)
            {
                AutomationFailure = Ex;
                if (Dialog is { IsVisible: true }) Dialog.DialogResult = false;
            }
        };

        Timeout.Start();
        Pending.Start();
        try { await Action(); }
        finally
        {
            Timeout.Stop();
            Pending.Stop();
        }
        if (AutomationFailure is not null) throw new InvalidOperationException("Could not automate the real name dialog.", AutomationFailure);
        if (!Confirmed) throw new InvalidOperationException("The name operation returned without showing its dialog: " + ExpectedTitle);
    }
}
