using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunGeneratedFilesRefreshSmokeAsync(List<string> Passed)
    {
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "GeneratedFiles")).FullName;
        var Folder = Directory.CreateDirectory(Path.Combine(Root, "Intermediate")).FullName;
        var Solution = Path.Combine(Root, "UAGame.sln");
        File.WriteAllText(Path.Combine(Root, "UAGame.uproject"), "Owned project fixture");
        File.WriteAllText(Solution, "Initial solution");
        for (var Index = 0; Index < 80; Index++) File.WriteAllText(Path.Combine(Root, $"File-{Index:00}.txt"), "Stable");
        var InitialPath = this.CurrentPath;
        var InitialMode = this.Settings.FileView;
        var InitialSize = this.Settings.FileViewIconSize;
        var InitialSort = this.SortProperty;
        var InitialDirection = this.SortDirection;
        var InitialGroup = this.FileGroupProperty;
        var InitialCalculateSizes = this.Settings.CalculateFolderSizes;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Generated files refresh check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        try
        {
            AppMain.RecordSmokeProgress("Generated files: opening owned fixture");
            this.SortProperty = nameof(FileEntry.Name);
            this.SortDirection = System.ComponentModel.ListSortDirection.Ascending;
            this.FileGroupProperty = "";
            this.Settings.CalculateFolderSizes = false;
            await this.NavigateAsync(Root, RecordHistory: false);
            foreach (var Mode in new[] { FileViewMode.Details, FileViewMode.LargeIcons, FileViewMode.List, FileViewMode.Tiles })
            {
                AppMain.RecordSmokeProgress("Generated files: view " + Mode);
                this.ApplyRememberedFolderView(Mode, 96);
                await Settle();
                var Entries = this.Entries.ToDictionary(Entry => Entry.FullPath);
                var Target = Entries[Solution];
                if (Mode == FileViewMode.Details)
                {
                    // A partial viewport at 150% DPI used to make the logical
                    // virtualizer alternate between neighboring row offsets.
                    FindVisualChild<ScrollViewer>(this.FileGrid)!.ScrollToVerticalOffset(39.125);
                    await Settle();
                }
                this.SelectPath(Solution);
                this.RevealFile(Target);
                await Settle();
                if (Mode == FileViewMode.Details)
                {
                    var RevealedOffset = FindVisualChild<ScrollViewer>(this.FileGrid)!.VerticalOffset;
                    Check(Math.Abs(RevealedOffset - Math.Round(RevealedOffset)) < 0.001,
                        "Revealing a bottom file from a fractional offset settles on a whole logical row");
                }
                var Container = (FrameworkElement)this.ActiveFileItems.ItemContainerGenerator.ContainerFromItem(Target)!;
                var Icon = FindVisualChild<ShellIcon>(Container)!;
                var Position = Container.TranslatePoint(new Point(), this.ActiveFileControl);
                var Size = Icon.RenderSize;
                var Viewer = FindVisualChild<ScrollViewer>(this.ActiveFileControl)!;
                var Offset = new Vector(Viewer.HorizontalOffset, Viewer.VerticalOffset);
                var Changes = 0;
                void Changed(object? Sender, NotifyCollectionChangedEventArgs E) => Changes++;
                var Source = (INotifyCollectionChanged)this.Entries;
                Source.CollectionChanged += Changed;
                try
                {
                    for (var Wave = 0; Wave < 4; Wave++)
                    {
                        AppMain.RecordSmokeProgress($"Generated files: {Mode} wave {Wave}");
                        File.WriteAllText(Solution, new string('x', 100 + Wave));
                        var Modified = DateTime.Now.AddMinutes(Wave + 1);
                        File.SetLastWriteTime(Solution, Modified);
                        Directory.SetLastWriteTime(Folder, Modified);
                        await this.RefreshCurrentFolderAsync(false);
                        await Settle();
                        Check(Changes == 0 && this.Entries.All(Entry => ReferenceEquals(Entries[Entry.FullPath], Entry))
                            && ReferenceEquals(Container, this.ActiveFileItems.ItemContainerGenerator.ContainerFromItem(Target))
                            && ReferenceEquals(Icon, FindVisualChild<ShellIcon>(Container))
                            && (Container.TranslatePoint(new Point(), this.ActiveFileControl) - Position).Length < 0.5
                            && Icon.RenderSize == Size && this.SelectedEntries().Contains(Target)
                            && (new Vector(Viewer.HorizontalOffset, Viewer.VerticalOffset) - Offset).Length < 0.5,
                            $"{Mode} generation wave {Wave} preserves file/folder objects, icon geometry, selection and scroll without collection churn");
                        Check(Target.Size == 100 + Wave && Target.Modified == File.GetLastWriteTime(Solution)
                            && Entries[Folder].Modified == Directory.GetLastWriteTime(Folder),
                            $"{Mode} generation wave {Wave} updates file sizes and file/folder timestamps in place");
                    }
                    await Task.Delay(900);
                    await Settle();
                    Check(Changes == 0 && ReferenceEquals(Container, this.ActiveFileItems.ItemContainerGenerator.ContainerFromItem(Target)),
                        Mode + " queued real watcher notifications preserve the generated file container");
                }
                finally { Source.CollectionChanged -= Changed; }
            }
            this.ApplyRememberedFolderView(FileViewMode.Details, 0);
            AppMain.RecordSmokeProgress("Generated files: metadata sorting and grouping");
            this.SortProperty = nameof(FileEntry.Modified);
            this.SortDirection = System.ComponentModel.ListSortDirection.Ascending;
            this.ApplySort();
            File.SetLastWriteTime(Solution, DateTime.Now.AddDays(-40));
            await this.RefreshCurrentFolderAsync(false);
            Check(this.View!.Cast<FileEntry>().First(Entry => !Entry.IsDirectory).FullPath == Solution,
                "Modified sorting moves an older generated file ahead of other files");
            File.SetLastWriteTime(Solution, DateTime.Now.AddDays(2));
            await this.RefreshCurrentFolderAsync(false);
            Check(this.View!.Cast<FileEntry>().Last().FullPath == Solution, "Modified sorting follows changed metadata after an incremental refresh");
            this.SortProperty = nameof(FileEntry.Size);
            this.ApplySort();
            File.WriteAllText(Solution, "x");
            await this.RefreshCurrentFolderAsync(false);
            Check(this.View!.Cast<FileEntry>().First(Entry => !Entry.IsDirectory).FullPath == Solution,
                "Size sorting moves a shortened generated file ahead of other files");
            File.WriteAllText(Solution, new string('x', 2000));
            await this.RefreshCurrentFolderAsync(false);
            Check(this.View!.Cast<FileEntry>().Last().FullPath == Solution, "Size sorting follows generated file growth after an incremental refresh");
            this.FileGroupProperty = nameof(FileEntry.Modified);
            this.ApplySort();
            File.SetLastWriteTime(Solution, DateTime.Now.AddDays(-10));
            await this.RefreshCurrentFolderAsync(false);
            var Refreshed = this.Entries.Single(Entry => Entry.FullPath == Solution);
            Check(Refreshed.ViewGroup == this.GroupLabel(Refreshed, DateTime.Today)
                && this.View!.Groups!.Cast<System.Windows.Data.CollectionViewGroup>().Any(Group => Equals(Group.Name, Refreshed.ViewGroup) && Group.Items.Contains(Refreshed)),
                "Date grouping moves a generated file to its updated group");
            File.WriteAllText(Path.Combine(Root, "New generated.vcxproj"), "New project");
            File.Delete(Solution);
            await this.RefreshCurrentFolderAsync(false);
            var NewEntry = this.Entries.SingleOrDefault(Entry => Entry.Name == "New generated.vcxproj");
            Check(NewEntry is not null && !this.Entries.Contains(Refreshed)
                && NewEntry.ViewGroup == this.GroupLabel(NewEntry, DateTime.Today),
                "Incremental generation refresh groups new files correctly and removes deleted files");
            this.FileGroupProperty = "";
            this.SortProperty = nameof(FileEntry.Modified);
            this.ApplyRememberedFolderView(FileViewMode.LargeIcons, 96);
            this.ApplySort();
            this.RevealFile(NewEntry!);
            await Settle();
            foreach (var Days in new[] { -40, 2 })
            {
                File.SetLastWriteTime(NewEntry!.FullPath, DateTime.Now.AddDays(Days));
                await this.RefreshCurrentFolderAsync(false);
                await Settle();
                var Edge = Days < 0 ? this.View!.Cast<FileEntry>().First(Entry => !Entry.IsDirectory) : this.View!.Cast<FileEntry>().Last();
                this.RevealFile(NewEntry);
                await Settle();
                Check(ReferenceEquals(Edge, NewEntry)
                    && this.FileTiles.ItemContainerGenerator.ContainerFromItem(NewEntry) is ListBoxItem Container
                    && ReferenceEquals(Container.DataContext, NewEntry) && FindVisualChild<ShellIcon>(Container)?.Path == NewEntry.FullPath,
                    "Icon virtualization safely moves a generated item between visible and offscreen sorted positions: " + Days);
            }
        }
        finally
        {
            this.SortProperty = InitialSort;
            this.SortDirection = InitialDirection;
            this.FileGroupProperty = InitialGroup;
            this.Settings.CalculateFolderSizes = InitialCalculateSizes;
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            this.ApplyRememberedFolderView(InitialMode, InitialSize);
        }
    }
}
