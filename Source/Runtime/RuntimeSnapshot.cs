using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CDir.Runtime;

internal sealed record RuntimeFile(string Path, long Length, string Sha256);
internal sealed record RuntimeManifest(int Schema, RuntimeFile[] Files);

internal static class RuntimeSnapshot
{
    internal const string ManifestFile = "RuntimeManifest.json";

    internal static string Prepare(string SourceDirectory, string CacheParent)
    {
        SourceDirectory = Path.GetFullPath(SourceDirectory);
        var SourceExecutable = Path.Combine(SourceDirectory, "CDir.exe");
        var Installation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceExecutable.ToUpperInvariant())))[..24];
        Exception? Failure = null;
        for (var Attempt = 0; Attempt < 4; Attempt++)
        {
            try
            {
                var Bytes = ReadShared(Path.Combine(SourceDirectory, ManifestFile));
                var Hash = Convert.ToHexString(SHA256.HashData(Bytes));
                var Manifest = JsonSerializer.Deserialize<RuntimeManifest>(Bytes)
                    ?? throw new InvalidDataException("The runtime manifest is empty.");
                if (Manifest.Schema != 1 || Manifest.Files.Length == 0
                    || !Manifest.Files.Any(File => File.Path == "CDir.App.exe")
                    || !Manifest.Files.Any(File => File.Path == "CDir.dll"))
                    throw new InvalidDataException("The runtime manifest is incomplete.");
                using var Gate = new Mutex(false, @"Local\CDir.Runtime." + Installation + "." + Hash);
                var Entered = false;
                try
                {
                    try { Entered = Gate.WaitOne(TimeSpan.FromSeconds(30)); }
                    catch (AbandonedMutexException) { Entered = true; }
                    if (!Entered) throw new IOException("Another CDir startup is still preparing its runtime.");
                    var Parent = Path.Combine(Path.GetFullPath(CacheParent), Installation);
                    var Destination = Path.Combine(Parent, Hash[..24]);
                    if (Directory.Exists(Destination))
                    {
                        if (RuntimeLocation.ReadSource(Destination) != SourceExecutable)
                            throw new IOException("The runtime snapshot has an unexpected owner.");
                        Verify(Destination, Manifest);
                        return Destination;
                    }
                    Directory.CreateDirectory(Parent);
                    var Staging = Path.Combine(Parent, Hash + "." + Guid.NewGuid().ToString("N") + ".tmp");
                    Directory.CreateDirectory(Staging);
                    // Failed copies stay in their unique staging directory. Never launch
                    // a partial snapshot or remove a directory used by another process.
                    foreach (var File in Manifest.Files)
                    {
                        var InputPath = ResolveFile(SourceDirectory, File.Path);
                        var OutputPath = ResolveFile(Staging, File.Path, MustExist: false);
                        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
                        using var Input = new FileStream(InputPath, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        using var Output = new FileStream(OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        Input.CopyTo(Output);
                    }
                    Verify(Staging, Manifest);
                    if (!Bytes.AsSpan().SequenceEqual(ReadShared(Path.Combine(SourceDirectory, ManifestFile))))
                        throw new IOException("The application files changed during startup.");
                    File.WriteAllBytes(Path.Combine(Staging, ManifestFile), Bytes);
                    File.WriteAllText(Path.Combine(Staging, RuntimeLocation.OriginFile),
                        JsonSerializer.Serialize(new RuntimeOrigin(SourceExecutable, Hash)));
                    Directory.Move(Staging, Destination);
                    return Destination;
                }
                finally { if (Entered) Gate.ReleaseMutex(); }
            }
            catch (Exception Error) when (Error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                Failure = Error;
                if (Attempt < 3) Thread.Sleep(100);
            }
        }
        throw new IOException("CDir could not prepare a complete runtime. Application files may be changing; try opening CDir again.", Failure);
    }

    private static void Verify(string Root, RuntimeManifest Manifest)
    {
        var Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var File in Manifest.Files)
        {
            if (!Names.Add(File.Path)) throw new InvalidDataException("The runtime manifest repeats a file.");
            var Path = ResolveFile(Root, File.Path);
            using var Stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (Stream.Length != File.Length || !Convert.ToHexString(SHA256.HashData(Stream)).Equals(File.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The runtime file could not be verified: " + File.Path);
        }
    }

    private static string ResolveFile(string Root, string Relative, bool MustExist = true)
    {
        var Full = Path.GetFullPath(Path.Combine(Root, Relative));
        if (Path.IsPathRooted(Relative) || !Full.StartsWith(Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The runtime manifest contains a path outside its directory.");
        for (var Current = Full; Current is not null && Current.Length >= Root.Length; Current = Path.GetDirectoryName(Current))
            if ((MustExist || File.Exists(Current) || Directory.Exists(Current))
                && (File.GetAttributes(Current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Runtime snapshots cannot follow reparse points.");
        return Full;
    }

    private static byte[] ReadShared(string Path)
    {
        using var Stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var Buffer = new MemoryStream();
        Stream.CopyTo(Buffer);
        return Buffer.ToArray();
    }
}
