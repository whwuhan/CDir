using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CDir.Localization;

namespace CDir.Services;

/// <summary>One process-wide, serialized history. Disk journals survive restarts; undo belongs to this session.</summary>
internal sealed class FileOperationHistory(string Root)
{
    private readonly object Gate = new();
    private readonly object JournalGate = new();
    private readonly Stack<Batch> Undo = new();
    private readonly Stack<Batch> Redo = new();
    private Batch? Current;
    private readonly string Session = Guid.NewGuid().ToString("N");
    private string SessionRoot => Path.Combine(Root, this.Session);
    internal string JournalRoot => Root;
    internal bool CanUndo { get { lock (this.Gate) return this.Undo.Count > 0; } }
    internal bool CanRedo { get { lock (this.Gate) return this.Redo.Count > 0; } }

    internal IDisposable Begin(string Operation)
    {
        Monitor.Enter(this.Gate);
        try
        {
            Directory.CreateDirectory(this.SessionRoot);
            // Refuse new mutations if their audit trail cannot be written.
            this.Write(Operation, "Started", [], null);
            if (this.Current is not null) throw new InvalidOperationException("Nested file history transaction.");
            this.Current = new(Operation);
            return new Scope(this);
        }
        catch { Monitor.Exit(this.Gate); throw; }
    }

    internal void Record(string Kind, string Source, string Target, string? Snapshot = null)
    {
        var Change = new Change(Kind, Source, Target, Snapshot ?? Fingerprint(Target));
        this.Current!.Changes.Add(Change);
        this.Write(this.Current.Operation, "Completed", [Source, Target], null);
    }

    internal string Backup(string Source)
    {
        var Snapshot = this.Snapshot(Source);
        var DirectoryPath = Path.Combine(this.SessionRoot, "Data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        new FileSystemService().CopyItems([Source], DirectoryPath, false);
        var Backup = Path.Combine(DirectoryPath, Path.GetFileName(Source));
        if (Snapshot != Fingerprint(Backup) || Snapshot != Fingerprint(Source))
            throw new IOException(LanguageService.Text("HistoryBackupUnverified", Source));
        return Backup;
    }

    internal string Snapshot(string Source)
    {
        var FullSource = Path.GetFullPath(Source).TrimEnd('\\') + "\\";
        var FullRoot = Path.GetFullPath(Root).TrimEnd('\\') + "\\";
        if (FullRoot.StartsWith(FullSource, StringComparison.OrdinalIgnoreCase)
            || FullSource.StartsWith(FullRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException(LanguageService.Text("HistoryStorageProtected"));
        return Fingerprint(Source);
    }

    internal void RecordDeletion(string Source, string Backup, Action<string>? DeleteAgain = null)
    {
        this.Current!.Changes.Add(new("Delete", Source, Backup, Fingerprint(Backup)) { DeleteAgain = DeleteAgain });
        this.Write(this.Current.Operation, "Completed", [Source], null);
    }

    internal void Audit(string Operation, string[] Paths, string Status = "Completed", string? Error = null)
    {
        lock (this.JournalGate)
        {
            Directory.CreateDirectory(this.SessionRoot);
            this.Write(Operation, Status, Paths, Error);
        }
    }

    internal void Replay(bool Redo)
    {
        lock (this.Gate)
        {
            var From = Redo ? this.Redo : this.Undo;
            var To = Redo ? this.Undo : this.Redo;
            if (!From.TryPeek(out var Batch)) throw new IOException(LanguageService.Text(Redo ? "NothingToRedo" : "NothingToUndo"));
            var Changes = (Redo ? Batch.Changes : Batch.Changes.AsEnumerable().Reverse()).ToArray();
            // Preflight the whole batch, including conflicts, before changing any item.
            var Vacated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var Change in Changes)
            {
                Validate(Change, Redo, Vacated);
                if (Change.Kind == "Move") Vacated.Add(Redo ? Change.Source : Change.Target);
                else if (Change.Kind == "Delete" && Redo) Vacated.Add(Change.Source);
                else if (Change.Kind != "Delete" && !Redo) Vacated.Add(Change.Target);
            }
            this.Write(Redo ? "Redo" : "Undo", "Started", Changes.SelectMany(Change => new[] { Change.Source, Change.Target }).ToArray(), null);
            var Done = new List<Change>();
            try
            {
                foreach (var Change in Changes)
                {
                    try { this.Apply(Change, Redo); }
                    catch
                    {
                        // Shell operations can report cancellation after completing an item.
                        // Its inverse must still be offered, just like a completed batch prefix.
                        if (WasApplied(Change, Redo)) Done.Add(Change);
                        throw;
                    }
                    Done.Add(Change);
                }
            }
            catch (Exception Error)
            {
                // Keep the exact successfully replayed prefix reversible after a partial I/O failure.
                if (Done.Count > 0)
                {
                    foreach (var Change in Done) Batch.Changes.Remove(Change);
                    To.Push(new(Batch.Operation, Redo ? Done : Done.AsEnumerable().Reverse().ToList()));
                    if (Batch.Changes.Count == 0) From.Pop();
                }
                this.Write(Redo ? "Redo" : "Undo", "Failed", [], Error.Message);
                throw;
            }
            From.Pop();
            To.Push(Batch);
            this.Write(Redo ? "Redo" : "Undo", "Completed", Changes.Select(Change => Change.Source).ToArray(), null);
        }
    }

    private static void Validate(Change Change, bool Redo, HashSet<string> Vacated)
    {
        var Active = Change.Kind == "Delete" ? (Redo ? Change.Source : Change.Target)
            : Change.Kind == "Move" ? (Redo ? Change.Source : Change.Target)
            : Redo ? Change.Backup! : Change.Target;
        if (Fingerprint(Active) != Change.Fingerprint) throw new IOException(LanguageService.Text("HistoryChanged", Active));
        var Destination = Change.Kind == "Delete" ? (Redo ? null : Change.Source)
            : Change.Kind == "Move" ? (Redo ? Change.Target : Change.Source)
            : Redo ? Change.Target : null;
        if (Destination is not null && !Vacated.Contains(Destination) && !string.Equals(Active, Destination, StringComparison.OrdinalIgnoreCase))
            RequireAbsent(Destination);
    }

    private void Apply(Change Change, bool Redo)
    {
        var Files = new FileSystemService();
        if (Change.Kind == "Move")
        {
            var Source = Redo ? Change.Source : Change.Target;
            var Target = Redo ? Change.Target : Change.Source;
            if (string.Equals(Path.GetDirectoryName(Source), Path.GetDirectoryName(Target), StringComparison.OrdinalIgnoreCase))
                Files.Rename(Source, Path.GetFileName(Target));
            else Files.CopyItems([Source], Path.GetDirectoryName(Target)!, true);
        }
        else if (Change.Kind == "Delete")
        {
            if (Redo)
            {
                if (Change.DeleteAgain is { } Delete) Delete(Change.Source);
                else Files.RecycleItems([Change.Source]);
                if (File.Exists(Change.Source) || Directory.Exists(Change.Source)) throw new OperationCanceledException();
            }
            else Files.CopyItems([Change.Target], Path.GetDirectoryName(Change.Source)!, false);
        }
        else if (Redo)
            Files.CopyItems([Change.Backup!], Path.GetDirectoryName(Change.Target)!, true);
        else
        {
            var Parent = Path.Combine(this.SessionRoot, "Data", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Parent);
            Change.Backup = Path.Combine(Parent, Path.GetFileName(Change.Target));
            Files.CopyItems([Change.Target], Parent, true);
        }
    }

    private static bool WasApplied(Change Change, bool Redo)
    {
        var Removed = Change.Kind == "Move" ? (Redo ? Change.Source : Change.Target)
            : Change.Kind == "Delete" ? (Redo ? Change.Source : null)
            : Redo ? Change.Backup : Change.Target;
        var Present = Change.Kind == "Move" ? (Redo ? Change.Target : Change.Source)
            : Change.Kind == "Delete" ? (Redo ? Change.Target : Change.Source)
            : Redo ? Change.Target : Change.Backup;
        try
        {
            return (Removed is null || !File.Exists(Removed) && !Directory.Exists(Removed))
                && Present is not null && Fingerprint(Present) == Change.Fingerprint;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void RequireAbsent(string Path)
    {
        try { File.GetAttributes(Path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new IOException(LanguageService.Text("ServiceTargetConflict", Path));
    }

    private static string Fingerprint(string Root)
    {
        using var Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string Value) => Hash.AppendData(Encoding.UTF8.GetBytes(Value + "\0"));
        void Read(string PathValue)
        {
            var Attributes = File.GetAttributes(PathValue);
            if ((Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException(LanguageService.Text("ServiceLinkUnsupported", PathValue));
            Add(Path.GetRelativePath(Root, PathValue));
            // Compression, sparse allocation, encryption and cloud residency belong to
            // the volume/storage provider. A verified copy can have different storage
            // flags; the archive bit can also change independently of file contents.
            const FileAttributes ContentAttributes = FileAttributes.Directory | FileAttributes.ReadOnly
                | FileAttributes.Hidden | FileAttributes.System | FileAttributes.NotContentIndexed;
            Add(((int)(Attributes & ContentAttributes)).ToString());
            if ((Attributes & FileAttributes.Directory) != 0)
                foreach (var Child in Directory.EnumerateFileSystemEntries(PathValue).Order(StringComparer.OrdinalIgnoreCase)) Read(Child);
            else
            {
                Add(File.GetLastWriteTimeUtc(PathValue).Ticks.ToString());
                using var Stream = File.OpenRead(PathValue);
                Hash.AppendData(SHA256.HashData(Stream));
            }
        }
        Read(Root);
        return Convert.ToHexString(Hash.GetHashAndReset());
    }

    private void Write(string Operation, string Status, string[] Paths, string? Error)
    {
        lock (this.JournalGate)
            File.AppendAllText(Path.Combine(this.SessionRoot, "Operations.jsonl"),
                JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Operation, Status, Paths, Error }) + Environment.NewLine);
    }

    internal string ReadJournal()
    {
        lock (this.JournalGate)
        {
            if (!Directory.Exists(Root)) return "";
            var Lines = new List<string>();
            foreach (var SessionPath in Directory.EnumerateDirectories(Root))
            {
                if ((File.GetAttributes(SessionPath) & FileAttributes.ReparsePoint) != 0) continue;
                var FilePath = Path.Combine(SessionPath, "Operations.jsonl");
                if (File.Exists(FilePath)) Lines.AddRange(File.ReadLines(FilePath));
            }
            return string.Join(Environment.NewLine, Lines.OrderDescending(StringComparer.Ordinal));
        }
    }

    private sealed class Scope(FileOperationHistory Owner) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (Owner.Current is { Changes.Count: > 0 } Batch) { Owner.Undo.Push(Batch); Owner.Redo.Clear(); }
                Owner.Current = null;
            }
            finally { Monitor.Exit(Owner.Gate); }
        }
    }
    private sealed record Batch(string Operation, List<Change> Changes)
    {
        internal Batch(string Operation) : this(Operation, []) { }
    }
    private sealed record Change(string Kind, string Source, string Target, string Fingerprint)
    {
        internal string? Backup { get; set; }
        internal Action<string>? DeleteAgain { get; init; }
    }
}
