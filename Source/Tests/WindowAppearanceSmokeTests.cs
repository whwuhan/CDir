using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;

namespace CDir.UI;

public partial class ExplorerTab
{
    private static void CheckVisibleWindowFrame(MainWindow Window, List<string> Passed)
    {
        Window.UpdateLayout();
        var Frame = Window.WindowFrame;
        var Chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(Window);
        var Brush = (SolidColorBrush)Frame.BorderBrush;
        if (Frame.BorderThickness != new Thickness(1) || Brush.Color.R != Brush.Color.G || Brush.Color.G != Brush.Color.B
            || Chrome.GlassFrameThickness != new Thickness(1) || Chrome.UseAeroCaptionButtons
            || Window.AllowsTransparency || Frame.Effect is not null)
            throw new InvalidOperationException("The main window must use a native shadow and a neutral thin edge without a blue frame or software blur.");
        Passed.Add("The main window enables native compositor shadows with a neutral thin edge and no blue active border or software blur");
        var Handle = new WindowInteropHelper(Window).Handle;
        var Result = GetSmokeWindowCornerPreference(Handle, 1, out var Rendering, sizeof(int));
        if (Result != 0 || Rendering != 1) throw new InvalidOperationException("Native non-client rendering must be enabled for the window shadow.");
        // BORDER_COLOR is a DwmSetWindowAttribute option, not a readable DWM
        // attribute. Verify the real API accepts it and query rendering separately.
        var ColorResult = WindowAppearance.UseNeutralShadowFrame(Window);
        if (ColorResult != 0) throw new InvalidOperationException($"The native neutral border color could not be applied ({ColorResult:X8}).");
        Passed.Add("DWM reports enabled native frame rendering and accepts neutral border coloring without activating the owned test window");
        if (AppMain.ScreenshotPath is { } Output)
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!, "WindowShadow.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                rendering_enabled = Rendering == 1, native_border_color = "#C2C2C2", border_set_hresult = ColorResult, glass_frame_thickness = 1,
                software_blur = false, allows_transparency = false, capture_note = "Client RenderTargetBitmap previews do not capture the compositor shadow outside the HWND."
            }));
    }
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
