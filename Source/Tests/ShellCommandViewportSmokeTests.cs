using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunShellCommandViewportSmokeAsync(List<string> Passed)
    {
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "ShellCommandViewport")).FullName;
        var Intermediate = Directory.CreateDirectory(Path.Combine(Root, "Intermediate", "Child")).Parent!.FullName;
        var Project = Path.Combine(Root, "UAGame.uproject");
        var Solution = Path.Combine(Root, "UAGame.sln");
        File.WriteAllText(Project, "Owned project fixture");
        File.WriteAllText(Solution, "Owned solution fixture");
        for (var Index = 0; Index < 220; Index++) File.WriteAllText(Path.Combine(Root, $"File-{Index:000}.txt"), "Stable");
        var InitialPath = this.CurrentPath;
        var InitialMode = this.Settings.FileView;
        var InitialSize = this.Settings.FileViewIconSize;
        var InitialSort = this.SortProperty;
        var InitialDirection = this.SortDirection;
        var InitialGroup = this.FileGroupProperty;
        var InitialSizes = this.Settings.CalculateFolderSizes;
        var InitialInvoke = this.InvokeShellCommandForTests;
        var InitialMenuOpen = this.ShellMenuOpen;
        var NameColumn = this.FileGrid.Columns.Single(Column => Column.SortMemberPath == nameof(FileEntry.Name));
        var InitialWidth = NameColumn.Width;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Shell command viewport check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        try
        {
            this.SortProperty = nameof(FileEntry.Name);
            this.SortDirection = System.ComponentModel.ListSortDirection.Ascending;
            this.FileGroupProperty = "";
            this.Settings.CalculateFolderSizes = false;
            await this.NavigateAsync(Root, RecordHistory: false);
            var Current = this.AllVersionNodes().Single(Node => SamePath(Node.FullPath, Root));
            await this.EnsureChildrenAsync(Current);
            Current.IsExpanded = true;
            var Child = Current.Children.Single(Node => SamePath(Node.FullPath, Intermediate));
            await this.EnsureChildrenAsync(Child);
            Child.IsExpanded = true;
            var Source = HwndSource.FromHwnd(new WindowInteropHelper(this.HostWindow).Handle)!;
            using var Menu = new ShellContextMenu(Source.Handle, Root, [Project]);
            IEnumerable<ShellContextMenu.MenuEntry> Flatten(IEnumerable<ShellContextMenu.MenuEntry> Items) =>
                Items.SelectMany(Item => new[] { Item }.Concat(Flatten(Item.Children)));
            var Command = Flatten(Menu.ReadEntries()).First(Item => string.Equals(Menu.DescribeCommand(Item.Id), "properties", StringComparison.OrdinalIgnoreCase));
            var Invocations = 0;
            var MoveSelection = false;
            var SelectionMoves = 0;
            this.InvokeShellCommandForTests = (ActualMenu, Id) =>
            {
                if (!ReferenceEquals(Menu, ActualMenu) || Id != Command.Id || ActualMenu.SelectionPaths.Single() != Project)
                    throw new InvalidOperationException("Unexpected test Shell invocation.");
                File.WriteAllText(Solution, "Generated solution wave " + ++Invocations);
                Directory.SetLastWriteTime(Intermediate, DateTime.Now.AddSeconds(Invocations));
                if (MoveSelection) File.SetLastWriteTime(Project, DateTime.Now.AddDays(SelectionMoves++ % 2 == 0 ? -40 : 2));
                return true;
            };
            foreach (var Mode in new[] { FileViewMode.Details, FileViewMode.LargeIcons, FileViewMode.List, FileViewMode.Tiles })
            {
                AppMain.RecordSmokeProgress("Shell command viewport: " + Mode);
                this.ApplyRememberedFolderView(Mode, 96);
                this.SortProperty = nameof(FileEntry.Name);
                this.ApplySort();
                if (Mode == FileViewMode.Details) NameColumn.Width = 1400;
                await Settle();
                this.SelectPath(Project);
                await Settle();
                var Selected = this.Entries.Single(Entry => Entry.FullPath == Project);
                var View = this.View;
                var ItemsSource = this.ActiveFileItems.ItemsSource;
                var Navigation = this.NavigationVersion;
                var Viewer = FindVisualChild<ScrollViewer>(this.ActiveFileControl)!;
                var Offsets = Mode == FileViewMode.Details ? new[] { 5.125, 190.375, 220.625 } : new[] { 5.125, 37.375 };
                var Cases = Offsets.Select(Offset => (Sort: nameof(FileEntry.Name), Offset))
                    .Concat(new[] { (Sort: nameof(FileEntry.Modified), Offset: 5.125), (Sort: nameof(FileEntry.Modified), Offset: 17.375) });
                foreach (var Case in Cases)
                {
                    MoveSelection = Case.Sort == nameof(FileEntry.Modified);
                    if (this.SortProperty != Case.Sort)
                    {
                        this.SortProperty = Case.Sort;
                        this.ApplySort();
                        this.SelectPath(Project);
                        await Settle();
                    }
                    var Offset = Case.Offset;
                    Viewer.ScrollToVerticalOffset(Offset);
                    Viewer.ScrollToHorizontalOffset(Mode == FileViewMode.List ? Offset : 37.25);
                    await Settle();
                    var Before = new Vector(Viewer.HorizontalOffset, Viewer.VerticalOffset);
                    this.ShellMenuOpen = true;
                    try { await this.ExecuteShellCommandAsync(Menu, Command.Id); }
                    finally { this.ShellMenuOpen = InitialMenuOpen; }
                    await Settle();
                    var After = new Vector(Viewer.HorizontalOffset, Viewer.VerticalOffset);
                    Check((After - Before).Length < 0.01 && Navigation == this.NavigationVersion
                        && ReferenceEquals(View, this.View) && ReferenceEquals(ItemsSource, this.ActiveFileItems.ItemsSource)
                        && this.SelectedEntries().Contains(Selected) && Child.IsExpanded
                        && ReferenceEquals(Child, Current.Children.Single(Node => SamePath(Node.FullPath, Intermediate))),
                        $"{Mode}/{Case.Sort} Shell command completion keeps the current viewport, selection and expanded tree at {Before} (actual {After})");
                    await Task.Delay(700);
                    await Settle();
                    Check((new Vector(Viewer.HorizontalOffset, Viewer.VerticalOffset) - Before).Length < 0.01,
                        Mode + " queued generator notifications do not scroll back to the selected project");
                }
                this.RevealFile(Selected);
                await Settle();
                Check(this.ActiveFileItems.ItemContainerGenerator.ContainerFromItem(Selected) is FrameworkElement { IsVisible: true },
                    Mode + " explicit navigation can still reveal the selected project after passive refresh");
            }
            Check(Invocations == 17, "The real external Shell command completion path is exercised for every viewport case without launching external applications");
        }
        finally
        {
            this.InvokeShellCommandForTests = InitialInvoke;
            this.ShellMenuOpen = InitialMenuOpen;
            NameColumn.Width = InitialWidth;
            this.SortProperty = InitialSort;
            this.SortDirection = InitialDirection;
            this.FileGroupProperty = InitialGroup;
            this.Settings.CalculateFolderSizes = InitialSizes;
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.ApplyRememberedFolderView(InitialMode, InitialSize);
        }
    }
}
