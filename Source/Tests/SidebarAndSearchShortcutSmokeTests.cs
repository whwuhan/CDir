using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunSidebarAndSearchShortcutSmokeChecksAsync(List<string> Passed)
    {
        var InitialSidebar = this.Settings.ShowSidebar;
        var InitialSettings = this.Settings.Shortcuts;
        var InitialBindings = new Dictionary<string, string>(this.ShortcutBindings);
        var InitialPath = this.CurrentPath;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Sidebar/search shortcut check failed: " + Name);
            Passed.Add(Name);
        }
        async Task<KeyEventArgs> Press(UIElement Element, Key Key, ModifierKeys Modifiers)
        {
            var Input = new KeyEventArgs(new SmokeKeyboardDevice(Modifiers), PresentationSource.FromVisual(Element), 0, Key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Element.RaiseEvent(Input);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
            return Input;
        }
        try
        {
            this.ShortcutBindings = KeyboardShortcut.Resolve(null);
            this.Settings.ShowSidebar = true;
            this.ApplySidebarVisibility();
            this.ApplyShortcutHints();
            var Width = this.SidebarColumn.Width;
            var Hide = await Press(this.FileGrid, Key.D1, ModifierKeys.Alt);
            Check(Hide.Handled && !this.Settings.ShowSidebar && !this.SidebarPanel.IsVisible,
                "Alt+1 hides the complete sidebar through the actual key route");
            var Show = await Press(this.SearchBox, Key.D1, ModifierKeys.Alt);
            Check(Show.Handled && this.Settings.ShowSidebar && this.SidebarPanel.IsVisible && this.SidebarColumn.Width == Width
                && SamePath(this.CurrentPath, InitialPath),
                "Alt+1 from a text field restores the sidebar width without changing navigation");
            Check(this.ChangeShortcut("ToggleSidebar", "Ctrl+Shift+B"), "The sidebar toggle accepts a personal binding");
            var OldKey = await Press(this.FileGrid, Key.D1, ModifierKeys.Alt);
            Check(!OldKey.Handled && this.Settings.ShowSidebar, "The old sidebar key stops dispatching after reassignment");
            var NewKey = await Press(this.SearchBox, Key.B, ModifierKeys.Control | ModifierKeys.Shift);
            Check(NewKey.Handled && !this.Settings.ShowSidebar, "The customized sidebar shortcut works from the search field");
            await Press(this.FileGrid, Key.B, ModifierKeys.Control | ModifierKeys.Shift);
            FocusManager.SetFocusedElement(this, this.FileGrid);
            var Search = await Press(this.FileGrid, Key.F, ModifierKeys.Control);
            Check(Search.Handled && this.SearchBox.IsFocused,
                "Ctrl+F focuses the upper-right search field through the actual shortcut route");
            var Query = this.SearchBox.Text;
            var Escape = await Press(this.SearchBox, Key.Escape, ModifierKeys.None);
            Check(Escape.Handled && !this.SearchBox.IsFocused && !this.SearchBox.IsKeyboardFocusWithin
                && this.SearchTips.Visibility == Visibility.Collapsed && this.SearchBox.Text == Query
                && ReferenceEquals(FocusManager.GetFocusedElement(this), this.FileGrid),
                "Escape exits search focus, closes tips and preserves the query while returning to the file list"
                + $" (handled={Escape.Handled}, focused={this.SearchBox.IsFocused}, within={this.SearchBox.IsKeyboardFocusWithin}, tips={this.SearchTips.Visibility}, query={this.SearchBox.Text == Query}, target={ReferenceEquals(FocusManager.GetFocusedElement(this), this.FileGrid)})");
        }
        finally
        {
            this.Settings.ShowSidebar = InitialSidebar;
            this.Settings.Shortcuts = InitialSettings;
            this.ShortcutBindings = InitialBindings;
            this.ApplySidebarVisibility();
            this.ApplyShortcutHints();
        }
    }
}
