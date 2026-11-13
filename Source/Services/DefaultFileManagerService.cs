using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CDir.Services;

// Only explicit user changes write registrations. Each overwritten value is
// backed up with its original registry type before the first shell change.
internal static class DefaultFileManagerService
{
    internal const string WinEKey = @"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command";
    internal static string Executable => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "CDir.exe"));
    internal static string Launcher => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "CDirShellLauncher.exe"));
    internal static string ClassesPath => AppMain.SmokeTest ? StartupService.RegistryPath + @"\FileManager\Classes" : @"Software\Classes";
    internal static string BackupPath => AppMain.SmokeTest ? StartupService.RegistryPath + @"\FileManager\Backup" : @"Software\CDir\DefaultFileManagerBackup";
    internal static string LaunchCommand => '"' + Launcher + "\" --shell-open";
    internal static Action<int>? RegistrationWriteProbe = null;
    private sealed record Registration(string Path, string Name, string Value);
    private static Registration[] Registrations =>
    [
        new(@"Directory\shell", "", "CDir.Open"),
        new(@"Directory\shell\CDir.Open", "", "Open In CDir"),
        new(@"Directory\shell\CDir.Open\command", "", LaunchCommand + " \"%1\\.\""),
        new(@"Drive\shell", "", "CDir.Open"),
        new(@"Drive\shell\CDir.Open", "", "Open In CDir"),
        new(@"Drive\shell\CDir.Open\command", "", LaunchCommand + " \"%1\\.\""),
        new(WinEKey, "", LaunchCommand),
        new(WinEKey, "DelegateExecute", ""),
        new(@"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\open\command", "", LaunchCommand),
        new(@"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\open\command", "DelegateExecute", ""),
        new(@"Folder\shell\open\command", "", LaunchCommand + " \"%1\\.\""),
        new(@"Folder\shell\open\command", "DelegateExecute", ""),
        new(@"Folder\shell\explore\command", "", LaunchCommand + " \"%1\\.\""),
        new(@"Folder\shell\explore\command", "DelegateExecute", ""),
        new(@"Folder\shell\opennewwindow\command", "", LaunchCommand + " \"%1\\.\""),
        new(@"Folder\shell\opennewwindow\command", "DelegateExecute", "")
    ];

    internal static bool IsEnabled()
    {
        using var Backup = Registry.CurrentUser.OpenSubKey(BackupPath);
        return string.Equals(Backup?.GetValue("Executable") as string, Executable, StringComparison.OrdinalIgnoreCase)
            && Registrations.All(Matches);
    }

    internal static void RefreshOwnedRegistration()
    {
        if (AppMain.SmokeTest || IsEnabled()) return;
        var Owned = false;
        using (var Backup = Registry.CurrentUser.OpenSubKey(BackupPath))
        {
            if (Backup is null || !string.Equals(Backup.GetValue("Executable") as string, Executable, StringComparison.OrdinalIgnoreCase) || Backup.GetValue("Complete") is not 1) return;
            var Names = Backup.GetSubKeyNames();
            Owned = Names.Length > 0 && Names.All(Name =>
            {
                using var Item = Backup.OpenSubKey(Name)!;
                return Matches(new((string)Item.GetValue("Path")!, (string)Item.GetValue("Name")!, (string)Item.GetValue("Desired")!));
            });
        }
        if (Owned) SetEnabled(true);
    }

    internal static bool OwnsCompleteRegistration()
    {
        using var Backup = Registry.CurrentUser.OpenSubKey(BackupPath);
        if (Backup is null || !string.Equals(Backup.GetValue("Executable") as string, Executable, StringComparison.OrdinalIgnoreCase) || Backup.GetValue("Complete") is not 1) return false;
        var Names=Backup.GetSubKeyNames();
        return Names.Length>0 && Names.All(Name=>
        {
            using var Item=Backup.OpenSubKey(Name)!;
            return Matches(new((string)Item.GetValue("Path")!,(string)Item.GetValue("Name")!,(string)Item.GetValue("Desired")!));
        });
    }

    private static bool Matches(Registration Item)
    {
        using var Key = Registry.CurrentUser.OpenSubKey(ClassesPath + "\\" + Item.Path);
        return Key?.GetValueKindOrNull(Item.Name) == RegistryValueKind.String
            && string.Equals(Key.GetValue(Item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, Item.Value, StringComparison.OrdinalIgnoreCase);
    }

    internal static void SetEnabled(bool Enabled)
    {
        using var Gate = new Mutex(false, @"Local\CDir.DefaultFileManagerRegistration");
        var Acquired = false;
        try
        {
            try { Acquired = Gate.WaitOne(0); } catch (AbandonedMutexException) { Acquired = true; }
            if (!Acquired) throw new IOException("Another window is changing the default file manager. Try again.");
            if (Enabled) Enable(); else Restore();
            if (IsEnabled() != Enabled) throw new IOException("The default file manager registration could not be verified.");
            if (!AppMain.SmokeTest) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { if (Acquired) Gate.ReleaseMutex(); }
    }

    private static void Enable()
    {
        if (!File.Exists(Executable) || !File.Exists(Launcher)) throw new FileNotFoundException("The application executable is unavailable.");
        if (IsEnabled()) return;
        if (AppMain.SmokeTest)
        {
            using var Owner = Registry.CurrentUser.CreateSubKey(StartupService.RegistryPath);
            if (Owner.GetValue("TestOwner") is string ExistingOwner && ExistingOwner != AppMain.SmokeRoot)
                throw new InvalidOperationException("The file manager test registration is not owned.");
            Owner.SetValue("TestOwner", AppMain.SmokeRoot!);
        }
        var Items = Registrations;
        using (var Existing = Registry.CurrentUser.OpenSubKey(BackupPath))
        {
            if (Existing is not null && !string.Equals(Existing.GetValue("Executable") as string, Executable, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Disable the default file manager option in the other CDir copy first.");
        }
        // Recover an interrupted attempt, retaining any later external changes,
        // before taking a fresh snapshot of the current user's choices.
        Restore();
        using (var Backup = Registry.CurrentUser.CreateSubKey(BackupPath, writable: true))
        {
            if (Backup.GetValue("Complete") is not 1)
            {
                Backup.SetValue("Executable", Executable);
                Backup.Flush();
                // No shell registration is touched until every original is durable.
                for (var Index = 0; Index < Items.Length; Index++)
                {
                    var Item = Items[Index];
                    using var Original = Registry.CurrentUser.OpenSubKey(ClassesPath + "\\" + Item.Path);
                    using var Saved = Backup.CreateSubKey(Index.ToString(), writable: true);
                    Saved.SetValue("Path", Item.Path);
                    Saved.SetValue("Name", Item.Name);
                    Saved.SetValue("Desired", Item.Value);
                    Saved.SetValue("KeyExisted", Original is null ? 0 : 1);
                    var Missing = new List<string>();
                    var Ancestor = "";
                    foreach (var Part in Item.Path.Split('\\'))
                    {
                        Ancestor = Ancestor.Length == 0 ? Part : Ancestor + "\\" + Part;
                        using var Parent = Registry.CurrentUser.OpenSubKey(ClassesPath + "\\" + Ancestor);
                        if (Parent is null) Missing.Add(Ancestor);
                    }
                    Saved.SetValue("CreatedKeys", Missing.ToArray(), RegistryValueKind.MultiString);
                    var Kind = Original?.GetValueKindOrNull(Item.Name);
                    Saved.SetValue("HadValue", Kind is null ? 0 : 1);
                    if (Kind is { } ValueKind) Saved.SetValue("Original", Original!.GetValue(Item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, ValueKind);
                    Saved.Flush();
                }
                Backup.SetValue("Executable", Executable);
                Backup.SetValue("Complete", 1);
                Backup.Flush();
            }
        }
        try
        {
            var Index = 0;
            // Install the commands before making them the default folder verbs.
            foreach (var Item in Items.OrderBy(Item => Item.Path.EndsWith(@"\shell", StringComparison.Ordinal) ? 1 : 0))
            {
                using var Key = Registry.CurrentUser.CreateSubKey(ClassesPath + "\\" + Item.Path, writable: true);
                Key.SetValue(Item.Name, Item.Value, RegistryValueKind.String);
                Key.Flush();
                if (!Matches(Item)) throw new IOException("A default file manager registration did not persist.");
                if (AppMain.SmokeTest) RegistrationWriteProbe?.Invoke(Index++);
            }
        }
        catch { Restore(); throw; }
    }

    private static void Restore()
    {
        var Created = new List<string>();
        var OwnedCommands = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using (var Backup = Registry.CurrentUser.OpenSubKey(BackupPath))
        {
            if (Backup is null) return;
            // A portable copy must never remove registrations belonging to another.
            if (!string.Equals(Backup.GetValue("Executable") as string, Executable, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var Name in Backup.GetSubKeyNames())
            {
                using var Saved = Backup.OpenSubKey(Name)!;
                if (Saved.GetValue("Path") is string Path && Saved.GetValue("Name") as string == "" && Path.EndsWith(@"\command", StringComparison.Ordinal))
                    OwnedCommands[Path] = Matches(new(Path, "", (string)Saved.GetValue("Desired")!));
            }
            if (Backup.GetValue("Complete") is 1)
            foreach (var Name in Backup.GetSubKeyNames())
            {
                using var Saved = Backup.OpenSubKey(Name)!;
                var Relative = (string)Saved.GetValue("Path")!;
                var ValueName = (string)Saved.GetValue("Name")!;
                var Desired = (string)Saved.GetValue("Desired")!;
                if (Saved.GetValue("KeyExisted") is 0) Created.Add(Relative);
                if (Saved.GetValue("CreatedKeys") is string[] Ancestors) Created.AddRange(Ancestors);
                var CommandPath = Relative == @"Directory\shell" ? @"Directory\shell\CDir.Open\command"
                    : Relative == @"Drive\shell" ? @"Drive\shell\CDir.Open\command" : Relative;
                if (OwnedCommands.TryGetValue(CommandPath, out var Owned) && !Owned) continue;
                // Do not overwrite values changed by another application after enable.
                if (!Matches(new(Relative, ValueName, Desired))) continue;
                using var Key = Registry.CurrentUser.OpenSubKey(ClassesPath + "\\" + Relative, writable: true)!;
                if (Saved.GetValue("HadValue") is 1)
                {
                    Key.SetValue(ValueName, Saved.GetValue("Original", null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, Saved.GetValueKind("Original"));
                    var Expected = Saved.GetValue("Original", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    var Actual = Key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (Key.GetValueKind(ValueName) != Saved.GetValueKind("Original")
                        || !(Expected is Array A && Actual is Array B ? A.Cast<object>().SequenceEqual(B.Cast<object>()) : Equals(Expected, Actual)))
                        throw new IOException("The previous file manager setting could not be restored.");
                }
                else Key.DeleteValue(ValueName, throwOnMissingValue: false);
                Key.Flush();
            }
        }
        // Remove empty keys created by this registration, preserving unrelated values.
        foreach (var Relative in Created.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(Path => Path.Length))
        {
            var Path = ClassesPath + "\\" + Relative;
            using var Key = Registry.CurrentUser.OpenSubKey(Path);
            if (Key is { ValueCount: 0, SubKeyCount: 0 }) Registry.CurrentUser.DeleteSubKey(Path, throwOnMissingSubKey: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(BackupPath, throwOnMissingSubKey: false);
    }

    private static RegistryValueKind? GetValueKindOrNull(this RegistryKey Key, string Name)
        => Key.GetValueNames().Contains(Name, StringComparer.OrdinalIgnoreCase) ? Key.GetValueKind(Name) : null;

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint Event, uint Flags, IntPtr Item1, IntPtr Item2);
}
