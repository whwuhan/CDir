using CDir.Models;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunTreeNavigationSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialFavorites = this.FavoritesSection.IsExpanded;
        var InitialTree = this.DirectoryTreeSection.IsExpanded;
        var InitialLanguage = LanguageService.CurrentLanguage;
        ContextMenu? ActiveMenu = null;
        var MenuStayOpen = new Dictionary<ContextMenu, bool>();
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirTreeNavigation" + FixtureId);
        var Parent = Directory.CreateDirectory(Path.Combine(Fixture, "Parent 中文")).FullName;
        var Target = Directory.CreateDirectory(Path.Combine(Parent, "Target With Spaces " + new string('W', 32))).FullName;
        var Descendant = Directory.CreateDirectory(Path.Combine(Target, "Visible Child")).FullName;
        var Deep = Directory.CreateDirectory(Path.Combine(Fixture, "Another Branch", "Deep", "Destination")).FullName;
        var Marker = Path.Combine(Fixture, ".CDirTreeNavigationOwner");
        File.WriteAllText(Marker, FixtureId);
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Tree navigation UI smoke test failed: " + Name);
            Passed.Add(Name);
            AppMain.RecordSmokeProgress("Tree navigation: " + Name);
        }
        async Task Settle()
        {
            this.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            this.UpdateLayout();
        }
        TreeViewItem Container(FolderNode Node)
        {
            TreeViewItem? Find(ItemsControl Parent)
            {
                foreach (var Child in Parent.Items.OfType<FolderNode>())
                {
                    if (Parent.ItemContainerGenerator.ContainerFromItem(Child) is not TreeViewItem Item) continue;
                    if (ReferenceEquals(Child, Node)) return Item;
                    if (Item.IsExpanded && Find(Item) is { } Match) return Match;
                }
                return null;
            }
            return Find(this.FolderTree) ?? throw new InvalidOperationException("Selected tree item is not rendered.");
        }
        static MouseButtonEventArgs ClickEvent(UIElement Header, RoutedEvent Event, int Count)
        {
            var Args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Event };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(Args, Count);
            Header.RaiseEvent(Args);
            return Args;
        }
        void Click(FolderNode Node, int Count)
        {
            var Item = Container(Node);
            var Header = (UIElement)Item.Template.FindName("PART_Header", Item);
            Check(ClickEvent(Header, Mouse.PreviewMouseDownEvent, Count).Handled, "Tree label press is handled for click count " + Count);
            ClickEvent(Header, Mouse.PreviewMouseUpEvent, Count);
        }
        async Task WaitForClick(string Expected)
        {
            await this.SidebarClickTask;
            var Deadline = DateTime.UtcNow.AddSeconds(10);
            while (this.Loading && DateTime.UtcNow < Deadline) await Task.Delay(20);
            await Settle();
            Check(!this.Loading && SamePath(this.CurrentPath, Expected), "Tree click navigates to " + Path.GetFileName(Expected));
        }
        void CheckRevealed(string Expected, string Name)
        {
            var Selected = this.FolderTree.SelectedItem as FolderNode;
            Check(Selected is not null && SamePath(Selected.FullPath, Expected) && Selected.IsExpanded && Selected.IsLoaded
                && this.DirectoryTreeSection.IsExpanded && this.FolderTree.IsVisible,
                Name + ": the tree section and selected folder are expanded");
            var Item = Container(Selected!);
            var Top = Item.TranslatePoint(new Point(), this.SidebarScroll).Y;
            Check(Item.IsVisible && Top >= -1 && Top + 20 <= this.SidebarScroll.ViewportHeight + 1,
                Name + ": the selected folder header is visible in the outer sidebar viewport");
            var Ancestor = Path.GetDirectoryName(Expected);
            while (Ancestor is not null && AppMain.IsInsideSmokeScope(Ancestor))
            {
                var Node = this.AllVersionNodes().Single(Node => SamePath(Node.FullPath, Ancestor));
                Check(Node.IsExpanded, Name + ": ancestor is expanded: " + Node.Name);
                Ancestor = Path.GetDirectoryName(Ancestor);
            }
        }
        try
        {
            for (var Index = 0; Index < 60; Index++) Directory.CreateDirectory(Path.Combine(Target, $"Folder-{Index:00}"));
            Check(await this.NavigateAsync(Parent), "Open the tree-navigation fixture in the middle panel");
            await Settle();
            var ClickTarget = this.AllVersionNodes().Single(Node => SamePath(Node.FullPath, Target));
            Check(!ClickTarget.IsExpanded && !ClickTarget.IsLoaded, "Click fixture starts with an unopened tree folder");
            var BeforeClickHistory = this.History.Count;
            var ClickItem = Container(ClickTarget);
            var SelectionStates = new List<bool>();
            ClickTarget.PropertyChanged += (_, E) => { if (E.PropertyName == nameof(FolderNode.IsSelected)) SelectionStates.Add(ClickTarget.IsSelected); };
            var TreeScroll = FindVisualChild<ScrollViewer>(this.FolderTree)!;
            TreeScroll.ScrollToHorizontalOffset(24);
            await Settle();
            var HorizontalBefore = TreeScroll.HorizontalOffset;
            var VerticalBefore = this.SidebarScroll.VerticalOffset;
            var BeforeClickVersion = this.NavigationVersion;
            var ClickStarted = false;
            var ImmediateClick = this.OpenSidebarFolderClickAsync(() => { ClickStarted = true; return Task.CompletedTask; });
            Check(ClickStarted && ImmediateClick.IsCompletedSuccessfully,
                "A single-click action starts synchronously without a double-click confirmation delay");
            Click(ClickTarget, 1);
            Check(this.NavigationVersion == BeforeClickVersion + 1 && ReferenceEquals(this.SidebarClickTarget, ClickItem),
                "A directory label starts main-panel navigation immediately on its first click");
            await WaitForClick(Target);
            Check(!ClickTarget.IsExpanded && !ClickTarget.IsLoaded && this.FolderEntries.Any(Entry => SamePath(Entry.FullPath, Descendant))
                && this.History.Count == BeforeClickHistory + 1,
                "A single directory-label click navigates once without changing tree expansion");
            Check(SelectionStates.SequenceEqual(new[] { true }) && ReferenceEquals(ClickItem, Container(ClickTarget)),
                "Navigation retains the selected highlight and tree container without deselecting and reselecting");
            Check(HorizontalBefore > 0 && Math.Abs(TreeScroll.HorizontalOffset - HorizontalBefore) < 0.5
                && Math.Abs(this.SidebarScroll.VerticalOffset - VerticalBefore) < 0.5,
                $"Click navigation preserves the sidebar viewport (horizontal {HorizontalBefore} -> {TreeScroll.HorizontalOffset}, vertical {VerticalBefore} -> {this.SidebarScroll.VerticalOffset})");
            Click(ClickTarget, 1);
            await WaitForClick(Target);
            Check(!ClickTarget.IsExpanded && this.History.Count == BeforeClickHistory + 1,
                "A single click on the already open directory preserves expansion and history");
            var Arrow = (System.Windows.Controls.Primitives.ToggleButton)ClickItem.Template.FindName("Expander", ClickItem);
            var Toggle = (System.Windows.Automation.Provider.IToggleProvider)
                new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(Arrow)
                    .GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
            var ArrowVersion = this.NavigationVersion;
            Toggle.Toggle();
            await Settle();
            Check(ClickTarget.IsExpanded && SamePath(this.CurrentPath, Target) && this.NavigationVersion == ArrowVersion,
                "The expansion arrow opens the current directory without refreshing or navigating the main panel");
            Toggle.Toggle();
            await Settle();
            Check(!ClickTarget.IsExpanded && this.NavigationVersion == ArrowVersion,
                "The expansion arrow closes the current directory without changing the main panel");

            await this.NavigateAsync(Parent);
            var DoubleClickVersion = this.NavigationVersion;
            var DoubleClickHistory = this.History.ToArray();
            var PanelEntries = this.FolderEntries;
            Click(ClickTarget, 1);
            await Task.Delay(40);
            var FirstPress = this.SidebarClickTask;
            Click(ClickTarget, 2);
            await this.SidebarClickTask;
            await FirstPress;
            await Settle();
            Check(ClickTarget.IsExpanded && SamePath(this.CurrentPath, Target) && this.NavigationVersion == DoubleClickVersion + 1
                && this.History.Count == DoubleClickHistory.Length + 1 && !ReferenceEquals(PanelEntries, this.FolderEntries)
                && this.FolderEntries.Any(Entry => SamePath(Entry.FullPath, Descendant)) && ClickTarget.IsSelected,
                "A double-click on another directory expands its tree and navigates the main panel exactly once");
            DoubleClickVersion = this.NavigationVersion;
            Click(ClickTarget, 1);
            Click(ClickTarget, 2);
            await this.SidebarClickTask;
            await Settle();
            Check(!ClickTarget.IsExpanded && SamePath(this.CurrentPath, Target) && this.NavigationVersion == DoubleClickVersion
                && this.History.Count == DoubleClickHistory.Length + 1,
                "A second double-click collapses the current directory while retaining its main-panel location and history");
            await this.NavigateAsync(Parent);
            DoubleClickVersion = this.NavigationVersion;
            ClickEvent(Arrow, Mouse.PreviewMouseDownEvent, 1);
            Toggle.Toggle();
            await this.SidebarClickTask;
            await Settle();
            Check(ClickTarget.IsExpanded && SamePath(this.CurrentPath, Parent) && this.NavigationVersion == DoubleClickVersion,
                "An arrow press only changes expansion while another directory stays open in the main panel");
            Click(ClickTarget, 1);
            var PendingBeforeEditing = this.SidebarClickTask;
            await PendingBeforeEditing;
            this.BeginAddressEdit();
            Check(this.SidebarClickTarget is null && this.AddressEditing && SamePath(this.CurrentPath, Target),
                "Editing the address clears completed tree-click tracking without discarding the draft");
            this.CancelAddressEdit();
            Click(ClickTarget, 1);
            var PendingBeforeSearch = this.SidebarClickTask;
            await PendingBeforeSearch;
            this.SearchBoxGotFocus(this.SearchBox, new RoutedEventArgs());
            Check(this.SidebarClickTarget is null && SamePath(this.CurrentPath, Target),
                "Focusing search clears tree-click tracking without replacing the user's search context");
            this.SearchTips.Visibility = Visibility.Collapsed;
            Click(ClickTarget, 1);
            await WaitForClick(Target);
            Check(ClickTarget.IsExpanded, "A single label click also navigates an already expanded directory without closing it");
            // An expired click pair is an ordinary click rather than an expansion toggle.
            this.SidebarClickDeadline = Environment.TickCount64 - 1;
            Click(ClickTarget, 2);
            await this.SidebarClickTask;
            await Settle();
            Check(ClickTarget.IsExpanded && SamePath(this.CurrentPath, Target),
                "A second press outside the system double-click interval never performs an unexpected expansion toggle");
            await this.OpenSidebarTreeFolderAsync(ClickItem, Navigate: false);
            var FirstClickVersion = this.NavigationVersion;
            // Background UI checks do not activate the window. Exercise the focus
            // notification directly when the desktop cannot grant keyboard focus.
            this.FileGrid.Focus();
            if (!this.FileGrid.IsKeyboardFocusWithin)
            {
                FocusManager.SetFocusedElement(this, this.FileGrid);
                this.FileGridKeyboardFocusWithinChanged(this.FileGrid, new DependencyPropertyChangedEventArgs(UIElement.IsKeyboardFocusWithinProperty, false, true));
            }
            await this.PanelTreeSyncTask;
            await Settle();
            CheckRevealed(Target, "Main Panel Focus");
            Check(ClickTarget.IsSelected && SamePath(this.CurrentPath, Target) && this.NavigationVersion == FirstClickVersion,
                "Main-panel focus restores the actual path in the tree without reloading or navigating the main panel");
            this.FolderTree.Focus();
            await this.NavigateAsync(Parent);
            this.FavoritesSection.IsExpanded = false;
            this.DirectoryTreeSection.IsExpanded = false;
            // Collapsing a selected ancestor makes WPF select its parent. Set up the
            // collapsed fixture without treating that selection change as navigation.
            this.SyncingTree = true;
            try { foreach (var Root in this.Roots) Root.IsExpanded = false; }
            finally { this.SyncingTree = false; }
            this.SelectPath(Target);
            await this.OpenSelectedAsync();
            await Settle();
            Check(SamePath(this.CurrentPath, Target) && !this.FavoritesSection.IsExpanded,
                "Opening a middle-panel folder changes location without expanding Quick Access");
            CheckRevealed(Target, "Open Selected");
            Check(((FolderNode)this.FolderTree.SelectedItem).Children.Any(Node => SamePath(Node.FullPath, Descendant)),
                "The selected folder expands to show its child directories");
            this.SidebarScroll.ScrollToBottom();
            await Settle();
            Check(await this.NavigateAsync(Target), "Direct navigation reopens the already selected location");
            await Settle();
            CheckRevealed(Target, "Reveal Already Selected Folder");

            this.DirectoryTreeSection.IsExpanded = false;
            ((FolderNode)this.FolderTree.SelectedItem).IsExpanded = false;
            await this.RefreshAsync();
            await Settle();
            Check(!this.DirectoryTreeSection.IsExpanded && !((FolderNode)this.FolderTree.SelectedItem).IsExpanded,
                "Background refresh preserves manually collapsed folders and sections");
            this.SidebarScroll.ScrollToBottom();
            await Settle();
            await this.HandleShortcutAsync(Key.F5, ModifierKeys.None, InText: false, InFileList: false);
            await Settle();
            CheckRevealed(Target, "F5 Refresh Of Already Selected Folder");
            Check(await this.NavigateAsync(Deep), "Navigate directly to a previously unopened deep folder");
            await Settle();
            CheckRevealed(Deep, "Direct Navigation");

            this.DirectoryTreeSection.IsExpanded = false;
            await this.BackAsync();
            await Settle();
            CheckRevealed(Target, "Back");
            this.DirectoryTreeSection.IsExpanded = false;
            await this.ForwardAsync();
            await Settle();
            CheckRevealed(Deep, "Forward");

            async Task<ContextMenu> OpenFolderMenu(FolderNode Node)
            {
                if (ActiveMenu is not null)
                {
                    ActiveMenu.IsOpen = false;
                    await Settle();
                }
                var Item = Container(Node);
                Item.BringIntoView(new Rect(0, 0, Math.Min(180, Item.ActualWidth), 24));
                await Settle();
                ActiveMenu = Item.ContextMenu ?? throw new InvalidOperationException("Folder menu is missing.");
                // Keep owned menu assertions independent of desktop mouse capture changes.
                MenuStayOpen.TryAdd(ActiveMenu, ActiveMenu.StaysOpen);
                ActiveMenu.StaysOpen = true;
                ActiveMenu.PlacementTarget = Item;
                ActiveMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                // A language change can still be closing the reused popup on the
                // current desktop. Let that transition finish before reopening it.
                for (var Attempt = 0; Attempt < 3; Attempt++)
                {
                    ActiveMenu.IsOpen = true;
                    await Settle();
                    await Task.Delay(50);
                    if (ActiveMenu.IsOpen && ActiveMenu.IsVisible) break;
                    ActiveMenu.IsOpen = false;
                    await Settle();
                    await Task.Delay(50);
                }
                Check(ActiveMenu.IsOpen && ActiveMenu.IsVisible && ReferenceEquals(ActiveMenu.DataContext, Node)
                    && ActiveMenu.Items.OfType<MenuItem>().Select(Action => Action.Tag?.ToString())
                        .SequenceEqual(new[] { "ExpandAllSubfolders", "CollapseAllSubfolders", "NewFolder" })
                    && ActiveMenu.Items[2] is Separator,
                    $"Drive or folder menu targets the clicked node and separates New Folder below collapse (open={ActiveMenu.IsOpen}, visible={ActiveMenu.IsVisible}, target={ActiveMenu.DataContext is FolderNode TargetNode && ReferenceEquals(TargetNode, Node)})");
                return ActiveMenu;
            }
            static void Invoke(ContextMenu Menu, string Key) => Menu.Items.OfType<MenuItem>()
                .Single(Action => Action.Tag?.ToString() == Key).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            static IEnumerable<FolderNode> Branch(FolderNode Root)
            {
                var Pending = new Stack<FolderNode>();
                Pending.Push(Root);
                while (Pending.TryPop(out var Node))
                {
                    if (Node.IsPlaceholder) continue;
                    yield return Node;
                    foreach (var Child in Node.Children) Pending.Push(Child);
                }
            }
            var ParentNode = this.AllVersionNodes().Single(Node => SamePath(Node.FullPath, Parent));
            var OtherBranch = this.AllVersionNodes().Single(Node => SamePath(Node.FullPath, Deep));
            var RootMenu = await OpenFolderMenu(this.Roots[0]);
            foreach (var Language in LanguageService.SupportedLanguages)
            {
                this.ChangeLanguage(Language.Code, Persist: false);
                RootMenu = await OpenFolderMenu(this.Roots[0]);
                Check(RootMenu.Items.OfType<MenuItem>().All(Action => Action.Header?.ToString() == LanguageService.Text((string)Action.Tag)),
                    Language.Code + " drive menu labels are localized");
            }
            this.ChangeLanguage(InitialLanguage, Persist: false);
            var FolderMenu = await OpenFolderMenu(ParentNode);
            Invoke(FolderMenu, "ExpandAllSubfolders");
            await this.TreeExpansionTask;
            await Settle();
            Check(Branch(ParentNode).All(Node => Node.IsLoaded && Node.IsExpanded)
                && Branch(ParentNode).Any(Node => SamePath(Node.FullPath, Descendant))
                && SamePath(this.CurrentPath, Deep) && OtherBranch.IsExpanded,
                "Expand all loads previously unopened descendants without navigating or changing other branches");
            await this.NavigateAsync(Descendant);
            FolderMenu = await OpenFolderMenu(ParentNode);
            Invoke(FolderMenu, "CollapseAllSubfolders");
            await Settle();
            Check(Branch(ParentNode).All(Node => !Node.IsExpanded) && SamePath(this.CurrentPath, Descendant)
                && OtherBranch.IsExpanded,
                "Collapse All closes the current directory's ancestors and descendants without navigating the main panel");
            var Unloaded = new FolderNode("Unloaded", Path.Combine(Fixture, "Not Enumerated"));
            ParentNode.Children.Add(Unloaded);
            FolderMenu = await OpenFolderMenu(ParentNode);
            Invoke(FolderMenu, "ExpandAllSubfolders");
            Invoke(FolderMenu, "CollapseAllSubfolders");
            await this.TreeExpansionTask;
            await Settle();
            Check(Branch(ParentNode).All(Node => !Node.IsExpanded) && !Unloaded.IsLoaded,
                "Collapse cancels an in-flight recursive expansion and does not load unopened directories");
            ParentNode.Children.Remove(Unloaded);
            await this.NavigateAsync(Deep);
            await Settle();

            if (AppMain.ScreenshotPath is { } Screenshot)
            {
                var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(this);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Screenshot)!, "TreeNavigation.png"));
                Encoder.Save(Stream);
            }
        }
        finally
        {
            if (ActiveMenu is not null) ActiveMenu.IsOpen = false;
            foreach (var Menu in MenuStayOpen) Menu.Key.StaysOpen = Menu.Value;
            this.ChangeLanguage(InitialLanguage, Persist: false);
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.FavoritesSection.IsExpanded = InitialFavorites;
            this.DirectoryTreeSection.IsExpanded = InitialTree;
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != FixtureId
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned tree-navigation fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
