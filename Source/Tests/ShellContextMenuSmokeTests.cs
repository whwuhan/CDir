using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using CDir.Localization;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunShellContextMenuSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = Path.Combine(Path.GetTempPath(), "Menu-" + FixtureId[..8]);
        var Folder = Directory.CreateDirectory(Path.Combine(Fixture, "Folder 中文 With Spaces")).FullName;
        var FilePath = Path.Combine(Folder, "Document 中文.txt");
        var SecondFile = Path.Combine(Folder, "Second.txt");
        var Subfolder = Directory.CreateDirectory(Path.Combine(Folder, "Subfolder")).FullName;
        var OtherFolder = Directory.CreateDirectory(Path.Combine(Fixture, "Other")).FullName;
        var OtherFile = Path.Combine(OtherFolder, "Another.txt");
        File.WriteAllText(FilePath, "unchanged");
        File.WriteAllText(SecondFile, "second unchanged file");
        File.WriteAllText(OtherFile, "another unchanged file");
        var Marker = Path.Combine(Fixture, ".CDirShellMenuOwner");
        File.WriteAllText(Marker, FixtureId);
        var Reports = new List<object>();
        var Source = HwndSource.FromHwnd(new WindowInteropHelper(this.HostWindow).Handle)!;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Windows Shell context menu UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        static IEnumerable<ShellContextMenu.MenuEntry> Flatten(IEnumerable<ShellContextMenu.MenuEntry> Entries) =>
            Entries.SelectMany(Entry => new[] { Entry }.Concat(Flatten(Entry.Children)));
        bool DismissMenu(Func<uint> Show)
        {
            var WasNativeMenuVisible = false;
            var Ticks = 0;
            IntPtr ObserveMenuLoop(IntPtr Window, int Message, IntPtr WParam, IntPtr LParam, ref bool Handled)
            {
                // WM_ENTERMENULOOP with a nonzero wParam identifies TrackPopupMenu.
                // Observe entry synchronously: an unrelated desktop click can dismiss
                // the inactive owned popup before the first polling timer fires.
                if (Message == 0x211 && WParam != IntPtr.Zero) WasNativeMenuVisible = true;
                return IntPtr.Zero;
            }
            NativeMenuTimerCallback Cancel = (_, _, _, _) =>
            {
                var State = new ShellSmokeGuiInfo { Size = (uint)Marshal.SizeOf<ShellSmokeGuiInfo>() };
                var Visible = GetShellSmokeGuiInfo(GetShellSmokeThreadId(), ref State)
                    && State.MenuOwner == Source.Handle && (State.Flags & 4) != 0;
                WasNativeMenuVisible |= Visible;
                // Shell extensions may initialize before TrackPopupMenu begins. Only
                // dismiss the owned menu after observing it, with a bounded fallback.
                if (Visible || ++Ticks >= 40) EndShellSmokeMenu();
            };
            var Timer = SetShellSmokeTimer(Source.Handle, 0xCD17, 250, Cancel);
            if (Timer == 0) throw new System.ComponentModel.Win32Exception();
            Source.AddHook(ObserveMenuLoop);
            try { Check(Show() == 0, "Dismissing an owned native popup returns no command"); }
            finally { Source.RemoveHook(ObserveMenuLoop); KillShellSmokeTimer(Source.Handle, Timer); GC.KeepAlive(Cancel); }
            return WasNativeMenuVisible;
        }
        try
        {
            Check(this.FileGrid.ContextMenu.Items.Count == 0, "The file list contains no CDir application menu items or fallback menu");
            var Cases = new (string Name, string Current, string[] Paths)[]
            {
                ("File", Folder, [FilePath]), ("Folder", Fixture, [Folder]),
                ("Multiple files", Folder, [FilePath, SecondFile]),
                ("Mixed files and folders", Folder, [FilePath, Subfolder]),
                ("Search results in different directories", Fixture, [FilePath, OtherFile]),
                ("Folder background", Folder, [])
            };
            foreach (var Case in Cases)
            {
                foreach (var Extended in new[] { false, true })
                {
                    using var Menu = new ShellContextMenu(Source.Handle, Case.Current, Case.Paths, Extended);
                    // Exercise a cold first popup before any inspection initializes a dynamic submenu.
                    Check(DismissMenu(() => Menu.Show(Source, this.FileGrid.PointToScreen(new Point(24, 32)))),
                        Case.Name + ": the first native popup opens without a warm-up click or submenu inspection");
                    var MenuEntries = Menu.ReadEntries(InitializeSubmenus: Case.Paths.Length == 0);
                    var Entries = Flatten(MenuEntries).ToArray();
                    var Verbs = Entries.Select(Entry => Menu.DescribeCommand(Entry.Id)).Where(Verb => !string.IsNullOrEmpty(Verb)).ToArray();
                    Reports.Add(new { Case.Name, Extended, Case.Paths, Entries = MenuEntries, Verbs });
                    Check(Entries.Any(Entry => !Entry.IsSeparator && Entry.Text.Length > 0), Case.Name + ": Windows supplies the complete " + (Extended ? "Shift " : "") + "menu");
                    if (Case.Paths.Length > 0)
                        Check(new[] { "cut", "copy", "delete", "properties" }.All(Verb => Verbs.Contains(Verb, StringComparer.OrdinalIgnoreCase)),
                            Case.Name + ": standard file operations come from the Shell with canonical command IDs");
                    else
                        Check(Verbs.Contains("NewFolder", StringComparer.OrdinalIgnoreCase)
                            && !Verbs.Contains("delete", StringComparer.OrdinalIgnoreCase),
                            Case.Name + ": the background menu includes New Folder and does not target the current folder for deletion");
                    Check(!Menu.Invoke(0) && !Menu.Invoke(uint.MaxValue) && Menu.DescribeCommand(0) is null,
                        Case.Name + ": cancelled and invalid command IDs cannot execute a Shell command");
                    Check(DismissMenu(() => Menu.Show(Source, this.FileGrid.PointToScreen(new Point(24, 32)))),
                        Case.Name + ": the real native popup is owned by CDir and can be dismissed without input injection");
                }
            }
            await this.NavigateAsync(Folder);
            await this.RunShellCutClipboardSmokeChecksAsync(Passed, Folder, [FilePath, SecondFile], Subfolder);
            this.FileGrid.SelectedItems.Clear();
            // Shell menu COM calls pump messages. A watcher tick queued by the cut
            // fixture can otherwise start loading between readiness and invocation.
            var Watcher = this.Watcher;
            var WatcherEnabled = Watcher?.EnableRaisingEvents == true;
            if (Watcher is not null) Watcher.EnableRaisingEvents = false;
            this.RefreshTimer.Stop();
            try
            {
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                var NewFolderReady = Environment.TickCount64 + 10000;
                while ((this.Loading || this.OperationBusy) && Environment.TickCount64 < NewFolderReady) await Task.Delay(20);
                this.RefreshTimer.Stop();
                // The fixture's earlier return navigation can lose a race with the
                // cut destination refresh. Establish the test target explicitly.
                await this.NavigateAsync(Folder, RecordHistory: false);
                while (this.Loading && Environment.TickCount64 < NewFolderReady) await Task.Delay(20);
                this.RefreshTimer.Stop();
                Check(!this.Loading && !this.OperationBusy && SamePath(this.CurrentPath, Folder),
                    $"The owned New Folder fixture is ready after cut cleanup (loading={this.Loading}, busy={this.OperationBusy}, path={this.CurrentPath})");
                using var Menu = new ShellContextMenu(Source.Handle, Folder, []);
                var NewFolder = Flatten(Menu.ReadEntries(InitializeSubmenus: true)).Single(Entry => string.Equals(Menu.DescribeCommand(Entry.Id), "NewFolder", StringComparison.OrdinalIgnoreCase));
                var Existing = Directory.GetDirectories(Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
                await this.ExecuteShellCommandAsync(Menu, NewFolder.Id);
                var Deadline = Environment.TickCount64 + 10000;
                while (Directory.GetDirectories(Folder).All(Existing.Contains) && Environment.TickCount64 < Deadline) await Task.Delay(20);
                var Created = Directory.GetDirectories(Folder).Where(Path => !Existing.Contains(Path)).ToArray();
                Check(Created.Length == 1 && SamePath(Path.GetDirectoryName(Created[0])!, Folder),
                    "The native New Folder command creates a folder inside the current directory");
                this.CancelInlineRename();
                await this.RefreshAsync();
                Check(this.Entries.Any(Entry => SamePath(Entry.FullPath, Created[0])) && SamePath(this.CurrentPath, Folder),
                    "The new folder appears in the main panel without changing the current location");
            }
            finally { if (WatcherEnabled && ReferenceEquals(Watcher, this.Watcher)) Watcher!.EnableRaisingEvents = true; }
            this.SelectPath(FilePath);
            using (var Menu = new ShellContextMenu(Source.Handle, Folder, [FilePath]))
            {
                var Rename = Flatten(Menu.ReadEntries()).Single(Entry => string.Equals(Menu.DescribeCommand(Entry.Id), "rename", StringComparison.OrdinalIgnoreCase));
                await this.AcceptSmokeInlineRenameAsync(() => this.ExecuteShellCommandAsync(Menu, Rename.Id), "Cancelled.txt", Confirm: false);
                Check(File.Exists(FilePath) && !File.Exists(Path.Combine(Folder, "Cancelled.txt")), "The native Rename item cancels without modifying the original file");
                await this.AcceptSmokeInlineRenameAsync(() => this.ExecuteShellCommandAsync(Menu, Rename.Id), "Renamed 中文.txt");
                var Renamed = Path.Combine(Folder, "Renamed 中文.txt");
                Check(!File.Exists(FilePath) && File.ReadAllText(Renamed) == "unchanged", "The native Rename item dispatches correctly for Unicode filenames");
                File.Move(Renamed, FilePath);
                await this.RefreshAsync();
            }
            var BatchFolder = Directory.CreateDirectory(Path.Combine(Fixture, "Batch")).FullName;
            var BatchPaths = new[] { Path.Combine(BatchFolder, "One.txt"), Path.Combine(BatchFolder, "Two.txt") };
            foreach (var Path in BatchPaths) File.WriteAllText(Path, "owned batch rename");
            Check(await this.RunOperationAsync(LanguageService.Text("Renaming"),
                () => ShellContextMenu.Rename(Source.Handle, BatchPaths, "Batch 中文.txt"),
                LanguageService.Text("RenameComplete"), RequiresSta: true), "The native batch rename operation completes");
            var BatchNames = Directory.GetFiles(BatchFolder);
            Check(BatchPaths.All(Path => !File.Exists(Path)) && BatchNames.Length == 2
                && BatchNames.All(Path => System.IO.Path.GetFileName(Path).StartsWith("Batch 中文", StringComparison.Ordinal)
                    && File.ReadAllText(Path) == "owned batch rename"),
                "Windows Shell batch rename assigns distinct numbered names and preserves file contents");
            // Let the refreshed rows and native modal-operation cleanup settle
            // before asking the view to position a keyboard context menu.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            this.UpdateLayout();
            this.SelectPath(FilePath);
            this.FileGrid.SelectedItems.Add(this.Entries.Single(Entry => SamePath(Entry.FullPath, SecondFile)));
            var ReadyDeadline = Environment.TickCount64 + 10000;
            while ((this.Loading || this.OperationBusy) && Environment.TickCount64 < ReadyDeadline) await Task.Delay(20);
            Check(!this.Loading && !this.OperationBusy && !this.ShellMenuOpen,
                "The refreshed file panel is ready before requesting its keyboard context menu");
            var Selected = this.SelectedEntries().Select(Entry => Entry.FullPath).ToArray();
            var BeforeHistory = this.History.ToArray();
            var BeforeVersion = this.NavigationVersion;
            var Opening = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [this.FileGrid, true, -1d, -1d], null)!;
            var NativeMenuShown = DismissMenu(() => { this.FileGrid.RaiseEvent(Opening); return 0; });
            Check(NativeMenuShown && Opening.Handled && !this.FileGrid.ContextMenu.IsOpen,
                "The real file-list context-menu event opens only the native Windows menu"
                + $" (native={NativeMenuShown}, handled={Opening.Handled}, fallback={this.FileGrid.ContextMenu.IsOpen}, loading={this.Loading}, busy={this.OperationBusy}, menuOpen={this.ShellMenuOpen}, status={this.StatusText.Text})");
            Check(this.NavigationVersion == BeforeVersion && this.History.SequenceEqual(BeforeHistory)
                && this.SelectedEntries().Select(Entry => Entry.FullPath).SequenceEqual(Selected) && SamePath(this.CurrentPath, Folder)
                && File.ReadAllText(FilePath) == "unchanged" && File.ReadAllText(OtherFile) == "another unchanged file",
                "Cancelling the native multi-selection menu preserves files selection and navigation");
        }
        finally
        {
            if (AppMain.ScreenshotPath is { } Output)
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output)!, "WindowsShellMenus.json"), JsonSerializer.Serialize(Reports, new JsonSerializerOptions { WriteIndented = true }));
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != FixtureId
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned Shell menu fixture.");
            var CleanupTimer = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try { Directory.Delete(Fixture, true); break; }
                catch (IOException Error) when ((Error.HResult & 0xFFFF) is 32 or 33 && CleanupTimer.Elapsed < TimeSpan.FromSeconds(5))
                { await Task.Delay(100); }
            }
        }
    }

    private delegate void NativeMenuTimerCallback(IntPtr Window, uint Message, nuint Id, uint Time);
    [StructLayout(LayoutKind.Sequential)]
    private struct ShellSmokeGuiInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll", EntryPoint = "GetGUIThreadInfo")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetShellSmokeGuiInfo(uint ThreadId, ref ShellSmokeGuiInfo Info);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")] private static extern uint GetShellSmokeThreadId();
    [DllImport("user32.dll", EntryPoint = "SetTimer", SetLastError = true)] private static extern nuint SetShellSmokeTimer(IntPtr Window, nuint Id, uint Delay, NativeMenuTimerCallback Callback);
    [DllImport("user32.dll", EntryPoint = "KillTimer")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillShellSmokeTimer(IntPtr Window, nuint Id);
    [DllImport("user32.dll", EntryPoint = "EndMenu")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EndShellSmokeMenu();
}
