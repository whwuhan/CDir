using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CDir.UI;

internal static class WindowAppearance
{
    private const int WindowCornerPreference = 33;
    private const int DoNotRound = 1;
    private static IntPtr DialogHook;
    private static HookProc? DialogHookCallback;

    internal static void Initialize()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        // This thread-local hook also covers native message boxes created by CDir.
        // It observes activation without changing input, focus, or other applications.
        DialogHookCallback = (Code, Window, Data) =>
        {
            if (Code == 5) UseSquareCorners(Window); // HCBT_ACTIVATE: the native window is fully created.
            return CallNextHookEx(DialogHook, Code, Window, Data);
        };
        DialogHook = SetWindowsHookEx(5, DialogHookCallback, IntPtr.Zero, GetCurrentThreadId()); // WH_CBT
        Application.Current.Exit += (_, _) =>
        {
            if (DialogHook != IntPtr.Zero) UnhookWindowsHookEx(DialogHook);
            DialogHook = IntPtr.Zero;
            DialogHookCallback = null;
        };
    }

    internal static void UseSquareCorners(Window Window) => UseSquareCorners(new WindowInteropHelper(Window).Handle);

    private static void UseSquareCorners(IntPtr Window)
    {
        if (Window == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var Preference = DoNotRound;
        _ = DwmSetWindowAttribute(Window, WindowCornerPreference, ref Preference, sizeof(int));
    }

    private delegate IntPtr HookProc(int Code, IntPtr Window, IntPtr Data);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr Window, int Attribute, ref int Value, int Size);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int Id, HookProc Callback, IntPtr Module, uint Thread);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr Hook, int Code, IntPtr Window, IntPtr Data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr Hook);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
