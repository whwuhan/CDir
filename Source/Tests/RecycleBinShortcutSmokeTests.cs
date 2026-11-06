using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunRecycleBinShortcutSmokeChecksAsync(List<string> Passed)
    {
        var InitialTarget = this.RecycleBinViewTarget;
        var InitialReader = this.ReadRecycleEntries;
        var InitialCommand = this.RecycleCommandOverride;
        var InitialLanguage = LanguageService.CurrentLanguage;
        var InitialPath = this.CurrentPath;
        var InitialExpanded = this.FavoritesSection.IsExpanded;
        var InitialHidden = this.Settings.HiddenColumns?.ToArray();
        ExplorerTab? Other = null;
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "RecycleGrid-" + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(Fixture, "Owned 中文.txt"), "Recycle grid fixture");
        Directory.CreateDirectory(Path.Combine(Fixture, "Owned Folder"));
        var DeletedAt = new DateTime(2026, 11, 5, 13, 24, 37);
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Recycle Bin grid check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            var Deadline = Environment.TickCount64 + 15000;
            while (this.Loading && Environment.TickCount64 < Deadline) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        KeyEventArgs Press(UIElement Element, Key Key, ModifierKeys Modifiers)
        {
            var Input = new KeyEventArgs(new SmokeKeyboardDevice(Modifiers), PresentationSource.FromVisual(Element), 0, Key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Element.RaiseEvent(Input);
            return Input;
        }
        try
        {
            this.RecycleBinViewTarget = Fixture;
            FileEntry Project(FileEntry Item) => new()
            {
                Name = Item.Name, FullPath = Item.FullPath, ShellId = Item.ShellId, IsDirectory = Item.IsDirectory,
                Extension = Item.Extension, TypeKey = Item.TypeKey, TypeArguments = Item.TypeArguments,
                Size = Item.Size, Modified = Item.Modified, OriginalLocation = Fixture, DateDeleted = DeletedAt
            };
            IReadOnlyList<FileEntry> ReadFixture(string Location, CancellationToken Token, Action<IReadOnlyList<FileEntry>>? BatchReady)
                => RecycleBinService.ReadEntries(Location, Token,
                    BatchReady is null ? null : Batch => BatchReady(Batch.Select(Project).ToArray())).Select(Project).ToArray();
            this.ReadRecycleEntries = ReadFixture;

            // Hold enumeration after its first real Shell entry. The UI must show
            // that entry and remain navigable before the rest of the read finishes.
            using (var Release = new ManualResetEventSlim())
            {
                var FirstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                this.ReadRecycleEntries = (Location, Token, BatchReady) => ReadFixture(Location, Token, Batch =>
                {
                    BatchReady?.Invoke(Batch);
                    FirstBatch.TrySetResult();
                    Release.Wait(Token);
                });
                var Timer = Stopwatch.StartNew();
                var Opening = this.NavigateAsync(ShellLocations.RecycleBin);
                try
                {
                    await Task.WhenAny(FirstBatch.Task, Opening).WaitAsync(TimeSpan.FromSeconds(15));
                    Check(FirstBatch.Task.IsCompletedSuccessfully, "The first Shell batch is available: " + this.StatusText.Text);
                    Check(!Opening.IsCompleted && this.IsRecycleBin && this.Loading && this.Entries.Count == 1
                        && this.BackButton.IsEnabled && this.FileGrid.IsVisible
                        && this.FileGrid.Columns.Count(Column => IsRecycleColumn(Column.SortMemberPath)) == 2,
                        "Recycle Bin displays its first Shell row and enables Back while the remaining enumeration is blocked");
                    AppMain.RecordSmokeProgress("Recycle Bin first-row display: " + Timer.ElapsedMilliseconds + " ms (owned Shell fixture)");
                    this.SearchBox.Text = "中文";
                }
                finally { Release.Set(); await Opening; }
                Check(this.RecycleBinLoaded && this.Entries.Count == 2 && this.View!.Cast<FileEntry>().Count() == 1,
                    "Progressive loading preserves all rows and applies a filter entered before enumeration completes");
                this.SearchBox.Clear();
                Check(this.Entries.All(Item => Item.Size is not null || Item.IsDirectory)
                    && this.Entries.Single(Item => !Item.IsDirectory).Modified.Year == DateTime.Now.Year,
                    "Delayed Shell property stores preserve size, directory attributes, and modified dates");
            }
            await this.BackAsync();
            var Snapshot = this.Entries.ToArray();
            using (var Release = new ManualResetEventSlim())
            {
                var Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var Cancelled = false;
                this.ReadRecycleEntries = (Location, Token, BatchReady) =>
                {
                    Started.TrySetResult();
                    if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Slow Shell fixture was not released.");
                    Cancelled = Token.IsCancellationRequested;
                    // Simulate a COM call finishing after navigation cancelled it.
                    BatchReady?.Invoke(Snapshot);
                    return Snapshot;
                };
                var Opening = this.NavigateAsync(ShellLocations.RecycleBin);
                try
                {
                    await Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await this.BackAsync();
                    Check(SamePath(this.CurrentPath, InitialPath) && !this.Loading,
                        "Back leaves a slow Recycle Bin read without waiting for its Shell worker");
                }
                finally { Release.Set(); await Opening; }
                Check(Cancelled && SamePath(this.CurrentPath, InitialPath) && this.Entries.Select(Item => Item.FullPath).SequenceEqual(Snapshot.Select(Item => Item.FullPath))
                    && !this.FileGrid.Columns.Any(Column => IsRecycleColumn(Column.SortMemberPath)),
                    "Cancelled Recycle Bin batches cannot overwrite a newer folder or restore its columns");
            }
            this.ReadRecycleEntries = ReadFixture;
            Check(await this.NavigateAsync(ShellLocations.RecycleBin), "Recycle Bin loads Shell items into the ordinary CDir panel");
            await Settle();
            Check(this.FileGrid.IsVisible && this.RecycleBinLoaded && this.Entries.Count == 2
                && this.Entries.All(Item => Item.ShellId is { Length: > 2 }) && this.Entries.Any(Item => Item.IsDirectory),
                "The common grid displays real owned Shell file and folder entries with their native identities");
            var PreviousSelection = this.SelectedEntries();
            var DragFolder = this.Entries.First(Item => Item.IsDirectory);
            this.FileGrid.SelectedItems.Clear();
            this.FileGrid.SelectedItems.Add(DragFolder);
            var DragRow = (DataGridRow)this.FileGrid.ItemContainerGenerator.ContainerFromItem(DragFolder);
            this.PrepareMainPanelFolderDrag(DragRow, new Point(), ModifierKeys.None, 1);
            Check(this.MainPanelDragCandidate == DragFolder && this.CreateMainPanelFolderDrag() is null,
                "Recycle Bin folders can start a file transfer without becoming category shortcuts");
            var ShellDrag = CreateFileTransferData([DragFolder]);
            Check(!HasFileDrop(ShellDrag) && ShellDropData.Read(ShellDrag) is { Length: 1 } DragIds
                && DragIds[0].SequenceEqual(DragFolder.ShellId!),
                "Dragging from CDir's Recycle Bin exports the original Shell identity rather than a backing file path");
            this.MainPanelDragCandidate = null;
            this.MainPanelDeferredSelection = null;
            this.FileGrid.SelectedItems.Clear();
            foreach (var Selected in PreviousSelection) this.FileGrid.SelectedItems.Add(Selected);
            var Original = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.OriginalLocation));
            var Deleted = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.DateDeleted));
            Check(Original.Visibility == Visibility.Visible && Deleted.Visibility == Visibility.Visible
                && this.FileGrid.Columns.Count(Column => Column.Visibility == Visibility.Visible) == 6
                && this.FileGrid.RowHeight == (double)this.FindResource("UIFileRowHeight"),
                "Recycle Bin shares ordinary row layout and adds only Original Location and Date Deleted");
            Check(!this.ChangeColumnVisibility(nameof(FileEntry.OriginalLocation), false)
                && !this.ChangeColumnVisibility(nameof(FileEntry.DateDeleted), false)
                && this.GetColumnPreferences().All(Item => !IsRecycleColumn(Item.Key)),
                "The two required Recycle Bin columns cannot be toggled and are absent from editable preferences");
            this.Settings.HiddenColumns = [nameof(FileEntry.OriginalLocation), nameof(FileEntry.DateDeleted)];
            this.ApplyColumnVisibility();
            Check(Original.Visibility == Visibility.Visible && Deleted.Visibility == Visibility.Visible,
                "Even manually edited hidden-column settings cannot hide the two required columns");
            this.Settings.HiddenColumns = InitialHidden?.ToList();
            this.ApplyColumnVisibility();
            this.ColumnMenuClick(this.ColumnMenuButton, new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            var Menu = this.ColumnMenuButton.ContextMenu!;
            Check(Menu.Items.OfType<MenuItem>().Count(Item => Item.IsChecked && !Item.IsEnabled) == 2,
                "The column dropdown lists both required columns as checked fixed items");
            Menu.IsOpen = false;
            Check(Press(this.FileGrid, Key.F, ModifierKeys.Control).Handled
                && (this.SearchBox.IsFocused || ReferenceEquals(FocusManager.GetFocusedElement(this), this.SearchBox)),
                "Ctrl+F focuses the search box in Recycle Bin");
            this.SearchBox.Text = "中文";
            Check(this.View!.Cast<FileEntry>().Count() == 1 && !this.Searching, "Recycle Bin filters the common grid without traversing a virtual filesystem path");
            Check(Press(this.FileGrid, Key.A, ModifierKeys.Control).Handled && this.SelectedEntries().Length == 1,
                "Ctrl+A selects exactly the visible filtered Recycle Bin items");
            this.SearchBox.Clear();
            Press(this.FileGrid, Key.A, ModifierKeys.Control);
            Check(this.SelectedEntries().Length == 2, "Ctrl+A selects all Recycle Bin files and folders in the common grid");
            foreach (var Language in LanguageService.SupportedLanguages)
            {
                this.ChangeLanguage(Language.Code, Persist: false);
                Check(Original.Header?.ToString() == LanguageService.Text("OriginalLocationColumn")
                    && Deleted.Header?.ToString() == LanguageService.Text("DateDeletedColumn")
                    && this.TabTitle == LanguageService.Text("RecycleBin"), Language.Code + " localizes the fixed columns and Recycle Bin title");
            }
            var Entry = this.Entries[0];
            Entry.Use12HourClock = true;
            Check(Entry.DisplayDateDeleted == "2026/11/05 01:24:37 PM", "Deletion dates preserve seconds and respect the configured 12-hour clock");
            Entry.Use12HourClock = false;
            Check(Entry.DisplayDateDeleted == "2026/11/05 13:24:37" && Entry.OriginalLocation == Fixture, "Original location and 24-hour deletion dates render their actual metadata");
            var Captured = new List<string>();
            this.RecycleCommandOverride = (Items, Verb) =>
            {
                if (Items.Any(Item => !AppMain.IsInsideSmokeScope(Item.FullPath) || Item.ShellId is null)) throw new InvalidOperationException("Unowned Shell command.");
                Captured.Add(Verb);
            };
            await this.ExecuteRecycleSelectionAsync("undelete");
            await this.DeleteAsync();
            Check(Captured.SequenceEqual(new[] { "undelete", "delete" }) && File.Exists(Path.Combine(Fixture, "Owned 中文.txt")),
                "Restore and permanent delete dispatch captured Shell identities through the STA worker without touching the user's Recycle Bin");
            this.FileGrid.SelectedItems.Clear();
            var Source = this.FileGrid.ItemsSource;
            File.WriteAllText(Path.Combine(Fixture, "After Refresh.txt"), "Refresh fixture");
            await this.RefreshAsync();
            Check(this.Entries.Count == 3 && ReferenceEquals(Source, this.FileGrid.ItemsSource), "Recycle Bin refresh updates rows without replacing the common grid view");
            if (AppMain.ScreenshotPath is { } Preview)
            {
                var SavedWidth = this.HostWindow.Width;
                this.HostWindow.Width = Math.Min(1600, SystemParameters.WorkArea.Width);
                this.ChangeLanguage("en-US", Persist: false);
                this.DismissSearchTips();
                this.FileGrid.Focus();
                await Settle();
                FindVisualChild<ScrollViewer>(this.FileGrid)?.ScrollToHorizontalOffset(0);
                this.UpdateLayout();
                this.UpdateCounts();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(this);
                var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Output = File.Create(Path.Combine(Path.GetDirectoryName(Preview)!, "RecycleBinGrid.png"));
                Encoder.Save(Output);
                this.HostWindow.Width = SavedWidth;
            }
            await this.BackAsync();
            Check(SamePath(this.CurrentPath, InitialPath) && this.FileGrid.IsVisible && !this.FileGrid.Columns.Any(Column => IsRecycleColumn(Column.SortMemberPath)),
                "Back restores ordinary browsing and removes the Recycle Bin-only columns");
            await this.ForwardAsync();
            Check(this.IsRecycleBin && this.Entries.Count == 3, "Forward restores the Recycle Bin common-grid view");
            Other = this.HostWindow.AddTab();
            Other.RecycleBinViewTarget = Fixture;
            Other.ReadRecycleEntries = this.ReadRecycleEntries;
            await Other.WaitUntilReadyAsync();
            Check(Other.IsRecycleBin && Other.FileGrid.IsVisible && !ReferenceEquals(Other.Entries, this.Entries), "Recycle Bin tabs retain independent lists and selection");
            this.HostWindow.SelectTab(this);
            this.HostWindow.CloseTab(Other); Other = null;
        }
        finally
        {
            this.HostWindow.SelectTab(this);
            if (Other is not null) this.HostWindow.CloseTab(Other);
            this.Settings.HiddenColumns = InitialHidden?.ToList();
            if (this.ColumnMenuButton.ContextMenu is { } Menu) Menu.IsOpen = false;
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.RecycleBinViewTarget = InitialTarget;
            this.ReadRecycleEntries = InitialReader;
            this.RecycleCommandOverride = InitialCommand;
            this.ChangeLanguage(InitialLanguage, Persist: false);
            this.FavoritesSection.IsExpanded = InitialExpanded;
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unowned Recycle Bin fixture cleanup.");
            Directory.Delete(Fixture, true);
        }
    }
}
