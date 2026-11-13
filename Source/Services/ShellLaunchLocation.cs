using System.Diagnostics;
using System.IO;

namespace CDir.Services;

internal static class ShellLaunchLocation
{
    internal static bool TryResolve(string? Raw, out string? Location)
    {
        Location = Raw?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(Location)) { Location = null; return true; }
        if (Location.EndsWith(@"\.", StringComparison.Ordinal)) Location = Location[..^2];
        var Id = Location.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? Location[6..] : Location;
        var NamespaceId = Guid.TryParse(Id.TrimStart(':'), out var Parsed) ? Parsed.ToString("D") : "";
        if (Id.Equals("MyComputerFolder", StringComparison.OrdinalIgnoreCase) || NamespaceId.Equals("20D04FE0-3AEA-1069-A2D8-08002B30309D", StringComparison.OrdinalIgnoreCase))
        { Location = ShellLocations.Computer; return true; }
        if (Id.Equals("RecycleBinFolder", StringComparison.OrdinalIgnoreCase) || NamespaceId.Equals("645FF040-5081-101B-9F08-00AA002F954E", StringComparison.OrdinalIgnoreCase))
        { Location = ShellLocations.RecycleBin; return true; }
        if (Id.Equals("Home", StringComparison.OrdinalIgnoreCase) || NamespaceId.Equals("679F85CB-0220-4080-B29B-5540CC05AAB6", StringComparison.OrdinalIgnoreCase)
            || NamespaceId.Equals("F874310E-B6B7-47DC-BC84-B9E6B38F5903", StringComparison.OrdinalIgnoreCase))
        { Location = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); return true; }
        if (Location.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || Location.StartsWith("::", StringComparison.Ordinal)) return false;
        // Leave normal filesystem normalization to the navigation model.
        Location = Raw;
        return true;
    }

    internal static void OpenInWindows(string Location)
    {
        var Start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
        Start.ArgumentList.Add(Location);
        using var Process = System.Diagnostics.Process.Start(Start);
    }
}
