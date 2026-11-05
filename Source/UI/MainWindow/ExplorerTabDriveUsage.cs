using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private readonly DispatcherTimer DriveUsageTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private Func<string, DriveUsage?> ReadDriveUsage = DriveUsage.Read;
    private Task DriveUsageRefreshTask = Task.CompletedTask;
    private DriveUsage? CurrentDriveUsage;
    private string DriveUsageRoot = "";
    private long DriveUsageEpoch;
    private long DriveUsageRequestedAt;
    private Task SidebarDriveUsageTask = Task.CompletedTask;
    private long SidebarDriveUsageEpoch;

    private void InitializeDriveUsage()
    {
        this.DriveUsageTimer.Tick += (_, _) =>
        {
            this.QueueDriveUsageRefresh(this.CurrentPath, Force: true);
            this.QueueSidebarDriveUsageRefresh();
        };
        this.DriveUsageTimer.Start();
    }

    private void QueueSidebarDriveUsageRefresh(bool Force = false)
    {
        var Epoch = ++this.SidebarDriveUsageEpoch;
        var Nodes = this.Roots.Where(Node => Node.IsDrive).ToArray();
        var Reader = this.ReadDriveUsage;
        this.SidebarDriveUsageTask = Task.WhenAll(Nodes.Select(async Node =>
        {
            var Usage = await this.HostWindow.ReadDriveUsageAsync(Node.FullPath, Reader, Force);
            if (!this.WindowClosed && Epoch == this.SidebarDriveUsageEpoch && this.Roots.Contains(Node)) Node.UpdateDriveUsage(Usage);
        }));
    }

    private void QueueDriveUsageRefresh(string Path, bool Force = false)
    {
        if (this.WindowClosed) return;
        var Root = DriveUsage.GetRoot(Path);
        var SameRoot = string.Equals(Root, this.DriveUsageRoot, StringComparison.OrdinalIgnoreCase);
        if (SameRoot && (!this.DriveUsageRefreshTask.IsCompleted
            || !Force && Environment.TickCount64 - this.DriveUsageRequestedAt < 10000)) return;
        var Epoch = ++this.DriveUsageEpoch;
        this.DriveUsageRoot = Root;
        this.DriveUsageRequestedAt = Environment.TickCount64;
        if (!SameRoot) { this.CurrentDriveUsage = null; this.UpdateDriveUsageStatus(); }
        this.DriveUsageRefreshTask = RefreshAsync();
        async Task RefreshAsync()
        {
            var Reader = this.ReadDriveUsage;
            var Usage = Root.Length == 0 ? null : await Task.Run(() => Reader(Root));
            if (this.WindowClosed || Epoch != this.DriveUsageEpoch) return;
            this.CurrentDriveUsage = Usage;
            foreach (var Node in this.Roots.Where(Node => Node.IsDrive && SamePath(Node.FullPath, Root))) Node.UpdateDriveUsage(Usage);
            this.UpdateDriveUsageStatus();
        }
    }

    private void UpdateDriveUsageStatus()
    {
        if (this.DriveUsagePanel is null) return;
        this.DriveUsagePanel.Visibility = this.DriveUsageRoot.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var Label = this.DriveUsageRoot.TrimEnd('\\', '/');
        var Available = this.CurrentDriveUsage is { TotalBytes: > 0 };
        this.DriveUsageBar.Visibility = Available ? Visibility.Visible : Visibility.Collapsed;
        this.DriveUsagePercentageText.Visibility = Available ? Visibility.Visible : Visibility.Collapsed;
        if (Available)
        {
            var Usage = this.CurrentDriveUsage!;
            this.DriveUsageText.Text = LanguageService.Text("DriveUsageFormat", Label, Usage.UsedGB, Usage.TotalGB);
            this.DriveUsagePanel.ToolTip = LanguageService.Text("DriveUsageTooltip", Label, Usage.UsedGB, Usage.TotalGB, Usage.UsedFraction);
            this.DriveUsageBar.Value = Usage.UsedFraction * 100;
            this.DriveUsageBar.Foreground = (Brush)this.FindResource(Usage.UsedFraction switch
            {
                >= 0.9 => "DriveUsageCritical",
                >= 0.7 => "DriveUsageWarning",
                _ => "DriveUsageLow"
            });
            this.DriveUsagePercentageText.Text = Usage.UsedFraction.ToString("P1", LanguageService.Culture);
        }
        else
        {
            this.DriveUsageText.Text = LanguageService.Text("DriveUsageUnavailable", Label);
            this.DriveUsagePanel.ToolTip = LanguageService.Text("DriveUsageUnavailableTooltip");
            this.DriveUsageBar.Value = 0;
            this.DriveUsagePercentageText.Text = "";
        }
        AutomationProperties.SetName(this.DriveUsagePanel, this.DriveUsagePanel.ToolTip.ToString());
    }
}
