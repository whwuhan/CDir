using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunTabsSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Tabs UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        var Shell = this.HostWindow;
        Button HeaderButton(ExplorerTab Page, bool Close = false)
        {
            Shell.UpdateLayout();
            var Index = Shell.Tabs.IndexOf(Page);
            var Container = Shell.TabHeaders.ItemContainerGenerator.ContainerFromIndex(Index) as ContentPresenter
                ?? throw new InvalidOperationException("The tab header was not rendered.");
            var Surface = FindVisualChild<Border>(Container)
                ?? throw new InvalidOperationException("The tab header surface is missing.");
            var Buttons = ((Grid)Surface.Child).Children.OfType<Button>().ToArray();
            return Close ? Buttons.Single(Button => Button.Name == "CloseTabButton") : Buttons[0];
        }
        MouseButtonEventArgs Press(UIElement Target, int ClickCount)
        {
            var Args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(Args, ClickCount);
            Target.RaiseEvent(Args);
            return Args;
        }
        async Task RenameAlias(ExplorerTab Page, string Alias, bool Confirm = true)
        {
            // Navigation queues focus restoration; finish it before opening a popup
            // so that a pending focus operation does not dismiss the inactive menu.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var Surface = (Border)((Grid)HeaderButton(Page).Parent).Parent;
            var Menu = Surface.ContextMenu;
            var PreviousStaysOpen = Menu.StaysOpen;
            Menu.StaysOpen = true;
            try
            {
            Menu.PlacementTarget = Surface;
            Menu.IsOpen = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var Rename = Menu.Items.OfType<MenuItem>().Single(Item => Item.Tag?.ToString() == "RenameTab");
            Check(Menu.IsVisible && Rename.Header?.ToString() == LanguageService.Text("RenameTab"),
                $"Right-click menu exposes the localized tab alias command (open={Menu.IsOpen}, visible={Menu.IsVisible}, header={Rename.Header}, expected={LanguageService.Text("RenameTab")}, targetVisible={Surface.IsVisible}, data={Menu.DataContext?.GetType().Name})");
            await this.AcceptSmokeNameDialogAsync(() =>
            {
                Menu.IsOpen = false;
                Rename.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                return Task.CompletedTask;
            }, LanguageService.Text("RenameTab"), Alias, Confirm);
            }
            finally { Menu.IsOpen = false; Menu.StaysOpen = PreviousStaysOpen; }
        }
        void CheckHeaders(string Name)
        {
            Shell.UpdateLayout();
            Check(Shell.TabHeaders.Items.Count == Shell.Tabs.Count && Shell.Tabs.All(Page =>
            {
                var Button = HeaderButton(Page);
                var Caption = FindVisualChild<TextBlock>(Button);
                var Surface = (Border)((Grid)Button.Parent).Parent;
                return Button.IsVisible && Button.ActualWidth > 0 && Button.ActualHeight > 0
                    && Caption is { IsVisible: true, ActualWidth: > 0 } && Caption.Text == Page.TabTitle
                    && Button.ToolTip?.ToString() == Page.TabPath
                    && Equals(Surface.BorderBrush, Shell.FindResource(Page.IsTabSelected ? "Accent" : "Line"))
                    && ReferenceEquals(Page.Parent, Shell.TabPages);
            }), Name);
            var LastHeader = (FrameworkElement)Shell.TabHeaders.ItemContainerGenerator.ContainerFromIndex(Shell.Tabs.Count - 1);
            var Gap = Shell.NewTabButton.TranslatePoint(new Point(), LastHeader).X - LastHeader.ActualWidth;
            Check(Gap >= 0 && Gap <= 8, "The new-tab button stays immediately after the last tab: " + Name);
        }
        var InitialPath = this.CurrentPath;
        var InitialHistory = this.History.ToArray();
        var InitialSidebar = this.Settings.ShowSidebar;
        var InitialExpanded = this.FavoritesSection.IsExpanded;
        var InitialSort = this.SortProperty;
        var InitialDirection = this.SortDirection;
        var File = this.Entries.First(Entry => !Entry.IsDirectory);
        this.SelectPath(File.FullPath);
        var Status = this.SelectionStatusText.Text;
        ExplorerTab? Second = null;
        try
        {
            CheckHeaders("Startup renders the current folder tab with its caption and selection border");
            Check(Shell.TabBar.ActualHeight <= 28 && HeaderButton(this).ActualWidth < 110
                && FindVisualChild<TextBlock>(HeaderButton(this))!.FontSize == 13,
                "Default tabs use compact height and width with a separate 13-point font");
            Check(Shell.TabBar.IsVisible && Shell.TabBar.TranslatePoint(new Point(), Shell).Y > Shell.FileMenuButton.TranslatePoint(new Point(), Shell).Y
                && this.NavigationBar.TranslatePoint(new Point(), Shell).Y >= Shell.TabBar.TranslatePoint(new Point(0, Shell.TabBar.ActualHeight), Shell).Y,
                "The tab strip sits below File and above the entire explorer workspace"
                + $" (visible={Shell.TabBar.IsVisible}, fileY={Shell.FileMenuButton.TranslatePoint(new Point(), Shell).Y}, tabsY={Shell.TabBar.TranslatePoint(new Point(), Shell).Y}, tabHeight={Shell.TabBar.ActualHeight}, navY={this.NavigationBar.TranslatePoint(new Point(), Shell).Y})");
            Press(Shell.TabScroll, 1);
            Check(Shell.Tabs.Count == 1, "A single click on blank tab-strip space does not create a tab");
            var ResourceSnapshot = Application.Current.Resources.Keys.Cast<object>()
                .ToDictionary(Key => Key, Key => Application.Current.Resources[Key]);
            var CreationTimer = System.Diagnostics.Stopwatch.StartNew();
            Check(Press(Shell.TabScroll, 2).Handled, "Double-clicking blank tab-strip space handles the gesture");
            var CreationDispatchMs = CreationTimer.Elapsed.TotalMilliseconds;
            Second = Shell.ActiveTab!;
            Check(ReferenceEquals(Shell.PresentedTab, this) && this.IsVisible && this.Opacity == 1
                && Second.Opacity == 0 && !Second.IsHitTestVisible,
                "New-tab initialization keeps the previous complete frame visible until the new workspace is ready");
            await Second.WaitUntilReadyAsync();
            Check(ReferenceEquals(Shell.PresentedTab, Second) && Second.Opacity == 1 && Second.IsHitTestVisible,
                "A ready workspace replaces the previous frame in one presentation step");
            Check(ResourceSnapshot.All(Item => ReferenceEquals(Item.Value, Application.Current.Resources[Item.Key])),
                "Creating a tab preserves unchanged global resources without invalidating existing layouts");
            if (AppMain.ScreenshotPath is { } PerformancePreview)
                System.IO.File.WriteAllText(Path.Combine(Path.GetDirectoryName(PerformancePreview)!, "TabCreationPerformance.json"),
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        DispatchMs = CreationDispatchMs,
                        ReadyMs = CreationTimer.Elapsed.TotalMilliseconds,
                        ReplacedResources = ResourceSnapshot.Count(Item => !ReferenceEquals(Item.Value, Application.Current.Resources[Item.Key]))
                    }));
            CheckHeaders("Both active and inactive tab headers remain visible without moving their workspaces");
            Check(Shell.Tabs.Count == 2 && !ReferenceEquals(this, Second) && SamePath(Second.CurrentPath, InitialPath)
                && this.Visibility == Visibility.Collapsed && Second.Visibility == Visibility.Visible,
                "Double-clicking blank tab-strip space creates an independent workspace at the current folder");
            Check(!Press(Shell.NewTabButton, 2).Handled && !Press(HeaderButton(Second, Close: true), 2).Handled
                && Shell.Tabs.Count == 2, "Double-click handling leaves dedicated tab buttons alone");
            Check(!ReferenceEquals(this.Roots, Second.Roots) && !ReferenceEquals(this.Settings, Second.Settings)
                && !ReferenceEquals(this.FileGrid, Second.FileGrid) && !ReferenceEquals(this.RefreshTimer, Second.RefreshTimer),
                "Each tab owns its controls, navigation trees, preferences snapshot and refresh lifecycle");
            Check(this.Roots.Select(Node => Node.FullPath).SequenceEqual(Second.Roots.Select(Node => Node.FullPath))
                && this.Roots.Zip(Second.Roots).All(Pair => !ReferenceEquals(Pair.First, Pair.Second)
                    && !ReferenceEquals(Pair.First.Children, Pair.Second.Children)),
                "New tabs reuse drive metadata while keeping tree nodes and pending loads independent");
            await Second.NavigateAsync(Path.Combine(InitialPath, "child"));
            Check(Second.TabTitle == "child" && SamePath(Second.TabPath, Second.CurrentPath), "Tab captions and tooltips follow navigation");
            CheckHeaders("Rendered tab captions and tooltips update after navigation");
            var AliasPath = Second.CurrentPath;
            await RenameAlias(Second, "  Work 工作 / Project  ");
            Check(Second.TabTitle == "Work 工作 / Project" && SamePath(Second.CurrentPath, AliasPath)
                && Directory.Exists(AliasPath) && System.IO.File.Exists(Path.Combine(InitialPath, "Example.txt"))
                && this.TabAlias.Length == 0,
                "A tab alias changes only its label, accepts non-filename text and leaves other tabs and disk names intact");
            CheckHeaders("The tab header renders its alias while its tooltip retains the original folder path");
            var LongAlias = string.Concat(Enumerable.Repeat("UE5.8 Engine And Project 自定义长标题 ", 16));
            await RenameAlias(Second, LongAlias);
            Shell.UpdateLayout();
            var LongCaption = FindVisualChild<TextBlock>(HeaderButton(Second))!;
            Check(LongCaption.Text == LongAlias.Trim() && LongCaption.TextTrimming == TextTrimming.None
                && LongCaption.ActualWidth > Shell.TabScroll.ViewportWidth
                && HeaderButton(Second).ActualWidth >= LongCaption.ActualWidth + 19
                && Shell.TabScroll.ExtentWidth > Shell.TabScroll.ViewportWidth,
                "A custom title wider than the window keeps its full text and scrolls horizontally without an ellipsis"
                + $" (caption={LongCaption.ActualWidth}, button={HeaderButton(Second).ActualWidth}, viewport={Shell.TabScroll.ViewportWidth}, extent={Shell.TabScroll.ExtentWidth}, textLength={LongCaption.Text.Length}, expectedLength={LongAlias.Trim().Length}, trimming={LongCaption.TextTrimming})");
            await RenameAlias(Second, "Work 工作 / Project");
            await RenameAlias(Second, "Cancelled", Confirm: false);
            Check(Second.TabTitle == "Work 工作 / Project", "Cancelling the alias dialog preserves the current alias");
            await Second.NavigateAsync(InitialPath);
            Check(Second.TabTitle == "Work 工作 / Project", "A custom alias stays with its tab after navigation");
            await Second.NavigateAsync(AliasPath);
            await RenameAlias(Second, " ");
            Check(Second.TabTitle == "child" && Second.TabAlias.Length == 0 && Directory.Exists(AliasPath),
                "Clearing an alias restores the current folder label without renaming the folder");
            Second.Settings.ShowSidebar = !InitialSidebar;
            Second.ApplySidebarVisibility();
            Second.FavoritesSection.IsExpanded = !InitialExpanded;
            Second.SortProperty = nameof(FileEntry.Modified);
            Second.SortDirection = ListSortDirection.Descending;
            Second.SearchBox.Text = "__second_tab_query__";
            await Second.WaitForSearchSmokeAsync();
            var SecondHistory = Second.History.ToArray();
            await Second.ToggleTerminalAsync();
            var SecondSession = Second.TerminalSession;
            Check(SecondSession is { HasExited: false } && this.TerminalSession is null,
                "Opening the second tab terminal creates only that tab's shell session");
            HeaderButton(this).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(ReferenceEquals(Shell.ActiveTab, this), "Clicking a rendered tab header selects its workspace");
            CheckHeaders("Clicking a tab updates the rendered selection border");
            Check(SamePath(this.CurrentPath, InitialPath) && this.History.SequenceEqual(InitialHistory)
                && this.SearchBox.Text.Length == 0 && this.Settings.ShowSidebar == InitialSidebar
                && this.FavoritesSection.IsExpanded == InitialExpanded && this.SortProperty == InitialSort
                && this.SortDirection == InitialDirection && this.FileGrid.SelectedItem is FileEntry Selected
                && SamePath(Selected.FullPath, File.FullPath) && this.SelectionStatusText.Text == Status,
                "Switching tabs preserves path, history, search, sidebar, sorting, selection and status");
            await this.ToggleTerminalAsync();
            var FirstSession = this.TerminalSession;
            Check(FirstSession is { HasExited: false } && !ReferenceEquals(FirstSession, SecondSession),
                "Each tab can run its own independent terminal session");
            await Shell.ExecuteTabCommandAsync("NextTab");
            Check(ReferenceEquals(Shell.ActiveTab, Second) && Second.SearchBox.Text == "__second_tab_query__"
                && Second.History.SequenceEqual(SecondHistory) && Second.Settings.ShowSidebar != InitialSidebar
                && ReferenceEquals(Second.TerminalSession, SecondSession) && Second.TerminalPanel.IsVisible && !this.TerminalPanel.IsVisible,
                "Next Tab restores the second tab without reloading its state");
            if (AppMain.ScreenshotPath is { } Preview)
            {
                Shell.UpdateLayout();
                var Bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Shell.ActualWidth, (int)Shell.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                Bitmap.Render(Shell);
                var Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(Bitmap));
                using var Stream = System.IO.File.Create(Path.Combine(Path.GetDirectoryName(Preview)!, "MultipleTabs.png"));
                Encoder.Save(Stream);
            }
            await Shell.ExecuteTabCommandAsync("NextTab");
            Check(ReferenceEquals(Shell.ActiveTab, this), "Next Tab wraps to the first tab");
            await Shell.ExecuteTabCommandAsync("PreviousTab");
            Check(ReferenceEquals(Shell.ActiveTab, Second), "Previous Tab wraps to the last tab");
            Second.OperationBusy = true;
            Check(!Shell.CloseTab(Second) && Shell.Tabs.Contains(Second), "A tab with a running file operation cannot be closed");
            Second.OperationBusy = false;
            Shell.SelectTab(this);
            Check(Shell.CloseTab(Second) && ReferenceEquals(Shell.ActiveTab, this) && Second.WindowClosed
                && !Second.RefreshTimer.IsEnabled && !Second.DriveUsageTimer.IsEnabled && Second.VersionWatchers.Count == 0 && !Second.VersionDebounce.IsEnabled
                && Second.TerminalSession is null && ReferenceEquals(this.TerminalSession, FirstSession) && FirstSession is { HasExited: false },
                "Closing an inactive tab preserves the active workspace and releases its background resources");
            this.ReleaseTerminalSession();
            this.HideTerminal();
            Second = Shell.AddTab();
            await Second.WaitUntilReadyAsync();
            HeaderButton(Second, Close: true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Second.WindowClosed && ReferenceEquals(Shell.ActiveTab, this) && Shell.Tabs.Count == 1,
                "Clicking the active tab close button selects its surviving neighbor");
            CheckHeaders("Closing a tab removes its header and keeps the surviving tab visible");
            Second = null;
            Shell.NewTabButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Second = Shell.ActiveTab!;
            await Second.WaitUntilReadyAsync();
            Second.OperationBusy = true;
            Press(FindVisualChild<TextBlock>(HeaderButton(Second))!, 2);
            Check(Shell.Tabs.Count == 2 && !Second.WindowClosed,
                "Double-click cannot close a tab while a file operation is running");
            Second.OperationBusy = false;
            Press(FindVisualChild<TextBlock>(HeaderButton(Second))!, 2);
            Check(Second.WindowClosed && Shell.Tabs.Count == 1 && ReferenceEquals(Shell.ActiveTab, this),
                "Double-clicking an existing tab caption closes it and selects its neighbor");
            CheckHeaders("Double-click close removes the header without creating another tab");
            Second = null;
            var Pending = Shell.AddTab();
            Shell.SelectTab(this);
            await Pending.WaitUntilReadyAsync();
            Check(ReferenceEquals(Shell.ActiveTab, this) && ReferenceEquals(Shell.PresentedTab, this)
                && this.Opacity == 1 && this.IsHitTestVisible && Pending.Visibility == Visibility.Collapsed,
                "Switching away from a pending tab prevents its later completion from replacing the selected workspace");
            Shell.CloseTab(Pending);
            Pending = Shell.AddTab();
            Shell.CloseTab(Pending);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(Pending.WindowClosed && ReferenceEquals(Shell.PresentedTab, this)
                && this.IsVisible && this.Opacity == 1 && this.IsHitTestVisible,
                "Closing a pending tab immediately restores the surviving workspace without a blank frame");
            var OverflowTabs = new List<ExplorerTab>();
            try
            {
                for (var Index = 0; Index < 9; Index++)
                {
                    Shell.NewTabButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var Added = Shell.ActiveTab!;
                    OverflowTabs.Add(Added);
                    await Added.WaitUntilReadyAsync();
                    Added.SetTabAlias("A long tab caption for overflow " + Index);
                }
                // Selecting the last tab also reveals the trailing button after captions resize.
                Shell.SelectTab(OverflowTabs.Last());
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                CheckHeaders("Adding many tabs keeps the new-tab button after the final header");
                var ButtonLeft = Shell.NewTabButton.TranslatePoint(new Point(), Shell.TabScroll).X;
                Check(Shell.TabScroll.ScrollableWidth > 0 && Shell.TabScroll.HorizontalOffset > 0
                    && ButtonLeft >= 0 && ButtonLeft + Shell.NewTabButton.ActualWidth <= Shell.TabScroll.ViewportWidth + 1,
                    "Overflow scrolls to the trailing new-tab button so it stays usable after adding tabs");
                Shell.CloseTab(OverflowTabs.Last());
                CheckHeaders("Closing the final overflowing tab moves the new-tab button after its new neighbor");
            }
            finally
            {
                foreach (var Page in OverflowTabs.Where(Page => Shell.Tabs.Contains(Page))) Shell.CloseTab(Page);
                Shell.SelectTab(this);
            }
            Check(this.TryGetTabCommand(Key.T, ModifierKeys.Control, out var Command) && Command == "NewTab"
                && this.TryGetTabCommand(Key.W, ModifierKeys.Control, out Command) && Command == "CloseTab"
                && this.TryGetTabCommand(Key.Tab, ModifierKeys.Control, out Command) && Command == "NextTab"
                && this.TryGetTabCommand(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, out Command) && Command == "PreviousTab",
                "Standard tab shortcuts resolve through the configurable shortcut model");
            await this.RunBackgroundDeleteSmokeChecksAsync(Passed);
            await this.RunTabContextMenuSmokeChecksAsync(Passed);
            await this.RunPinnedTabSmokeChecksAsync(Passed);
            await this.RunTabDragSmokeChecksAsync(Passed);
            await this.RunTabBulkCloseSmokeChecksAsync(Passed);
        }
        finally
        {
            if (Second is not null && Shell.Tabs.Contains(Second)) { Second.OperationBusy = false; Shell.CloseTab(Second); }
            Shell.SelectTab(this);
        }
    }

    private async Task RunBackgroundDeleteSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Background delete UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        var InitialPath = this.CurrentPath;
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "BackgroundDelete-" + Guid.NewGuid().ToString("N"))).FullName;
        var File = Path.Combine(Fixture, "File.txt");
        System.IO.File.WriteAllText(File, "owned deletion fixture");
        var Folder = Directory.CreateDirectory(Path.Combine(Fixture, "Folder")).FullName;
        System.IO.File.WriteAllText(Path.Combine(Folder, "Child.txt"), "owned folder fixture");
        var Original = this.DeleteShellItems;
        using var Release = new ManualResetEventSlim();
        var Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var WorkerThread = 0;
        var WorkerSta = false;
        string[] Captured = [];
        Task? Pending = null;
        ExplorerTab? Other = null;
        try
        {
            await this.NavigateAsync(Fixture);
            this.DeleteShellItems = (Paths, Permanent) =>
            {
                WorkerThread = Environment.CurrentManagedThreadId;
                WorkerSta = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
                Captured = Paths;
                Started.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException();
                // Tests never touch the user's Recycle Bin. Only these exact owned fixtures are removed.
                foreach (var Path in Paths)
                {
                    if (!AppMain.IsInsideSmokeScope(Path) || Path != File && Path != Folder) throw new InvalidOperationException("Unexpected deletion target.");
                    if (Directory.Exists(Path)) { System.IO.File.Delete(System.IO.Path.Combine(Path, "Child.txt")); Directory.Delete(Path); }
                    else System.IO.File.Delete(Path);
                }
            };
            using var Menu = new ShellContextMenu(new WindowInteropHelper(this.HostWindow).Handle, Fixture, [File, Folder]);
            static IEnumerable<ShellContextMenu.MenuEntry> Flatten(IEnumerable<ShellContextMenu.MenuEntry> Entries) => Entries.SelectMany(Entry => new[] { Entry }.Concat(Flatten(Entry.Children)));
            var Delete = Flatten(Menu.ReadEntries()).Single(Entry => Menu.DescribeCommand(Entry.Id)?.Equals("delete", StringComparison.OrdinalIgnoreCase) == true);
            Pending = this.ExecuteShellCommandAsync(Menu, Delete.Id);
            await Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(WorkerThread != Environment.CurrentManagedThreadId && WorkerSta && this.OperationBusy && !Pending.IsCompleted,
                "The native Delete command dispatches to a dedicated STA worker while the UI thread stays free");
            Check(Captured.SequenceEqual(new[] { File, Folder }) && this.HostWindow.IsEnabled,
                "Deletion captures both file and folder targets and leaves the main window enabled");
            Check(await this.NavigateAsync(InitialPath) && SamePath(this.CurrentPath, InitialPath), "Navigation remains usable while deletion is running");
            this.SelectPath(Path.Combine(InitialPath, "child"));
            await this.OpenSelectedAsync();
            Check(SamePath(this.CurrentPath, Path.Combine(InitialPath, "child")), "Opening a folder from the file list remains usable during deletion");
            await this.OpenSidebarFolderClickAsync(() => this.NavigateAsync(InitialPath));
            Check(SamePath(this.CurrentPath, InitialPath), "Sidebar navigation remains usable during deletion");
            Other = this.HostWindow.AddTab();
            await Other.WaitUntilReadyAsync();
            this.HostWindow.SelectTab(this);
            Check(this.HostWindow.Tabs.Contains(Other) && ReferenceEquals(this.HostWindow.ActiveTab, this) && !Pending.IsCompleted,
                "Creating and switching tabs remains usable during deletion");
            this.SearchBox.Text = "Example";
            await this.WaitForSearchSmokeAsync();
            Check(this.View!.Cast<FileEntry>().Any(Entry => Entry.Name == "Example.txt"), "Search remains usable during deletion");
            Release.Set();
            await Pending.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!System.IO.File.Exists(File) && !Directory.Exists(Folder) && !this.OperationBusy
                && SamePath(this.CurrentPath, InitialPath) && this.SearchBox.Text == "Example",
                "Deletion completes without changing the browsed folder or search");
            Check((ShellContextMenu.DeleteOperationFlags(false) & 0x80040u) == 0x80040u
                && ShellContextMenu.DeleteOperationFlags(true) == 0,
                "Normal deletion requests the Recycle Bin and Shift deletion retains permanent-delete semantics");
            this.DeleteShellItems = (_, _) => throw new OperationCanceledException();
            Check(!await this.DeleteShellSelectionAsync([File], false) && !this.OperationBusy && this.HostWindow.IsEnabled,
                "Cancellation clears the busy state and keeps the browser usable");
            this.DeleteShellItems = (_, _) => throw new IOException("Owned failure fixture");
            Check(!await this.DeleteShellSelectionAsync([File], false) && !this.OperationBusy && this.HostWindow.IsEnabled,
                "Deletion errors clear the busy state and keep the browser usable");
        }
        finally
        {
            Release.Set();
            if (Pending is not null) await Pending;
            this.DeleteShellItems = Original;
            this.SearchBox.Clear();
            if (Other is not null) this.HostWindow.CloseTab(Other);
            await this.NavigateAsync(InitialPath);
        }
    }
}
