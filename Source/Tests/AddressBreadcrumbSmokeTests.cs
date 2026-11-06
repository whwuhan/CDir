using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunAddressBreadcrumbSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialSearch = this.SearchBox.Text;
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CDirAddressUI" + FixtureId));
        var Marker = Path.Combine(Fixture, ".CDirAddressOwner");
        var ParentPath = Directory.CreateDirectory(Path.Combine(Fixture, "父目录 With Spaces")).FullName;
        var ChildPath = Directory.CreateDirectory(Path.Combine(ParentPath, "子目录 中文")).FullName;
        var LongPath = Directory.CreateDirectory(Path.Combine(ChildPath, "LongFolderSegmentWithSpaces0123456789", "AnotherLongFolderSegmentForOverflow0123456789")).FullName;
        File.WriteAllText(Marker, FixtureId);

        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Address breadcrumb UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task WaitUntil(Func<bool> Condition)
        {
            var Deadline = Environment.TickCount64 + 15000;
            while (!Condition())
            {
                if (Environment.TickCount64 > Deadline)
                    throw new TimeoutException("Address breadcrumb UI did not settle: " + this.CurrentPath + "; editor=" + this.AddressBox.Text
                        + "; loading=" + this.Loading + "; editing=" + this.AddressEditing + "; display=" + this.AddressDisplay.IsVisible
                        + "; pending breadcrumb=" + (this.AddressNavigationDelay is not null) + "; version=" + this.NavigationVersion
                        + "; last check=" + Passed.LastOrDefault());
                await Task.Delay(25);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            }
        }
        async Task Settle()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        AddressSegment[] Segments() => this.AddressBreadcrumbs.Items.Cast<AddressSegment>().ToArray();
        Button SegmentButton(string FullPath)
        {
            this.AddressBreadcrumbs.UpdateLayout();
            var Segment = Segments().Single(Segment => SamePath(Segment.FullPath, FullPath));
            var Container = this.AddressBreadcrumbs.ItemContainerGenerator.ContainerFromItem(Segment) as DependencyObject
                ?? throw new InvalidOperationException("Breadcrumb item container is missing");
            return FindVisualChild<Button>(Container) ?? throw new InvalidOperationException("Breadcrumb button is missing");
        }
        static void Invoke(FrameworkElement Element)
        {
            var Peer = UIElementAutomationPeer.CreatePeerForElement(Element)
                ?? throw new InvalidOperationException("Breadcrumb automation peer is missing");
            if (Peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider Invocation)
                throw new InvalidOperationException("Breadcrumb control does not expose Invoke");
            Invocation.Invoke();
        }
        void AddressKey(Key Key)
        {
            var Source = PresentationSource.FromVisual(this.AddressBox) ?? throw new InvalidOperationException("Address editor has no presentation source");
            this.AddressBoxKeyDown(this.AddressBox, new KeyEventArgs(Keyboard.PrimaryDevice, Source, Environment.TickCount, Key)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
        }
        MouseButtonEventArgs Press(UIElement Target, int ClickCount)
        {
            var Args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            };
            // Reproduce WPF's input-manager click count without moving the desktop pointer.
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(Args, ClickCount);
            Target.RaiseEvent(Args);
            return Args;
        }
        try
        {
            if (!await this.NavigateAsync(ChildPath)) throw new InvalidOperationException("Cannot open breadcrumb UI fixture");
            await Settle();
            var Before = Segments();
            Check(this.AddressDisplay.IsVisible && !this.AddressBox.IsVisible
                && Before.Select(Segment => Segment.FullPath).SequenceEqual(AddressSegment.Create(ChildPath).Select(Segment => Segment.FullPath))
                && SegmentButton(ParentPath).Tag is string Target && SamePath(Target, ParentPath),
                "address display renders clickable complete ancestors for Unicode folders and spaces");

            var OriginalHistory = this.History.ToArray();
            var AncestorButton = SegmentButton(ParentPath);
            Press(AncestorButton, 1);
            AncestorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(this.AddressNavigationDelay is not null && SamePath(this.CurrentPath, ChildPath),
                "A breadcrumb's first click briefly waits for a possible second press before navigating");
            Check(BreadcrumbNavigationDelayMilliseconds is > 0 and <= 180,
                "Single-click breadcrumb navigation waits at most 180 milliseconds regardless of a longer system double-click interval");
            var DoublePress = Press(AncestorButton, 2);
            await Task.Delay(BreadcrumbNavigationDelayMilliseconds + 50);
            Check(DoublePress.Handled && this.AddressBox.IsVisible && !this.AddressDisplay.IsVisible
                && SamePath(this.AddressBox.SelectedText, ChildPath) && SamePath(this.CurrentPath, ChildPath)
                && this.History.SequenceEqual(OriginalHistory) && this.AddressNavigationDelay is null,
                "Double-clicking an ancestor edits the full current path without navigating or adding history");
            this.CaptureAddressBreadcrumbPreview("AddressSelectionPreview.png");
            this.AddressBox.Text = ParentPath;
            Check(!Press(this.AddressBox, 1).Handled && this.AddressBox.Text == ParentPath,
                "Mouse clicks inside the path editor remain available for caret placement and text selection");
            AddressKey(Key.Escape);

            foreach (var Region in new UIElement[] { this.AddressBar, this.AddressDisplay, this.AddressIcon })
            {
                var RegionPress = Press(Region, 2);
                Check(RegionPress.Handled && this.AddressBox.IsVisible && SamePath(this.AddressBox.SelectedText, ChildPath)
                    && SamePath(this.CurrentPath, ChildPath),
                    "Double-clicking the path bar, empty display, or folder icon enters full-path editing: " + Region.GetType().Name);
                AddressKey(Key.Escape);
            }
            Check(!Press(this.AddressGoButton, 2).Handled && !this.AddressEditing,
                "The path bar does not intercept the Go button's mouse action");

            // A real single-click sequence must still follow the selected ancestor.
            AncestorButton = SegmentButton(ParentPath);
            Press(AncestorButton, 1);
            AncestorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(this.AddressNavigationDelay is not null && !this.AddressEditing,
                "A single breadcrumb click schedules navigation after leaving path editing");
            await this.RefreshAsync();
            Check(this.AddressNavigationDelay is not null || SamePath(this.CurrentPath, ParentPath),
                "A background refresh does not discard a pending explicit breadcrumb jump");
            await WaitUntil(() => !this.Loading && SamePath(this.CurrentPath, ParentPath));
            Check(this.AddressDisplay.IsVisible && !this.AddressBox.IsVisible,
                "A single breadcrumb click navigates after the shortened delay");
            await this.BackAsync();

            Invoke(SegmentButton(ParentPath));
            await WaitUntil(() => !this.Loading && SamePath(this.CurrentPath, ParentPath));
            await this.BackAsync();
            var BackMatches = SamePath(this.CurrentPath, ChildPath) && SamePath(Segments()[^1].FullPath, ChildPath);
            await this.ForwardAsync();
            Check(BackMatches && SamePath(this.CurrentPath, ParentPath) && SamePath(Segments()[^1].FullPath, ParentPath),
                "invoking an ancestor navigates directly and back forward keep address segments synchronized");

            this.BeginAddressEdit();
            var StartsSelected = this.AddressBox.IsVisible && !this.AddressDisplay.IsVisible
                && SamePath(this.AddressBox.SelectedText, ParentPath)
                && (this.AddressBox.IsKeyboardFocusWithin || ReferenceEquals(FocusManager.GetFocusedElement(this), this.AddressBox));
            this.AddressBox.Text = ChildPath;
            this.BeginAddressEdit();
            Check(StartsSelected && this.AddressBox.Text == ChildPath && this.AddressBox.SelectedText == ChildPath,
                "address edit entry selects the full path and repeated entry preserves an unsubmitted draft");

            AddressKey(Key.Enter);
            await WaitUntil(() => !this.Loading && SamePath(this.CurrentPath, ChildPath) && this.AddressDisplay.IsVisible);
            this.BeginAddressEdit();
            this.AddressBox.Text = "__discarded_address_draft__";
            AddressKey(Key.Escape);
            Check(this.AddressDisplay.IsVisible && !this.AddressBox.IsVisible && SamePath(this.CurrentPath, ChildPath)
                && SamePath(this.AddressBox.Text, ChildPath) && SamePath(Segments()[^1].FullPath, ChildPath),
                "Enter navigates from editable addresses and Escape restores the current breadcrumb path");

            var BeforeDismissalHistory = this.History.ToArray();
            foreach (var (ClickSurface, Name) in new (UIElement, string)[]
            {
                (this.FileGrid, "file list"), (this.SidebarPanel, "sidebar"), (this.RightPanel, "main panel"),
                (this.StatusText, "status bar"), (this.HostWindow.TitleBrand, "title bar"), (this.NavigationBar, "toolbar"), (this.HostWindow.WindowFrame, "window frame"),
                (this.HostWindow.TabBar, "blank tab strip"), (this.HostWindow.TabScroll, "blank tab scroller")
            })
            {
                this.BeginAddressEdit();
                FocusManager.SetFocusedElement(this, this.AddressBox);
                FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this.AddressBox), this.AddressBox);
                this.AddressBox.Text = ParentPath;
                var OutsideClick = Press(ClickSurface, 1);
                // A routed file-list press can legitimately start a marquee at the desktop pointer's
                // current position. The editor must dismiss without preventing that normal action.
                var StartedMarquee = ReferenceEquals(ClickSurface, this.FileGrid) && this.MarqueeOrigin is not null;
                Check(OutsideClick.Handled == StartedMarquee && !this.AddressEditing && !this.AddressBox.IsFocused && !this.AddressBox.IsKeyboardFocusWithin
                    && !ReferenceEquals(FocusManager.GetFocusedElement(FocusManager.GetFocusScope(this.AddressBox)), this.AddressBox)
                    && this.AddressDisplay.IsVisible && !this.AddressBox.IsVisible && this.AddressBox.Text == ChildPath
                    && SamePath(this.CurrentPath, ChildPath) && this.History.SequenceEqual(BeforeDismissalHistory),
                    "Clicking the " + Name + " removes address focus and discards its draft without navigating or swallowing the click");
                this.EndMarqueeSelection();
            }
            this.BeginAddressEdit();
            FocusManager.SetFocusedElement(this, this.AddressBox);
            this.AddressBox.Text = ParentPath;
            Check(!Press(this.AddressBox, 1).Handled && this.AddressEditing && this.AddressBox.Text == ParentPath,
                "Clicking inside an active address editor preserves the draft for caret placement and selection");
            Check(!Press(this.AddressGoButton, 1).Handled && this.AddressEditing && this.AddressBox.Text == ParentPath,
                "Pressing Go preserves the typed path until the button's navigation action consumes it");
            Press(this.AddressIcon, 1);
            Check(!this.AddressEditing && !this.AddressBox.IsFocused && this.AddressDisplay.IsVisible,
                "Clicking the address bar's non-editor area dismisses editing without immediately reopening it");
            this.BeginAddressEdit();
            FocusManager.SetFocusedElement(this, this.AddressBox);
            this.AddressBox.Text = ParentPath;
            var RoutedEscape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this.AddressBox), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            this.AddressBox.RaiseEvent(RoutedEscape);
            Check(RoutedEscape.Handled && !this.AddressEditing && !this.AddressBox.IsFocused && !this.AddressBox.IsKeyboardFocusWithin
                && this.AddressDisplay.IsVisible && this.AddressBox.Text == ChildPath && SamePath(this.CurrentPath, ChildPath),
                "Escape through the normal key route cancels address editing and removes focus without submitting the draft");
            this.BeginAddressEdit();
            FocusManager.SetFocusedElement(this, this.AddressBox);
            this.AddressBox.Text = ParentPath;
            var CaptionHandled = false;
            this.HostWindow.BrowserAppCommand(IntPtr.Zero, 0x00A1, (IntPtr)2, IntPtr.Zero, ref CaptionHandled);
            Check(!CaptionHandled && !this.AddressEditing && !this.AddressBox.IsFocused && SamePath(this.CurrentPath, ChildPath),
                "A native caption press dismisses address focus while preserving normal window dragging and caption actions");

            this.BeginAddressEdit();
            var Missing = Path.Combine(Fixture, "__missing_address_folder__");
            this.AddressBox.Text = Missing;
            AddressKey(Key.Enter);
            await WaitUntil(() => !this.Loading);
            Check(this.AddressBox.IsVisible && this.AddressBox.Text == Missing && SamePath(this.CurrentPath, ChildPath)
                && SamePath(Segments()[^1].FullPath, ChildPath),
                "invalid typed addresses keep the draft and do not replace the current directory segments");

            const string Draft = "~\\Address draft not submitted";
            this.AddressBox.Text = Draft;
            await this.RefreshAsync();
            Check(this.AddressBox.IsVisible && this.AddressBox.Text == Draft && SamePath(this.CurrentPath, ChildPath),
                "background directory refresh preserves the address editor draft");
            this.EndAddressEdit();
            this.FileGrid.Focus();

            if (!await this.NavigateAsync(LongPath)) throw new InvalidOperationException("Cannot open long breadcrumb fixture");
            await Settle();
            await WaitUntil(() => this.AddressOverflowButton.IsVisible && this.AddressBreadcrumbScroll.HorizontalOffset > 0);
            Invoke(this.AddressOverflowButton);
            await WaitUntil(() => this.AddressOverflowButton.ContextMenu?.IsOpen == true);
            var Menu = this.AddressOverflowButton.ContextMenu!;
            var MenuItems = Menu.Items.OfType<MenuItem>().ToArray();
            var Root = Path.GetPathRoot(LongPath)!;
            var FullAncestorMenu = MenuItems.Length == Segments().Length
                && MenuItems.Any(Item => Item.Tag is string Path && SamePath(Path, Root))
                && MenuItems.Any(Item => Item.Tag is string Path && SamePath(Path, LongPath));
            this.CaptureAddressBreadcrumbPreview();
            var ParentMenuItem = MenuItems.Single(Item => Item.Tag is string Path && SamePath(Path, ParentPath));
            Invoke(ParentMenuItem);
            await WaitUntil(() => !this.Loading && SamePath(this.CurrentPath, ParentPath));
            Menu.IsOpen = false;
            Check(FullAncestorMenu && SamePath(Segments()[^1].FullPath, ParentPath),
                "long addresses retain the leaf and overflow menu can invoke every complete ancestor path");

            this.BeginAddressEdit();
            this.AddressBox.Text = ChildPath;
            this.AddressGoButton.Focus();
            Invoke(this.AddressGoButton);
            await WaitUntil(() => !this.Loading && SamePath(this.CurrentPath, ChildPath) && this.AddressDisplay.IsVisible);
            Check(SamePath(this.AddressBox.Text, ChildPath), "Go button navigation preserves typed addresses across editor focus loss");
            await this.RunAddressSourceSmokeChecksAsync(Passed, ParentPath, ChildPath, LongPath);
            await this.RunAddressDropSmokeChecksAsync(Passed);
        }
        finally
        {
            this.AddressOverflowButton.ContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            this.EndAddressEdit();
            try
            {
                if (!this.WindowClosed && !string.IsNullOrEmpty(InitialPath)) await this.NavigateAsync(InitialPath, RecordHistory: false);
                this.SearchBox.Text = InitialSearch;
            }
            finally
            {
                var TempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Fixture.StartsWith(TempRoot, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(Fixture) != "CDirAddressUI" + FixtureId
                    || !File.Exists(Marker) || File.ReadAllText(Marker) != FixtureId
                    || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Refusing cleanup outside the owned address smoke fixture");
                for (var Attempt = 0; ; Attempt++)
                {
                    try { Directory.Delete(Fixture, true); break; }
                    catch (IOException) when (Attempt < 3) { await Task.Delay(100); }
                }
            }
        }
    }

    private void CaptureAddressBreadcrumbPreview(string FileName = "AddressBreadcrumbPreview.png")
    {
        if (AppMain.ScreenshotPath is null) return;
        UpdateLayout();
        var Bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        Bitmap.Render(this);
        var Encoder = new PngBitmapEncoder();
        Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
        using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(AppMain.ScreenshotPath)!, FileName));
        Encoder.Save(Stream);
    }
}
