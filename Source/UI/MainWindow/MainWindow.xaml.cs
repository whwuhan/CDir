using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CDir.Localization;
using CDir.UI.Dialogs;
using CDir.Services;

namespace CDir.UI;

public partial class MainWindow : Window
{
    static MainWindow()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler((Sender, E) =>
            {
                if (Sender is not ContextMenu Menu) return;
                var Owner = Menu.PlacementTarget is { } Target ? Window.GetWindow(Target) as MainWindow : null;
                if (Owner is null) return;
                if (E.Key == Key.A && E.KeyboardDevice.Modifiers == ModifierKeys.Control)
                {
                    Menu.IsOpen = false;
                    E.Handled = true;
                    Owner.Dispatcher.BeginInvoke(() => Owner.ActiveTab?.SelectAllVisibleFiles());
                    return;
                }
                if (E.Key != Key.Escape) return;
                Menu.IsOpen = false;
                E.Handled = true;
                Owner.Dispatcher.BeginInvoke(() => Owner.ActiveTab?.FocusMainFilePanel());
            }), true);
    }
    // Header items must be data objects so the ItemsControl never reparents the workspace controls.
    public sealed record TabHeader(ExplorerTab Page);

    public ObservableCollection<ExplorerTab> Tabs { get; } = [];
    public ObservableCollection<TabHeader> Headers { get; } = [];
    private bool SeparatePinnedTabs;
    private bool ClosingTabBatch;
    internal ExplorerTab? ActiveTab { get; private set; }
    internal TrayIconService? TrayIcon { get; private set; }
    private bool ExitRequested;
    private WindowState StateBeforeTray;
    private WindowWorkArea? WorkAreaBounds;
    private readonly Dictionary<string, (long Started, Func<string, DriveUsage?> Reader, Task<DriveUsage?> Read)> DriveUsageReads = new(StringComparer.OrdinalIgnoreCase);

    internal Task<DriveUsage?> ReadDriveUsageAsync(string Root, Func<string, DriveUsage?> Reader, bool Force)
    {
        if (this.DriveUsageReads.TryGetValue(Root, out var Previous) && Previous.Reader == Reader
            && (!Previous.Read.IsCompleted || !Force && Environment.TickCount64 - Previous.Started < 30000)) return Previous.Read;
        var Read = Task.Run(() => Reader(Root));
        this.DriveUsageReads[Root] = (Environment.TickCount64, Reader, Read);
        return Read;
    }

    public MainWindow() : this(null, RestoreWorkspace: true) { }

    internal MainWindow(string? LayoutSettingsFile, bool RestoreWorkspace = false, CDir.Models.WorkspaceState? InitialWorkspace = null)
    {
        this.TrackInstances = LayoutSettingsFile is null && !AppMain.SmokeTest && AppMain.ScreenshotPath is null;
        var Workspace = this.InitializeWorkspace(LayoutSettingsFile, RestoreWorkspace, InitialWorkspace);
        var Initial = new ExplorerTab(this, LayoutSettingsFile, InitialPath: Workspace?.Tabs[0].Settings.LastPath,
            InitialSettings: Workspace?.Tabs[0].Settings, RunStartupChecks: LayoutSettingsFile is null);
        InitializeComponent();
        this.ActivePane = this.CreatePane();
        this.LayoutRoot = new PaneNode { Pane = this.ActivePane };
        this.WorkspaceSurface.Children.Add(this.ActivePane);
        this.InitializePaneDropRouting();
        var Version = typeof(MainWindow).Assembly.GetName().Version!;
        this.TitleVersion.Text = $"v{Version.Major}.{Version.Minor}.{Version.Build}";
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        var Settings = Initial.PageSettings;
        var SavedWidth = Workspace?.Width ?? Settings.Width;
        var SavedHeight = Workspace?.Height ?? Settings.Height;
        this.Width = Math.Clamp(double.IsFinite(SavedWidth) ? SavedWidth : 1280, MinWidth, SystemParameters.WorkArea.Width);
        this.Height = Math.Clamp(double.IsFinite(SavedHeight) ? SavedHeight : 820, MinHeight, SystemParameters.WorkArea.Height);
        if (Workspace is not null && double.IsFinite(Workspace.Left) && double.IsFinite(Workspace.Top))
        {
            this.WindowStartupLocation = WindowStartupLocation.Manual;
            this.Left = Math.Clamp(Workspace.Left, SystemParameters.VirtualScreenLeft - this.Width + 64,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 64);
            this.Top = Math.Clamp(Workspace.Top, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 32);
        }
        if (Workspace?.Maximized ?? Settings.Maximized) WindowState = WindowState.Maximized;
        this.LastWorkspaceMaximized = this.WindowState == WindowState.Maximized;
        StateChanged += (_, _) =>
        {
            if (this.WindowState != WindowState.Minimized) this.LastWorkspaceMaximized = this.WindowState == WindowState.Maximized;
            this.UpdateCaption();
        };
        Activated += (_, _) => this.LastActivatedUtc = DateTimeOffset.UtcNow;
        Deactivated += (_, _) => { this.ActiveTab?.DismissSearchTips(); this.ActiveTab?.DismissFileDragFeedback(); };
        this.SeparatePinnedTabs = Settings.SeparatePinnedTabs;
        this.AttachTab(Initial);
        Initial.RefreshShellResources();
        this.UpdateCaption();
        if (this.TrackInstances)
        {
            this.InstanceService = new InstanceWorkspaceService(async () => await this.Dispatcher.InvokeAsync(() =>
            {
                this.MultiInstanceDetected = true;
                return this.CaptureWorkspace();
            }));
            this.InstanceService.Start(RegisterUserInstance: true);
        }
        if (this.WorkspacePath is not null) Loaded += async (_, _) =>
        {
            this.WorkspaceRestored = this.RestoreWorkspaceAsync(Workspace);
            try { await this.WorkspaceRestored; }
            catch (Exception Error) when (Error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
            { this.ActiveTab?.ShowRecoveryError(); }
        };
        SourceInitialized += (_, _) =>
        {
            if (System.Windows.PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource Source)
                Source.AddHook(this.BrowserAppCommand);
            this.WorkAreaBounds = new WindowWorkArea(this);
            this.EnsureTrayIcon();
        };
        Closed += (_, _) =>
        {
            this.PersistWorkspaceNow();
            if (!this.SessionIsEnding) this.SessionStore?.Discard();
            this.WorkspaceClosed = true;
            this.InstanceService?.Dispose();
            this.WorkspaceTimer?.Stop();
            this.TrayIcon?.Dispose();
            this.WorkAreaBounds?.Dispose();
            foreach (var Page in this.Tabs) Page.DisposePage();
        };
    }

    private void UpdateCaption()
    {
        this.MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        // WindowWorkArea already fits the native window to the monitor's work area.
        // A Window border here exposes the template's dark frame when maximized.
        BorderThickness = new Thickness(0);
        this.WindowFrame.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
    }

    private void AttachTab(ExplorerTab Page)
    {
        // Keep the previous frame visible while the new workspace loads and lays out.
        Page.Opacity = 0;
        Page.IsHitTestVisible = false;
        this.Tabs.Add(Page);
        Page.PropertyChanged += (_, _) => this.PersistWorkspaceNow();
        this.TabPages.Children.Add(Page);
        this.Headers.Add(new TabHeader(Page));
        this.RefreshTabHeaders();
        this.SelectTab(Page);
    }

    internal ExplorerTab AddTab()
    {
        var Page = new ExplorerTab(this, InitialPath: this.ActiveTab?.TabPath,
            InitialSettings: this.ActiveTab?.ClonePageSettings(), InitialRoots: this.ActiveTab?.CloneRootNodes());
        this.AttachTab(Page);
        return Page;
    }

    internal void SelectTab(ExplorerTab Page)
    {
        if (!this.Tabs.Contains(Page)) return;
        this.ActiveTab?.DismissSearchTips();
        this.ActivatePane(this.PaneFor(Page));
        this.ActiveTab = Page;
        this.ActivePane.Selected = Page;
        foreach (var Tab in this.PaneTabs(this.ActivePane)) Tab.SetTabSelected(ReferenceEquals(Tab, Page));
        if (Page.IsInitialLayoutReady) this.PresentTab(Page);
        else
        {
            Page.Visibility = Visibility.Visible;
            if (this.PresentedTab is { } Previous) Previous.IsHitTestVisible = false;
        }
        var HeaderControl = this.SeparatePinnedTabs && Page.IsTabPinned ? this.PinnedTabHeaders : this.TabHeaders;
        HeaderControl.UpdateLayout();
        (HeaderControl.ItemContainerGenerator.ContainerFromItem(this.Headers.Single(Header => ReferenceEquals(Header.Page, Page))) as FrameworkElement)?.BringIntoView();
        if (ReferenceEquals(Page, this.PaneTabs(this.ActivePane).Last()) && !(this.SeparatePinnedTabs && Page.IsTabPinned))
        {
            this.TabScroll.UpdateLayout();
            this.NewTabButton.BringIntoView();
        }
        Page.RestoreTabFocus();
        this.PersistWorkspaceNow();
    }

    internal void TabReady(ExplorerTab Page)
    {
        if (!this.Tabs.Contains(Page)) return;
        if (ReferenceEquals(this.PaneFor(Page).Selected, Page)) this.PresentTab(Page);
        else Page.Visibility = Visibility.Collapsed;
    }

    private void PresentTab(ExplorerTab Page)
    {
        var Pane = this.PaneFor(Page);
        Page.SetTabSelected(true);
        Page.Visibility = Visibility.Visible;
        Page.Opacity = 1;
        Page.IsHitTestVisible = true;
        foreach (var Other in this.PaneTabs(Pane).Where(Other => !ReferenceEquals(Other, Page)))
        {
            // Pending tabs still need a Loaded event to complete initialization.
            if (Other.IsInitialLayoutReady) Other.Visibility = Visibility.Collapsed;
            Other.Opacity = 0;
            Other.IsHitTestVisible = false;
        }
        Pane.Presented = Page;
    }

    internal bool CloseTab(ExplorerTab Page)
    {
        if (!this.Tabs.Contains(Page)) return false;
        if (Page.IsOperationBusy) { Page.ShowBusyCloseNotice(); return false; }
        // The final tab follows the same saved close behavior as the caption button.
        if (this.Tabs.Count == 1) { this.Close(); return true; }
        var Pane = this.PaneFor(Page);
        var LocalTabs = this.PaneTabs(Pane);
        var Index = Array.IndexOf(LocalTabs, Page);
        var WasSelected = ReferenceEquals(Pane.Selected, Page);
        var WasActive = ReferenceEquals(Page, this.ActiveTab);
        Page.DisposePage();
        Pane.TabPages.Children.Remove(Page);
        this.Tabs.Remove(Page);
        this.Headers.Remove(this.Headers.Single(Header => ReferenceEquals(Header.Page, Page)));
        if (ReferenceEquals(Page, Pane.Presented)) Pane.Presented = null;
        if (WasSelected)
        {
            var Remaining = this.PaneTabs(Pane);
            Pane.Selected = Remaining.Length == 0 ? null : Remaining[Math.Min(Index, Remaining.Length - 1)];
            if (Pane.Selected is { } Next) this.PresentTab(Next);
        }
        if (this.PaneTabs(Pane).Length == 0) this.RemoveEmptyPane(Pane);
        if (WasActive)
        {
            this.ActiveTab = this.ActivePane.Selected;
            if (!this.ClosingTabBatch && this.ActiveTab is { } Next) this.SelectTab(Next);
        }
        if (!this.ClosingTabBatch) this.RefreshTabHeaders();
        this.PersistWorkspaceNow();
        return true;
    }

    internal async Task SynchronizePreferencesAsync(ExplorerTab Source)
    {
        foreach (var Page in this.Tabs.ToArray())
            if (!ReferenceEquals(Page, Source)) await Page.ApplySharedPreferencesAsync(Source.PageSettings);
    }

    private void NewTabClick(object Sender, RoutedEventArgs E) => this.AddTab();

    internal void TabBarPreviewMouseDown(object Sender, MouseButtonEventArgs E)
    {
        if (E.ChangedButton != MouseButton.Left || E.ClickCount != 2) return;
        TabHeader? Header = null;
        for (var Element = E.OriginalSource as DependencyObject; Element is not null && !ReferenceEquals(Element, this.TabBar);
            Element = Element is Visual ? VisualTreeHelper.GetParent(Element) : LogicalTreeHelper.GetParent(Element))
        {
            // The dedicated buttons and overflow scrollbar keep their own gestures.
            if (Element is ScrollBar || Element is Button Button && (ReferenceEquals(Button, this.NewTabButton) || Button.Name is "CloseTabButton" or "PinTabButton")) return;
            if (Element is FrameworkElement { DataContext: TabHeader Item }) Header = Item;
        }
        E.Handled = true;
        if (Header is not null) this.CloseTab(Header.Page);
        else this.AddTab();
    }

    private void RenameTabClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (Sender is not FrameworkElement { DataContext: TabHeader Header } || !this.Tabs.Contains(Header.Page)) return;
        var Dialog = new NameDialog(this, LanguageService.Text("RenameTab"), Header.Page.TabTitle,
            HintKey: "TabAliasHint", AllowEmpty: true) { ShowActivated = !AppMain.SmokeTest };
        if (Dialog.ShowDialog() == true) Header.Page.SetTabAlias(Dialog.EnteredName);
    }

    private ExplorerTab[] TabsForCloseCommand(ExplorerTab Anchor, string Command)
    {
        var Pages = this.PaneTabs(this.PaneFor(Anchor));
        var Index = Array.IndexOf(Pages, Anchor);
        if (Index < 0) return [];
        return Command switch
        {
            "CloseRightTabs" => Pages[(Index + 1)..],
            "CloseLeftTabs" => Pages[..Index],
            "CloseOtherTabs" => Pages.Where(Page => !ReferenceEquals(Page, Anchor) && !Page.IsTabPinned).ToArray(),
            "CloseTab" => [Anchor],
            _ => []
        };
    }

    private void TabContextMenuOpened(object Sender, RoutedEventArgs E)
    {
        if (Sender is not ContextMenu { DataContext: TabHeader Header } Menu) return;
        Menu.SetResourceReference(Control.FontSizeProperty, "UIFontMenus");
        foreach (var Item in Menu.Items.OfType<MenuItem>())
        {
            if (Item.Tag is not string Command) continue;
            Item.SetResourceReference(Control.FontSizeProperty, "UIFontMenus");
            Item.Header = LanguageService.Text(Command);
            Item.IsEnabled = Command == "RenameTab" ? this.Tabs.Contains(Header.Page)
                : this.TabsForCloseCommand(Header.Page, Command).Length > 0;
        }
    }

    private void CloseTabsMenuClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (Sender is not MenuItem { DataContext: TabHeader Header, Tag: string Command }) return;
        var Targets = this.TabsForCloseCommand(Header.Page, Command);
        // Check the whole batch before changing the tab collection.
        if (Targets.FirstOrDefault(Page => Page.IsOperationBusy) is { } Busy)
        {
            this.SelectTab(Busy);
            Busy.ShowBusyCloseNotice();
            return;
        }
        if (Targets.Length == 0) return;
        if (Command == "CloseTab") { this.CloseTab(Header.Page); return; }
        var SelectAnchor = this.ActiveTab is null || Targets.Contains(this.ActiveTab);
        // Close the complete set before rebuilding headers, revealing a survivor,
        // or serializing it. Per-tab saves otherwise repeatedly scan all remaining trees.
        this.ClosingTabBatch = true;
        try
        {
            foreach (var Page in Targets) this.CloseTab(Page);
            this.RefreshTabHeaders();
            if (SelectAnchor) this.SelectTab(Header.Page);
        }
        finally
        {
            this.ClosingTabBatch = false;
            this.PersistWorkspaceNow();
        }
    }
    private void SelectTabClick(object Sender, RoutedEventArgs E)
    {
        if (Sender is FrameworkElement { DataContext: TabHeader Header }) this.SelectTab(Header.Page);
    }
    private void CloseTabClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (Sender is FrameworkElement { DataContext: TabHeader Header }) this.CloseTab(Header.Page);
    }

    private async void WindowPreviewKeyDown(object Sender, KeyEventArgs E)
    {
        if (this.ActiveTab is null) return;
        if (this.ActiveTab.HandleInlineRenameKey(E)) return;
        var Key = E.Key == System.Windows.Input.Key.System ? E.SystemKey : E.Key;
        if (Key == System.Windows.Input.Key.A && E.KeyboardDevice.Modifiers == ModifierKeys.Control)
        {
            E.Handled = true;
            this.FileMenu.IsOpen = false;
            this.HelpMenu.IsOpen = false;
            this.ActiveTab.SelectAllVisibleFiles();
            return;
        }
        if (Key is System.Windows.Input.Key.BrowserBack or System.Windows.Input.Key.BrowserForward)
        {
            E.Handled = true;
            await this.ActiveTab.NavigateBrowseHistoryAsync(Key == System.Windows.Input.Key.BrowserForward);
            return;
        }
        if (!this.ActiveTab.TryGetTabCommand(Key, E.KeyboardDevice.Modifiers, out var Command))
        {
            this.ActiveTab.WindowPreviewKeyDown(Sender, E);
            return;
        }
        E.Handled = true;
        await this.ExecuteTabCommandAsync(Command);
    }

    internal Task ExecuteTabCommandAsync(string Command)
    {
        if (Command == "NewTab") this.AddTab();
        else if (Command == "CloseTab" && this.ActiveTab is { } Page) this.CloseTab(Page);
        else if (Command == "ToggleTabPin" && this.ActiveTab is { } PinnedPage) this.SetTabPinned(PinnedPage, !PinnedPage.IsTabPinned);
        else if (this.ActiveTab is not null && this.Tabs.Count > 1)
        {
            var Delta = Command == "PreviousTab" ? -1 : 1;
            this.SelectTab(this.PaneTabs(this.ActivePane)[(Array.IndexOf(this.PaneTabs(this.ActivePane), this.ActiveTab) + Delta + this.PaneTabs(this.ActivePane).Length) % this.PaneTabs(this.ActivePane).Length]);
        }
        return Task.CompletedTask;
    }

    private void WindowPreviewMouseWheel(object Sender, MouseWheelEventArgs E)
    {
        if (!E.Handled && this.ActiveTab is { } Page
            && Page.TerminalThemeMouseWheel(E.Delta, Keyboard.Modifiers, Page.IsMouseOverTerminal)) E.Handled = true;
    }

    private async void WindowInputPreviewMouseDown(object Sender, MouseButtonEventArgs E)
    {
        if (E.OriginalSource is DependencyObject Origin && this.PaneFromSource(Origin) is { } InputPane) this.ActivatePane(InputPane);
        if (E.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
        {
            E.Handled = true;
            if (this.ActiveTab is { } Tab) await Tab.NavigateBrowseHistoryAsync(E.ChangedButton == MouseButton.XButton2);
            return;
        }
        this.ActiveTab?.AddressEditPreviewMouseDown(E);
        // Tab-strip gestures must preserve the workspace selection when leaving a tab.
        if (E.OriginalSource is Visual Source && (ReferenceEquals(Source, this.TabBar) || Source.IsDescendantOf(this.TabBar))) return;
        this.ActiveTab?.HandleWindowMouseDown(Sender, E);
    }
    internal IntPtr BrowserAppCommand(IntPtr Window, int Message, IntPtr WParam, IntPtr LParam, ref bool Handled)
    {
        this.ActiveTab?.AddressWindowMouseMessage(Window, Message, WParam, LParam, ref Handled);
        // Consume both halves of the native gesture so DefWindowProc cannot
        // turn its release into a second browser command after navigation.
        if (Message is 0x020B or 0x020C or 0x020D or 0x00AB or 0x00AC or 0x00AD)
        {
            var Button = (WParam.ToInt64() >> 16) & 0xffff;
            if (Button is not (1 or 2)) return IntPtr.Zero;
            Handled = true;
            if (Message is 0x020B or 0x020D or 0x00AB or 0x00AD)
                if (this.ActiveTab is { } Page) _ = Page.NavigateBrowseHistoryAsync(Button == 2);
            return (IntPtr)1;
        }
        if (Message != 0x319) return IntPtr.Zero; // WM_APPCOMMAND from mouse or keyboard drivers.
        var Command = (LParam.ToInt64() >> 16) & 0xfff;
        if (Command is not (1 or 2)) return IntPtr.Zero;
        Handled = true;
        if (this.ActiveTab is { } Tab) _ = Tab.NavigateBrowseHistoryAsync(Command == 2);
        return (IntPtr)1;
    }
    private void FileMenuClick(object Sender, RoutedEventArgs E)
    {
        this.FileMenu.PlacementTarget = this.FileMenuButton;
        this.FileMenu.IsOpen = true;
        E.Handled = true;
    }
    private void PreferencesClick(object Sender, RoutedEventArgs E) => this.ActiveTab?.PreferencesClick(Sender, E);
    private void HelpMenuClick(object Sender, RoutedEventArgs E)
    {
        this.HelpMenu.PlacementTarget = this.HelpMenuButton;
        this.HelpMenu.IsOpen = true;
        E.Handled = true;
    }
    private void AboutClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        this.ShowAbout();
    }
    private void ShowAbout()
    {
        this.FileMenu.IsOpen = false;
        this.HelpMenu.IsOpen = false;
        if (this.TrayIcon is { } Tray) Tray.Menu.IsOpen = false;
        if (!this.IsVisible || this.WindowState == WindowState.Minimized) this.RestoreFromTray();
        new AboutWindow(this).ShowDialog();
    }
    private async void UndoClick(object Sender, RoutedEventArgs E) { this.FileMenu.IsOpen = false; if (this.ActiveTab is { } Tab) await Tab.HandleHistoryCommandAsync(false); }
    private async void RedoClick(object Sender, RoutedEventArgs E) { this.FileMenu.IsOpen = false; if (this.ActiveTab is { } Tab) await Tab.HandleHistoryCommandAsync(true); }
    private void ExitClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        this.FileMenu.IsOpen = false;
        this.RequestExit();
    }
    private void MinimizeClick(object Sender, RoutedEventArgs E) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object Sender, RoutedEventArgs E) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object Sender, RoutedEventArgs E) => this.Close();
    internal void RequestExit()
    {
        this.ExitRequested = true;
        try { this.Close(); }
        finally { this.ExitRequested = false; }
    }

    internal void RestoreFromTray()
    {
        var RestoreState = this.IsVisible ? this.WindowState : this.StateBeforeTray;
        if (RestoreState == WindowState.Minimized) RestoreState = WindowState.Normal;
        // WPF cannot show an inactive hidden window while its state is Maximized.
        if (!this.ShowActivated && this.WindowState == WindowState.Maximized) this.WindowState = WindowState.Normal;
        this.Show();
        this.WindowState = RestoreState;
        if (this.TrayIcon is { } Tray) Tray.Menu.IsOpen = false;
        if (!AppMain.SmokeTest) this.Activate();
    }

    private bool EnsureTrayIcon()
    {
        try
        {
            this.TrayIcon ??= new TrayIconService(this, this.RestoreFromTray, this.RequestExit);
            if (this.TrayIcon.Show()) return true;
        }
        catch (Win32Exception) { }
        this.ActiveTab?.ShowTrayUnavailableNotice();
        return false;
    }

    private void WindowClosing(object? Sender, CancelEventArgs E)
    {
        if (this.SessionIsEnding) return;
        if (!this.ExitRequested && this.ActiveTab?.PageSettings.ExitOnClose != true)
        {
            E.Cancel = true;
            if (!this.EnsureTrayIcon()) return;
            this.ActiveTab?.PersistPageSettings();
            this.StateBeforeTray = this.WindowState;
            this.Hide();
            return;
        }
        var Busy = this.Tabs.FirstOrDefault(Page => Page.IsOperationBusy);
        if (Busy is not null)
        {
            E.Cancel = true;
            this.SelectTab(Busy);
            Busy.ShowBusyCloseNotice();
            return;
        }
        if (!this.ChooseExitPersistence()) { E.Cancel = true; return; }
        this.ActiveTab?.PersistPageSettings();
        this.PersistWorkspaceNow();
    }
}
