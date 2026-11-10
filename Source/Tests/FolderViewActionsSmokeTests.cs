using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunFolderViewActionsSmokeAsync(List<string> Passed)
    {
        var Root = Directory.CreateDirectory(Path.Combine(AppMain.SmokeRoot!, "Folder View Actions")).FullName;
        var Deep = Directory.CreateDirectory(Path.Combine(Root, "Child", "Grandchild", "Deepest")).FullName;
        var Sibling = Directory.CreateDirectory(Root + " Sibling").FullName;
        var Configuration = Path.Combine(AppMain.SmokeRoot!, "RecursiveViews.json");
        File.WriteAllText(Configuration, JsonSerializer.Serialize(new AppSettings { LastPath = Root, ExitOnClose = true }));
        File.WriteAllText(Path.Combine(Root, "Alpha.txt"), "small");
        File.WriteAllBytes(Path.Combine(Root, "Beta.bin"), new byte[2048]);
        MainWindow? Probe = null;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Folder view action failed: " + Name);
            Passed.Add(Name);
        }
        async Task Open()
        {
            Probe = new MainWindow(Configuration, RestoreWorkspace: true) { Owner = this.HostWindow, ShowActivated = false, ShowInTaskbar = false };
            Probe.Show(); await Probe.WorkspaceRestored; await Probe.ActiveTab!.WaitUntilReadyAsync();
        }
        async Task Settle() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); Probe!.UpdateLayout(); }
        try
        {
            await Open(); var Page = Probe!.ActiveTab!;
            await Page.NavigateAsync(Deep); Page.ChangeFileView(FileViewMode.Content);
            await Page.NavigateAsync(Sibling); Page.ChangeFileView(FileViewMode.SmallIcons);
            await Page.NavigateAsync(Root); Page.ChangeFileView(FileViewMode.LargeIcons);
            Page.ColumnMenuClick(Page.ColumnMenuButton, new RoutedEventArgs(Button.ClickEvent));
            var Menu = Page.ColumnMenuButton.ContextMenu!;
            var Apply = Menu.Items.OfType<MenuItem>().Single(Item => Equals(Item.Tag, "ApplyViewToSubfolders"));
            var Reset = Menu.Items.OfType<MenuItem>().Single(Item => Equals(Item.Tag, "ResetFolderViews"));
            Check(Menu.Items[Menu.Items.IndexOf(Apply) - 1] is Separator && Menu.Items[Menu.Items.IndexOf(Reset) + 1] is Separator,
                "Recursive Apply and Reset actions occupy their own separated dropdown section");
            Apply.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Menu.IsOpen = false;
            Check(Probe.FolderViewFor(Deep) == FileViewMode.LargeIcons && Probe.FolderViewFor(Sibling) == FileViewMode.SmallIcons,
                "Applying the view replaces existing deepest-folder choices and preserves similarly named sibling folders");
            var Future = Path.Combine(Deep, "Created Later", "Level Six");
            Check(Probe.FolderViewFor(Future) == FileViewMode.LargeIcons,
                "Recursive inheritance reaches arbitrary depth and future subfolders without filesystem traversal");
            Probe.RegisterFolderViews(new AppSettings { FolderViews = new() { [Deep] = FileViewMode.Content } });
            Check(Probe.FolderViewFor(Deep) == FileViewMode.LargeIcons,
                "Late workspace-tab snapshots cannot resurrect overwritten descendant view choices");
            await Page.NavigateAsync(Deep);
            Check(Page.Settings.FileView == FileViewMode.LargeIcons, "Navigating to an existing deep descendant uses the recursively applied view");
            await Page.NavigateAsync(Root); Page.ChangeFileView(FileViewMode.Details); await Settle();
            var Source = HwndSource.FromHwnd(new WindowInteropHelper(Probe).Handle)!;
            using (var Background = new ShellContextMenu(Source.Handle, Root, [], ViewSettings: new(Page.Settings.FileView, "Name", false, "", false)))
            {
                var Top = Background.ReadEntries(true);
                var Verbs = Top.Take(4).Select(Entry => Entry.Children.Count > 0 ? Background.DescribeCommand(Entry.Children[0].Id) : Background.DescribeCommand(Entry.Id)).ToArray();
                Check(Verbs[0] == "CDir.View.ExtraLargeIcons" && Verbs[1] == "CDir.Sort.Name" && Verbs[2] == "CDir.Group.Name" && Verbs[3] == "CDir.Refresh",
                    "The native background menu begins with View, Sort By, Group By, and Refresh in Windows order");
                Check(Top[0].Children.Count == 8 && Top[4].IsSeparator && Background.DescribeCommand(Top[5].Id) == "CDir.Customize" && Top[6].IsSeparator,
                    "The native menu includes all view sizes and Customize This Folder between the correct separators");
                if (AppMain.ScreenshotPath is { } Output)
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!, "BackgroundMenuCommands.json"),
                        JsonSerializer.Serialize(Top.Select(Entry => new { Entry.Text, Entry.Id, Entry.Enabled, Entry.IsSeparator, Verb = Background.DescribeCommand(Entry.Id) }), new JsonSerializerOptions { WriteIndented = true }));
                var Paste = Top.Single(Entry => Background.DescribeCommand(Entry.Id) is "CDir.Paste" or "paste");
                Check(Background.DescribeCommand(Paste.Id) != "CDir.Paste" || !Paste.Enabled && Top.ToList().IndexOf(Paste) == 7,
                    $"Blank-space menus retain a disabled Paste action even when the native background provider omits it (verb={Background.DescribeCommand(Paste.Id)}, enabled={Paste.Enabled}, index={Top.ToList().IndexOf(Paste)})");
                var Large = Top[0].Children.Single(Entry => Background.DescribeCommand(Entry.Id) == "CDir.View.LargeIcons");
                await Page.ExecuteShellCommandAsync(Background, Large.Id);
                Check(Page.Settings.FileView == FileViewMode.LargeIcons, "Native View commands update the actual main panel and saved folder preference");
            }
            await Page.ExecuteBackgroundCommandAsync("CDir.Sort.Size");
            await Page.ExecuteBackgroundCommandAsync("CDir.Sort.Descending");
            var Files = Page.View!.Cast<FileEntry>().Where(Entry => !Entry.IsDirectory).ToArray();
            Check(Files.Zip(Files.Skip(1)).All(Pair => Pair.First.Size >= Pair.Second.Size), "Native sorting commands change the real item order");
            await Page.ExecuteBackgroundCommandAsync("CDir.Group.TypeName"); await Settle();
            var Panel = FindVisualChild<FileTilePanel>(Page.FileTiles)!;
            Check(Panel.Grouped && Page.View!.Cast<FileEntry>().All(Entry => Entry.ViewGroup.Length > 0)
                && Panel.ItemBounds(0).Top >= 28, "Icon grouping renders real group headers and reserves their layout space");
            Page.SearchBox.Text = "/file"; await Page.WaitForSearchSmokeAsync(); await Settle();
            Check(Page.Settings.FileView == FileViewMode.LargeIcons && FindVisualChild<FileTilePanel>(Page.FileTiles)!.Grouped,
                "Searching preserves the current thumbnail view and grouping");
            Page.SearchBox.Clear(); await Page.WaitForSearchSmokeAsync();
            Page.ChangeFileView(FileViewMode.Details); await Settle();
            Check(Page.View!.Groups?.Count >= 2 && Page.View!.GroupDescriptions.Count == 1,
                "Switching to Details displays the same grouping through DataGrid row groups");
            await Page.ExecuteBackgroundCommandAsync("CDir.Group.None"); await Settle();
            Check(Page.View!.GroupDescriptions.Count == 0 && Page.FileGroupProperty.Length == 0, "None restores the ungrouped item layout");
            await Page.ExecuteBackgroundCommandAsync("CDir.Refresh");
            Check(Page.Entries.Any(Entry => Entry.Name == "Alpha.txt"), "The native Refresh command refreshes the current CDir folder");
            var ReadClipboard = Page.ReadClipboardFiles;
            try
            {
                await Page.NavigateAsync(Sibling);
                var Data = new DataObject();
                Data.SetData(DataFormats.FileDrop, new[] { Path.Combine(Root, "Alpha.txt") });
                Page.ReadClipboardFiles = () => Data;
                Check(Page.CanPasteBackground(), "Background paste availability reflects the real supported file clipboard format");
                await Page.ExecuteBackgroundCommandAsync("CDir.Paste");
                Check(File.Exists(Path.Combine(Sibling, "Alpha.txt")), "The background Paste command performs the owned file copy in the current folder");
                await Page.NavigateAsync(Root);
            }
            finally { Page.ReadClipboardFiles = ReadClipboard; }
            Page.HostWindow.ApplyFolderViewRecursively(Root, FileViewMode.Tiles); Page.SaveSettings();
            Probe.PersistWorkspaceNow(); Probe.RequestExit(); Probe = null;
            await Open(); Page = Probe!.ActiveTab!;
            Check(Probe.FolderViewFor(Deep) == FileViewMode.Tiles && Probe.FolderViewFor(Future) == FileViewMode.Tiles,
                "Recursive folder view rules persist through application restart at every depth");
            Page.ColumnMenuClick(Page.ColumnMenuButton, new RoutedEventArgs(Button.ClickEvent));
            Menu = Page.ColumnMenuButton.ContextMenu!;
            Menu.Items.OfType<MenuItem>().Single(Item => Equals(Item.Tag, "ResetFolderViews")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Menu.IsOpen = false;
            Check(Page.IsDetailsView && Probe.FolderViewFor(Deep) == FileViewMode.Details && Probe.FolderViewFor(Future) == FileViewMode.Details
                && Probe.FolderViewFor(Sibling) == FileViewMode.SmallIcons,
                "Reset recursively restores Details without affecting sibling folders");
        }
        finally { Probe?.RequestExit(); }
    }
}
