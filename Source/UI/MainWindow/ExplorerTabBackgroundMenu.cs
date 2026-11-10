using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Data;
using CDir.Localization;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private string FileGroupProperty = "";
    private bool FileGroupDescending;
    private bool CanPasteBackground()
    {
        try { return !this.IsVirtualLocation && this.ReadClipboardFiles()?.GetDataPresent(System.Windows.DataFormats.FileDrop, true) == true; }
        catch (Exception Error) when (Error is System.Runtime.InteropServices.ExternalException or System.Threading.ThreadStateException) { return false; }
    }
    private static readonly string[] BackgroundSortKeys = [nameof(FileEntry.Name), nameof(FileEntry.Modified), nameof(FileEntry.TypeName), nameof(FileEntry.Size)];
    private string GroupLabel(FileEntry Entry) => this.FileGroupProperty switch
    {
        nameof(FileEntry.Name) => Entry.Name.Length == 0 ? "#" : char.IsLetterOrDigit(Entry.Name[0]) ? Entry.Name[..1].ToUpper(LanguageService.Culture) : "#",
        nameof(FileEntry.TypeName) => Entry.TypeName,
        nameof(FileEntry.Modified) => Entry.Modified.Date == DateTime.Today ? LanguageService.Text("GroupToday")
            : Entry.Modified.Date == DateTime.Today.AddDays(-1) ? LanguageService.Text("GroupYesterday") : Entry.Modified.ToString("yyyy/MM"),
        nameof(FileEntry.Size) => LanguageService.Text(Entry.IsDirectory ? "FolderType" : Entry.Size is null or 0 ? "GroupEmpty"
            : Entry.Size < 16 * 1024 ? "GroupTiny" : Entry.Size < 1024 * 1024 ? "GroupSmall" : Entry.Size < 16 * 1024 * 1024 ? "GroupMedium" : "GroupLarge"),
        _ => ""
    };
    private sealed class FileGroupComparer(ExplorerTab Tab) : IComparer
    {
        public int Compare(object? Left, object? Right)
        {
            if (Left is not FileEntry A || Right is not FileEntry B) return 0;
            var Group = LanguageService.Culture.CompareInfo.Compare(A.ViewGroup, B.ViewGroup, System.Globalization.CompareOptions.StringSort);
            if (Group != 0) return Tab.FileGroupDescending ? -Group : Group;
            if (A.IsDirectory != B.IsDirectory) return A.IsDirectory ? -1 : 1;
            var Order = Tab.SortProperty switch
            {
                nameof(FileEntry.Modified) => A.Modified.CompareTo(B.Modified), nameof(FileEntry.Size) => Nullable.Compare(A.Size, B.Size),
                nameof(FileEntry.TypeName) => LanguageService.Culture.CompareInfo.Compare(A.TypeName, B.TypeName),
                _ => LanguageService.Culture.CompareInfo.Compare(A.Name, B.Name)
            };
            if (Order == 0) Order = LanguageService.Culture.CompareInfo.Compare(A.Name, B.Name);
            return Tab.SortDirection == ListSortDirection.Descending ? -Order : Order;
        }
    }
    private void ApplyFileGroups()
    {
        if (this.View is not ListCollectionView List) return;
        List.GroupDescriptions.Clear();
        foreach (var Entry in this.Entries) Entry.ViewGroup = this.IsComputer ? "" : this.GroupLabel(Entry);
        if (this.IsComputer || this.FileGroupProperty.Length == 0) return;
        List.CustomSort = new FileGroupComparer(this);
        if (this.IsDetailsView) List.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileEntry.ViewGroup)));
    }
    private void RefreshBackgroundLayout()
    {
        var Selection = this.SelectedEntries().ToArray();
        this.ApplySort();
        this.UpdateComputerSorting();
        this.AppliedFileView = null;
        this.ApplyFileView();
        foreach (var Entry in Selection) this.SelectPath(Entry.FullPath, Append: true);
        this.HostWindow.PersistWorkspaceNow();
    }
    private async Task<bool> ExecuteBackgroundCommandAsync(string Verb)
    {
        if (Verb == "refresh" || Verb == "CDir.Refresh") { await this.RefreshAsync(true); return true; }
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
            if (Parts[2] is "Ascending" or "Descending") this.FileGroupDescending = Parts[2] == "Descending";
            else this.FileGroupProperty = BackgroundSortKeys.Contains(Parts[2]) ? Parts[2] : "";
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
