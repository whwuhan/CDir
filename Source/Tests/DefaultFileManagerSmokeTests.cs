using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CDir.Services;
using Microsoft.Win32;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunDefaultFileManagerSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Default file manager check failed: " + Name);
            Passed.Add(Name);
        }
        var Prefix = StartupService.RegistryPath + @"\FileManager";
        Check(DefaultFileManagerService.ClassesPath == Prefix + @"\Classes"
            && DefaultFileManagerService.BackupPath == Prefix + @"\Backup"
            && Prefix.StartsWith(@"Software\CDir\StartupTests\", StringComparison.Ordinal),
            "Default file manager UI tests use only the current owned registry scope");
        var Classes = DefaultFileManagerService.ClassesPath;
        var WinE = Classes + "\\" + DefaultFileManagerService.WinEKey;
        string Snapshot(string Path)
        {
            using var Key = Registry.CurrentUser.OpenSubKey(Path);
            if (Key is null) return "missing";
            return JsonSerializer.Serialize(new
            {
                values = Key.GetValueNames().Order().Select(Name => new { name = Name, kind = Key.GetValueKind(Name), value = Key.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) }),
                children = Key.GetSubKeyNames().Order().Select(Name => new { name = Name, value = Snapshot(Path + "\\" + Name) })
            });
        }
        try
        {
            var NativeVerified=false;
            var NativeReply=DefaultFileManagerBridge.Request("owned-probe",AppMain.SmokeRoot!,()=>
            {
                // The out-of-process Windows registry provider observes the real
                // user hive independently of this desktop host's virtual view.
                dynamic Locator=Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!)!;
                dynamic Services=Locator.ConnectServer(".",@"root\default");
                dynamic Provider=Services.Get("StdRegProv");
                dynamic Parameters=Provider.Methods_.Item("GetStringValue").InParameters.SpawnInstance_();
                Parameters.hDefKey=0x80000003u;
                Parameters.sSubKeyName=System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value+@"\Software\CDir\StartupTests\"+Path.GetFileName(AppMain.SmokeRoot!)+@"\NativeProbe";
                Parameters.sValueName="Owner";
                dynamic Result=Services.ExecMethod("StdRegProv","GetStringValue",Parameters);
                NativeVerified=Convert.ToUInt32(Result.ReturnValue)==0 && (string)Result.sValue==AppMain.SmokeRoot;
                Check(NativeVerified,"The Windows system registry provider sees the owned native helper's value outside host registry virtualization (result="+Result.ReturnValue+", owner="+Result.sValue+")");
            });
            Check(NativeReply.Success && NativeVerified,"Native registry integration retains the runner's job idle CPU and very-low I/O protections");
            Check(!DefaultFileManagerService.IsEnabled(), "Default file manager is disabled without any registration");
            using (var Key = Registry.CurrentUser.CreateSubKey(WinE))
            {
                Key.SetValue("", @"%SystemRoot%\explorer.exe", RegistryValueKind.ExpandString);
                Key.SetValue("DelegateExecute", "Original delegate");
                Key.SetValue("Unrelated", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            }
            using (var Key = Registry.CurrentUser.CreateSubKey(Classes + @"\Directory\shell")) Key.SetValue("", "ExistingManager");
            using (var Key = Registry.CurrentUser.CreateSubKey(Classes + @"\Directory\shell\CDir.Open\command")) Key.SetValue("", "Prior custom command");
            var Original = Snapshot(Classes);
            await this.RunPreferencesSmokeChecksAsync(Passed, InspectShortcuts: Dialog =>
            {
                Dialog.Categories.SelectedItem = Dialog.GeneralCategory;
                Dialog.UpdateLayout();
                Check(Dialog.DefaultFileManagerCheck.IsVisible && Dialog.DefaultFileManagerCheck.IsChecked == false,
                    "General initially exposes an unchecked default file manager checkbox");
                if (AppMain.ScreenshotPath is { } Output)
                {
                    var Image = new RenderTargetBitmap((int)Dialog.ActualWidth, (int)Dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    Image.Render(Dialog);
                    var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Image));
                    using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, "DefaultFileManager-General.png")); Encoder.Save(Stream);
                }
                Dialog.DefaultFileManagerCheck.IsChecked = true;
                Check(DefaultFileManagerService.IsEnabled(), "Checking General registers folders drives and Win+E immediately");
                return Task.CompletedTask;
            });
            using (var Key = Registry.CurrentUser.OpenSubKey(WinE))
                Check(Key?.GetValue("") as string == DefaultFileManagerService.LaunchCommand && Key.GetValue("DelegateExecute") as string == "",
                    "Win+E uses the quoted current executable and clears its delegate in the isolated key");
            foreach (var Kind in new[] { "Directory", "Drive" })
            {
                using var Shell = Registry.CurrentUser.OpenSubKey(Classes + "\\" + Kind + @"\shell");
                using var CommandKey = Shell!.OpenSubKey(@"CDir.Open\command");
                var Command = (string)CommandKey!.GetValue("")!;
                Check(Shell.GetValue("") as string == "CDir.Open", Kind + " uses the CDir verb");
                foreach (var Folder in new[] { @"C:\", @"C:\folder with spaces\资料", @"\\server\share\", @"\\server\share\folder with spaces" })
                {
                    var Arguments = ParseShellCommandForSmoke(Command.Replace("%1", Folder, StringComparison.Ordinal));
                    Check(Arguments.Length == 3 && Arguments[0] == DefaultFileManagerService.Launcher && Arguments[1] == "--shell-open"
                        && this.Files.NormalizePath(Arguments[2]) == this.Files.NormalizePath(Folder),
                        Kind + " shell command preserves drive roots UNC Unicode and spaces: " + Folder);
                }
            }
            foreach (var Verb in new[] { "open", "explore", "opennewwindow" })
            {
                using var Key = Registry.CurrentUser.OpenSubKey(Classes + @"\Folder\shell\" + Verb + @"\command");
                Check(Key?.GetValue("") as string == DefaultFileManagerService.LaunchCommand + " \"%1\\.\"" && Key.GetValue("DelegateExecute") as string == "",
                    "Generic Folder " + Verb + " delegates to the reusable CDir launcher instead of Windows Explorer");
            }
            foreach (var Raw in new[] { "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\\.", "shell:MyComputerFolder" })
                Check(ShellLaunchLocation.TryResolve(Raw, out var Target) && Target == ShellLocations.Computer,
                    "Shell namespace This PC arguments resolve to CDir My Computer: " + Raw);
            DefaultFileManagerService.SetEnabled(true);
            await this.RunPreferencesSmokeChecksAsync(Passed, InspectShortcuts: Dialog =>
            {
                Check(Dialog.DefaultFileManagerCheck.IsChecked == true, "Reopening Preferences reads the enabled system registration");
                Dialog.DefaultFileManagerCheck.IsChecked = false;
                Check(!DefaultFileManagerService.IsEnabled() && Snapshot(Classes) == Original,
                    "Unchecking restores all previous values kinds and unrelated registrations after repeated enable");
                return Task.CompletedTask;
            });
            DefaultFileManagerService.RegistrationWriteProbe = Index => { if (Index == 6) throw new IOException("Injected owned registration failure."); };
            try { DefaultFileManagerService.SetEnabled(true); throw new InvalidOperationException("Expected a simulated registry failure."); }
            catch (IOException) { Check(!DefaultFileManagerService.IsEnabled() && Snapshot(Classes) == Original, "An interrupted enable rolls every written registration back to its exact previous value"); }
            finally { DefaultFileManagerService.RegistrationWriteProbe = null; }
            DefaultFileManagerService.SetEnabled(true);
            using (var Key = Registry.CurrentUser.OpenSubKey(WinE, writable: true)!)
            {
                Key.SetValue("", "External manager");
                Key.SetValue("DelegateExecute", "External delegate");
            }
            DefaultFileManagerService.SetEnabled(false);
            using (var Key = Registry.CurrentUser.OpenSubKey(WinE))
                Check(Key!.GetValue("") as string == "External manager" && Key.GetValue("DelegateExecute") as string == "External delegate",
                    "Disable preserves Win+E commands and delegates changed by another application");
            var External = Snapshot(Classes);
            DefaultFileManagerService.SetEnabled(true);
            DefaultFileManagerService.SetEnabled(false);
            Check(Snapshot(Classes) == External, "Reenable takes a fresh backup of the latest external file manager");
            DefaultFileManagerService.SetEnabled(true);
            // Simulate an older release whose saved commands target CDir.exe.
            using (var Backup = Registry.CurrentUser.OpenSubKey(DefaultFileManagerService.BackupPath, writable: true)!)
                foreach (var Name in Backup.GetSubKeyNames())
                {
                    using var Saved = Backup.OpenSubKey(Name, writable: true)!;
                    var Desired = (string)Saved.GetValue("Desired")!;
                    if (!Desired.Contains(DefaultFileManagerService.Launcher, StringComparison.Ordinal)) continue;
                    var Legacy = Desired.Replace(DefaultFileManagerService.Launcher, DefaultFileManagerService.Executable, StringComparison.Ordinal);
                    Saved.SetValue("Desired", Legacy);
                    using var Key = Registry.CurrentUser.OpenSubKey(Classes + "\\" + Saved.GetValue("Path"), writable: true)!;
                    Key.SetValue((string)Saved.GetValue("Name")!, Legacy);
                }
            DefaultFileManagerService.SetEnabled(true);
            Check(DefaultFileManagerService.IsEnabled(), "An enabled legacy CDir command migrates to the reusable launcher");
            DefaultFileManagerService.SetEnabled(false);
            Check(Snapshot(Classes) == External, "Migrating a previous CDir release retains the original external-manager backup for disable");
            using (var Key = Registry.CurrentUser.CreateSubKey(DefaultFileManagerService.BackupPath)) Key.SetValue("Executable", @"C:\Other CDir\CDir.exe");
            await this.RunPreferencesSmokeChecksAsync(Passed, InspectShortcuts: Dialog =>
            {
                Dialog.DefaultFileManagerCheck.IsChecked = true;
                Check(Dialog.DefaultFileManagerCheck.IsChecked == false && Snapshot(Classes) == External,
                    "A registration owned by another portable copy is preserved and the failed checkbox change is reverted");
                return Task.CompletedTask;
            });
        }
        finally
        {
            DefaultFileManagerService.RegistrationWriteProbe = null;
            // Prefix is derived from the validated filesystem owner marker.
            Registry.CurrentUser.DeleteSubKeyTree(Prefix, throwOnMissingSubKey: false);
        }
    }

    private static string[] ParseShellCommandForSmoke(string Command)
    {
        var Pointer = CommandLineToArgvForSmoke(Command, out var Count);
        if (Pointer == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try { return Enumerable.Range(0, Count).Select(Index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(Pointer, Index * IntPtr.Size))!).ToArray(); }
        finally { FreeShellArgumentsForSmoke(Pointer); }
    }
    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvForSmoke(string Command, out int Count);
    [DllImport("kernel32.dll", EntryPoint = "LocalFree")] private static extern IntPtr FreeShellArgumentsForSmoke(IntPtr Memory);
}
