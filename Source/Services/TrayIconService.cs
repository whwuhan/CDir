using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace CDir.Services;

// Own the icon and its callbacks on the window's dispatcher, without a second UI message loop.
internal sealed class TrayIconService : IDisposable
{
    internal const int CallbackMessage = 0x8000 + 41;
    private readonly HwndSource Source;
    private readonly Action Restore;
    private readonly Action Exit;
    private readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private NotifyIconData Data;
    private bool Disposed;
    private bool ShouldBeVisible;
    internal bool IsVisible { get; private set; }
    internal ContextMenu Menu { get; } = new();
    internal IntPtr WindowHandle => this.Source.Handle;

    internal TrayIconService(Window Window, Action Restore, Action Exit)
    {
        this.Restore = Restore;
        this.Exit = Exit;
        this.Source = HwndSource.FromHwnd(new WindowInteropHelper(Window).EnsureHandle())
            ?? throw new InvalidOperationException("The window handle is unavailable.");
        ExtractIconEx(Environment.ProcessPath!, 0, out var LargeIcon, out var SmallIcon, 1);
        var Icon = SmallIcon != IntPtr.Zero ? SmallIcon : LargeIcon;
        if (LargeIcon != IntPtr.Zero && LargeIcon != Icon) DestroyIcon(LargeIcon);
        if (Icon == IntPtr.Zero) throw new Win32Exception("The application icon is unavailable.");
        this.Data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = this.Source.Handle, Id = 1,
            Flags = 1 | 2 | 4, Callback = CallbackMessage, Icon = Icon, Tip = "CDir",
            Info = "", InfoTitle = ""
        };
        var Open = new MenuItem();
        Open.SetResourceReference(HeaderedItemsControl.HeaderProperty, "TextTrayRestore");
        Open.Click += (_, _) => this.Restore();
        var Quit = new MenuItem();
        Quit.SetResourceReference(HeaderedItemsControl.HeaderProperty, "TextTrayExit");
        Quit.Click += (_, _) => { this.Menu.IsOpen = false; this.Exit(); };
        this.Menu.SetResourceReference(Control.FontSizeProperty, "UIFontMenus");
        this.Menu.SetResourceReference(FrameworkElement.MinWidthProperty, "UIMenuMinimumWidth");
        this.Menu.PlacementTarget = Window;
        this.Menu.Items.Add(Open);
        // ContextMenu otherwise assigns the Windows separator style, whose icon gutter
        // leaves only a short line in this compact, icon-free menu.
        var Divider = new Separator();
        Divider.SetResourceReference(FrameworkElement.StyleProperty, typeof(Separator));
        this.Menu.Items.Add(Divider);
        this.Menu.Items.Add(Quit);
        this.Source.AddHook(this.WindowMessage);
    }

    internal bool Show()
    {
        ObjectDisposedException.ThrowIf(this.Disposed, this);
        this.ShouldBeVisible = true;
        if (this.IsVisible) return true;
        this.IsVisible = Shell_NotifyIcon(0, ref this.Data);
        return this.IsVisible;
    }

    private void Remove()
    {
        this.ShouldBeVisible = false;
        this.Menu.IsOpen = false;
        if (this.IsVisible) Shell_NotifyIcon(2, ref this.Data);
        this.IsVisible = false;
    }

    private IntPtr WindowMessage(IntPtr Window, int Message, IntPtr WParam, IntPtr LParam, ref bool Handled)
    {
        if ((uint)Message == this.TaskbarCreatedMessage && this.ShouldBeVisible)
        {
            // Explorer discards notification icons when it restarts.
            this.IsVisible = false;
            this.Show();
        }
        else if (Message == CallbackMessage && WParam.ToInt64() == this.Data.Id && this.IsVisible)
        {
            Handled = true;
            if (LParam.ToInt64() == 0x203) this.Restore(); // WM_LBUTTONDBLCLK
            else if (LParam.ToInt64() == 0x205) // WM_RBUTTONUP
            {
                if (!AppMain.SmokeTest) SetForegroundWindow(Window);
                this.Menu.Placement = PlacementMode.MousePoint;
                this.Menu.IsOpen = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (this.Disposed) return;
        this.Remove();
        this.Source.RemoveHook(this.WindowMessage);
        DestroyIcon(this.Data.Icon);
        this.Disposed = true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint Message, ref NotifyIconData Data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string File, int Index, out IntPtr Large, out IntPtr Small, uint Count);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr Icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string Message);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr Window);
}
