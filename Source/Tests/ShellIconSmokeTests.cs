using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Controls;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunShellIconSmokeChecksAsync(List<string> Passed)
    {
        if (!AppMain.SmokeTest) return;
        var InitialPath = this.CurrentPath;
        var InitialSearch = this.SearchBox.Text;
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CDirShellIcons" + FixtureId));
        var Marker = System.IO.Path.Combine(Fixture, ".CDirShellIconOwner");
        var PreviewPath = System.IO.Path.Combine(Fixture, "Preview");
        var StressPath = System.IO.Path.Combine(Fixture, "Stress");
        System.IO.Directory.CreateDirectory(PreviewPath);
        System.IO.Directory.CreateDirectory(StressPath);
        System.IO.File.WriteAllText(Marker, FixtureId);
        Grid? ProbeHost = null;
        ShellIcon? Probe = null;
        Task<Dictionary<string, BitmapSource>>? ReferenceWork = null;
        Exception? TestFailure = null;

        void Check(bool Condition, string Name, string? Detail = null)
        {
            if (!Condition) throw new InvalidOperationException("Shell icon smoke test failed: " + Name + (Detail is null ? "" : "\n" + Detail));
            Passed.Add(Name);
        }

        try
        {
            Check(TaskbarService.ReadIdentity() == TaskbarService.ApplicationId,
                "Windows Shell uses the application's explicit taskbar identity instead of an inferred legacy group");
            var FolderPath = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(PreviewPath, "Documents")).FullName;
            var TextPath = System.IO.Path.Combine(PreviewPath, "Example.txt");
            System.IO.File.WriteAllText(TextPath, "Windows Shell icon fixture");
            var AppPath = System.IO.Path.Combine(PreviewPath, "Application.exe");
            var CommandPath = System.IO.Path.Combine(PreviewPath, "Command.exe");
            System.IO.File.Copy(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate current executable."), AppPath);
            System.IO.File.Copy(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), CommandPath);
            var AppLink = System.IO.Path.Combine(PreviewPath, "Application.lnk");
            var CommandLink = System.IO.Path.Combine(PreviewPath, "Command.lnk");
            CreateIconTestShortcut(AppLink, AppPath);
            CreateIconTestShortcut(CommandLink, CommandPath);
            var Samples = new[]
            {
                new ShellIconSample("Computer", ShellLocations.Computer, true),
                new ShellIconSample("Folder", FolderPath, true),
                new ShellIconSample("OpenFolder", FolderPath, true, 16, true),
                new ShellIconSample("Drive", System.IO.Path.GetPathRoot(Fixture)!, true),
                new ShellIconSample("Text", TextPath, false),
                new ShellIconSample("Application", AppPath, false),
                new ShellIconSample("Command", CommandPath, false),
                new ShellIconSample("ApplicationLink", AppLink, false),
                new ShellIconSample("CommandLink", CommandLink, false),
                new ShellIconSample("FolderLarge", FolderPath, true, 32)
            };
            var Images = await Task.WhenAll(Samples.Select(Sample => ShellIconService.GetIconAsync(Sample.Path, Sample.IsDirectory, Sample.Size, Sample.OpenFolder)));
            var Icons = Samples.Select((Sample, Index) => (Sample.Name, Icon: Images[Index])).ToDictionary(Pair => Pair.Name, Pair => Pair.Icon, StringComparer.Ordinal);
            Check(Samples.All(Sample => Icons[Sample.Name] is BitmapSource Bitmap && Bitmap.IsFrozen
                    && Bitmap.PixelWidth == Sample.Size && Bitmap.PixelHeight == Sample.Size && HasVisibleIconPixels(Bitmap))
                && HasTransparentIconPixels((BitmapSource)Icons["Folder"]!)
                && ShellIconService.GetFallbackIcon(false, 16) is BitmapSource { IsFrozen: true }
                && ShellIconService.GetDriveFallbackIcon(16) is BitmapSource { IsFrozen: true },
                "system file folder drive and shortcut icons are frozen bitmaps with preserved alpha");

            // This separate native call is the authority: a stock fallback must not make the test pass.
            ReferenceWork = ReadReferenceShellIconsAsync(Samples);
            var References = await ReferenceWork.WaitAsync(TimeSpan.FromSeconds(10));
            var Mismatches = Samples.Where(Sample => !IconPixelsEqual(Icons[Sample.Name], References[Sample.Name])).Select(Sample => Sample.Name).ToArray();
            Check(Mismatches.Length == 0
                && !IconPixelsEqual(Icons["Application"], Icons["Command"])
                && !IconPixelsEqual(Icons["ApplicationLink"], Icons["CommandLink"]),
                "icons match native Shell base icons with shortcut arrows and without third-party status overlays",
                "Native comparison mismatches: " + string.Join(", ", Mismatches));

            var SharedPath = System.IO.Path.Combine(StressPath, "Shared.txt");
            System.IO.File.WriteAllText(SharedPath, "shared icon");
            var SharedRequests = Enumerable.Range(0, 24).Select(_ => ShellIconService.GetIconAsync(SharedPath, false)).ToArray();
            var SharedIcons = await Task.WhenAll(SharedRequests);
            var Diagnostics = ShellIconService.GetDiagnostics();
            Check(SharedIcons[0] is BitmapSource && SharedIcons.All(Icon => ReferenceEquals(Icon, SharedIcons[0]))
                && ReferenceEquals(await ShellIconService.GetIconAsync(SharedPath, false), SharedIcons[0])
                && Diagnostics.CacheCount <= ShellIconService.CacheCapacity
                && Diagnostics.PendingCount <= ShellIconService.PendingCapacity
                && Diagnostics.QueuedCount < ShellIconService.PendingCapacity,
                "concurrent icon requests share cached images and stay within cache and queue limits");

            await WaitForShellIconIdleAsync();
            var Before = ReadIconGuiResources();
            var NativeBefore = ShellIconService.GetDiagnostics().NativeRequestCount;
            var First = Before;
            for (var Round = 0; Round < 2; Round++)
            {
                var Paths = Enumerable.Range(0, 24).Select(Index => System.IO.Path.Combine(StressPath, $"Round{Round}Item{Index}.txt")).ToArray();
                foreach (var FilePath in Paths) System.IO.File.WriteAllText(FilePath, "icon");
                await Task.WhenAll(Paths.Select(FilePath => ShellIconService.GetIconAsync(FilePath, false)));
                await WaitForShellIconIdleAsync();
                if (Round == 0) First = ReadIconGuiResources();
            }
            var After = ReadIconGuiResources();
            Diagnostics = ShellIconService.GetDiagnostics();
            Check(Diagnostics.NativeRequestCount - NativeBefore >= 48
                && (long)After.User - First.User <= 8 && (long)After.Gdi - First.Gdi <= 8
                && (long)After.User - Before.User <= 16 && (long)After.Gdi - Before.Gdi <= 16
                && Diagnostics.CacheCount <= ShellIconService.CacheCapacity
                && Diagnostics.PendingCount <= ShellIconService.PendingCapacity,
                "repeated uncached icon loads do not accumulate native GUI handles",
                $"Before={Before}; first={First}; after={After}; native requests={Diagnostics.NativeRequestCount - NativeBefore}");

            if (!await this.NavigateAsync(PreviewPath)) throw new InvalidOperationException("Cannot open icon layout fixture.");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.FileGrid.UpdateLayout();
            var FirstRow = this.FileGrid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
            var RowIcon = FirstRow is null ? null : FindVisualChild<ShellIcon>(FirstRow);
            Check(FirstRow is not null && Math.Abs(FirstRow.ActualHeight - 22) <= 1
                && RowIcon is not null && Math.Abs(RowIcon.ActualWidth - 16) <= 1 && Math.Abs(RowIcon.ActualHeight - 16) <= 1,
                "Shell icons preserve the compact 22 DIP rows and 16 DIP image layout",
                $"Row={FirstRow?.ActualHeight}; icon={RowIcon?.ActualWidth}x{RowIcon?.ActualHeight}");

            ProbeHost = this.Content as Grid ?? throw new InvalidOperationException("Cannot attach icon probe to the main visual tree.");
            Probe = new ShellIcon { Width = 16, Height = 16, IconSize = 16, Opacity = 0, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, HideWhenEmpty = true };
            ProbeHost.Children.Add(Probe);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var Pixels = Math.Clamp((int)Math.Ceiling(16 * VisualTreeHelper.GetDpi(Probe).DpiScaleX), 8, 64);
            var ExpectedFolder = await ShellIconService.GetIconAsync(FolderPath, true, Pixels);
            var RacePath = System.IO.Path.Combine(StressPath, "Rebind.exe");
            System.IO.File.Copy(CommandPath, RacePath);
            Probe.IsDirectory = false;
            Probe.Path = RacePath;
            Probe.IsDirectory = true;
            Probe.Path = FolderPath;
            await ShellIconService.GetIconAsync(RacePath, false, Pixels);
            await ShellIconService.GetIconAsync(FolderPath, true, Pixels);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var CorrectAfterRebind = ReferenceEquals(Probe.Source, ExpectedFolder);
            Probe.Path = null;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(CorrectAfterRebind && Probe.Source is null,
                "recycled icon controls reject stale results and hide empty placeholder paths");
        }
        catch (Exception Error)
        {
            TestFailure = Error;
            throw;
        }
        finally
        {
            try
            {
                if (Probe is not null) ProbeHost?.Children.Remove(Probe);
                if (!this.WindowClosed && !string.IsNullOrEmpty(InitialPath))
                {
                    await this.NavigateAsync(InitialPath, RecordHistory: false);
                    this.SearchBox.Text = InitialSearch;
                }
                if (ReferenceWork is { IsCompleted: false }) await ReferenceWork.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForShellIconIdleAsync();
                var TempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                if (!Fixture.StartsWith(TempRoot, StringComparison.OrdinalIgnoreCase)
                    || System.IO.Path.GetFileName(Fixture) != "CDirShellIcons" + FixtureId
                    || !System.IO.File.Exists(Marker) || System.IO.File.ReadAllText(Marker) != FixtureId
                    || (System.IO.File.GetAttributes(Fixture) & System.IO.FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Refusing cleanup outside the owned icon smoke fixture.");
                for (var Attempt = 0; ; Attempt++)
                {
                    try { System.IO.Directory.Delete(Fixture, true); break; }
                    catch (System.IO.IOException) when (Attempt < 11) { await Task.Delay(250); }
                }
            }
            catch (Exception CleanupError) when (TestFailure is not null)
            {
                throw new AggregateException("Shell icon smoke assertion failed and its owned fixture could not be cleaned: " + Fixture,
                    TestFailure, CleanupError);
            }
        }
    }

    private static void CreateIconTestShortcut(string LinkPath, string TargetPath)
    {
        object? Shell = null;
        object? Shortcut = null;
        try
        {
            Shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
            Shortcut = ((dynamic)Shell).CreateShortcut(LinkPath);
            ((dynamic)Shortcut).TargetPath = TargetPath;
            ((dynamic)Shortcut).WorkingDirectory = System.IO.Path.GetDirectoryName(TargetPath);
            ((dynamic)Shortcut).Save();
        }
        finally
        {
            if (Shortcut is not null && Marshal.IsComObject(Shortcut)) Marshal.FinalReleaseComObject(Shortcut);
            if (Shell is not null && Marshal.IsComObject(Shell)) Marshal.FinalReleaseComObject(Shell);
        }
    }

    private static Task<Dictionary<string, BitmapSource>> ReadReferenceShellIconsAsync(ShellIconSample[] Samples)
    {
        var Completion = new TaskCompletionSource<Dictionary<string, BitmapSource>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var Worker = new Thread(() =>
        {
            var ComResult = IconReferenceInitializeCom(IntPtr.Zero, 2);
            Dictionary<string, BitmapSource>? Results = null;
            Exception? Failure = null;
            try
            {
                Marshal.ThrowExceptionForHR(ComResult);
                Results = new Dictionary<string, BitmapSource>(StringComparer.Ordinal);
                foreach (var Sample in Samples)
                {
                    var Info = new IconReferenceFileInfo { DisplayName = string.Empty, TypeName = string.Empty };
                    uint Flags = 0x100; // SHGFI_ICON: deliberately exclude third-party status overlays.
                    if (Sample.Size <= 16) Flags |= 1;
                    if (Sample.IsDirectory && Sample.OpenFolder) Flags |= 2;
                    if (System.IO.Path.GetExtension(Sample.Path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) Flags |= 0x8000;
                    var Pidl = IntPtr.Zero;
                    try
                    {
                        IntPtr Result;
                        if (ShellLocations.IsVirtual(Sample.Path))
                        {
                            Marshal.ThrowExceptionForHR(IconReferenceParseName(Sample.Path, IntPtr.Zero, out Pidl, 0, out _));
                            Result = IconReferenceGetPidlInfo(Pidl, 0, ref Info, (uint)Marshal.SizeOf<IconReferenceFileInfo>(), Flags | 8);
                        }
                        else Result = IconReferenceGetFileInfo(Sample.Path, 0, ref Info, (uint)Marshal.SizeOf<IconReferenceFileInfo>(), Flags);
                        if (Result == IntPtr.Zero || Info.Icon == IntPtr.Zero)
                            throw new InvalidOperationException("Native Shell reference lookup failed: " + Sample.Name);
                        var Bitmap = Imaging.CreateBitmapSourceFromHIcon(Info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(Sample.Size, Sample.Size));
                        Bitmap.Freeze();
                        Results.Add(Sample.Name, Bitmap);
                    }
                    finally
                    {
                        if (Info.Icon != IntPtr.Zero) IconReferenceDestroyIcon(Info.Icon);
                        if (Pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(Pidl);
                    }
                }
            }
            catch (Exception Error) { Failure = Error; }
            finally { if (ComResult >= 0) IconReferenceUninitializeCom(); }
            if (Failure is not null) Completion.SetException(Failure);
            else Completion.SetResult(Results!);
        }) { IsBackground = true, Name = "CDir.IconSmokeReference" };
        Worker.SetApartmentState(ApartmentState.STA);
        Worker.Start();
        return Completion.Task;
    }

    private static byte[] ReadIconPixels(BitmapSource Bitmap)
    {
        var Converted = new FormatConvertedBitmap(Bitmap, PixelFormats.Bgra32, null, 0);
        var Pixels = new byte[Converted.PixelWidth * Converted.PixelHeight * 4];
        Converted.CopyPixels(Pixels, Converted.PixelWidth * 4, 0);
        return Pixels;
    }

    private static bool IconPixelsEqual(ImageSource? Left, ImageSource? Right) => Left is BitmapSource A && Right is BitmapSource B
        && A.PixelWidth == B.PixelWidth && A.PixelHeight == B.PixelHeight && ReadIconPixels(A).SequenceEqual(ReadIconPixels(B));
    private static bool HasVisibleIconPixels(BitmapSource Bitmap) => ReadIconPixels(Bitmap).Where((_, Index) => Index % 4 == 3).Any(Alpha => Alpha > 0);
    private static bool HasTransparentIconPixels(BitmapSource Bitmap) => ReadIconPixels(Bitmap).Where((_, Index) => Index % 4 == 3).Any(Alpha => Alpha < 255);

    private async Task WaitForShellIconIdleAsync()
    {
        var Deadline = Environment.TickCount64 + 5000;
        while (ShellIconService.GetDiagnostics().PendingCount > 0 && Environment.TickCount64 < Deadline) await Task.Delay(25);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (ShellIconService.GetDiagnostics().PendingCount > 0)
            throw new TimeoutException("Native Shell requests have not finished: " + ShellIconService.GetDiagnostics());
    }

    private static IconGuiResources ReadIconGuiResources()
    {
        using var Process = System.Diagnostics.Process.GetCurrentProcess();
        return new(IconReferenceGetGuiResources(Process.Handle, 0), IconReferenceGetGuiResources(Process.Handle, 1));
    }

    private sealed record ShellIconSample(string Name, string Path, bool IsDirectory, int Size = 16, bool OpenFolder = false);
    private sealed record IconGuiResources(uint Gdi, uint User);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconReferenceFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr IconReferenceGetFileInfo(string Path, uint Attributes, ref IconReferenceFileInfo Info, uint Size, uint Flags);
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr IconReferenceGetPidlInfo(IntPtr Pidl, uint Attributes, ref IconReferenceFileInfo Info, uint Size, uint Flags);
    [DllImport("shell32.dll", EntryPoint = "SHParseDisplayName", CharSet = CharSet.Unicode)]
    private static extern int IconReferenceParseName(string Name, IntPtr Context, out IntPtr Pidl, uint Requested, out uint Attributes);
    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IconReferenceDestroyIcon(IntPtr Icon);
    [DllImport("user32.dll", EntryPoint = "GetGuiResources")]
    private static extern uint IconReferenceGetGuiResources(IntPtr Process, uint Flags);
    [DllImport("ole32.dll", EntryPoint = "CoInitializeEx")]
    private static extern int IconReferenceInitializeCom(IntPtr Reserved, uint Flags);
    [DllImport("ole32.dll", EntryPoint = "CoUninitialize")]
    private static extern void IconReferenceUninitializeCom();
}
