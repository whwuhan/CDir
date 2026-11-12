using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunColumnFitSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Column fit check failed: " + Name);
            Passed.Add(Name);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Column Fit")).FullName;
        var Folder = Directory.CreateDirectory(Path.Combine(Root, "Files")).FullName;
        for (var Index = 0; Index < 110; Index++) File.WriteAllText(Path.Combine(Folder, $"File {Index:000}.txt"), "Owned fixture");
        var LongName = "zzzz The longest off-screen 文件夹名称 with a long descriptive filename and Unicode 内容.txt";
        File.WriteAllText(Path.Combine(Folder, LongName), "Longest");
        var Empty = Directory.CreateDirectory(Path.Combine(Root, "Empty")).FullName;
        var SettingsFile = Path.Combine(Root, "Settings.json");
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new AppSettings { LastPath = Folder, ExitOnClose = true }));
        MainWindow? Probe = null;
        async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); Probe!.UpdateLayout(); }
        async Task Open()
        {
            Probe = new MainWindow(SettingsFile, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show(); await Probe.WorkspaceRestored; await Probe.ActiveTab!.WaitUntilReadyAsync(); await Settle();
        }
        IEnumerable<DataGridColumnHeader> Headers(DependencyObject Parent)
        {
            for (var Index = 0; Index < VisualTreeHelper.GetChildrenCount(Parent); Index++)
            {
                var Child = VisualTreeHelper.GetChild(Parent, Index);
                if (Child is DataGridColumnHeader Header && Header.Column is not null) yield return Header;
                foreach (var Item in Headers(Child)) yield return Item;
            }
        }
        DataGridColumn Column(ExplorerTab Page, string Key) => Page.FileGrid.Columns.Single(Item => Item.SortMemberPath == Key);
        DataGridColumnHeader Header(ExplorerTab Page, DataGridColumn Column) => Headers(Page.FileGrid).Single(Item => Item.Column == Column);
        async Task<KeyEventArgs> Press(UIElement Target, Key Key, ModifierKeys Modifiers)
        {
            var Input = new KeyEventArgs(new SmokeKeyboardDevice(Modifiers), PresentationSource.FromVisual(Target), 0, Key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Target.RaiseEvent(Input); await Settle(); return Input;
        }
        double TextWidth(ExplorerTab Page, string Text, double Size)
        {
            var Label = new TextBlock { Text = Text, FontFamily = Page.FileGrid.FontFamily, FontSize = Size };
            Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return Label.DesiredSize.Width;
        }
        void Capture(string Name, Visual View, double Width, double Height)
        {
            if (AppMain.ScreenshotPath is not { } Output) return;
            var Bitmap = new RenderTargetBitmap((int)Math.Ceiling(Width), (int)Math.Ceiling(Height), 96, 96, PixelFormats.Pbgra32);
            Bitmap.Render(View); var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
            using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, Name + ".png")); Encoder.Save(Stream);
        }
        try
        {
            await Open();
            var A = Probe!.ActiveTab!; A.SetTabAlias("Fit Column");
            var B = Probe.AddTab(); await B.WaitUntilReadyAsync(); B.SetTabAlias("Independent Tab"); Probe.SelectTab(A); await Settle();
            var Name = Column(A, nameof(FileEntry.Name)); var Other = Column(B, nameof(FileEntry.Name));
            var OtherWidth = (Other.Width.UnitType, Other.Width.Value);
            var Longest = A.Entries.Single(Entry => Entry.Name == LongName);
            Check(A.FileGrid.ItemContainerGenerator.ContainerFromItem(Longest) is null, "The longest fitting fixture is outside the virtualized viewport");
            A.FileGrid.SelectedItem = A.Entries[0]; var Selection = A.SelectedEntries().Select(Entry => Entry.FullPath).ToArray();
            var NameHeader = Header(A, Name);
            NameHeader.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            var Args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [NameHeader, true], culture: null)!;
            Args.RoutedEvent = ContextMenuService.ContextMenuOpeningEvent; NameHeader.RaiseEvent(Args); await Settle();
            var Menu = NameHeader.ContextMenu!; Menu.StaysOpen = true;
            var Fit = Menu.Items.OfType<MenuItem>().Single();
            Check(Args.Handled && Menu.IsOpen && Fit.Header?.ToString() == LanguageService.Text("AutoFitColumns") && Fit.InputGestureText == "Alt+E"
                && A.SelectedEntries().Select(Entry => Entry.FullPath).SequenceEqual(Selection),
                "Header right-click opens the localized fit menu with its shortcut and preserves file selection");
            Check(Fit.FontSize == (double)A.FindResource("UIFontMenus") && Fit.Padding.Top <= 3 && Menu.Padding.Top <= 2,
                "The fit menu keeps readable menu fonts and compact item spacing");
            Capture("ColumnFitMenu", Menu, Menu.ActualWidth, Menu.ActualHeight);
            Menu.IsOpen = false; Fit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Settle();
            var ContentWidth = TextWidth(A, LongName, A.FileGrid.FontSize) + 42;
            Check(Name.Width.IsAbsolute && Name.ActualWidth >= ContentWidth && Name.ActualWidth <= ContentWidth + 5,
                "Fitting measures the longest off-screen Unicode filename including its icon and cell padding");
            Check(A.FileGrid.Columns.Where(Item => Item.Visibility == Visibility.Visible).All(Item => Item.Width.IsAbsolute)
                && (Other.Width.UnitType, Other.Width.Value) == OtherWidth, "Fitting all columns affects only its own tab");
            foreach (var Metadata in A.FileGrid.Columns.OfType<DataGridTextColumn>().Where(Item => Item.Visibility == Visibility.Visible))
            {
                A.FileGrid.ScrollIntoView(A.FileGrid.Items[0], Metadata); await Settle();
                var Label = (TextBlock)Metadata.GetCellContent(A.FileGrid.Items[0]);
                Check(Label.ActualWidth + .1 >= TextWidth(A, Label.Text, Label.FontSize), "Fitted metadata cells display their complete formatted text: " + Metadata.SortMemberPath);
            }
            var Type = Column(A, nameof(FileEntry.TypeName)); Type.Width = 500; Name.Width = 300;
            var KeyResult = await Press(A.SearchBox, Key.E, ModifierKeys.Alt);
            Check(KeyResult.Handled && Type.ActualWidth < 500 && Name.ActualWidth >= ContentWidth,
                "Alt+E from the current tab's search field fits every visible column");
            Check(A.ChangeShortcut("AutoFitColumns", "Ctrl+Alt+E"), "Column fitting accepts a custom shortcut");
            Type.Width = 500; await Press(A.FileGrid, Key.E, ModifierKeys.Alt);
            Check(Type.ActualWidth == 500, "The old fit shortcut stops dispatching after reassignment");
            KeyResult = await Press(A.FileGrid, Key.E, ModifierKeys.Control | ModifierKeys.Alt);
            Check(KeyResult.Handled && Type.ActualWidth < 500, "The customized fit shortcut dispatches through the active tab");
            Check(A.ChangeShortcut("AutoFitColumns", ""), "The fit shortcut can be unassigned");
            Type.Width = 500; await Press(A.FileGrid, Key.E, ModifierKeys.Control | ModifierKeys.Alt);
            Check(Type.ActualWidth == 500, "An unassigned fit shortcut leaves widths unchanged");
            A.ChangeShortcut("AutoFitColumns", "Alt+E");
            A.SearchBox.Text = "File 000"; await A.WaitForSearchSmokeAsync(); await Settle();
            await Press(A.FileGrid, Key.E, ModifierKeys.Alt);
            Check(Name.ActualWidth < ContentWidth && Name.ActualWidth < 230, "Fitting uses the current filtered list and can shrink Name below its initial minimum");
            A.SearchBox.Clear(); await A.WaitForSearchSmokeAsync(); await A.NavigateAsync(Empty, RecordHistory: false); await Settle();
            A.Resources["UIFontHeaders"] = 22d; A.FileGrid.FontSize = 24; await Settle();
            A.AutoFitColumns(); await Settle();
            var HeaderWidth = TextWidth(A, Name.Header.ToString()!, 22) + 38;
            Check(Name.ActualWidth >= HeaderWidth && Name.ActualWidth <= HeaderWidth + 5,
                "An empty folder fits the complete header and sort gutter using the configured header font");
            var SavedWidth = Name.ActualWidth;
            Check(A.ChangeShortcut("ToggleSidebar", "Alt+1") && A.CaptureSaveSettings().ShortcutDefaultsRevision == 1,
                "The one-time shortcut upgrade marker is included in saved preferences");
            Probe.PersistWorkspaceNow(); Probe.RequestExit(); Probe = null; await Open();
            A = Probe!.Tabs.Single(Page => Page.TabAlias == "Fit Column"); Probe.SelectTab(A); await Settle();
            Check(Math.Abs(Column(A, nameof(FileEntry.Name)).ActualWidth - SavedWidth) < 1,
                "A fitted column narrower than the initial minimum survives workspace restoration");
            Check(A.ShortcutBindings["ToggleSidebar"] == "Alt+1", "A personal sidebar reassignment to Alt+1 survives a complete workspace reload");
            await A.NavigateAsync(Folder, RecordHistory: false); await Settle();
            await Press(A.FileGrid, Key.E, ModifierKeys.Alt);
            Check(!new AppSettings().AutoFitColumnsOnNavigation
                && !JsonSerializer.Deserialize<AppSettings>("{}")!.AutoFitColumnsOnNavigation,
                "Fresh and legacy settings leave automatic column fitting off");
            Name = Column(A, nameof(FileEntry.Name)); Name.Width = 333;
            await A.NavigateAsync(Empty, RecordHistory: false); await Settle();
            Check(Math.Abs(Name.ActualWidth - 333) < 1, "Opening another folder preserves personal widths while automatic fitting is off");
            var Hidden = Probe.Tabs.Single(Page => !ReferenceEquals(Page, A));
            await Hidden.WaitUntilReadyAsync();
            await Hidden.NavigateAsync(Folder, RecordHistory: false);
            var HiddenName = Column(Hidden, nameof(FileEntry.Name)); HiddenName.Width = 300;
            Hidden.SearchBox.Text = "File 000"; await Hidden.WaitForSearchSmokeAsync();
            Hidden.FileGrid.SelectedItem = Hidden.Entries[0];
            var HiddenSelection = Hidden.SelectedEntries().Select(Entry => Entry.FullPath).ToArray();
            var Paths = Probe.Tabs.Select(Page => Page.TabPath).ToArray();
            await A.RunPreferencesSmokeChecksAsync(Passed, InspectShortcuts: async Dialog =>
            {
                Dialog.Categories.SelectedItem = Dialog.UICategory;
                Dialog.AutoFitColumnsOnNavigationCheck.IsChecked = true;
                await A.AutoFitColumnsPreferenceUpdate; await Settle();
                Check(Name.ActualWidth < 230 && HiddenName.Width.IsAbsolute && HiddenName.Width.Value < 230,
                    "Enabling automatic fitting immediately shrinks the active empty folder and the hidden filtered tab");
                Check(Dialog.IsVisible && ReferenceEquals(Probe.ActiveTab, A)
                    && Probe.Tabs.Select(Page => Page.TabPath).SequenceEqual(Paths)
                    && Hidden.SearchBox.Text == "File 000" && Hidden.SelectedEntries().Select(Entry => Entry.FullPath).SequenceEqual(HiddenSelection),
                    "Immediate fitting preserves the open dialog active tab paths filter and selection");
                Name.Width = 333; HiddenName.Width = 300;
                Dialog.AutoFitColumnsOnNavigationCheck.IsChecked = false;
                await A.AutoFitColumnsPreferenceUpdate; await Settle();
                Check(Math.Abs(Name.ActualWidth - 333) < 1 && HiddenName.Width.Value == 300,
                    "Disabling automatic fitting leaves existing widths unchanged");
                Hidden.SearchBox.Clear(); await Hidden.WaitForSearchSmokeAsync();
                Dialog.AutoFitColumnsOnNavigationCheck.IsChecked = true;
                await A.AutoFitColumnsPreferenceUpdate; await Settle();
                Check(Name.ActualWidth < 230 && HiddenName.Width.Value >= TextWidth(Hidden, LongName, Hidden.FileGrid.FontSize) + 42,
                    "Re-enabling automatic fitting immediately expands the hidden tab for its longest off-screen filename");
            });
            Check(A.Settings.AutoFitColumnsOnNavigation && Probe.Tabs.All(Page => Page.Settings.AutoFitColumnsOnNavigation),
                "Enabling automatic fitting in Preferences synchronizes existing tabs");
            await A.NavigateAsync(Folder, RecordHistory: false); await Settle();
            Check(Name.ActualWidth >= TextWidth(A, LongName, A.FileGrid.FontSize) + 42
                && A.FileGrid.Columns.Where(Item => Item.Visibility == Visibility.Visible).All(Item => Item.Width.IsAbsolute),
                "Opening a populated folder automatically fits every visible column including off-screen names");
            Name.Width = 444;
            await A.RefreshCurrentFolderAsync(RefreshTree: false); await Settle();
            Check(Math.Abs(Name.ActualWidth - 444) < 1, "Watcher refreshes preserve manually adjusted widths with automatic fitting enabled");
            var RefreshedName = "zzzzz Newly refreshed file with an even longer descriptive Unicode 文件名称 " + new string('w', 65) + ".txt";
            File.WriteAllText(Path.Combine(Folder, RefreshedName), "Refreshed fixture");
            A.FileGrid.SelectedItem = A.Entries.Single(Entry => Entry.Name == "File 000.txt");
            var RefreshSelection = A.SelectedEntries().Select(Entry => Entry.FullPath).ToArray();
            HiddenName.Width = 555;
            Check(await A.HandleShortcutAsync(Key.F5, ModifierKeys.None, InText: false, InFileList: true),
                "F5 dispatches the manual refresh command");
            await Settle();
            Check(A.Entries.Any(Entry => Entry.Name == RefreshedName)
                && Name.ActualWidth >= TextWidth(A, RefreshedName, A.FileGrid.FontSize) + 42
                && A.SelectedEntries().Select(Entry => Entry.FullPath).SequenceEqual(RefreshSelection)
                && HiddenName.Width.Value == 555,
                "F5 fits newly refreshed off-screen filenames and preserves selection and other tabs' widths");
            A.SearchBox.Text = "File 000"; await A.WaitForSearchSmokeAsync(); Name.Width = 444;
            await A.HandleShortcutAsync(Key.F5, ModifierKeys.None, InText: true, InFileList: false); await Settle();
            Check(Name.ActualWidth < 230 && A.SearchBox.Text == "File 000" && A.FileGrid.Items.Count == 1
                && A.SelectedEntries().Select(Entry => Entry.FullPath).SequenceEqual(RefreshSelection),
                "F5 from a text field waits for the filtered refresh before fitting and preserves its filter and selection");
            Name.Width = 444;
            Check(await A.ExecuteBackgroundCommandAsync("CDir.Refresh"), "The background menu dispatches manual refresh");
            await Settle();
            Check(Name.ActualWidth < 230, "The manual refresh menu uses the same automatic fitting behavior as F5");
            await A.ChangeAutoFitColumnsOnNavigationAsync(false); Name.Width = 444;
            await A.HandleShortcutAsync(Key.F5, ModifierKeys.None, InText: false, InFileList: true); await Settle();
            Check(Math.Abs(Name.ActualWidth - 444) < 1, "F5 preserves manual widths when automatic fitting is disabled");
            await A.ChangeAutoFitColumnsOnNavigationAsync(true);
            A.SearchBox.Clear(); await A.WaitForSearchSmokeAsync();
            await A.NavigateAsync(Empty, RecordHistory: false); await Settle();
            Check(Name.ActualWidth < 230, "Opening an empty folder automatically shrinks columns to their headers");
            Name.Width = 444;
            await A.HandleShortcutAsync(Key.F5, ModifierKeys.None, InText: false, InFileList: true); await Settle();
            Check(Name.ActualWidth < 230, "F5 also refits an empty folder to its headers");
            Name.Width = 444;
            Check(!await A.NavigateAsync(Path.Combine(Root, "Missing folder"), RecordHistory: false)
                && Math.Abs(Name.ActualWidth - 444) < 1, "Failed navigation leaves the existing folder widths unchanged");
            Probe.PersistWorkspaceNow(); Probe.RequestExit(); Probe = null; await Open();
            A = Probe!.Tabs.Single(Page => Page.TabAlias == "Fit Column"); Probe.SelectTab(A); await Settle();
            Check(A.Settings.AutoFitColumnsOnNavigation && Column(A, nameof(FileEntry.Name)).ActualWidth < 230,
                "The enabled preference survives restart and fits the initially opened folder");
            var NewTab = Probe.AddTab(); await NewTab.WaitUntilReadyAsync();
            Check(NewTab.Settings.AutoFitColumnsOnNavigation && Column(NewTab, nameof(FileEntry.Name)).ActualWidth < 230,
                "New tabs inherit automatic fitting and fit the folder they open");
            Probe.SelectTab(A);
            await A.RunPreferencesSmokeChecksAsync(Passed, RestoreDefaults: true);
            Check(!A.Settings.AutoFitColumnsOnNavigation && Probe.Tabs.All(Page => !Page.Settings.AutoFitColumnsOnNavigation),
                "Restoring default preferences switches automatic fitting off in every tab");
            await A.NavigateAsync(Folder, RecordHistory: false); A.AutoFitColumns(); await Settle();
            Capture("ColumnFitReleasePreview", Probe, Probe.ActualWidth, Probe.ActualHeight);
        }
        finally { Probe?.RequestExit(); }
    }
}
