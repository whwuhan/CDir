using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunNaturalNameSortSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Natural name sort check failed: " + Name);
            Passed.Add(Name);
        }
        var Initial = this.CurrentPath;
        var OldSort = this.SortProperty;
        var OldDirection = this.SortDirection;
        var OldGroup = this.FileGroupProperty;
        var OldGroupDescending = this.FileGroupDescending;
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Natural Name Sort")).FullName;
        var Folders = new[] { "FR" }.Concat(Enumerable.Range(0, 16).Select(Index => "wallpapers-" + Index)).ToArray();
        string[] Files = ["image2.txt", "image9.txt", "image10.txt", "image100.txt"];
        foreach (var Name in Folders.Reverse()) Directory.CreateDirectory(Path.Combine(Root, Name));
        foreach (var Name in Files.Reverse()) File.WriteAllText(Path.Combine(Root, Name), "Owned natural name sort fixture");
        string[] VisibleNames() => this.View!.Cast<FileEntry>().Select(Entry => Entry.Name).ToArray();
        var NameColumn = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Name));
        try
        {
            this.FileGroupProperty = "";
            this.SortProperty = nameof(FileEntry.Name);
            this.SortDirection = ListSortDirection.Ascending;
            Check(await this.NavigateAsync(Root), "Natural name sort fixture opens");
            this.UpdateComputerSorting();
            Check(VisibleNames().SequenceEqual(Folders.Concat(Files)), "Ascending names place wallpapers-2 before wallpapers-10 and keep folders first");
            var Node = (FolderNode)this.FolderTree.SelectedItem;
            await this.EnsureChildrenAsync(Node);
            Check(Node.Children.Select(Child => Child.Name).SequenceEqual(Folders), "Expanded directory tree uses the same natural name order");
            this.SelectPath(Path.Combine(Root, "wallpapers-2"));
            this.FileGridSorting(this.FileGrid, new DataGridSortingEventArgs(NameColumn));
            Check(VisibleNames().SequenceEqual(Folders.Reverse().Concat(Files.Reverse())), "Name header toggles to descending numeric order with folders first");
            this.FileGridSorting(this.FileGrid, new DataGridSortingEventArgs(NameColumn));
            Check(VisibleNames().SequenceEqual(Folders.Concat(Files)) && this.SelectedEntries().Single().Name == "wallpapers-2",
                "Name header toggles back to ascending and preserves selection");
            foreach (var Property in new[] { nameof(FileEntry.Size), nameof(FileEntry.TypeName) })
            {
                this.SortProperty = Property;
                this.SortDirection = ListSortDirection.Descending;
                this.ApplySort();
                Check(VisibleNames().SequenceEqual(Folders.Concat(Files)), Property + " ties use ascending natural names");
            }
            this.SortProperty = nameof(FileEntry.Name);
            foreach (var Group in new[] { nameof(FileEntry.Name), nameof(FileEntry.TypeName) })
                foreach (var Direction in new[] { ListSortDirection.Ascending, ListSortDirection.Descending })
                {
                    this.FileGroupProperty = Group;
                    this.SortDirection = Direction;
                    this.ApplySort();
                    var Actual = this.View!.Cast<FileEntry>().Where(Entry => Entry.Name.StartsWith("wallpapers-", StringComparison.Ordinal)).Select(Entry => Entry.Name);
                    var Expected = Folders.Skip(1);
                    if (Direction == ListSortDirection.Descending) Expected = Expected.Reverse();
                    Check(Actual.SequenceEqual(Expected), Group + " grouping preserves " + Direction + " natural name order");
                }
            this.FileGroupProperty = "";
            this.SortDirection = ListSortDirection.Ascending;
            this.ApplySort();
            this.UpdateComputerSorting();
            Check(VisibleNames().SequenceEqual(Folders.Concat(Files)), "Removing grouping restores ordinary natural name order");
            this.SearchBox.Text = "wallpapers /dir";
            await this.WaitForSearchSmokeAsync();
            Check(VisibleNames().SequenceEqual(Folders.Skip(1)), "Recursive search results use natural name order");
            this.SearchBox.Clear();
            await this.WaitForSearchSmokeAsync();
            if (AppMain.ScreenshotPath is { } Output)
            {
                var OldNameWidth = NameColumn.Width;
                NameColumn.Width = new DataGridLength(260);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                this.HostWindow.UpdateLayout();
                var Bitmap = new RenderTargetBitmap((int)this.HostWindow.ActualWidth, (int)this.HostWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(this.HostWindow);
                var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, "NaturalNameSort.png"));
                Encoder.Save(Stream);
                NameColumn.Width = OldNameWidth;
            }
            var A = Directory.CreateDirectory(Path.Combine(Root, "A")).FullName;
            var Z = Directory.CreateDirectory(Path.Combine(Root, "Z")).FullName;
            File.WriteAllText(Path.Combine(A, "pathsort-z.txt"), "Owned path sort fixture");
            File.WriteAllText(Path.Combine(Z, "pathsort-a.txt"), "Owned path sort fixture");
            this.SearchBox.Text = "pathsort /file";
            await this.WaitForSearchSmokeAsync();
            this.SortProperty = nameof(FileEntry.FullPath);
            this.SortDirection = ListSortDirection.Ascending;
            this.ApplySort();
            Check(VisibleNames().SequenceEqual(new[] { "pathsort-z.txt", "pathsort-a.txt" }), "Absolute Path sorting still compares paths instead of names");
            this.SortDirection = ListSortDirection.Descending;
            this.ApplySort();
            Check(VisibleNames().SequenceEqual(new[] { "pathsort-a.txt", "pathsort-z.txt" }), "Absolute Path descending sorting preserves its column semantics");
            var Earlier = new FileEntry { Name = "z", OriginalLocation = @"C:\A", DateDeleted = new DateTime(2026, 1, 1) };
            var Later = new FileEntry { Name = "a", OriginalLocation = @"C:\Z", DateDeleted = new DateTime(2026, 2, 1) };
            foreach (var Property in new[] { nameof(FileEntry.OriginalLocation), nameof(FileEntry.DateDeleted) })
            {
                this.SortProperty = Property;
                Check(new FileViewComparer(this).Compare(Earlier, Later) > 0, Property + " sorting preserves recycle-bin column semantics");
            }
        }
        finally
        {
            this.SearchBox.Clear();
            this.FileGroupProperty = OldGroup;
            this.FileGroupDescending = OldGroupDescending;
            this.SortProperty = OldSort;
            this.SortDirection = OldDirection;
            await this.NavigateAsync(Initial);
            this.UpdateComputerSorting();
        }
    }
}
