using System.ComponentModel;
using System.Diagnostics;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private Action<ProcessStartInfo> StartNewWindowProcess = Info =>
    {
        using var Child = Process.Start(Info) ?? throw new InvalidOperationException("The new window could not be started.");
    };

    private void OpenNewWindow()
    {
        try
        {
            var Executable = Environment.ProcessPath ?? throw new InvalidOperationException("The application path is unavailable.");
            var Info = new ProcessStartInfo(Executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            Info.ArgumentList.Add("--clone-from");
            Info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            this.StartNewWindowProcess(Info);
        }
        catch (Exception Error) when (Error is Win32Exception or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            this.ShowNotice(LanguageService.Text("NewWindowError", FriendlyError(Error)), true);
        }
    }
}
