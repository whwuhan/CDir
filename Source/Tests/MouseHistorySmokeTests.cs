using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunMouseHistorySmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialBindings = new Dictionary<string, string>(this.ShortcutBindings);
        var InitialHistory = this.History.ToArray();
        var InitialTreeKeys = this.HistoryTreeKeys.ToArray();
        var InitialHistoryIndex = this.HistoryIndex;
        var InitialTreeKey = this.NavigationTreeKey;
        var InitialTabs = this.HostWindow.Tabs.ToArray();
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "MouseHistory-" + Guid.NewGuid().ToString("N"))).FullName;
        var First = Directory.CreateDirectory(Path.Combine(Fixture, "First")).FullName;
        var Second = Directory.CreateDirectory(Path.Combine(Fixture, "Second")).FullName;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Mouse history check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Press(UIElement Target, MouseButton Button, string Expected)
        {
            var Input = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, Button)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
            Target.RaiseEvent(Input);
            var Deadline = Environment.TickCount64 + 10000;
            while (this.Loading && Environment.TickCount64 < Deadline) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(Input.Handled && !this.Loading && SamePath(this.CurrentPath, Expected),
                Button + " navigates the active tab from " + Target.GetType().Name);
        }
        try
        {
            // Mouse history remains available even when its keyboard shortcuts are unassigned.
            this.ShortcutBindings["Back"] = "";
            this.ShortcutBindings["Forward"] = "";
            await this.NavigateAsync(First);
            await this.NavigateAsync(Second);
            await Press(this.FileGrid, MouseButton.XButton1, First);
            await Press(this.FileGrid, MouseButton.XButton2, Second);
            await Press(this.SearchBox, MouseButton.XButton1, First);
            this.BeginAddressEdit();
            await Press(this.AddressBox, MouseButton.XButton2, Second);
            this.CancelAddressEdit();
            foreach (var (Command, Expected) in new[] { (1, First), (2, Second) })
            {
                var Handle = new System.Windows.Interop.WindowInteropHelper(this.HostWindow).Handle;
                var Result = SendTraySmokeMessage(Handle, 0x319, Handle, new IntPtr(((long)Command | 0x8000) << 16));
                var End = Environment.TickCount64 + 10000;
                while (this.Loading && Environment.TickCount64 < End) await Task.Delay(20);
                Check(Result == (IntPtr)1 && SamePath(this.CurrentPath, Expected),
                    "Mouse-driver browser command navigates the active tab: " + Command);
            }
            foreach (var (Key, Expected) in new[] { (Key.BrowserBack, First), (Key.BrowserForward, Second) })
            {
                var BrowserInput = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(this.FileGrid), 0, Key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                this.FileGrid.RaiseEvent(BrowserInput);
                var End = Environment.TickCount64 + 10000;
                while (this.Loading && Environment.TickCount64 < End) await Task.Delay(20);
                Check(BrowserInput.Handled && SamePath(this.CurrentPath, Expected),
                    "Dedicated browser key navigates the active tab: " + Key);
            }
            var Other = this.HostWindow.AddTab();
            await Other.WaitUntilReadyAsync();
            await Other.NavigateAsync(First);
            await Other.NavigateAsync(Second);
            var Input = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton1)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
            Other.FileGrid.RaiseEvent(Input);
            var Deadline = Environment.TickCount64 + 10000;
            while (Other.Loading && Environment.TickCount64 < Deadline) await Task.Delay(20);
            Check(Input.Handled && SamePath(Other.CurrentPath, First) && SamePath(this.CurrentPath, Second),
                "Mouse Back changes only the active tab's independent history");
            this.HostWindow.CloseTab(Other);
            this.HostWindow.SelectTab(this);
        }
        finally
        {
            foreach (var Tab in this.HostWindow.Tabs.Except(InitialTabs).ToArray()) this.HostWindow.CloseTab(Tab);
            this.HostWindow.SelectTab(this);
            this.ShortcutBindings = InitialBindings;
            this.CancelAddressEdit();
            await this.NavigateAsync(InitialPath, RecordHistory: false, TreeKey: InitialTreeKey);
            this.History.Clear(); this.History.AddRange(InitialHistory);
            this.HistoryTreeKeys.Clear(); this.HistoryTreeKeys.AddRange(InitialTreeKeys);
            this.HistoryIndex = InitialHistoryIndex;
            this.UpdateActions();
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned mouse history fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
