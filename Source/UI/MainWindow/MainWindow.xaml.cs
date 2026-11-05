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
    internal ExplorerTab? ActiveTab { get; private set; }
    internal ExplorerTab? PresentedTab { get; private set; }
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

    public MainWindow() : this(null) { }

    internal MainWindow(string? LayoutSettingsFile)
    {
        var Initial = new ExplorerTab(this, LayoutSettingsFile, RunStartupChecks: LayoutSettingsFile is null);
        InitializeComponent();
        var Version = typeof(MainWindow).Assembly.GetName().Version!;
        this.TitleVersion.Text = $"v{Version.Major}.{Version.Minor}.{Version.Build}";
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        var Settings = Initial.PageSettings;
        this.Width = Math.Clamp(double.IsFinite(Settings.Width) ? Settings.Width : 1280, MinWidth, SystemParameters.WorkArea.Width);
        this.Height = Math.Clamp(double.IsFinite(Settings.Height) ? Settings.Height : 820, MinHeight, SystemParameters.WorkArea.Height);
        if (Settings.Maximized) WindowState = WindowState.Maximized;
        StateChanged += (_, _) => this.UpdateCaption();
        Deactivated += (_, _) => this.ActiveTab?.DismissSearchTips();
        this.AttachTab(Initial);
        Initial.RefreshShellResources();
        this.UpdateCaption();
        SourceInitialized += (_, _) =>
        {
            if (System.Windows.PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource Source)
                Source.AddHook(this.BrowserAppCommand);
            this.WorkAreaBounds = new WindowWorkArea(this);
            this.EnsureTrayIcon();
        };
        Closed += (_, _) =>
        {
            this.TrayIcon?.Dispose();
            this.WorkAreaBounds?.Dispose();
            foreach (var Page in this.Tabs) Page.DisposePage();
        };
    }

    private void UpdateCaption()
    {
        this.MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        BorderThickness = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    private void AttachTab(ExplorerTab Page)
    {
        // Keep the previous frame visible while the new workspace loads and lays out.
        Page.Opacity = 0;
        Page.IsHitTestVisible = false;
        this.Tabs.Add(Page);
        this.TabPages.Children.Add(Page);
        this.Headers.Add(new TabHeader(Page));
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
        this.ActiveTab = Page;
        foreach (var Tab in this.Tabs) Tab.SetTabSelected(ReferenceEquals(Tab, Page));
        if (Page.IsInitialLayoutReady) this.PresentTab(Page);
        else
        {
            Page.Visibility = Visibility.Visible;
            if (this.PresentedTab is { } Previous) Previous.IsHitTestVisible = false;
        }
        this.TabHeaders.UpdateLayout();
        (this.TabHeaders.ItemContainerGenerator.ContainerFromItem(this.Headers.Single(Header => ReferenceEquals(Header.Page, Page))) as FrameworkElement)?.BringIntoView();
        if (ReferenceEquals(Page, this.Tabs.Last()))
        {
            this.TabScroll.UpdateLayout();
            this.NewTabButton.BringIntoView();
        }
        Page.RestoreTabFocus();
    }

    internal void TabReady(ExplorerTab Page)
    {
        if (!this.Tabs.Contains(Page)) return;
        if (ReferenceEquals(this.ActiveTab, Page)) this.PresentTab(Page);
        else Page.Visibility = Visibility.Collapsed;
    }

    private void PresentTab(ExplorerTab Page)
    {
        Page.Visibility = Visibility.Visible;
        Page.Opacity = 1;
        Page.IsHitTestVisible = true;
        foreach (var Other in this.Tabs.Where(Other => !ReferenceEquals(Other, Page)))
        {
            // Pending tabs still need a Loaded event to complete initialization.
            if (Other.IsInitialLayoutReady) Other.Visibility = Visibility.Collapsed;
            Other.Opacity = 0;
            Other.IsHitTestVisible = false;
        }
        this.PresentedTab = Page;
    }

    internal bool CloseTab(ExplorerTab Page)
    {
        if (!this.Tabs.Contains(Page)) return false;
        if (Page.IsOperationBusy) { Page.ShowBusyCloseNotice(); return false; }
        // Explicitly closing the final tab still closes its workspace.
        if (this.Tabs.Count == 1) { this.RequestExit(); return true; }
        var Index = this.Tabs.IndexOf(Page);
        var WasActive = ReferenceEquals(Page, this.ActiveTab);
        Page.DisposePage();
        this.TabPages.Children.Remove(Page);
        this.Tabs.Remove(Page);
        this.Headers.Remove(this.Headers.Single(Header => ReferenceEquals(Header.Page, Page)));
        if (ReferenceEquals(Page, this.PresentedTab)) this.PresentedTab = null;
        if (WasActive) this.SelectTab(this.Tabs[Math.Min(Index, this.Tabs.Count - 1)]);
        return true;
    }

    internal async Task SynchronizePreferencesAsync(ExplorerTab Source)
    {
        foreach (var Page in this.Tabs.ToArray())
            if (!ReferenceEquals(Page, Source)) await Page.ApplySharedPreferencesAsync(Source.PageSettings);
    }

    private void NewTabClick(object Sender, RoutedEventArgs E) => this.AddTab();

    private void TabBarPreviewMouseDown(object Sender, MouseButtonEventArgs E)
    {
        if (E.ChangedButton != MouseButton.Left || E.ClickCount != 2) return;
        TabHeader? Header = null;
        for (var Element = E.OriginalSource as DependencyObject; Element is not null && !ReferenceEquals(Element, this.TabBar);
            Element = Element is Visual ? VisualTreeHelper.GetParent(Element) : LogicalTreeHelper.GetParent(Element))
        {
            // The dedicated buttons and overflow scrollbar keep their own gestures.
            if (Element is ScrollBar || Element is Button Button && (ReferenceEquals(Button, this.NewTabButton) || Button.Name == "CloseTabButton")) return;
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
        var Pages = this.Tabs.ToArray();
        var Index = Array.IndexOf(Pages, Anchor);
        if (Index < 0) return [];
        return Command switch
        {
            "CloseRightTabs" => Pages[(Index + 1)..],
            "CloseLeftTabs" => Pages[..Index],
            "CloseOtherTabs" => Pages.Where(Page => !ReferenceEquals(Page, Anchor)).ToArray(),
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
        if (Command != "CloseTab" && this.ActiveTab is { } Active && Targets.Contains(Active)) this.SelectTab(Header.Page);
        foreach (var Page in Targets) this.CloseTab(Page);
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
        else if (this.ActiveTab is not null && this.Tabs.Count > 1)
        {
            var Delta = Command == "PreviousTab" ? -1 : 1;
            this.SelectTab(this.Tabs[(this.Tabs.IndexOf(this.ActiveTab) + Delta + this.Tabs.Count) % this.Tabs.Count]);
        }
        return Task.CompletedTask;
    }

    private async void WindowInputPreviewMouseDown(object Sender, MouseButtonEventArgs E)
    {
        if (E.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
        {
            E.Handled = true;
            if (this.ActiveTab is { } Tab) await Tab.NavigateBrowseHistoryAsync(E.ChangedButton == MouseButton.XButton2);
            return;
        }
        // Tab-strip gestures must preserve the workspace selection when leaving a tab.
        if (E.OriginalSource is Visual Source && (ReferenceEquals(Source, this.TabBar) || Source.IsDescendantOf(this.TabBar))) return;
        this.ActiveTab?.HandleWindowMouseDown(Sender, E);
    }
    private IntPtr BrowserAppCommand(IntPtr Window, int Message, IntPtr WParam, IntPtr LParam, ref bool Handled)
    {
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
        this.ActiveTab?.PersistPageSettings();
    }
}
