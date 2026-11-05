using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using CDir.Models;
using CDir.Localization;
using Microsoft.VisualBasic.FileIO;

namespace CDir.Services;

/// <summary>Filesystem operations. Call potentially expensive methods from a worker thread.</summary>
public sealed class FileSystemService
{
    internal FileOperationHistory? OperationHistory { get; set; }
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false
    };

    public IReadOnlyList<DriveEntry> GetRoots()
    {
        var Roots = new List<DriveEntry>();
        foreach (var Drive in DriveInfo.GetDrives())
        {
            var Root = Drive.RootDirectory.FullName;
            var Name = Root.TrimEnd(System.IO.Path.DirectorySeparatorChar);
            var DescriptionKey = "DriveGeneric";
            var LocalizeName = true;
            try
            {
                DescriptionKey = Drive.DriveType switch
                {
                    DriveType.Network => "DriveNetwork",
                    DriveType.Removable => "DriveRemovable",
                    DriveType.CDRom => "DriveOptical",
                    _ => "DriveLocal"
                };
                if (Drive.IsReady && !string.IsNullOrWhiteSpace(Drive.VolumeLabel))
                {
                    Name = $"{Drive.VolumeLabel} ({Name})";
                    LocalizeName = false;
                }
                else
                    Name = LanguageService.Text("DriveLabel", LanguageService.Text(DescriptionKey), Name);
            }
            catch (System.Exception Ex) when (IsFileSystemError(Ex)) { }
            Roots.Add(new DriveEntry(Name, Root, LanguageService.Text(DescriptionKey))
            {
                DescriptionKey = DescriptionKey,
                LocalizeName = LocalizeName
            });
        }
        return Roots;
    }

    public IReadOnlyList<DirectoryEntry> GetDirectories(string Path, bool ShowHidden)
    {
        var Result = new List<DirectoryEntry>();
        foreach (var Directory in new DirectoryInfo(Path).EnumerateDirectories("*", Enumeration))
        {
            try
            {
                var Attributes = Directory.Attributes;
                if (!ShowHidden && IsHidden(Attributes)) continue;
                Result.Add(new DirectoryEntry(Directory.Name, Directory.FullName));
            }
            catch (System.Exception Ex) when (IsFileSystemError(Ex)) { /* A child may disappear or be unreadable. */ }
        }
        return Result.OrderBy(Item => Item.Name, PathComparer).ToArray();
    }

    public IReadOnlyList<FileEntry> GetEntries(string Path, bool ShowHidden)
    {
        var Result = new List<FileEntry>();
        foreach (var Info in new DirectoryInfo(Path).EnumerateFileSystemInfos("*", Enumeration))
        {
            try
            {
                var Attributes = Info.Attributes;
                var Hidden = IsHidden(Attributes);
                if (!ShowHidden && Hidden) continue;
                Result.Add(ReadEntry(Info, Attributes));
            }
            catch (System.Exception Ex) when (IsFileSystemError(Ex)) { /* Keep other readable entries visible. */ }
        }
        return Result.OrderByDescending(Item => Item.IsDirectory)
            .ThenBy(Item => Item.Name, PathComparer).ToArray();
    }

    private static FileEntry ReadEntry(FileSystemInfo Info, FileAttributes Attributes)
    {
        var IsDirectory = (Attributes & FileAttributes.Directory) != 0;
        var Extension = IsDirectory ? string.Empty : Info.Extension.ToLowerInvariant();
        var IsLink = (Attributes & FileAttributes.ReparsePoint) != 0;
        return new FileEntry
        {
            Name = Info.Name,
            FullPath = Info.FullName,
            IsDirectory = IsDirectory,
            Extension = Extension,
            TypeKey = IsLink ? (IsDirectory ? "TypeFolderLink" : "TypeFileLink") : GetTypeKey(IsDirectory, Extension),
            TypeArguments = [Extension.TrimStart('.').ToUpperInvariant()],
            Size = Info is FileInfo File ? File.Length : null,
            Modified = Info.LastWriteTime,
            IsHidden = IsHidden(Attributes)
        };
    }

    public IReadOnlyList<FileEntry> SearchEntries(string Root, SearchQuery Query, bool ShowHidden,
        CancellationToken Cancellation = default, Action<IReadOnlyList<FileEntry>>? OnBatch = null)
    {
        Cancellation.ThrowIfCancellationRequested();
        var RootPath = this.NormalizePath(Root);
        if (Query.ErrorKey is not null) return [];
        var Pending = new Stack<string>();
        Pending.Push(RootPath);
        var Result = new List<FileEntry>();
        var Batch = new List<FileEntry>();
        var BatchTimer = Stopwatch.StartNew();
        void Flush()
        {
            if (Batch.Count == 0) return;
            Cancellation.ThrowIfCancellationRequested();
            OnBatch?.Invoke(Batch.ToArray());
            Batch.Clear();
            BatchTimer.Restart();
        }
        while (Pending.TryPop(out var DirectoryPath))
        {
            Cancellation.ThrowIfCancellationRequested();
            try
            {
                foreach (var Info in new DirectoryInfo(DirectoryPath).EnumerateFileSystemInfos("*", Enumeration))
                {
                    Cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        var Attributes = Info.Attributes;
                        if (!ShowHidden && IsHidden(Attributes)) continue;
                        var IsDirectory = (Attributes & FileAttributes.Directory) != 0;
                        // Display links if they match, but never follow them into loops or outside the search root.
                        if (IsDirectory && (Attributes & FileAttributes.ReparsePoint) == 0) Pending.Push(Info.FullName);
                        var Matches = Query.Matches(Info.Name, IsDirectory);
                        if (Query.ErrorKey is not null) return [];
                        if (!Matches) continue;
                        var Entry = ReadEntry(Info, Attributes);
                        Result.Add(Entry);
                        Batch.Add(Entry);
                        if (Batch.Count >= 128 || BatchTimer.ElapsedMilliseconds >= 100) Flush();
                    }
                    catch (Exception Error) when (IsFileSystemError(Error)) { /* Keep searching readable siblings. */ }
                }
            }
            catch (Exception Error) when (IsFileSystemError(Error) && !PathComparer.Equals(DirectoryPath, RootPath)) { }
        }
        Flush();
        Cancellation.ThrowIfCancellationRequested();
        return Result;
    }

    public string NormalizePath(string Raw, string? CurrentPath = null)
    {
        if (string.IsNullOrWhiteSpace(Raw)) throw new ArgumentException(LanguageService.Text("ServicePathRequired"), nameof(Raw));
        var Value = Raw.Trim();
        if (Value.Length >= 2 && ((Value[0] == '"' && Value[^1] == '"') || (Value[0] == '\'' && Value[^1] == '\'')))
            Value = Value[1..^1];
        Value = Environment.ExpandEnvironmentVariables(Value);
        if (Value == "~" || Value.StartsWith("~/", StringComparison.Ordinal) || Value.StartsWith("~\\", StringComparison.Ordinal))
            Value = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + Value[1..];
        // A bare drive letter should go to its root, not the shell's per-drive current directory.
        if (Value.Length == 2 && char.IsAsciiLetter(Value[0]) && Value[1] == ':') Value += "\\";
        if (System.Uri.TryCreate(Value, UriKind.Absolute, out var Uri) && Uri.IsFile) Value = Uri.LocalPath;
        var BasePath = string.IsNullOrWhiteSpace(CurrentPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : System.IO.Path.GetFullPath(CurrentPath);
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Value, BasePath));
    }

    public string? GetParent(string Path) => System.IO.Directory.GetParent(this.NormalizePath(Path))?.FullName;

    public string CreateFolder(string Parent, string Name)
    {
        using var History = this.OperationHistory?.Begin("NewFolder");
        ValidateName(Name);
        var ParentPath = this.NormalizePath(Parent);
        if (!System.IO.Directory.Exists(ParentPath)) throw new DirectoryNotFoundException(LanguageService.Text("ServiceTargetMissing"));
        var Destination = System.IO.Path.Combine(ParentPath, Name);
        EnsureAbsent(Destination);
        System.IO.Directory.CreateDirectory(Destination);
        this.OperationHistory?.Record("Create", "", Destination);
        return Destination;
    }

    public string Rename(string Path, string Name)
    {
        using var History = this.OperationHistory?.Begin("Rename");
        ValidateName(Name);
        var Source = this.NormalizePath(Path);
        var Parent = System.IO.Directory.GetParent(Source)?.FullName ?? throw new IOException(LanguageService.Text("ServiceRenameRootDenied"));
        var Destination = System.IO.Path.Combine(Parent, Name);
        if (string.Equals(Source, Destination, StringComparison.Ordinal)) return Source;
        var IsDirectory = (System.IO.File.GetAttributes(Source) & FileAttributes.Directory) != 0;
        var Snapshot = this.OperationHistory?.Snapshot(Source);
        if (PathComparer.Equals(Source, Destination))
        {
            // Windows permits case-only renames through a temporary sibling name.
            var Temporary = System.IO.Path.Combine(Parent, $".CDirRename{Guid.NewGuid():N}");
            MoveEntry(Source, Temporary, IsDirectory);
            try { MoveEntry(Temporary, Destination, IsDirectory); }
            catch
            {
                try { MoveEntry(Temporary, Source, IsDirectory); } catch { }
                throw;
            }
        }
        else
        {
            EnsureAbsent(Destination);
            MoveEntry(Source, Destination, IsDirectory);
        }
        this.OperationHistory?.Record("Move", Source, Destination, Snapshot);
        return Destination;
    }

    internal void RenameItems(string[] Paths, string Name)
    {
        if (Paths.Length == 1) { this.Rename(Paths[0], Name); return; }
        ValidateName(Name);
        using var History = this.OperationHistory?.Begin("Rename");
        // Use the same numbered-name convention as the native multi-selection command.
        var Plans = Paths.Select((Source, Index) => (Source, Name: $"{System.IO.Path.GetFileNameWithoutExtension(Name)} ({Index + 1})"
            + (System.IO.Directory.Exists(Source) ? "" : System.IO.Path.GetExtension(Source)))).ToArray();
        foreach (var Plan in Plans)
        {
            EnsureAbsent(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Plan.Source)!, Plan.Name));
            this.OperationHistory?.Snapshot(Plan.Source);
        }
        var Worker = new FileSystemService();
        foreach (var Plan in Plans)
        {
            var Snapshot = this.OperationHistory?.Snapshot(Plan.Source);
            var Target = Worker.Rename(Plan.Source, Plan.Name);
            this.OperationHistory?.Record("Move", Plan.Source, Target, Snapshot);
        }
    }

    /// <summary>
    /// Copies or moves entries without overwriting. Each copy is staged before being exposed at
    /// its final name. A multi-entry operation can partially complete; completed entries remain.
    /// Directory links and junctions are refused so recursive copies cannot escape their source.
    /// </summary>
    public void CopyItems(IReadOnlyList<string> Paths, string Destination, bool Move, CancellationToken Ct = default)
    {
        using var History = this.OperationHistory?.Begin(Move ? "Move" : "Copy");
        ArgumentNullException.ThrowIfNull(Paths);
        Ct.ThrowIfCancellationRequested();
        var TargetDirectory = this.NormalizePath(Destination);
        if (!System.IO.Directory.Exists(TargetDirectory)) throw new DirectoryNotFoundException(LanguageService.Text("ServiceTargetMissing"));
        var Normalized = Paths.Select(Path => this.NormalizePath(Path)).Distinct(PathComparer).ToArray();
        var Sources = Normalized.Where(Path => !Normalized.Any(Other =>
            !PathComparer.Equals(Path, Other) && IsDescendant(Path, Other))).ToArray();
        var Plans = new List<TransferPlan>();
        var TargetNames = new HashSet<string>(PathComparer);
        // Validate the entire batch before making any changes.
        foreach (var Source in Sources)
        {
            Ct.ThrowIfCancellationRequested();
            var Attributes = System.IO.File.GetAttributes(Source);
            RejectLink(Source, Attributes);
            var IsDirectory = (Attributes & FileAttributes.Directory) != 0;
            var Name = System.IO.Path.GetFileName(Source);
            if (string.IsNullOrEmpty(Name)) throw new IOException(LanguageService.Text("ServiceCopyRootDenied"));
            var Target = System.IO.Path.Combine(TargetDirectory, Name);
            if (PathComparer.Equals(Source, Target))
            {
                if (Move) throw new IOException(LanguageService.Text("ServiceSameLocation"));
                Target = FindCopyTarget(TargetDirectory, Name, IsDirectory, TargetNames, Ct);
            }
            if (IsDirectory && (PathComparer.Equals(Source, TargetDirectory) || IsDescendant(TargetDirectory, Source)))
                throw new IOException(LanguageService.Text("ServiceRecursiveCopyDenied"));
            EnsureAbsent(Target);
            if (!TargetNames.Add(Target)) throw new IOException(LanguageService.Text("ServiceDuplicateSelection", Name));
            var Entries = IsDirectory ? ReadManifest(Source, Ct) : [];
            this.OperationHistory?.Snapshot(Source);
            Plans.Add(new TransferPlan(Source, Target, IsDirectory, Entries));
        }
        foreach (var Plan in Plans)
        {
            Ct.ThrowIfCancellationRequested();
            if (Move && (!Plan.IsDirectory || PathComparer.Equals(System.IO.Path.GetPathRoot(Plan.Source), System.IO.Path.GetPathRoot(Plan.Target))))
            {
                var Snapshot = this.OperationHistory?.Snapshot(Plan.Source);
                MoveEntry(Plan.Source, Plan.Target, Plan.IsDirectory);
                this.OperationHistory?.Record("Move", Plan.Source, Plan.Target, Snapshot);
                continue;
            }
            CopyStaged(Plan, Ct);
            try
            {
                if (Move)
                {
                    // A cross-volume move copies first. Refuse to delete if the source changed while copying.
                    VerifyManifest(Plan.Source, Plan.Entries);
                    foreach (var Entry in Plan.Entries.Where(Entry => !Entry.IsDirectory))
                        System.IO.File.Delete(System.IO.Path.Combine(Plan.Source, Entry.RelativePath));
                    foreach (var Entry in Plan.Entries.Where(Entry => Entry.IsDirectory).Reverse())
                        System.IO.Directory.Delete(System.IO.Path.Combine(Plan.Source, Entry.RelativePath), false);
                    System.IO.Directory.Delete(Plan.Source, false);
                }
            }
            finally
            {
                this.OperationHistory?.Record(Move && !System.IO.Directory.Exists(Plan.Source) && !System.IO.File.Exists(Plan.Source) ? "Move" : "Copy", Plan.Source, Plan.Target);
            }
        }
    }

    public void RecycleItems(IReadOnlyList<string> Paths)
    {
        using var History = this.OperationHistory?.Begin("Recycle");
        ArgumentNullException.ThrowIfNull(Paths);
        var Normalized = Paths.Select(Path => this.NormalizePath(Path)).Distinct(PathComparer).ToArray();
        var Sources = Normalized.Where(Path => !Normalized.Any(Other =>
            !PathComparer.Equals(Path, Other) && IsDescendant(Path, Other))).ToArray();
        foreach (var Source in Sources)
        {
            if (System.IO.Directory.GetParent(Source) is null) throw new IOException(LanguageService.Text("ServiceDeleteRootDenied"));
            var Root = System.IO.Path.GetPathRoot(Source)!;
            if (Root.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(Root).DriveType != DriveType.Fixed)
                throw new IOException(LanguageService.Text("ServiceRecycleUnsupported"));
            System.IO.File.GetAttributes(Source);
        }
        foreach (var Source in Sources)
        {
            var Attributes = System.IO.File.GetAttributes(Source);
            var Backup = this.OperationHistory?.Backup(Source);
            try
            {
                if ((Attributes & FileAttributes.Directory) != 0)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(Source, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(Source, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            }
            finally
            {
                if (Backup is not null && !System.IO.File.Exists(Source) && !System.IO.Directory.Exists(Source))
                    this.OperationHistory!.RecordDeletion(Source, Backup);
            }
        }
    }

    public void OpenPath(string Path)
    {
        var Normalized = this.NormalizePath(Path);
        System.Diagnostics.Process.Start(new ProcessStartInfo(Normalized) { UseShellExecute = true });
    }

    public void OpenTerminal(string Directory)
    {
        var Normalized = this.NormalizePath(Directory);
        if (!System.IO.Directory.Exists(Normalized)) throw new DirectoryNotFoundException(LanguageService.Text("ServiceFolderMissing"));
        var SystemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var StartInfo = new ProcessStartInfo(System.IO.Path.Combine(SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            WorkingDirectory = Normalized,
            UseShellExecute = false
        };
        StartInfo.ArgumentList.Add("-NoLogo");
        StartInfo.ArgumentList.Add("-NoExit");
        System.Diagnostics.Process.Start(StartInfo);
    }

    private static void CopyStaged(TransferPlan Plan, CancellationToken Ct)
    {
        var Parent = System.IO.Path.GetDirectoryName(Plan.Target)!;
        var Temporary = System.IO.Path.Combine(Parent, $".CDirTransfer{Guid.NewGuid():N}");
        try
        {
            if (Plan.IsDirectory)
            {
                System.IO.Directory.CreateDirectory(Temporary);
                foreach (var Entry in Plan.Entries)
                {
                    Ct.ThrowIfCancellationRequested();
                    var Source = System.IO.Path.Combine(Plan.Source, Entry.RelativePath);
                    RejectLink(Source, System.IO.File.GetAttributes(Source));
                    var Target = System.IO.Path.Combine(Temporary, Entry.RelativePath);
                    if (Entry.IsDirectory) System.IO.Directory.CreateDirectory(Target);
                    else CopyFile(Source, Target, Ct);
                }
                Ct.ThrowIfCancellationRequested();
                foreach (var Entry in Plan.Entries.Where(Entry => Entry.IsDirectory).Reverse())
                    CopyDirectoryMetadata(System.IO.Path.Combine(Plan.Source, Entry.RelativePath), System.IO.Path.Combine(Temporary, Entry.RelativePath));
                CopyDirectoryMetadata(Plan.Source, Temporary);
                System.IO.Directory.Move(Temporary, Plan.Target);
            }
            else
            {
                CopyFile(Plan.Source, Temporary, Ct);
                Ct.ThrowIfCancellationRequested();
                System.IO.File.Move(Temporary, Plan.Target, false);
            }
        }
        catch
        {
            // Delete only this operation's freshly-created staging path, never a source or final target.
            try
            {
                if (System.IO.Directory.Exists(Temporary))
                {
                    foreach (var Entry in ReadManifest(Temporary, CancellationToken.None))
                        System.IO.File.SetAttributes(System.IO.Path.Combine(Temporary, Entry.RelativePath), FileAttributes.Normal);
                    System.IO.File.SetAttributes(Temporary, FileAttributes.Normal);
                    System.IO.Directory.Delete(Temporary, true);
                }
                else if (System.IO.File.Exists(Temporary))
                {
                    System.IO.File.SetAttributes(Temporary, FileAttributes.Normal);
                    System.IO.File.Delete(Temporary);
                }
            }
            catch { /* An external lock can leave a recognizable temporary file for later cleanup. */ }
            throw;
        }
    }

    private static void CopyFile(string Source, string Destination, CancellationToken Ct)
    {
        Ct.ThrowIfCancellationRequested();
        RejectLink(Source, System.IO.File.GetAttributes(Source));
        // Windows preserves NTFS alternate streams (including Mark of the Web) and file attributes.
        CopyProgressRoutine Progress = (_, _, _, _, _, _, _, _, _) => Ct.IsCancellationRequested ? 1u : 0u;
        if (!CopyFileEx(ToWin32Path(Source), ToWin32Path(Destination), Progress, IntPtr.Zero, IntPtr.Zero, 0x00000001))
        {
            var Error = Marshal.GetLastWin32Error();
            Ct.ThrowIfCancellationRequested();
            var NativeException = new Win32Exception(Error);
            throw new IOException(LanguageService.Text("ServiceCopyFailed", System.IO.Path.GetFileName(Source), NativeException.Message), NativeException);
        }
    }

    private static void CopyDirectoryMetadata(string Source, string Destination)
    {
        System.IO.Directory.SetCreationTimeUtc(Destination, System.IO.Directory.GetCreationTimeUtc(Source));
        System.IO.Directory.SetLastWriteTimeUtc(Destination, System.IO.Directory.GetLastWriteTimeUtc(Source));
        const FileAttributes PortableAttributes = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly
            | FileAttributes.Archive | FileAttributes.NotContentIndexed;
        System.IO.File.SetAttributes(Destination, System.IO.File.GetAttributes(Source) & PortableAttributes);
    }

    private static string ToWin32Path(string Path) => Path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? Path
        : Path.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + Path[2..] : "\\\\?\\" + Path;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint CopyProgressRoutine(long TotalFileSize, long TotalBytesTransferred, long StreamSize,
        long StreamBytesTransferred, uint StreamNumber, uint CallbackReason, IntPtr SourceFile, IntPtr DestinationFile, IntPtr Data);

    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(string ExistingFile, string NewFile, CopyProgressRoutine Progress,
        IntPtr Data, IntPtr Cancel, uint Flags);

    private static List<ManifestEntry> ReadManifest(string Root, CancellationToken Ct)
    {
        var Entries = new List<ManifestEntry>();
        var Pending = new Stack<string>();
        Pending.Push(Root);
        while (Pending.Count > 0)
        {
            Ct.ThrowIfCancellationRequested();
            var Current = Pending.Pop();
            RejectLink(Current, System.IO.File.GetAttributes(Current));
            foreach (var Info in new DirectoryInfo(Current).EnumerateFileSystemInfos("*", Enumeration))
            {
                Ct.ThrowIfCancellationRequested();
                var Attributes = Info.Attributes;
                RejectLink(Info.FullName, Attributes);
                var IsDirectory = (Attributes & FileAttributes.Directory) != 0;
                Entries.Add(new ManifestEntry(System.IO.Path.GetRelativePath(Root, Info.FullName), IsDirectory,
                    Info is FileInfo File ? File.Length : 0, Info.LastWriteTimeUtc));
                if (IsDirectory) Pending.Push(Info.FullName);
            }
        }
        return Entries;
    }

    private static void VerifyManifest(string Root, IReadOnlyList<ManifestEntry> Original)
    {
        var Current = ReadManifest(Root, CancellationToken.None).ToDictionary(Entry => Entry.RelativePath, PathComparer);
        if (Current.Count != Original.Count || Original.Any(Entry => !Current.TryGetValue(Entry.RelativePath, out var Now)
            || Entry.IsDirectory != Now.IsDirectory || (!Entry.IsDirectory && (Entry.Size != Now.Size || Entry.ModifiedUtc != Now.ModifiedUtc))))
            throw new IOException(LanguageService.Text("ServiceSourceChanged"));
    }

    private static void MoveEntry(string Source, string Target, bool IsDirectory)
    {
        if (IsDirectory) System.IO.Directory.Move(Source, Target);
        else System.IO.File.Move(Source, Target, false);
    }

    private static void RejectLink(string Path, FileAttributes Attributes)
    {
        if ((Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException(LanguageService.Text("ServiceLinkUnsupported", Path));
    }

    private static void EnsureAbsent(string Path)
    {
        try
        {
            System.IO.File.GetAttributes(Path);
            throw new IOException(LanguageService.Text("ServiceTargetConflict", System.IO.Path.GetFileName(Path)));
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static string FindCopyTarget(string Directory, string Name, bool IsDirectory, HashSet<string> Reserved, CancellationToken Ct)
    {
        var Extension = IsDirectory ? "" : System.IO.Path.GetExtension(Name);
        if (Extension.Length == Name.Length) Extension = "";
        var Stem = Name[..(Name.Length - Extension.Length)];
        for (var Index = 1; ; Index++)
        {
            Ct.ThrowIfCancellationRequested();
            var CopyName = Index == 1 ? LanguageService.Text("CopyName", Stem, Extension)
                : LanguageService.Text("NumberedCopyName", Stem, Index, Extension);
            var Target = System.IO.Path.Combine(Directory, CopyName);
            if (!Reserved.Contains(Target) && !System.IO.File.Exists(Target) && !System.IO.Directory.Exists(Target)) return Target;
        }
    }

    private static void ValidateName(string Name)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name is "." or ".." || Name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
            || Name.EndsWith(' ') || Name.EndsWith('.'))
            throw new ArgumentException(LanguageService.Text("ServiceInvalidName"), nameof(Name));
        var Stem = Name.Split('.')[0].TrimEnd(' ');
        string[] Reserved = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"];
        if (Reserved.Contains(Stem, PathComparer) || (Stem.Length == 4
            && (Stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || Stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (Stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
            throw new ArgumentException(LanguageService.Text("ServiceReservedName"), nameof(Name));
    }

    private static bool IsDescendant(string Candidate, string Parent) =>
        Candidate.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(Parent) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsHidden(FileAttributes Attributes) => (Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
    private static bool IsFileSystemError(System.Exception Exception) => Exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    internal static string GetTypeKey(bool Directory, string Extension) => Directory ? "TypeFolder" : Extension switch
    {
        ".txt" or ".log" => "TypeText",
        ".md" => "TypeMarkdown",
        ".pdf" => "TypePdf",
        ".doc" or ".docx" => "TypeWord",
        ".xls" or ".xlsx" or ".csv" => "TypeSpreadsheet",
        ".ppt" or ".pptx" => "TypePresentation",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" => "TypeImage",
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "TypeVideo",
        ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" => "TypeAudio",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "TypeArchive",
        ".exe" or ".msi" => "TypeApplication",
        ".lnk" => "TypeShortcut",
        ".cs" or ".js" or ".ts" or ".py" or ".json" or ".html" or ".css" or ".xml" or ".xaml" => "TypeCode",
        "" => "TypeFile",
        _ => "TypeExtensionFile"
    };

    private sealed record ManifestEntry(string RelativePath, bool IsDirectory, long Size, DateTime ModifiedUtc);
    private sealed record TransferPlan(string Source, string Target, bool IsDirectory, List<ManifestEntry> Entries);
}
