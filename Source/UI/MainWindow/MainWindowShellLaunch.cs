using System.IO;
using CDir.Services;

namespace CDir.UI;

public partial class MainWindow
{
    private ShellLaunchServer? ShellLaunch;

    internal async Task<ShellLaunchReply> HandleShellLaunchAsync(ShellLaunchRequest Request)
    {
        if (Request.Probe) return new(true, this.LastActivatedUtc.UtcTicks);
        try
        {
            if (!ShellLaunchLocation.TryResolve(Request.Location, out var Location))
            { ShellLaunchLocation.OpenInWindows(Location!); return new(true); }
            // Win+E sends no location and preserves the active tab and workspace.
            if (Location is not null)
            {
                var Page = this.Tabs.FirstOrDefault(Page => string.Equals(Page.TabPath.TrimEnd('\\'), Location.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                if (Page is not null) this.SelectTab(Page);
                else
                {
                    Page = new ExplorerTab(this, InitialPath: Location, InitialSettings: this.ActiveTab?.ClonePageSettings(), InitialRoots: this.ActiveTab?.CloneRootNodes());
                    this.AttachTab(Page);
                    await Page.WaitUntilReadyAsync();
                }
            }
            this.RestoreFromTray();
            return new(true);
        }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return new(false); }
    }
}
