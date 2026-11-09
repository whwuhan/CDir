using System.IO;
using System.Text.Json;

namespace CDir.Runtime;

internal sealed record RuntimeOrigin(string SourceExecutable, string ManifestHash);

internal static class RuntimeLocation
{
    internal const string OriginFile = ".CDirRuntime.json";
    internal static string SourceExecutable => ReadSource(AppContext.BaseDirectory)
        ?? Path.Combine(AppContext.BaseDirectory, "CDir.exe");

    internal static string? ReadSource(string Directory)
    {
        try
        {
            var Origin = JsonSerializer.Deserialize<RuntimeOrigin>(File.ReadAllText(Path.Combine(Directory, OriginFile)));
            return Origin is not null && Path.IsPathFullyQualified(Origin.SourceExecutable)
                && Path.GetFileName(Origin.SourceExecutable).Equals("CDir.exe", StringComparison.OrdinalIgnoreCase)
                && Origin.ManifestHash.Length == 64 ? Path.GetFullPath(Origin.SourceExecutable) : null;
        }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    internal static bool SameInstallation(string Executable)
    {
        var Source = ReadSource(Path.GetDirectoryName(Executable)!) ?? Executable;
        return string.Equals(Source, SourceExecutable, StringComparison.OrdinalIgnoreCase);
    }
}
