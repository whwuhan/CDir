using System.IO;
using Microsoft.Win32;

namespace CDir.Services;

// The Windows registration is the source of truth, so opening Preferences never
// enables startup or overwrites a choice made outside this installation.
internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string TestParent = @"Software\CDir\StartupTests\";
    internal const string ValueName = "CDir";
    internal static string Command => '"' + Runtime.RuntimeLocation.SourceExecutable + '"';

    internal static string RegistryPath
    {
        get
        {
            if (!AppMain.SmokeTest) return RunKey;
            var Root = AppMain.SmokeRoot ?? throw new InvalidOperationException("The startup test root is unavailable.");
            var Name = Path.GetFileName(Root);
            if (!Name.StartsWith("CDirSmoke", StringComparison.Ordinal)
                || !Guid.TryParseExact(Name[9..], "N", out _)
                || File.ReadAllText(Path.Combine(Root, ".CDirSmokeOwner")) != Name[9..])
                throw new InvalidOperationException("The startup test root is not owned.");
            return TestParent + Name;
        }
    }

    internal static bool IsEnabled()
    {
        using var Key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return string.Equals(Key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            Command, StringComparison.OrdinalIgnoreCase);
    }

    internal static void SetEnabled(bool Enabled)
    {
        var Path = RegistryPath;
        if (Enabled)
        {
            if (!File.Exists(Runtime.RuntimeLocation.SourceExecutable))
                throw new FileNotFoundException("The application executable is unavailable.");
            using var Key = Registry.CurrentUser.CreateSubKey(Path, writable: true);
            if (AppMain.SmokeTest) Key.SetValue("TestOwner", AppMain.SmokeRoot!, RegistryValueKind.String);
            Key.SetValue(ValueName, Command, RegistryValueKind.String);
            Key.Flush();
        }
        else
        {
            using var Key = Registry.CurrentUser.OpenSubKey(Path, writable: true);
            // Do not remove a startup entry belonging to another portable copy.
            if (string.Equals(Key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase))
            {
                Key!.DeleteValue(ValueName, throwOnMissingValue: false);
                Key.Flush();
            }
        }
        if (IsEnabled() != Enabled) throw new IOException("The Windows startup setting could not be verified.");
    }

    internal static void CleanupSmokeRegistration()
    {
        if (!AppMain.SmokeTest) return;
        var Path = RegistryPath;
        using (var Key = Registry.CurrentUser.OpenSubKey(Path))
        {
            if (Key is null) return;
            if (Key.GetValue("TestOwner") as string != AppMain.SmokeRoot)
                throw new InvalidOperationException("Refusing to remove an unowned startup test key.");
        }
        Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
    }
}
