using System.IO;
using System.Windows;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunShellDeletionRefreshSmokeChecksAsync(List<string> Passed)
    {
        var Initial = this.CurrentPath;
        var OriginalDelete = this.DeleteShellItems;
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "DeleteRefresh-" + Guid.NewGuid().ToString("N"))).FullName;
        var FilePath = Path.Combine(Root, "Deleted.txt");
        var Folder = Directory.CreateDirectory(Path.Combine(Root, "Deleted Folder", "Nested")).Parent!.FullName;
        var Keep = Path.Combine(Root, "Keep.txt");
        File.WriteAllText(FilePath, "Owned file");
        File.WriteAllText(Keep, "Keep this file");
        ExplorerTab? Other = null;
        void Check(bool Value, string Name)
        {
            if (!Value) throw new InvalidOperationException("Shell delete refresh check failed: " + Name);
            Passed.Add(Name);
        }
        try
        {
            await this.NavigateAsync(Root, RecordHistory: false);
            Other = this.HostWindow.AddTab();
            await Other.WaitUntilReadyAsync();
            await Other.NavigateAsync(Root, RecordHistory: false);
            this.HostWindow.SelectTab(this);
            this.Watcher!.EnableRaisingEvents = false;
            Other.Watcher!.EnableRaisingEvents = false;
            this.BeginFileRename(FilePath);
            Check(this.InlineRenameTarget is not null, "The deletion fixture includes an active name editor that previously blocked refresh");
            this.DeleteShellItems = (Paths, _) =>
            {
                foreach (var PathValue in Paths)
                    if (Directory.Exists(PathValue)) Directory.Delete(PathValue, true); else File.Delete(PathValue);
            };
            Check(await this.DeleteShellSelectionAsync([FilePath, Folder], Permanently: true)
                && this.InlineRenameTarget is null && !this.Entries.Any(Entry => SamePath(Entry.FullPath, FilePath) || SamePath(Entry.FullPath, Folder))
                && Other.Entries.Count == 1 && File.Exists(Keep),
                "Deletion clears a stale name editor and refreshes every open panel before returning, without relying on filesystem notifications");
            var First = Path.Combine(Root, "Partial First.txt");
            var Second = Path.Combine(Root, "Partial Second.txt");
            File.WriteAllText(First, "First"); File.WriteAllText(Second, "Second");
            await this.RefreshCurrentFolderAsync(false);
            this.DeleteShellItems = (Paths, _) => { File.Delete(Paths[0]); throw new OperationCanceledException(); };
            Check(!await this.DeleteShellSelectionAsync([First, Second], Permanently: true)
                && !this.Entries.Any(Entry => SamePath(Entry.FullPath, First)) && this.Entries.Any(Entry => SamePath(Entry.FullPath, Second))
                && this.StatusText.Text == LanguageService.Text("OperationCancelled") && !this.OperationBusy,
                "Cancelling a partial deletion removes completed rows and keeps remaining files, with a terminal cancellation status");
            var Nested = Directory.CreateDirectory(Path.Combine(Root, "Search", "Nested")).FullName;
            var Deep = Path.Combine(Nested, "Deep.txt"); File.WriteAllText(Deep, "Search result");
            await this.RefreshCurrentFolderAsync(false);
            this.SearchBox.Text = "Deep";
            await this.SearchTask;
            Check(this.Entries.Any(Entry => SamePath(Entry.FullPath, Deep)), "Recursive search includes the owned nested deletion target");
            await this.DeleteShellSelectionAsync([Deep], Permanently: true);
            await this.SearchTask;
            Check(!this.Entries.Any(Entry => SamePath(Entry.FullPath, Deep)),
                "A deleted recursive search result cannot remain visible or be republished by an older search");
            this.SearchBox.Clear(); await this.SearchTask;
            var Native = Path.Combine(Root, "Native Delete.txt"); File.WriteAllText(Native, "Native Shell fixture");
            await this.RefreshCurrentFolderAsync(false);
            this.DeleteShellItems = OriginalDelete;
            Check(await this.DeleteShellSelectionAsync([Native], Permanently: true) && !File.Exists(Native)
                && !this.Entries.Any(Entry => SamePath(Entry.FullPath, Native)),
                "Actual Windows permanent deletion refreshes the panel while quiet fixtures leave the user's Recycle Bin and undo stack untouched");
        }
        finally
        {
            this.DeleteShellItems = OriginalDelete;
            this.CancelInlineRename();
            this.SearchBox.Clear();
            await this.SearchTask;
            if (Other is not null) this.HostWindow.CloseTab(Other);
            this.HostWindow.SelectTab(this);
            await this.NavigateAsync(Initial, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Root) || (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unowned deletion refresh fixture.");
            Directory.Delete(Root, true);
        }
    }
}
