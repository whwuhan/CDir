using System.IO;
using System.Text;
using System.Text.Json;

namespace CDir.Services;

internal static class DurableJsonStore
{
    internal static string ReadText(string Path)
    {
        CheckTestPath(Path);
        Exception? Failure = null;
        foreach (var Candidate in new[] { Path, Path + ".bak" })
        {
            CheckTestPath(Candidate);
            try
            {
                var Text = File.ReadAllText(Candidate);
                using var Document = JsonDocument.Parse(Text);
                if (Document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a settings object.");
                return Text;
            }
            catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or JsonException) { Failure = Error; }
        }
        throw new IOException("No readable settings snapshot is available.", Failure);
    }

    internal static T? Read<T>(string Path) where T : class
    {
        CheckTestPath(Path);
        foreach (var Candidate in new[] { Path, Path + ".bak" })
        {
            CheckTestPath(Candidate);
            try { if (JsonSerializer.Deserialize<T>(File.ReadAllText(Candidate)) is { } Value) return Value; }
            catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return null;
    }

    internal static void Write(string Path, string Json)
    {
        var Destination = System.IO.Path.GetFullPath(Path);
        CheckTestPath(Destination);
        CheckTestPath(Destination + ".bak");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Destination)!);
        var Temporary = Destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var Stream = new FileStream(Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                Stream.Write(Encoding.UTF8.GetBytes(Json));
                Stream.Flush(flushToDisk: true);
            }
            if (!File.Exists(Destination)) File.Move(Temporary, Destination);
            else
            {
                var Valid = false;
                try { using var Document = JsonDocument.Parse(File.ReadAllText(Destination)); Valid = Document.RootElement.ValueKind == JsonValueKind.Object; }
                catch (JsonException) { }
                // Keep the known-good backup when the primary was interrupted/corrupt.
                if (Valid) File.Replace(Temporary, Destination, Destination + ".bak", ignoreMetadataErrors: true);
                else File.Move(Temporary, Destination, overwrite: true);
            }
        }
        finally { if (File.Exists(Temporary)) File.Delete(Temporary); }
    }

    internal static void CheckTestPath(string Path)
    {
        if (!AppMain.SmokeTest) return;
        if (!AppMain.IsInsideSmokeScope(Path)) throw new IOException("Test settings must stay inside the owned UI-check scope.");
        var Current = System.IO.Path.GetFullPath(Path);
        while (AppMain.IsInsideSmokeScope(Current))
        {
            if ((File.Exists(Current) || Directory.Exists(Current))
                && (File.GetAttributes(Current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Test settings cannot follow a directory or file link.");
            Current = System.IO.Path.GetDirectoryName(Current)!;
        }
    }
}
