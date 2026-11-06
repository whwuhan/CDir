using CDir.Services;
using Microsoft.Win32;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunStartupSmokeChecksAsync(List<string> Passed)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Startup UI check failed: " + Name);
            Passed.Add(Name);
        }
        using var RealRun = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var Original = RealRun?.GetValue(StartupService.ValueName);
        Check(StartupService.RegistryPath.StartsWith(@"Software\CDir\StartupTests\", StringComparison.Ordinal)
            && !StartupService.IsEnabled(), "Windows startup is initially unchecked in an isolated owned registry key");
        try
        {
            await this.RunPreferencesSmokeChecksAsync(Passed, InspectShortcuts: Dialog =>
            {
                Dialog.Categories.SelectedItem = Dialog.GeneralCategory;
                Check(Dialog.StartOnLoginCheck.IsVisible && Dialog.StartOnLoginCheck.IsChecked == false,
                    "General initially shows an unchecked Windows startup checkbox");
                Dialog.StartOnLoginCheck.IsChecked = true;
                using var Key = Registry.CurrentUser.OpenSubKey(StartupService.RegistryPath);
                Check(StartupService.IsEnabled() && Key?.GetValue(StartupService.ValueName) as string == StartupService.Command
                    && StartupService.Command.StartsWith('"') && StartupService.Command.EndsWith('"'),
                    "Checking startup immediately registers the quoted current executable without launching it");
                return Task.CompletedTask;
            });
            await this.RunPreferencesSmokeChecksAsync(Passed, InspectShortcuts: Dialog =>
            {
                Check(Dialog.StartOnLoginCheck.IsChecked == true, "Reopening Preferences reads the enabled Windows registration");
                Dialog.StartOnLoginCheck.IsChecked = false;
                using var Key = Registry.CurrentUser.OpenSubKey(StartupService.RegistryPath);
                Check(!StartupService.IsEnabled() && Key?.GetValue(StartupService.ValueName) is null,
                    "Unchecking startup immediately removes its registration");
                return Task.CompletedTask;
            });
            Check(Equals(Original, RealRun?.GetValue(StartupService.ValueName)),
                "Startup UI checks leave the user's real Windows startup registration unchanged");
        }
        finally { StartupService.SetEnabled(false); }
    }
}
