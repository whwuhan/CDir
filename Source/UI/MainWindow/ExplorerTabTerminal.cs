using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CDir.Localization;
using CDir.Services;
using VirtualTerminal;

namespace CDir.UI;

public partial class ExplorerTab
{
    private EmbeddedTerminalSession? TerminalSession;
    private TerminalControl? TerminalSurface;
    private Task? TerminalStartupTask;
    private double SavedTerminalHeight = 240;
    private IInputElement? TerminalReturnFocus;
    private bool UpdatingTerminalScrollBar;

    private void UpdateTerminalScrollBar()
    {
        if (this.TerminalSurface is not CDir.Controls.EmbeddedTerminalControl Surface) return;
        this.UpdatingTerminalScrollBar = true;
        try
        {
            this.TerminalScrollBar.Maximum = Surface.HistoryLines;
            this.TerminalScrollBar.ViewportSize = Surface.Session?.Buffer.Rows ?? 1;
            this.TerminalScrollBar.LargeChange = this.TerminalScrollBar.ViewportSize;
            this.TerminalScrollBar.Value = Surface.HistoryLines - Surface.HistoryOffset;
            this.TerminalScrollBar.IsEnabled = Surface.HistoryLines > 0;
        }
        finally { this.UpdatingTerminalScrollBar = false; }
    }

    private void TerminalScrollValueChanged(object Sender, RoutedPropertyChangedEventArgs<double> E)
    {
        if (!this.UpdatingTerminalScrollBar && this.TerminalSurface is CDir.Controls.EmbeddedTerminalControl Surface)
            Surface.SetHistoryPosition(E.NewValue);
    }

    private void TerminalViewportResized(object? Sender, EventArgs E)
    {
        // The terminal debounces its grid resize and may suppress screen events.
        // Render after it finishes adjusting the history offset, even for a quiet shell.
        this.TerminalSurface?.InvalidateVisual();
    }

    private async void ToggleTerminalClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        await this.ToggleTerminalAsync();
    }

    private async Task ToggleTerminalAsync(bool RestoreFocus = true)
    {
        if (this.WindowClosed) return;
        if (this.TerminalPanel.Visibility == Visibility.Visible) { this.HideTerminal(); return; }
        this.TerminalReturnFocus = Keyboard.FocusedElement;
        this.TerminalPanel.Visibility = Visibility.Visible;
        this.TerminalSplitter.Visibility = Visibility.Visible;
        this.TerminalSplitterRow.Height = new GridLength(5);
        this.TerminalRow.MinHeight = 96;
        this.TerminalRow.Height = new GridLength(this.SavedTerminalHeight);
        this.ClampTerminalHeight();
        this.TerminalToggleButton.Background = (Brush)this.FindResource("FocusHint");
        this.TerminalToggleButton.Foreground = (Brush)this.FindResource("HighlightText");
        this.TerminalToggleButton.SetValue(System.Windows.Automation.AutomationProperties.ItemStatusProperty, LanguageService.Text("TerminalVisible"));
        if (this.TerminalSession is null)
        {
            if (this.TerminalStartupTask is not { IsCompleted: false }) this.TerminalStartupTask = this.StartTerminalAsync();
            await this.TerminalStartupTask;
        }
        if (RestoreFocus && !this.WindowClosed && this.TerminalPanel.Visibility == Visibility.Visible) this.TerminalSurface?.Focus();
    }

    private async Task StartTerminalAsync()
    {
        var Directory = this.FileSystemWorkingDirectory;
        try
        {
            // Snapshot the directory at first opening; navigation during startup must not change it.
            var Session = await Task.Run(() => new EmbeddedTerminalSession(Directory, this.Dispatcher));
            if (this.WindowClosed) { Session.Dispose(); return; }
            this.TerminalSession = Session;
            Session.Disconnected += this.TerminalDisconnected;
            Session.ViewportResized += this.TerminalViewportResized;
            if (this.TerminalSurface is null)
            {
                this.TerminalSurface = new CDir.Controls.EmbeddedTerminalControl
                {
                    FontFamily = new FontFamily("Lucida Console"), FontSize = 13,
                    Background = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                    ScreenBackground = Color.FromRgb(0x11, 0x18, 0x27), ScreenForeground = Color.FromRgb(0xE5, 0xE7, 0xEB),
                    AllowDirectInput = true, ScrollbackLines = 5000,
                    Margin = new Thickness(8, 4, 8, 6)
                };
                this.TerminalSurface.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "TextTerminal");
                this.TerminalHost.Child = this.TerminalSurface;
                ((CDir.Controls.EmbeddedTerminalControl)this.TerminalSurface).ScrollStateChanged += (_, _) => this.UpdateTerminalScrollBar();
                var Menu = new ContextMenu();
                foreach (var (Key, Command) in new[] { ("Copy", TerminalControl.CopyCommand), ("Paste", TerminalControl.PasteCommand), ("ClearTerminal", TerminalControl.ClearCommand) })
                {
                    var Item = new MenuItem { Tag = Key, Command = Command, CommandTarget = this.TerminalSurface };
                    Item.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Text" + Key);
                    Menu.Items.Add(Item);
                }
                this.TerminalSurface.ContextMenu = Menu;
                ((CDir.Controls.EmbeddedTerminalControl)this.TerminalSurface).ClipboardFailed = Error =>
                    this.ShowNotice(LanguageService.Text("ClipboardError", FriendlyError(Error)), true);
            }
            this.TerminalSurface.Session = Session;
            this.TerminalSurface.ScrollToBottom();
            this.UpdateTerminalScrollBar();
            // The glyph renderer uses explicit fallback families rather than WPF's composite-font fallback.
            ((TerminalDecoder)Session.Decoder).Options.FontFallback = CDir.Controls.EmbeddedTerminalControl.UnicodeFontFallback;
            this.TerminalSurface.FontFamily = new FontFamily("Consolas");
            this.ApplyTerminalTheme();
            this.TerminalSurface.AllowDirectInput = !Session.HasExited;
            this.TerminalExitedHint.Visibility = Session.HasExited ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception Error) when (Error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            this.ShowNotice(LanguageService.Text("TerminalStartError", FriendlyError(Error)), true);
        }
    }

    private void TerminalDisconnected(object? Sender, EventArgs E)
    {
        if (this.WindowClosed || !ReferenceEquals(Sender, this.TerminalSession)) return;
        this.TerminalExitedHint.Visibility = Visibility.Visible;
        if (this.TerminalSurface is not null) this.TerminalSurface.AllowDirectInput = false;
    }

    private void HideTerminal()
    {
        this.SavedTerminalHeight = Math.Max(96, this.TerminalRow.ActualHeight);
        this.TerminalPanel.Visibility = Visibility.Collapsed;
        this.TerminalSplitter.Visibility = Visibility.Collapsed;
        this.TerminalRow.MinHeight = 0;
        this.TerminalRow.Height = new GridLength(0);
        this.TerminalSplitterRow.Height = new GridLength(0);
        this.TerminalToggleButton.ClearValue(Control.BackgroundProperty);
        this.TerminalToggleButton.ClearValue(Control.ForegroundProperty);
        this.TerminalToggleButton.SetValue(System.Windows.Automation.AutomationProperties.ItemStatusProperty, LanguageService.Text("TerminalHidden"));
        if (this.TerminalReturnFocus is UIElement { IsVisible: true } Previous) Previous.Focus();
        else this.FileGrid.Focus();
    }

    private async void RestartTerminalClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (this.TerminalStartupTask is { IsCompleted: false }) return;
        this.ReleaseTerminalSession();
        this.TerminalStartupTask = this.StartTerminalAsync();
        await this.TerminalStartupTask;
        if (this.TerminalPanel.Visibility == Visibility.Visible) this.TerminalSurface?.Focus();
    }

    private void ReleaseTerminalSession()
    {
        if (this.TerminalSurface is not null) this.TerminalSurface.Session = null;
        if (this.TerminalSession is { } Session)
        {
            Session.Disconnected -= this.TerminalDisconnected;
            Session.ViewportResized -= this.TerminalViewportResized;
            this.TerminalSession = null;
            Session.Dispose();
        }
    }

    private void TerminalAreaSizeChanged(object Sender, SizeChangedEventArgs E) => this.ClampTerminalHeight();
    private void ClampTerminalHeight()
    {
        if (this.TerminalPanel is null || this.TerminalPanel.Visibility != Visibility.Visible || this.RightPanel.ActualHeight < 221) return;
        this.TerminalRow.MaxHeight = Math.Max(96, this.RightPanel.ActualHeight - 125);
        if (this.TerminalRow.Height.IsAbsolute && this.TerminalRow.Height.Value > this.TerminalRow.MaxHeight)
            this.TerminalRow.Height = new GridLength(this.TerminalRow.MaxHeight);
    }
}
