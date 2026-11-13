using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CDir.Localization;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private string FileGroupProperty = "";
    private bool FileGroupDescending;
    private bool RefreshingFileLayout;
    private Action<string>? FileLayoutStage;

    private MenuItem CreateGroupingMenu()
    {
        var Style = (Style)this.FindResource("ColumnToggleMenuItem");
        var Menu = new MenuItem { Tag = "GroupBy", IsEnabled = !this.IsComputer, Style = Style };
        Menu.SetResourceReference(HeaderedItemsControl.HeaderProperty, "TextGroupByDropdown");
        void Add(string Key, string Caption)
        {
            var Item = new MenuItem { Tag = "CDir.Group." + Key, IsCheckable = true, StaysOpenOnClick = true, Style = Style };
            Item.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Text" + Caption);
            Item.Click += async (_, Args) => { Args.Handled = true; await this.ExecuteBackgroundCommandAsync((string)Item.Tag); };
            Menu.Items.Add(Item);
        }
        foreach (var Key in BackgroundSortKeys) Add(Key, ColumnResourceKey(Key));
        Menu.Items.Add(new Separator());
        Add("Ascending", "Ascending"); Add("Descending", "Descending");
        Menu.Items.Add(new Separator());
        Add("None", "GroupNone");
        return Menu;
    }

    private void UpdateGroupingMenu()
    {
        if (this.ColumnMenuButton.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(Item => Equals(Item.Tag, "GroupBy")) is not { } Menu) return;
        Menu.IsEnabled = !this.IsComputer;
        foreach (var Item in Menu.Items.OfType<MenuItem>())
        {
            var Key = ((string)Item.Tag)["CDir.Group.".Length..];
            Item.IsChecked = Key switch
            {
                "None" => this.FileGroupProperty.Length == 0,
                "Ascending" => !this.FileGroupDescending,
                "Descending" => this.FileGroupDescending,
                _ => Key == this.FileGroupProperty
            };
            Item.IsEnabled = Key is not ("Ascending" or "Descending") || this.FileGroupProperty.Length > 0;
        }
    }
    private bool CanPasteBackground()
    {
        try { return !this.IsVirtualLocation && this.ReadClipboardFiles()?.GetDataPresent(System.Windows.DataFormats.FileDrop, true) == true; }
        catch (Exception Error) when (Error is System.Runtime.InteropServices.ExternalException or System.Threading.ThreadStateException) { return false; }
    }
    private static readonly string[] BackgroundSortKeys = [nameof(FileEntry.Name), nameof(FileEntry.Modified), nameof(FileEntry.TypeName), nameof(FileEntry.Size)];
    private string GroupLabel(FileEntry Entry, DateTime Today) => this.FileGroupProperty switch
    {
        nameof(FileEntry.Name) => Entry.Name.Length == 0 ? "#" : char.IsLetterOrDigit(Entry.Name[0]) ? Entry.Name[..1].ToUpper(LanguageService.Culture) : "#",
        nameof(FileEntry.TypeName) => Entry.TypeName,
        nameof(FileEntry.Modified) => Entry.Modified.Date == Today ? LanguageService.Text("GroupToday")
            : Entry.Modified.Date == Today.AddDays(-1) ? LanguageService.Text("GroupYesterday") : Entry.Modified.ToString("yyyy/MM"),
        nameof(FileEntry.Size) => LanguageService.Text(Entry.IsDirectory ? "FolderType" : Entry.Size is null or 0 ? "GroupEmpty"
            : Entry.Size < 16 * 1024 ? "GroupTiny" : Entry.Size < 1024 * 1024 ? "GroupSmall" : Entry.Size < 16 * 1024 * 1024 ? "GroupMedium" : "GroupLarge"),
        _ => ""
    };
    private sealed class FileViewComparer(ExplorerTab Tab) : IComparer
    {
        private readonly System.Globalization.CompareInfo Comparison = LanguageService.Culture.CompareInfo;
        public int Compare(object? Left, object? Right)
        {
            if (Left is not FileEntry A || Right is not FileEntry B) return 0;
            var Grouped = Tab.FileGroupProperty.Length > 0;
            if (Grouped)
            {
                var Group = A.ViewGroupOrder.CompareTo(B.ViewGroupOrder);
                if (Group == 0) Group = this.Comparison.Compare(A.ViewGroup, B.ViewGroup, System.Globalization.CompareOptions.StringSort);
                if (Group != 0) return Tab.FileGroupDescending ? -Group : Group;
            }
            // Date sorting must compare folders and files on the same timeline.
            if (Tab.SortProperty != nameof(FileEntry.Modified) && A.IsDirectory != B.IsDirectory) return A.IsDirectory ? -1 : 1;
            var Order = Tab.SortProperty switch
            {
                nameof(FileEntry.Modified) => A.Modified.CompareTo(B.Modified), nameof(FileEntry.Size) => Nullable.Compare(A.Size, B.Size),
                nameof(FileEntry.TypeName) => this.Comparison.Compare(A.TypeName, B.TypeName),
                nameof(FileEntry.FullPath) => this.Comparison.Compare(A.FullPath, B.FullPath),
                nameof(FileEntry.OriginalLocation) => this.Comparison.Compare(A.OriginalLocation, B.OriginalLocation),
                nameof(FileEntry.DateDeleted) => Nullable.Compare(A.DateDeleted, B.DateDeleted),
                _ => FileNameComparer.Instance.Compare(A.Name, B.Name)
            };
            // Keep the existing ascending name tie-break for ordinary column
            // sorts and the selected direction within groups.
            if (Order == 0)
            {
                Order = FileNameComparer.Instance.Compare(A.Name, B.Name);
                if (!Grouped) return Order;
            }
            return Tab.SortDirection == ListSortDirection.Descending ? -Order : Order;
        }
    }
    private void ApplyFileGroups()
    {
        if (this.View is not ListCollectionView List) return;
        List.GroupDescriptions.Clear();
        // The comparer places equal group labels next to one another. Let WPF
        // build groups in that order instead of searching every prior group.
        List.IsDataInGroupOrder = !this.IsComputer && this.FileGroupProperty.Length > 0;
        var Today = DateTime.Today;
        foreach (var Entry in this.Entries) this.AssignFileGroup(Entry, Today);
        if (this.IsComputer) return;
        List.CustomSort = new FileViewComparer(this);
        if (this.FileGroupProperty.Length == 0) return;
        if (this.IsDetailsView) List.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileEntry.ViewGroup)));
    }
    private void AssignFileGroup(FileEntry Entry, DateTime Today)
    {
        Entry.ViewGroup = this.IsComputer ? "" : this.GroupLabel(Entry, Today);
        // Group order follows the underlying dates and sizes, independently
        // of translated labels such as Small, Medium, Today and Yesterday.
        Entry.ViewGroupOrder = this.FileGroupProperty switch
        {
            nameof(FileEntry.Modified) => Entry.Modified.Date == Today || Entry.Modified.Date == Today.AddDays(-1)
                ? Entry.Modified.Date.Ticks : new DateTime(Entry.Modified.Year, Entry.Modified.Month, 1).Ticks,
            nameof(FileEntry.Size) => Entry.IsDirectory ? -1 : Entry.Size is null or 0 ? 0 : Entry.Size < 16 * 1024 ? 1
                : Entry.Size < 1024 * 1024 ? 2 : Entry.Size < 16 * 1024 * 1024 ? 3 : 4,
            _ => 0
        };
    }
    private void RefreshBackgroundLayout()
    {
        this.CancelInlineRename();
        this.EndMarqueeSelection();
        var Selection = this.SelectedEntries();
        var Anchor = this.FileGrid.CurrentItem as FileEntry ?? Selection.FirstOrDefault();
        this.RefreshingFileLayout = true;
        try
        {
            this.ApplySort();
            this.FileLayoutStage?.Invoke("Sort");
            this.UpdateComputerSorting();
            // Grouping changes geometry, but the current view mode is unchanged.
            // Invalidating AppliedFileView would sort the same collection twice.
            this.ApplyFileView();
            this.FileLayoutStage?.Invoke("View");
            var Retained = this.SelectedEntries().ToHashSet();
            foreach (var Entry in Selection)
                if (!Retained.Contains(Entry)) this.FileGrid.SelectedItems.Add(Entry);
        }
        finally { this.RefreshingFileLayout = false; }
        this.SyncTileSelection();
        this.FileLayoutStage?.Invoke("Selection");
        this.UpdateActions(); this.UpdateCounts();
        this.FileLayoutStage?.Invoke("Counts");
        if (Anchor is not null) this.FileGrid.CurrentItem = Anchor;
        this.FileLayoutStage?.Invoke("Current");
        // Do not force layout/ScrollIntoView from the menu callback: that first
        // realizes the old viewport and then builds it again for the selection.
        // Details starts at the first group; tiles position the anchor during
        // their next normal measure, before generating visible containers.
        if (!this.IsDetailsView && Anchor is not null && FindVisualChild<CDir.Controls.FileTilePanel>(this.FileTiles) is { } Panel)
            Panel.RevealAfterResize(this.FileTiles.Items.IndexOf(Anchor));
        else FindVisualChild<ScrollViewer>(this.FileGrid)?.ScrollToTop();
        this.FileLayoutStage?.Invoke("Reveal");
        this.HostWindow.PersistWorkspaceNow();
        this.FileLayoutStage?.Invoke("Persist");
    }
    private async Task<bool> ExecuteBackgroundCommandAsync(string Verb)
    {
        if (Verb == "refresh" || Verb == "CDir.Refresh") { await this.RefreshAndFitColumnsAsync(); return true; }
        if (Verb == "CDir.Paste") { await this.PasteAsync(); return true; }
        if (!Verb.StartsWith("CDir.", StringComparison.Ordinal)) return false;
        var Parts = Verb.Split('.');
        if (Parts.Length == 3 && Parts[1] == "View" && Enum.TryParse<FileViewMode>(Parts[2], out var Mode)) this.ChangeFileView(Mode);
        else if (Parts.Length == 3 && Parts[1] == "Sort" && !this.IsComputer)
        {
            if (Parts[2] is "Ascending" or "Descending") this.SortDirection = Parts[2] == "Ascending" ? ListSortDirection.Ascending : ListSortDirection.Descending;
            else if (BackgroundSortKeys.Contains(Parts[2])) this.SortProperty = Parts[2];
            this.RefreshBackgroundLayout();
        }
        else if (Parts.Length == 3 && Parts[1] == "Group" && !this.IsComputer)
        {
            var Property = Parts[2] == "None" ? "" : BackgroundSortKeys.Contains(Parts[2]) ? Parts[2] : this.FileGroupProperty;
            var Descending = Parts[2] is "Ascending" or "Descending" ? Parts[2] == "Descending" : this.FileGroupDescending;
            if (Property == this.FileGroupProperty && Descending == this.FileGroupDescending) { this.UpdateGroupingMenu(); return true; }
            this.FileGroupProperty = Property;
            this.FileGroupDescending = Descending;
            this.RefreshBackgroundLayout();
        }
        else if (Verb == "CDir.Customize")
        {
            if (!this.IsVirtualLocation)
            {
                SHObjectProperties(new System.Windows.Interop.WindowInteropHelper(this.HostWindow).Handle, 2, this.CurrentPath, "Customize");
                ShellIconService.InvalidateCache();
                await this.RefreshAsync(true);
            }
        }
        else if (Verb == "CDir.Terminal")
        {
            var Terminal = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
            var Opened = false;
            if (System.IO.File.Exists(Terminal))
            {
                try
                {
                    var Start = new System.Diagnostics.ProcessStartInfo(Terminal) { UseShellExecute = true, WorkingDirectory = this.CurrentPath };
                    Start.ArgumentList.Add("-d"); Start.ArgumentList.Add(this.CurrentPath);
                    System.Diagnostics.Process.Start(Start); Opened = true;
                }
                catch (System.ComponentModel.Win32Exception) { }
            }
            if (!Opened) this.Files.OpenTerminal(this.CurrentPath);
        }
        return true;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SHObjectProperties(IntPtr Owner, uint Type, string Path, string? Page);
}
