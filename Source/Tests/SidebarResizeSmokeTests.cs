using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunSidebarResizeSmokeChecksAsync(List<string> Passed)
    {
        var InitialWidth = this.HostWindow.Width;
        var InitialState = this.HostWindow.WindowState;
        var InitialSidebar = this.SidebarColumn.Width;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Sidebar resize UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        var Peer = new GridSplitterAutomationPeer(this.SidebarSplitter);
        var Splitter = (ITransformProvider)Peer.GetPattern(PatternInterface.Transform)!;
        void Move(double Delta)
        {
            Splitter.Move(Delta, 0);
            this.UpdateLayout();
        }
        void CheckLimit(string Name)
        {
            var ContentRight = this.MainPanelContent.TranslatePoint(new Point(this.MainPanelContent.ActualWidth, 0), this.ContentPanels).X;
            Check(Math.Abs(this.MainPanelColumn.ActualWidth - 10) < 0.1
                && this.MainPanelContent.ActualWidth >= 9.9 && this.FileGrid.ActualWidth >= 9.9
                && Math.Abs(ContentRight - this.ContentPanels.ActualWidth) < 0.1, Name);
        }
        void CheckFilePanelEdges(string Name)
        {
            var Left = this.FileGrid.TranslatePoint(new Point(), this.ContentPanels).X;
            var Right = this.FileGrid.TranslatePoint(new Point(this.FileGrid.ActualWidth, 0), this.ContentPanels).X;
            Check(Math.Abs(Left - this.SidebarColumn.ActualWidth - this.SidebarSplitter.ActualWidth) < 0.1
                && Math.Abs(Right - this.ContentPanels.ActualWidth) < 0.1, Name);
        }
        try
        {
            this.HostWindow.WindowState = WindowState.Normal;
            this.HostWindow.Width = Math.Max(this.HostWindow.MinWidth + 160, InitialWidth);
            await Settle();
            var Before = this.SidebarColumn.ActualWidth;
            Check(Math.Abs(Before - Models.DefaultSettings.Current.SidebarWidth) < 0.1,
                "Initial layout preserves the configured default sidebar width");
            Move(100);
            Check(Math.Abs(this.SidebarColumn.ActualWidth - Before - 100) < 0.1,
                "Splitter widens the sidebar");
            await this.RunSidebarVisibilitySmokeChecksAsync(Passed);
            CheckFilePanelEdges("File list fills the area from the sidebar splitter to the right window edge without side margins");
            Check(this.FileGrid.BorderThickness == new Thickness(1) && this.FileGrid.BorderBrush is not null,
                "File list has a thin border on all four sides");
            Move(1000000);
            CheckLimit("Oversized right drag stops with 10px of visible file content at the right edge");
            CheckFilePanelEdges("File list remains flush with both edges at the sidebar drag limit");
            Move(1000000);
            CheckLimit("Further right drags cannot cross the 10px limit");
            Move(-80);
            Check(this.MainPanelColumn.ActualWidth > 89.9,
                "Splitter can immediately move left again after hitting the limit");
            Move(1000000);
            this.HostWindow.Width = this.HostWindow.MinWidth;
            await Settle();
            CheckLimit("Shrinking the window clamps an already widened sidebar and preserves the 10px panel");
            CheckFilePanelEdges("Shrinking the window keeps the bordered file list stretched to both edges");
            this.HostWindow.Width += 120;
            await Settle();
            Move(1000000);
            CheckLimit("Growing the window recalculates the draggable right boundary");
            Move(-1000000);
            Check(Math.Abs(this.SidebarColumn.ActualWidth - this.SidebarColumn.MinWidth) < 0.1,
                "Left drag retains the existing sidebar minimum width");
        }
        finally
        {
            this.HostWindow.Width = InitialWidth;
            this.HostWindow.WindowState = InitialState;
            await Settle();
            this.SidebarColumn.Width = InitialSidebar;
            await Settle();
        }
    }
}
