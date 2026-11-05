using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task WaitForSearchSmokeAsync()
    {
        var Timer = Stopwatch.StartNew();
        while (Timer.Elapsed < TimeSpan.FromSeconds(12))
        {
            var Pending = this.SearchTask;
            await Pending.WaitAsync(TimeSpan.FromSeconds(12));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (ReferenceEquals(Pending, this.SearchTask) && !this.Searching && !this.Loading && !this.RefreshTimer.IsEnabled)
            {
                this.UpdateLayout();
                return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("Recursive search did not settle in its owned fixture.");
    }

    private async Task RunSearchSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialSearch = this.SearchBox.Text;
        var InitialHidden = this.Settings.ShowHidden;
        var InitialClock = this.Settings.Use12HourClock;
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirSearch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Fixture, "needle-folder"));
        var Deep = Directory.CreateDirectory(Path.Combine(Fixture, "branch", "deeper")).FullName;
        var MatchedFolder = Directory.CreateDirectory(Path.Combine(Deep, "needle-dir")).FullName;
        var DeepFile = Path.Combine(Deep, "needle-leaf.txt");
        File.WriteAllText(Path.Combine(Fixture, "needle-root.txt"), "root");
        File.WriteAllText(Path.Combine(Fixture, "needle-folder", "ordinary.txt"), "ordinary");
        File.WriteAllText(DeepFile, "deep");
        File.WriteAllText(Path.Combine(Fixture, "branch", "needle-leaf.txt"), "duplicate");
        File.WriteAllText(Path.Combine(Deep, "资料 needle 文件.txt"), "unicode");
        var HiddenFile = Path.Combine(Fixture, "needle-hidden.txt");
        File.WriteAllText(HiddenFile, "hidden");
        File.SetAttributes(HiddenFile, FileAttributes.Hidden);
        var Secret = Directory.CreateDirectory(Path.Combine(Fixture, "secret")).FullName;
        File.WriteAllText(Path.Combine(Secret, "needle-hidden-child.txt"), "hidden child");
        File.SetAttributes(Secret, FileAttributes.Directory | FileAttributes.Hidden);
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Search UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Query(string Text)
        {
            this.SearchBox.Text = Text;
            await this.WaitForSearchSmokeAsync();
        }
        FileEntry[] Results() => this.View!.Cast<FileEntry>().ToArray();
        try
        {
            this.Settings.ShowHidden = false;
            Check(await this.NavigateAsync(Fixture), "Search opens its owned recursive fixture");
            Check(this.SearchPathColumn.Visibility == Visibility.Collapsed, "Absolute path column is hidden during ordinary folder browsing");
            var Baseline = this.Entries.Select(Entry => Entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var History = this.History.ToArray();
            FocusManager.SetFocusedElement(this, this.FileGrid);
            Check(this.SearchTips.Visibility == Visibility.Collapsed, "Filter tips stay hidden without search focus");
            FocusManager.SetFocusedElement(this, this.SearchBox);
            Check(this.SearchBox.IsFocused && this.SearchTips.Visibility == Visibility.Visible
                && this.SearchFileTip.Visibility == Visibility.Visible && this.SearchDirectoryTip.Visibility == Visibility.Visible
                && this.SearchRegexTip.IsVisible && this.SearchWildcardTip.IsVisible,
                "Focusing an empty search displays all file and folder tips without activating the test window");
            this.UpdateLayout();
            Check(this.SearchTips.ActualWidth == 300 && this.SearchTips.ActualHeight > 0
                && this.SearchTips.TranslatePoint(new Point(0, this.SearchTips.ActualHeight), this).Y < this.ActualHeight
                && this.SearchTips.TranslatePoint(new Point(), this).X + this.SearchTips.ActualWidth <= this.ActualWidth,
                "Search tips fit beneath the search box inside the window");
            if (AppMain.ScreenshotPath is { } TipDestination)
            {
                var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(this);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(TipDestination)!, "SearchTips.png"));
                Encoder.Save(Stream);
            }
            async Task TipQuery(string Text)
            {
                this.SearchBox.Text = Text;
                this.SearchBox.CaretIndex = Text.Length;
                await this.WaitForSearchSmokeAsync();
            }
            void ClickSurface(UIElement Surface)
            {
                var Click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = Surface };
                // Check the window's dismissal rule without inventing a file-grid
                // drag at the user's current desktop pointer coordinates.
                this.WindowInputPreviewMouseDown(this.HostWindow, Click);
                Check(!Click.Handled, "Dismissing search tips preserves the outside click's normal handling");
            }
            foreach (var (Surface, Name) in new (UIElement, string)[]
            {
                (this.FileGrid, "file list"), (this.SidebarPanel, "sidebar"),
                (this.RightPanel, "main panel"), (this.StatusText, "status bar"), (this.HostWindow.TitleBrand, "title bar")
            })
            {
                FocusManager.SetFocusedElement(this, this.SearchBox);
                Check(this.SearchTips.IsVisible, "Search tips reopen before clicking the " + Name);
                ClickSurface(Surface);
                Check(!this.SearchBox.IsFocused && !this.SearchBox.IsKeyboardFocusWithin
                    && this.SearchTips.Visibility == Visibility.Collapsed && this.SearchBox.Text == "",
                    "Clicking the " + Name + " removes search focus and closes tips without clearing the query");
            }
            FocusManager.SetFocusedElement(this, this.SearchBox);
            ClickSurface(this.SearchContainer);
            Check(this.SearchBox.IsFocused && this.SearchTips.IsVisible,
                "Clicking inside the search container keeps focus and tips available");
            ClickSurface(this.SearchFileTip);
            Check(this.SearchBox.IsFocused && this.SearchTips.IsVisible,
                "Pressing a search tip preserves search focus until its completion action runs");
            await TipQuery("/fi");
            ClickSurface(this.RightPanel);
            Check(this.SearchBox.Text == "/fi" && !this.SearchBox.IsFocused && !this.SearchTips.IsVisible,
                "Clicking outside preserves a partially typed query while dismissing tips");
            this.SearchBox.CaretIndex = 1;
            Check(!this.SearchTips.IsVisible, "Caret changes cannot reopen tips after outside-click dismissal");
            FocusManager.SetFocusedElement(this, this.SearchBox);
            await TipQuery("/");
            Check(this.SearchTips.Visibility == Visibility.Visible && this.SearchFileTip.IsVisible && this.SearchDirectoryTip.IsVisible,
                "A slash shows every matching command");
            await TipQuery("/F");
            Check(this.SearchFileTip.IsVisible && !this.SearchDirectoryTip.IsVisible && Results().Length == 5 && Results().All(Entry => !Entry.IsDirectory),
                "File shorthand filters recursive results and displays only the matching file tip");
            await TipQuery("/d");
            Check(!this.SearchFileTip.IsVisible && this.SearchDirectoryTip.IsVisible && Results().Length == 4 && Results().All(Entry => Entry.IsDirectory),
                "Directory shorthand filters recursive results and displays only the matching folder tip");
            await TipQuery("needle /fi");
            Check(this.SearchFileTip.IsVisible && !this.SearchDirectoryTip.IsVisible, "Tips match an incomplete switch after a filename");
            this.SearchFileTip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await this.WaitForSearchSmokeAsync();
            Check(this.SearchBox.Text == "needle /file " && Results().Length == 4 && this.SearchTips.Visibility == Visibility.Collapsed,
                "Clicking a tip completes the switch while preserving the filename and applies the filter");
            await TipQuery("/di needle");
            this.SearchBox.CaretIndex = 2;
            Check(this.SearchDirectoryTip.IsVisible && !this.SearchFileTip.IsVisible, "Moving the caret updates tips for the switch being edited");
            this.SearchDirectoryTip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await this.WaitForSearchSmokeAsync();
            Check(this.SearchBox.Text == "/dir needle" && Results().Length == 2,
                "Completing a leading switch preserves the following filename");
            await TipQuery("/re");
            Check(this.SearchRegexTip.IsVisible && !this.SearchFileTip.IsVisible && !this.SearchDirectoryTip.IsVisible
                && !this.SearchWildcardTip.IsVisible, "Typing the regex switch displays only the regex completion tip");
            this.SearchRegexTip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await this.WaitForSearchSmokeAsync();
            Check(this.SearchBox.Text == "/regex " && !this.SearchTips.IsVisible && this.CurrentSearch.IsRegex,
                "Clicking the regex tip enables regex mode and leaves room for the pattern");
            await TipQuery("/w");
            Check(this.SearchWildcardTip.IsVisible && !this.SearchRegexTip.IsVisible,
                "Wildcard shorthand displays the matching completion tip");
            this.SearchWildcardTip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await this.WaitForSearchSmokeAsync();
            Check(this.SearchBox.Text == "/wildcard " && this.CurrentSearch.UsesWildcards && Results().Length == 9,
                "Clicking the wildcard tip enables the mode without hiding results before a pattern is entered");
            await TipQuery(@"/regex ^needle-(root|leaf)\.txt$ /file");
            Check(Results().Length == 3 && Results().All(Entry => !Entry.IsDirectory)
                && Results().Count(Entry => Entry.Name == "needle-leaf.txt") == 2,
                "Regex patterns search recursively with alternatives anchors and file-only filtering");
            await TipQuery(@"/r ^NEEDLE-(folder|dir)$ /d");
            Check(Results().Length == 2 && Results().All(Entry => Entry.IsDirectory),
                "Regex shorthand combines with directory filtering and ignores case");
            await TipQuery("*needle*.txt /f");
            Check(Results().Length == 4 && Results().Any(Entry => Entry.Name.Contains("资料", StringComparison.Ordinal)),
                "Implicit wildcard matching includes deep Unicode filenames and duplicate names");
            await TipQuery("needle-????.txt /wildcard /f");
            Check(Results().Length == 3, "Question-mark wildcards match exactly one character at each position");
            await TipQuery("needle-* /w /dir");
            Check(Results().Length == 2 && Results().All(Entry => Entry.IsDirectory),
                "Explicit wildcard mode combines with directory-only filtering");
            await TipQuery("/regex [");
            Check(Results().Length == 0 && !this.Searching && this.SearchTask.IsCompleted
                && this.StatusText.Text == LanguageService.Text("SearchRegexInvalid")
                && this.EmptyTitle.Text == LanguageService.Text("SearchPatternError")
                && this.EmptyDescription.Text == LanguageService.Text("SearchRegexInvalid"),
                "Invalid regex syntax shows a useful error without starting a recursive search");
            var SearchLanguage = LanguageService.CurrentLanguage;
            try
            {
                foreach (var Language in LanguageService.SupportedLanguages)
                {
                    this.ChangeLanguage(Language.Code, Persist: false);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    Check(this.StatusText.Text == LanguageService.Text("SearchRegexInvalid")
                        && this.EmptyDescription.Text == LanguageService.Text("SearchRegexInvalid")
                        && ((StackPanel)this.SearchRegexTip.Content).Children.OfType<TextBlock>().Last().Text == LanguageService.Text("SearchRegexTip")
                        && ((StackPanel)this.SearchWildcardTip.Content).Children.OfType<TextBlock>().Last().Text == LanguageService.Text("SearchWildcardTip"),
                        Language.Code + " pattern tips and invalid-regex feedback update with the application language");
                }
            }
            finally { this.ChangeLanguage(SearchLanguage, Persist: false); }
            this.SearchBox.Text = "/r (";
            this.SearchBox.Text = @"/r ^needle-leaf\.txt$ /f";
            await this.WaitForSearchSmokeAsync();
            Check(Results().Length == 2 && this.CurrentSearch.ErrorKey is null && !this.EmptyState.IsVisible
                && this.StatusText.Text.StartsWith(LanguageService.Text("SearchResultsCount", 2), StringComparison.Ordinal),
                "Correcting an invalid regex replaces the error with current results and ignores obsolete queries");
            await TipQuery("/unknown");
            Check(this.SearchTips.Visibility == Visibility.Collapsed, "Unknown switches have no matching tips");
            await TipQuery("needle/file");
            Check(this.SearchTips.Visibility == Visibility.Collapsed, "Slashes inside ordinary names do not trigger command tips");
            await TipQuery("needle");
            Check(this.SearchTips.Visibility == Visibility.Collapsed, "Ordinary filename input hides filter tips");
            await TipQuery("");
            Check(this.SearchTips.Visibility == Visibility.Visible, "Clearing a focused search restores all tips");
            this.SearchBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(this.SearchTips.Visibility == Visibility.Collapsed && this.SearchBox.Text.Length == 0
                && !this.SearchBox.IsFocused && !this.SearchBox.IsKeyboardFocusWithin,
                "Escape dismisses tips and removes search focus without changing the query");
            foreach (var DraftQuery in new[] { "needle", "/fi" })
            {
                FocusManager.SetFocusedElement(this, this.SearchBox);
                await TipQuery(DraftQuery);
                var Input = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(this.SearchBox), 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                this.SearchBox.RaiseEvent(Input);
                Check(Input.Handled && !this.SearchBox.IsFocused && !this.SearchBox.IsKeyboardFocusWithin
                    && this.SearchTips.Visibility == Visibility.Collapsed && this.SearchBox.Text == DraftQuery
                    && SamePath(this.CurrentPath, Fixture),
                    "Escape removes search focus with visible or hidden tips while retaining the entered query: " + DraftQuery);
            }
            await TipQuery("");
            FocusManager.SetFocusedElement(this, this.FileGrid);
            FocusManager.SetFocusedElement(this, this.SearchBox);
            Check(this.SearchTips.Visibility == Visibility.Visible, "Refocusing the empty search restores dismissed tips");
            FocusManager.SetFocusedElement(this, this.FileGrid);
            Check(this.SearchTips.Visibility == Visibility.Collapsed, "Moving focus away dismisses filter tips");
            // Reproduce a directory notification queued immediately before starting a search.
            this.ScheduleRefresh();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Query("needle");
            Check(Results().Length == 6 && Results().Count(Entry => Entry.IsDirectory) == 2
                && Results().Count(Entry => Entry.Name == "needle-leaf.txt") == 2,
                "Plain search includes deep files folders Unicode and duplicate basenames");
            Check(Results().TakeWhile(Entry => Entry.IsDirectory).Count() == 2
                && this.View!.SortDescriptions.Count >= 2,
                "Incoming recursive matches retain folder-first column sorting");
            Check(SamePath(this.CurrentPath, Fixture) && this.History.SequenceEqual(History)
                && Results().All(Entry => Entry.FullPath.StartsWith(Fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                && this.FolderTree.SelectedItem is FolderNode Node && SamePath(Node.FullPath, Fixture),
                "Recursive results stay inside the current root without changing navigation or tree selection");
            Check(this.StatusText.Text.StartsWith(LanguageService.Text("SearchResultsCount", 6), StringComparison.Ordinal)
                && this.Watcher is { IncludeSubdirectories: false } && this.LoadingBar.Visibility == Visibility.Collapsed,
                "Search reports the result count and finishes without monitoring every descendant");
            Check(this.SearchPathColumn.Visibility == Visibility.Visible
                && this.SearchPathColumn.Header?.ToString() == LanguageService.Text("AbsolutePathColumn"),
                "Search displays a localized absolute-path column");
            foreach (var Entry in Results().Where(Entry => Entry.Name is "needle-leaf.txt" or "needle-dir"))
            {
                this.FileGrid.ScrollIntoView(Entry, this.SearchPathColumn);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                this.UpdateLayout();
                Check(this.SearchPathColumn.GetCellContent(Entry) is TextBlock PathCell
                    && PathCell.Text == Entry.FullPath && Path.IsPathFullyQualified(PathCell.Text)
                    && PathCell.ToolTip?.ToString() == Entry.FullPath,
                    "Absolute-path cells and tooltips distinguish duplicate filenames and identify directory results");
            }
            this.SearchPathColumn.SortDirection = null;
            this.FileGridSorting(this.FileGrid, new DataGridSortingEventArgs(this.SearchPathColumn));
            Check(this.SortProperty == nameof(FileEntry.FullPath) && Results().TakeWhile(Entry => Entry.IsDirectory).Count() == 2,
                "Absolute-path column supports sorting while retaining folders first");
            this.FileGrid.Columns[0].SortDirection = null;
            this.FileGridSorting(this.FileGrid, new DataGridSortingEventArgs(this.FileGrid.Columns[0]));
            this.SelectPath(DeepFile);
            var StableVersion = this.SearchVersion;
            var StableTask = this.SearchTask;
            var StableEntries = this.Entries;
            var Noise = Path.Combine(Fixture, "background-noise.tmp");
            File.WriteAllText(Noise, "background write");
            await Task.Delay(1100);
            File.AppendAllText(Noise, "more background writes");
            await Task.Delay(900);
            File.Delete(Noise);
            await Task.Delay(1400);
            Check(this.SearchVersion == StableVersion && ReferenceEquals(this.SearchTask, StableTask)
                && ReferenceEquals(this.Entries, StableEntries) && !this.Searching && !this.Loading && !this.RefreshTimer.IsEnabled
                && this.SelectedEntries() is { Length: 1 } StableSelection && SamePath(StableSelection[0].FullPath, DeepFile),
                "Queued notifications and repeated background writes do not restart a completed search or clear its selection");
            if (AppMain.ScreenshotPath is { } Destination)
            {
                var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(this);
                var Encoder = new PngBitmapEncoder();
                Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Destination)!, "RecursiveSearch.png"));
                Encoder.Save(Stream);
            }
            await Query("needle /file");
            Check(Results().Length == 4 && Results().All(Entry => !Entry.IsDirectory), "Trailing /file filters recursive matches to files");
            await Query("/DIR needle");
            Check(Results().Length == 2 && Results().All(Entry => Entry.IsDirectory), "Leading case-insensitive /dir filters matches to folders");
            await Query("/file");
            Check(Results().Length == 5 && Results().All(Entry => !Entry.IsDirectory), "A standalone /file lists all recursive files");
            await Query("/dir");
            Check(Results().Length == 4 && Results().All(Entry => Entry.IsDirectory), "A standalone /dir lists all recursive folders");
            await Query("needle /file /dir");
            Check(Results().Length == 6, "Combining both type switches includes both files and folders");
            await Query("/files");
            Check(Results().Length == 0 && this.EmptyTitle.Text == LanguageService.Text("NoResults"), "Unknown switches remain literal and completed empty searches show no results");
            this.SearchBox.Text = "needle /file";
            this.SearchBox.Text = "does-not-exist";
            this.SearchBox.Text = "needle /dir";
            await this.WaitForSearchSmokeAsync();
            Check(Results().Length == 2 && Results().All(Entry => Entry.IsDirectory), "Rapid query changes cannot publish stale results");
            this.SearchBox.Text = "/file";
            var Cancelled = this.SearchTask;
            this.ClearSearchClick(this.SearchBox, new RoutedEventArgs());
            await Cancelled;
            Check(Results().Select(Entry => Entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(Baseline)
                && this.Watcher is { IncludeSubdirectories: false } && !this.Searching && this.SearchPathColumn.Visibility == Visibility.Collapsed,
                "Clear restores the current-folder list hides the path column and cancels further recursive results");
            await this.WaitForSearchSmokeAsync();
            var BrowsingChange = Path.Combine(Fixture, "ordinary-browse-update.txt");
            File.WriteAllText(BrowsingChange, "ordinary browsing still refreshes");
            var BrowsingTimer = Stopwatch.StartNew();
            while (!Results().Any(Entry => SamePath(Entry.FullPath, BrowsingChange)) && BrowsingTimer.Elapsed < TimeSpan.FromSeconds(8))
            {
                await Task.Delay(100);
                await this.WaitForSearchSmokeAsync();
            }
            Check(Results().Any(Entry => SamePath(Entry.FullPath, BrowsingChange)),
                "Ordinary folder browsing still automatically refreshes after filesystem changes");
            File.Delete(BrowsingChange);
            await Query("needle");
            await this.RunPreferencesSmokeChecksAsync(Passed, ShowHidden: true);
            await this.WaitForSearchSmokeAsync();
            Check(Results().Length == 8 && this.SearchBox.Text == "needle", "Hidden-items preference refreshes hidden files and hidden descendants inside the active search");
            await this.RunPreferencesSmokeChecksAsync(Passed, ShowHidden: false);
            await this.WaitForSearchSmokeAsync();
            Check(Results().Length == 6, "Hiding items again removes hidden descendants from search results");
            await this.RunPreferencesSmokeChecksAsync(Passed, Use12HourClock: true);
            Check(Results().All(Entry => Entry.Use12HourClock && (Entry.DisplayModified.EndsWith("AM", StringComparison.Ordinal) || Entry.DisplayModified.EndsWith("PM", StringComparison.Ordinal))),
                "Recursive results follow the chosen modification-date clock format");
            await Query("needle /file");
            this.SelectPath(DeepFile);
            await this.AcceptSmokeNameDialogAsync(this.RenameAsync, LanguageService.Text("Rename"), "needle-renamed.txt");
            await this.WaitForSearchSmokeAsync();
            var Renamed = Path.Combine(Deep, "needle-renamed.txt");
            Check(File.Exists(Renamed) && !File.Exists(DeepFile)
                && this.SelectedEntries() is { Length: 1 } Selected && SamePath(Selected[0].FullPath, Renamed),
                "Renaming a deep search result acts on its real path and reselects it after search refresh");
            var Added = Path.Combine(Deep, "needle-added.txt");
            var AddedRoot = Path.Combine(Fixture, "needle-added-root.txt");
            var BeforeChange = this.SearchVersion;
            File.WriteAllText(Added, "watcher");
            File.WriteAllText(AddedRoot, "root watcher");
            await Task.Delay(2200);
            Check(this.SearchVersion == BeforeChange && !this.Searching
                && !Results().Any(Entry => SamePath(Entry.FullPath, Added) || SamePath(Entry.FullPath, AddedRoot)),
                "Root and nested changes keep completed search results stable until explicit refresh");
            await this.RefreshAsync(true);
            await this.WaitForSearchSmokeAsync();
            Check(Results().Any(Entry => SamePath(Entry.FullPath, Added)) && Results().Any(Entry => SamePath(Entry.FullPath, AddedRoot))
                && this.SearchVersion > BeforeChange && this.SearchBox.Text == "needle /file",
                "Explicit refresh reruns the same query and includes new root and deep results");
            await Query("needle /dir");
            this.SelectPath(MatchedFolder);
            await this.OpenSelectedAsync();
            Check(SamePath(this.CurrentPath, MatchedFolder) && this.SearchBox.Text == "", "Opening a recursive folder result navigates to that folder and clears the old search");
            await this.NavigateAsync(Fixture);
            this.SearchBox.Text = "/file";
            var OldSearch = this.SearchTask;
            await this.NavigateAsync(Path.Combine(Fixture, "needle-folder"));
            await OldSearch;
            await this.WaitForSearchSmokeAsync();
            Check(Results().Length == 1 && Results()[0].Name == "ordinary.txt" && this.SearchBox.Text == "",
                "Switching folders during search cancels the old root and never mixes its results into the new folder");
        }
        finally
        {
            var Pending = this.SearchTask;
            this.SearchBox.Clear();
            await Pending;
            this.Settings.ShowHidden = InitialHidden;
            this.ChangeTimeFormat(InitialClock);
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.SearchBox.Text = InitialSearch;
            await this.WaitForSearchSmokeAsync();
        }
    }
}
