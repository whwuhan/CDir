using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;
using ShapePath = System.Windows.Shapes.Path;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunHeaderSortSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialSearch = this.SearchBox.Text;
        var InitialSort = this.SortProperty;
        var InitialDirection = this.SortDirection;
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirHeaderSort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Fixture, "a-folder"));
        Directory.CreateDirectory(Path.Combine(Fixture, "z-folder"));
        var Samples = new[] { ("zeta.txt", 1, 4), ("alpha.cs", 20, 1), ("beta.txt", 3, 3), ("gamma.bin", 10, 2) };
        foreach (var (Name, Size, Day) in Samples)
        {
            var FilePath = Path.Combine(Fixture, Name);
            File.WriteAllText(FilePath, new string('x', Size));
            File.SetLastWriteTime(FilePath, new DateTime(2026, 1, Day, 13, 4, 7));
        }
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Header sort UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        IEnumerable<DataGridColumnHeader> Headers(DependencyObject Parent)
        {
            for (var Index = 0; Index < VisualTreeHelper.GetChildrenCount(Parent); Index++)
            {
                var Child = VisualTreeHelper.GetChild(Parent, Index);
                if (Child is DataGridColumnHeader Header && Header.Column is not null) yield return Header;
                foreach (var Descendant in Headers(Child)) yield return Descendant;
            }
        }
        int Compare(FileEntry Left, FileEntry Right, string Key) => Key switch
        {
            nameof(FileEntry.Modified) => DateTime.Compare(Left.Modified, Right.Modified),
            nameof(FileEntry.Size) => Nullable.Compare(Left.Size, Right.Size),
            nameof(FileEntry.TypeName) => LanguageService.Culture.CompareInfo.Compare(Left.TypeName, Right.TypeName),
            nameof(FileEntry.FullPath) => LanguageService.Culture.CompareInfo.Compare(Left.FullPath, Right.FullPath),
            _ => LanguageService.Culture.CompareInfo.Compare(Left.Name, Right.Name)
        };
        void Screenshot(string Name)
        {
            if (AppMain.ScreenshotPath is not { } Destination) return;
            var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            Bitmap.Render(this);
            var Encoder = new PngBitmapEncoder();
            Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
            using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Destination)!, Name + ".png"));
            Encoder.Save(Stream);
        }
        try
        {
            Check(await this.NavigateAsync(Fixture), "Sort arrows test opens an owned file fixture");
            this.SearchBox.Text = "/file /dir";
            await this.WaitForSearchSmokeAsync();
            foreach (var Column in this.FileGrid.Columns)
            {
                this.FileGrid.ScrollIntoView(this.FileGrid.Items[0], Column);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                this.UpdateLayout();
                var Header = Headers(this.FileGrid).Single(Header => ReferenceEquals(Header.Column, Column));
                var Arrow = (ShapePath)Header.Template.FindName("SortArrow", Header);
                // Enter the native header click handler without moving the desktop pointer or sending global input.
                var Click = typeof(DataGridColumnHeader).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Column.SortDirection = null;
                for (var Step = 0; Step < 3; Step++)
                {
                    Click.Invoke(Header, null);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    this.UpdateLayout();
                    var Direction = Step % 2 == 0 ? ListSortDirection.Descending : ListSortDirection.Ascending;
                    Check(Column.SortDirection == Direction && this.SortProperty == Column.SortMemberPath
                        && (Arrow.RenderTransform is RotateTransform Rotation ? Rotation.Angle : 0) == (Direction == ListSortDirection.Ascending ? 0 : 180)
                        && Arrow.IsVisible && Arrow.StrokeThickness >= 2,
                        Column.SortMemberPath + " header click points the bold arrow up for ascending and down for descending");
                    var Rows = this.View!.Cast<FileEntry>().ToArray();
                    var Files = Rows.Where(Entry => !Entry.IsDirectory).ToArray();
                    Check(Rows.TakeWhile(Entry => Entry.IsDirectory).Count() == 2
                        && Files.Zip(Files.Skip(1)).All(Pair => (Direction == ListSortDirection.Ascending ? 1 : -1) * Compare(Pair.First, Pair.Second, Column.SortMemberPath) <= 0),
                        Column.SortMemberPath + " arrow direction agrees with the actual row order and keeps folders first");
                    Check(Headers(this.FileGrid).Where(Other => !ReferenceEquals(Other.Column, Column))
                        .All(Other => !((ShapePath)Other.Template.FindName("SortArrow", Other)).IsVisible),
                        "Inactive columns hide their arrows when sorting switches to another column");
                    if (Column.SortMemberPath == nameof(FileEntry.Name)) Screenshot(Direction == ListSortDirection.Ascending ? "HeaderSort-Ascending" : "HeaderSort-Descending");
                }
                Check(Header.Template.FindName("PART_LeftHeaderGripper", Header) is Thumb
                    && Header.Template.FindName("PART_RightHeaderGripper", Header) is Thumb
                    && this.FileGrid.CanUserReorderColumns && Column.CanUserReorder,
                    Column.SortMemberPath + " header retains native resize handles and drag reordering");
                var ArrowRight = Arrow.TransformToAncestor(Header).TransformBounds(new Rect(Arrow.RenderSize)).Right;
                Check(!Arrow.IsHitTestVisible && ArrowRight <= Header.ActualWidth && ArrowRight >= Header.ActualWidth - 20,
                    Column.SortMemberPath + " arrow sits at the right edge without intercepting header clicks");
            }
        }
        finally
        {
            this.SearchBox.Clear();
            await this.WaitForSearchSmokeAsync();
            this.SortProperty = InitialSort;
            this.SortDirection = InitialDirection;
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.SearchBox.Text = InitialSearch;
            await this.WaitForSearchSmokeAsync();
        }
    }
}
