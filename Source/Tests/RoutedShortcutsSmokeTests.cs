using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunRoutedShortcutSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialSidebar = this.Settings.ShowSidebar;
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "RoutedKeys-" + Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(Fixture, "Folder"));
        File.WriteAllText(Path.Combine(Fixture, "File.txt"), "Shortcut routing fixture");
        var InitialTabs = this.HostWindow.Tabs.ToArray();
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Routed shortcut check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            var Deadline = Environment.TickCount64 + 15000;
            while (this.Loading && Environment.TickCount64 < Deadline) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
            Check(!this.Loading, "The shortcut completes its owned navigation");
        }
        try
        {
            await this.NavigateAsync(Fixture);
            // File mutation commands are exercised while busy so routing can be checked without dialogs,
            // launching a document, or consuming the user's clipboard. Their operations have separate tests.
            foreach (var Definition in KeyboardShortcut.All.Where(Item => !Item.TerminalOnly
                && Item.Id is not ("ToggleTerminal" or "CopyCurrentFolderPath" or "NewWindow")))
            {
                this.CancelAddressEdit();
                this.FileGrid.SelectedItems.Clear();
                this.OperationBusy = true;
                Check(KeyboardShortcut.TryParse(this.ShortcutBindings[Definition.Id], out var Key, out var Modifiers),
                    "A configured shortcut parses for the actual WPF route: " + Definition.Id);
                var Input = new KeyEventArgs(new SmokeKeyboardDevice(Modifiers), PresentationSource.FromVisual(this.FileGrid), 0, Key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                this.FileGrid.RaiseEvent(Input);
                Check(Input.Handled, "The main window dispatches the configured shortcut from the file list: " + Definition.Id);
                if (Definition.Id == "NewTab") await this.HostWindow.ActiveTab!.WaitUntilReadyAsync();
                await Settle();
            }
            this.OperationBusy = false;
            await this.NavigateAsync(Fixture, RecordHistory: false);
            this.FileGrid.SelectedItems.Clear();
            var SelectAll = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control), PresentationSource.FromVisual(this.FileGrid), 0, Key.A)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            this.FileGrid.RaiseEvent(SelectAll);
            Check(SelectAll.Handled && this.FileGrid.SelectedItems.Count == 2,
                "Ctrl+A selects the actual files and folders through the real main-window route");
            foreach (var Target in new UIElement[] { this.SearchBox, this.AddressBox, this.SidebarToggleButton, this.HostWindow.TabBar })
            {
                this.FileGrid.SelectedItems.Clear();
                if (ReferenceEquals(Target, this.AddressBox)) this.BeginAddressEdit();
                var Input = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control), PresentationSource.FromVisual(Target), 0, Key.A)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                Target.RaiseEvent(Input);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Check(Input.Handled && this.FileGrid.SelectedItems.Count == 2 && !this.AddressEditing
                    && ReferenceEquals(FocusManager.GetFocusedElement(this), this.FileGrid),
                    "Ctrl+A returns to the file panel and selects visible items from " + Target.GetType().Name);
            }
            this.SearchBox.Text = ".txt";
            await this.WaitForSearchSmokeAsync();
            this.FileGrid.SelectedItems.Clear();
            var FilterSelectAll = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control), PresentationSource.FromVisual(this.SearchBox), 0, Key.A)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            this.SearchBox.RaiseEvent(FilterSelectAll);
            Check(FilterSelectAll.Handled && this.SearchBox.Text == ".txt" && this.FileGrid.SelectedItems.Count == this.FileGrid.Items.Count
                && this.FileGrid.Items.Count > 0, "Ctrl+A preserves the search filter and selects exactly the visible results");
            this.HostWindow.HelpMenuButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var MenuSelectAll = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control), PresentationSource.FromVisual(this.HostWindow.AboutMenuItem), 0, Key.A)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            this.FileGrid.SelectedItems.Clear();
            this.HostWindow.AboutMenuItem.RaiseEvent(MenuSelectAll);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(MenuSelectAll.Handled && !this.HostWindow.HelpMenu.IsOpen && this.FileGrid.SelectedItems.Count == this.FileGrid.Items.Count,
                "Ctrl+A closes a main-window dropdown and selects the filtered file panel");
            this.SearchBox.Clear();
            await this.WaitForSearchSmokeAsync();
            foreach (var Key in new[] { Key.C, Key.V })
            {
                var Input = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control), PresentationSource.FromVisual(this.SearchBox), 0, Key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                this.SearchBox.RaiseEvent(Input);
                Check(!Input.Handled, "Native text-field editing retains its Windows shortcut: Ctrl+" + Key);
            }
        }
        finally
        {
            this.OperationBusy = false;
            this.Settings.ShowSidebar = InitialSidebar;
            this.ApplySidebarVisibility();
            this.CancelAddressEdit();
            foreach (var Tab in this.HostWindow.Tabs.Except(InitialTabs).ToArray()) this.HostWindow.CloseTab(Tab);
            this.HostWindow.SelectTab(this);
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned shortcut fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
