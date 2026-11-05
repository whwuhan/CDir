using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace CDir.UI;

public partial class ExplorerTab
{
    private static void CheckSquareWindowCorners(Window Window) => CheckSquareWindowCorners(new WindowInteropHelper(Window).Handle);

    private static void CheckSquareWindowCorners(IntPtr Window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var Result = GetSmokeWindowCornerPreference(Window, 33, out var Preference, sizeof(int));
        if (Result != 0 || Preference != 1)
            throw new InvalidOperationException($"Window frame must use square corners (result={Result:X8}, preference={Preference}).");
    }

    private void RunWindowAppearanceSmokeChecks(List<string> Passed)
    {
        CheckSquareWindowCorners(this.HostWindow);
        Passed.Add("The main window disables system rounded corners");
        var Owner = new WindowInteropHelper(this.HostWindow).Handle;
        var Caption = "CDir Window Frame Check " + Guid.NewGuid().ToString("N");
        Exception? Failure = null;
        var Inspected = false;
        NativeMenuTimerCallback Inspect = (_, _, _, _) =>
        {
            var Dialog = GetSmokeEnabledPopup(Owner, 6); // GW_ENABLEDPOPUP
            if (Dialog == IntPtr.Zero || Dialog == Owner || GetSmokeWindowThread(Dialog, out _) != GetShellSmokeThreadId()) return;
            var Title = new StringBuilder(128);
            GetSmokeWindowTitle(Dialog, Title, Title.Capacity);
            if (Title.ToString() != Caption) return;
            try { CheckSquareWindowCorners(Dialog); Inspected = true; }
            catch (Exception Error) { Failure = Error; }
            finally { CloseSmokeMessageBox(Dialog, 0x111, (IntPtr)2, IntPtr.Zero); } // WM_COMMAND / IDCANCEL
        };
        var Timer = SetShellSmokeTimer(Owner, 0xCD18, 150, Inspect);
        if (Timer == 0) throw new System.ComponentModel.Win32Exception();
        MessageBoxResult Result;
        try { Result = MessageBox.Show(this.HostWindow, "Window frame validation.", Caption, MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel); }
        finally { KillShellSmokeTimer(Owner, Timer); GC.KeepAlive(Inspect); }
        if (Failure is not null) throw Failure;
        if (!Inspected || Result != MessageBoxResult.Cancel) throw new InvalidOperationException("The owned native message box was not inspected and dismissed safely.");
        Passed.Add("Native message boxes use square corners and retain normal cancellation");
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetSmokeWindowCornerPreference(IntPtr Window, int Attribute, out int Preference, int Size);
    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern IntPtr GetSmokeEnabledPopup(IntPtr Window, uint Command);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetSmokeWindowThread(IntPtr Window, out uint Process);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetSmokeWindowTitle(IntPtr Window, StringBuilder Title, int Capacity);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr CloseSmokeMessageBox(IntPtr Window, uint Message, IntPtr WParam, IntPtr LParam);
}
