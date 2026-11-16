using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunStartupResponsivenessSmokeAsync(List<string> Passed)
    {
        void Check(bool Value, string Message)
        {
            if (!Value) throw new InvalidOperationException("Startup responsiveness: " + Message);
            Passed.Add(Message);
        }
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Responsive startup")).FullName;
        var Next = Directory.CreateDirectory(Path.Combine(Root, "Open while sizing")).FullName;
        File.WriteAllText(Path.Combine(Next, "Current.txt"), "Owned startup fixture");
        var SettingsFile = Path.Combine(Root, "Settings.json");
        var Settings = new AppSettings { LastPath = Root, ExitOnClose = true, AutoFitColumnsOnNavigation = true, SidebarTreeLayouts = [] };
        DurableJsonStore.Write(SettingsFile, JsonSerializer.Serialize(Settings));
        var State = new WorkspaceState { Tabs = [new WorkspaceTabState { Settings = Settings,
            SelectedPaths = [SettingsFile], Search = "Old saved search" }] };
        var Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var Probe = new MainWindow(SettingsFile, RestoreWorkspace: true, InitialWorkspace: State)
            { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
        var Page = Probe.ActiveTab!;
        Page.BeforeColumnMeasurementForTests = async () =>
        {
            Started.TrySetResult(!Page.Dispatcher.CheckAccess());
            await Release.Task;
        };
        try
        {
            Check(Page.Opacity == 1 && Page.IsHitTestVisible && !Page.IsInitialContentReady,
                "The first page accepts address and menu input before its directory read finishes");
            Probe.Show();
            Check(await Started.Task.WaitAsync(TimeSpan.FromSeconds(15)),
                "Automatic font measurement runs away from the UI dispatcher");
            Check(Page.IsInitialContentReady && Page.IsVisible && Page.IsHitTestVisible && !Page.Loading
                && Probe.PreferencesMenuItem.IsEnabled && !Probe.WorkspaceRestored.IsCompleted,
                "A drawn startup file panel is usable while automatic width measurement is deliberately held");
            var InputServiced = false;
            await Dispatcher.InvokeAsync(() => InputServiced = true, DispatcherPriority.Input);
            Check(InputServiced, "Input is serviced while the startup font worker is pending");
            // Exercise the real Open guard; a direct Navigate call alone would
            // miss a drawn file panel whose Loading flag still disables actions.
            Page.RaiseEvent(new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(Probe), 0, Key.Down)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Page.FileGrid.SelectedItem = Page.Entries.Single(Entry => Entry.FullPath == Next);
            Page.BeforeColumnMeasurementForTests = null;
            await Page.OpenSelectedAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(Page.CurrentPath == Next && !Page.Loading,
                "Opening a selected directory supersedes startup without waiting for the old width worker");
            Page.SearchBox.Text = "Current";
            await Page.SearchTask;
            var Name = Page.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Name));
            Name.Width = 137;
            Release.TrySetResult();
            await Probe.WorkspaceRestored.WaitAsync(TimeSpan.FromSeconds(15));
            Check(Page.CurrentPath == Next && Page.SearchBox.Text == "Current" && Name.Width.Value == 137
                && Page.Entries.Any(Entry => Entry.Name == "Current.txt"),
                "Late startup sizing and workspace recovery preserve the new folder, search and user width");
            var FrameCalls = WindowAppearance.ShadowFrameApplicationCount;
            // Raise local managed lifecycle events without activating a window,
            // locking the desktop or sending global input to the user's session.
            foreach (var Method in new[] { "OnDeactivated", "OnActivated", "OnDeactivated", "OnActivated" })
                typeof(Window).GetMethod(Method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Probe, [EventArgs.Empty]);
            Check(WindowAppearance.ShadowFrameApplicationCount == FrameCalls && Page.IsHitTestVisible && !Page.Loading,
                "Repeated activation recovery makes no synchronous DWM frame calls and keeps the file panel usable");
        }
        finally
        {
            Release.TrySetResult();
            Page.BeforeColumnMeasurementForTests = null;
            Probe.RequestExit();
            await Probe.WorkspaceRestored.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }
}
