using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunBrowsePositionSmokeAsync(List<string> Passed)
    {
        var Initial = this.CurrentPath;
        var Mode = this.Settings.FileView;
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Browse positions")).FullName;
        for (var Index = 0; Index < 180; Index++) Directory.CreateDirectory(Path.Combine(Root, "Folder " + Index.ToString("D3")));
        var Target = Path.Combine(Root, "Folder 140");
        async Task Settle() { this.UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); }
        void Check(bool Value, string Message)
        {
            if (!Value) throw new InvalidOperationException("Browse position: " + Message);
            Passed.Add(Message);
        }
        try
        {
            foreach (var View in new[] { FileViewMode.Details, FileViewMode.LargeIcons, FileViewMode.List, FileViewMode.Tiles })
            {
                await this.NavigateAsync(Root);
                this.ChangeFileView(View);
                this.SelectPath(Target);
                await Settle();
                var Viewer = FindVisualChild<ScrollViewer>(this.ActiveFileControl)!;
                var Offset = Viewer.VerticalOffset;
                var Horizontal = Viewer.HorizontalOffset;
                Check(Offset > 0 || Horizontal > 0, View + " history fixture is scrolled away from the start");
                await this.OpenSelectedAsync();
                await this.BackAsync();
                await Settle();
                Viewer = FindVisualChild<ScrollViewer>(this.ActiveFileControl)!;
                Check(SamePath(this.CurrentPath, Root) && this.SelectedEntries().SingleOrDefault()?.FullPath == Target
                    && Math.Abs(Viewer.VerticalOffset - Offset) < 1 && Math.Abs(Viewer.HorizontalOffset - Horizontal) < 1,
                    View + " Back restores the entered folder's selection and exact viewport");
                await this.ForwardAsync();
                Check(SamePath(this.CurrentPath, Target), View + " Forward retains the child history entry");
            }
            this.ChangeFileView(FileViewMode.Details);
            await this.NavigateAsync(Root);
            this.SearchBox.Text = "Folder 1";
            await this.SearchTask;
            this.SelectPath(Target);
            await Settle();
            var SearchOffset = FindVisualChild<ScrollViewer>(this.ActiveFileControl)!.VerticalOffset;
            await this.OpenSelectedAsync();
            await this.BackAsync();
            Check(this.SearchBox.Text == "Folder 1" && this.SelectedEntries().SingleOrDefault()?.FullPath == Target
                && Math.Abs(FindVisualChild<ScrollViewer>(this.ActiveFileControl)!.VerticalOffset - SearchOffset) < 1,
                "Back restores filtered results and their previous viewport");
            await this.NavigateAsync(Path.Combine(Root, "Folder 010"));
            Check(!this.ForwardButton.IsEnabled, "A new navigation discards the old forward branch");
        }
        finally { this.SearchBox.Clear(); await this.NavigateAsync(Initial); this.ChangeFileView(Mode); }
    }
}
