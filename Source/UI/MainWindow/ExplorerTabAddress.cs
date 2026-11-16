using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private bool AddressEditing;
    private bool AddressLayoutQueued;
    private IReadOnlyList<AddressSegment> AddressSegments = [];
    private CancellationTokenSource? AddressNavigationDelay;
    private ContextMenu? AddressChildrenMenu;

    internal IntPtr AddressWindowMouseMessage(IntPtr Window, int Message, IntPtr WParam, IntPtr LParam, ref bool Handled)
    {
        if (Message == 0x00A1 && WParam == (IntPtr)2) this.ClearFileSelection();
        // WindowChrome handles caption/resize-frame presses as non-client input,
        // so those blank areas do not raise WPF's PreviewMouseDown event.
        if (this.AddressEditing && Message is 0x00A1 or 0x00A4) this.CancelAddressEdit();
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private static int BreadcrumbNavigationDelayMilliseconds => (int)Math.Min(GetDoubleClickTime(), 180u);

    private void CancelAddressNavigation()
    {
        this.AddressNavigationDelay?.Cancel();
        this.AddressNavigationDelay = null;
    }

    private void UpdateAddressPath(bool PreserveEditing = false)
    {
        if (this.AddressChildrenMenu is { } Children) Children.IsOpen = false;
        if (this.AddressOverflowButton.ContextMenu is { } Menu) Menu.IsOpen = false;
        this.AddressSegments = this.IsVirtualLocation ? [new AddressSegment(CDir.Localization.LanguageService.Text(this.IsComputer ? "DirectoryTree" : "RecycleBin"), this.CurrentPath, "")]
            : AddressSegment.Create(this.CurrentPath);
        this.AddressBreadcrumbs.ItemsSource = this.AddressSegments;
        if (!PreserveEditing)
        {
            this.AddressBox.Text = this.CurrentPath;
            this.AddressBox.CaretIndex = this.AddressBox.Text.Length;
            this.EndAddressEdit();
        }
        this.QueueAddressLayout();
    }

    private void BeginAddressEdit()
    {
        if (this.AddressChildrenMenu is { } Children) Children.IsOpen = false;
        this.CancelSidebarFolderClick();
        this.CancelAddressNavigation();
        if (!this.AddressEditing) this.AddressBox.Text = this.CurrentPath;
        this.AddressEditing = true;
        if (this.AddressOverflowButton.ContextMenu is { } Menu) Menu.IsOpen = false;
        this.AddressDisplay.Visibility = Visibility.Collapsed;
        this.AddressBox.Visibility = Visibility.Visible;
        this.AddressBox.Focus();
        this.AddressBox.SelectAll();
    }

    private void EndAddressEdit()
    {
        this.AddressEditing = false;
        this.AddressBox.Text = this.CurrentPath;
        this.AddressBox.Visibility = Visibility.Collapsed;
        this.AddressDisplay.Visibility = Visibility.Visible;
        this.QueueAddressLayout();
    }

    private void CancelAddressEdit()
    {
        this.CancelAddressNavigation();
        this.EndAddressEdit();
        FocusManager.SetFocusedElement(this, this.ActiveFileControl);
        // Clear the real window focus scope as well as the page's saved focus.
        // Non-focusable blank surfaces must not leave the collapsed editor selected.
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this.AddressBox), this.ActiveFileControl);
    }

    internal void AddressEditPreviewMouseDown(MouseButtonEventArgs E)
    {
        if (!this.AddressEditing) return;
        var InsideBar = false;
        for (var Source = E.OriginalSource as DependencyObject; Source is not null;)
        {
            // Keep caret placement, selection and the bar's buttons available.
            if (ReferenceEquals(Source, this.AddressBox)) return;
            if (Source is ButtonBase && this.AddressBar.IsAncestorOf(Source)) return;
            if (ReferenceEquals(Source, this.AddressBar)) { InsideBar = true; break; }
            Source = Source is FrameworkContentElement Content ? Content.Parent : System.Windows.Media.VisualTreeHelper.GetParent(Source);
        }
        this.CancelAddressEdit();
        // A blank press inside the bar must not immediately enter editing again.
        // Other controls continue to receive the original click normally.
        if (InsideBar) E.Handled = true;
    }

    private void AddressDisplayMouseDown(object Sender, MouseButtonEventArgs E)
    {
        if (this.AddressEditing) return;
        this.PrepareAddressFolderDrag(E.OriginalSource as DependencyObject, E.ClickCount, E.GetPosition(this.AddressBar));
        if (this.AddressFolderDragCandidate?.Element == this.AddressIcon) { E.Handled = true; return; }
        var Button = FindAncestor<ButtonBase>(E.OriginalSource as DependencyObject);
        // Copy, Go and the ancestor menu retain their own button actions.
        if (Button is not null && Button.Tag is not string) return;
        if (Button is not null && E.ClickCount < 2) return;
        E.Handled = true;
        this.BeginAddressEdit();
    }

    private void AddressBoxLostKeyboardFocus(object Sender, KeyboardFocusChangedEventArgs E)
    {
        // Keep the draft until the Go button consumes it. Switching to another app also keeps the draft.
        if (!this.AddressEditing || ReferenceEquals(E.NewFocus, this.AddressGoButton)) return;
        if (E.NewFocus is DependencyObject Focus && Window.GetWindow(Focus) == this.HostWindow) this.EndAddressEdit();
    }

    private async void AddressSegmentClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (this.AddressFolderDragActive) return;
        this.CancelSidebarFolderClick();
        if (Sender is not FrameworkElement { Tag: string Path }) return;
        var TreeKey = this.NavigationTreeKey;
        this.CancelAddressNavigation();
        if (Sender is Button)
        {
            // Keep a short window for a second press to enter editing, without making
            // single-click navigation wait through the full system double-click interval.
            using var Delay = new CancellationTokenSource();
            this.AddressNavigationDelay = Delay;
            try { await Task.Delay(BreadcrumbNavigationDelayMilliseconds, Delay.Token); }
            catch (OperationCanceledException) { return; }
            finally
            {
                if (ReferenceEquals(this.AddressNavigationDelay, Delay)) this.AddressNavigationDelay = null;
            }
        }
        if (!this.WindowClosed && !this.AddressEditing && await this.NavigateAsync(Path, TreeKey: TreeKey)) this.ActiveFileControl.Focus();
    }

    private void AddressOverflowClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        var Menu = new ContextMenu { PlacementTarget = this.AddressOverflowButton, Placement = PlacementMode.Bottom, FontFamily = this.FontFamily };
        foreach (var Segment in this.AddressSegments)
        {
            var Item = new MenuItem { Header = new TextBlock { Text = Segment.Name }, Tag = Segment.FullPath, ToolTip = Segment.FullPath };
            System.Windows.Automation.AutomationProperties.SetName(Item, Segment.FullPath);
            Item.Click += this.AddressSegmentClick;
            Menu.Items.Add(Item);
        }
        this.AddressOverflowButton.ContextMenu = Menu;
        Menu.IsOpen = true;
    }

    private async void AddressChildrenClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        this.CancelAddressNavigation();
        if (Sender is not Button { Tag: AddressSegment Segment } Button || this.AddressFolderDragActive) return;
        if (AppMain.SmokeTest && !this.IsVirtualLocation && !AppMain.IsInsideSmokeScope(Segment.FullPath)) return;
        if (this.AddressChildrenMenu is { } Previous)
        {
            var ToggleClosed = Previous.IsOpen && ReferenceEquals(Previous.PlacementTarget, Button);
            Previous.IsOpen = false;
            if (ToggleClosed) { this.AddressChildrenMenu = null; return; }
        }
        var Version = this.NavigationVersion;
        var Menu = new ContextMenu { PlacementTarget = Button, Placement = PlacementMode.Bottom,
            Style = (Style)this.FindResource("ScrollableColumnContextMenu"), MaxHeight = Math.Min(420, SystemParameters.WorkArea.Height * .8) };
        Menu.SetResourceReference(Control.FontFamilyProperty, "InterfaceFont");
        Menu.Opened += (_, _) => Button.Content = "⌄";
        Menu.Closed += (_, _) => Button.Content = "›";
        this.AddressChildrenMenu = Menu;
        try
        {
            var Children = this.IsComputer ? this.Roots.Select(Node => new DirectoryEntry(Node.Name, Node.FullPath)).ToArray()
                : this.IsRecycleBin ? []
                : await Task.Run(() => this.Files.GetDirectories(Segment.FullPath, this.Settings.ShowHidden).ToArray());
            if (this.WindowClosed || Version != this.NavigationVersion || !ReferenceEquals(this.AddressChildrenMenu, Menu)) return;
            foreach (var Child in Children.OrderBy(Child => Child.Name, CDir.Services.FileNameComparer.Instance))
            {
                var Label = new StackPanel { Orientation = Orientation.Horizontal };
                Label.Children.Add(new CDir.Controls.ShellIcon { Path = Child.FullPath, IsDirectory = true, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
                Label.Children.Add(new TextBlock { Text = Child.Name, VerticalAlignment = VerticalAlignment.Center });
                var Item = new MenuItem { Header = Label, Tag = Child.FullPath,
                    ToolTip = Child.FullPath, IsCheckable = true,
                    IsChecked = this.AddressSegments.Any(Part => SamePath(Part.FullPath, Child.FullPath)),
                    Style = (Style)this.FindResource("ColumnToggleMenuItem") };
                Item.Click += this.AddressSegmentClick;
                Menu.Items.Add(Item);
            }
            if (Menu.Items.Count == 0) return;
            Button.ContextMenu = Menu;
            Menu.IsOpen = true;
        }
        catch (Exception Error) when (Error is System.IO.IOException or UnauthorizedAccessException)
        { this.ShowNotice(CDir.Localization.LanguageService.Text("OpenLocationError", FriendlyError(Error)), true); }
    }

    private void AddressBreadcrumbScrollChanged(object Sender, ScrollChangedEventArgs E)
    {
        if (E.ExtentWidthChange != 0 || E.ViewportWidthChange != 0) this.QueueAddressLayout();
    }

    private void AddressBreadcrumbSizeChanged(object Sender, SizeChangedEventArgs E) => this.QueueAddressLayout();

    private void QueueAddressLayout()
    {
        if (this.AddressLayoutQueued || this.WindowClosed || Dispatcher.HasShutdownStarted) return;
        this.AddressLayoutQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            this.AddressLayoutQueued = false;
            if (this.WindowClosed || !this.AddressDisplay.IsVisible) return;
            this.AddressOverflowButton.Visibility = this.AddressBreadcrumbScroll.ExtentWidth > this.AddressDisplay.ActualWidth + 0.5 ? Visibility.Visible : Visibility.Collapsed;
            if (Math.Abs(this.AddressBreadcrumbScroll.HorizontalOffset - this.AddressBreadcrumbScroll.ScrollableWidth) > 0.5)
                this.AddressBreadcrumbScroll.ScrollToHorizontalOffset(this.AddressBreadcrumbScroll.ScrollableWidth);
        }, DispatcherPriority.Loaded);
    }
}
