using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunMainPanelNavigationSmokeChecksAsync(List<string> Passed)
    {
        await this.RunFileListScrollTailSmokeChecksAsync(Passed);
        var Initial = this.CurrentPath;
        var Fixture = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Main panel navigation " + Guid.NewGuid().ToString("N"))).FullName;
        var Child = Directory.CreateDirectory(Path.Combine(Fixture, "Child 中文")).FullName;
        var Nested = Directory.CreateDirectory(Path.Combine(Child, "Nested")).FullName;
        File.WriteAllText(Path.Combine(Nested, "Deep.txt"), "deep");
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Main-panel navigation UI check failed: " + Name);
            Passed.Add(Name);
        }
        try
        {
            Check(await this.NavigateAsync(Fixture), "Open the main-panel navigation fixture");
            var Entry = this.Entries.Single();
            var BeforeVersion = this.NavigationVersion;
            var BeforeHistory = this.History.Count;
            this.FileGrid.SelectedItem = Entry;
            this.FileGrid.UpdateLayout();
            var Row = (DataGridRow)this.FileGrid.ItemContainerGenerator.ContainerFromItem(Entry);
            Check(this.FileGrid.SelectedItem == Entry && SamePath(this.CurrentPath, Fixture)
                && this.NavigationVersion == BeforeVersion && this.History.Count == BeforeHistory && this.Entries.Count == 1,
                "Selecting a folder leaves the directory and history unchanged without showing descendants");
            Check(FindVisualChild<ToggleButton>(Row) is null, "Main-panel folder rows have no expansion arrows");
            var Click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Control.MouseDoubleClickEvent, Source = Row };
            this.FileGrid.RaiseEvent(Click);
            var Deadline = Environment.TickCount64 + 10000;
            while (this.Loading && Environment.TickCount64 < Deadline) await Task.Delay(20);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(Click.Handled && !this.Loading && SamePath(this.CurrentPath, Child) && SamePath(this.AddressBox.Text, Child)
                && this.NavigationVersion == BeforeVersion + 1 && this.History.Count == BeforeHistory + 1,
                "Double-clicking a main-panel folder enters it once and updates the address and history");
            Check(this.Entries.Count == 1 && SamePath(this.Entries[0].FullPath, Nested)
                && this.Watcher is { IncludeSubdirectories: false }, "The main panel only lists immediate children and watches the current directory");
            await this.BackAsync();
            Check(SamePath(this.CurrentPath, Fixture) && this.Entries.Count == 1 && SamePath(this.Entries[0].FullPath, Child),
                "Going back restores the flat parent-directory listing");
        }
        finally
        {
            await this.NavigateAsync(Initial);
            this.RefreshTimer.Stop();
            if (!AppMain.IsInsideSmokeScope(Fixture) || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned main-panel navigation fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
