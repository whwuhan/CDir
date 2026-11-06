using CDir.Services;
using CDir.Localization;
using CDir.Models;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var Runner = new RegressionRunner();
return Runner.Run(args);

internal sealed partial class RegressionRunner
{
    private readonly FileSystemService Service = new();
    private readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CDirTests" + Guid.NewGuid().ToString("N"));
    private int Passed;
    private int Failed;
    private int Skipped;

    public int Run(string[] Arguments)
    {
        string[] Groups;
        try { Groups = SelectGroups(Arguments); }
        catch (ArgumentException Error)
        {
            Console.Error.WriteLine(Error.Message);
            Console.Error.WriteLine("Usage: CDirTests.exe [--group filesystem|search|language|address|version-control|all]");
            return 2;
        }
        if (Groups.Length == 0)
        {
            Console.WriteLine("Usage: CDirTests.exe [--group filesystem|search|language|address|version-control|all]");
            Console.WriteLine("Default groups: filesystem, language, address. Version-control tests require an explicit group.");
            return 0;
        }
        ConfigureBackgroundScheduling();
        Console.WriteLine("Selected groups: " + string.Join(", ", Groups));
        System.IO.Directory.CreateDirectory(this.Root);
        System.IO.File.WriteAllText(System.IO.Path.Combine(this.Root, ".TestFixture"), "Owned temporary regression fixture");
        Console.WriteLine($"Temporary fixture: {this.Root}");
        try
        {
            foreach (var Group in Groups)
                switch (Group)
                {
                    case "filesystem": this.RunFileSystemChecks(); break;
                    case "search": this.RunSearchChecks(); break;
                    case "language": this.RunLanguageChecks(); break;
                    case "address": this.RunAddressSegmentChecks(); break;
                    case "version-control": this.RunVersionControlChecks(); break;
                }
        }
        finally { this.CleanupFixture(); }
        Console.WriteLine($"Result ({string.Join(", ", Groups)}): {this.Passed} passed, {this.Failed} failed, {this.Skipped} skipped.");
        return this.Failed == 0 ? 0 : 1;
    }

    private static string[] SelectGroups(string[] Arguments)
    {
        string[] Available = ["filesystem", "search", "language", "address", "version-control"];
        if (Arguments.Length == 0) return ["filesystem", "language", "address"];
        if (Arguments is ["--help"] or ["-h"]) return [];
        var Selected = new HashSet<string>(StringComparer.Ordinal);
        for (var Index = 0; Index < Arguments.Length; Index++)
        {
            if (Arguments[Index] != "--group" || ++Index >= Arguments.Length)
                throw new ArgumentException("Expected --group followed by a test group.");
            var Names = Arguments[Index].Split(',', StringSplitOptions.TrimEntries);
            foreach (var RawName in Names)
            {
                var Name = RawName.ToLowerInvariant();
                if (Name == "all") Selected.UnionWith(Available);
                else if (Available.Contains(Name, StringComparer.Ordinal)) Selected.Add(Name);
                else throw new ArgumentException("Unknown test group: " + Name);
            }
        }
        return Available.Where(Selected.Contains).ToArray();
    }

    private static void ConfigureBackgroundScheduling()
    {
        using var Current = Process.GetCurrentProcess();
        try { Current.PriorityClass = ProcessPriorityClass.Idle; }
        catch (Exception Error) when (Error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("Could not lower test CPU priority: " + Error.Message);
        }
        // The launcher already sets very-low I/O with normal page priority. Avoid
        // resetting its memory policy by entering background mode a second time.
        if (Environment.GetEnvironmentVariable("CDIR_QUIET_RUNNER") == "1") return;
        if (OperatingSystem.IsWindows() && !SetPriorityClass(Current.Handle, 0x00100000) // PROCESS_MODE_BACKGROUND_BEGIN
            && Marshal.GetLastWin32Error() != 402) // ERROR_PROCESS_MODE_ALREADY_BACKGROUND
            Console.Error.WriteLine("Could not enable background I/O scheduling; use Source/Scripts/Test.ps1 for enforced process limits.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr Process, uint PriorityClass);

    private void RunFileSystemChecks()
    {
        this.RunHistoryBackupChecks();
        this.RunFolderSizeChecks();
        this.Test("Normalize quoted Unicode path", () =>
        {
            var Directory = this.MakeDirectory("路径 带空格");
            EqualPath(Directory, this.Service.NormalizePath("\"" + Directory + "\""));
        });
        this.Test("Expand environment variables", () =>
        {
            Environment.SetEnvironmentVariable("CDirTestRoot", this.Root);
            try { EqualPath(this.Root, this.Service.NormalizePath("%CDirTestRoot%")); }
            finally { Environment.SetEnvironmentVariable("CDirTestRoot", null); }
        });
        this.Test("Resolve relative paths against current directory", () =>
        {
            var Child = this.MakeDirectory("relative", "child");
            EqualPath(System.IO.Path.GetDirectoryName(Child)!, this.Service.NormalizePath("..", Child));
            EqualPath(Child, this.Service.NormalizePath(".\\child", System.IO.Path.GetDirectoryName(Child)!));
        });
        this.Test("Normalize home shortcut and bare drive letter", () =>
        {
            EqualPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), this.Service.NormalizePath("~"));
            EqualPath(System.IO.Path.GetPathRoot(this.Root)!, this.Service.NormalizePath(System.IO.Path.GetPathRoot(this.Root)![..2]));
        });
        this.Test("Preserve file paths for UI navigation", () =>
        {
            var File = this.MakeFile("文件 路径.txt", "hello");
            EqualPath(File, this.Service.NormalizePath(File));
        });
        this.Test("Normalize UNC address without accessing the network", () =>
        {
            EqualPath(@"\\server\share\folder", this.Service.NormalizePath(@"\\server\share\folder"));
        });
        this.Test("Reject blank address", () => Throws<ArgumentException>(() => this.Service.NormalizePath("   ")));
        this.Test("Enumerate metadata and directory-only tree entries", () =>
        {
            var Directory = this.MakeDirectory("enumerate");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Directory, "子文件夹"));
            var File = System.IO.Path.Combine(Directory, "sample.txt");
            System.IO.File.WriteAllText(File, "12345");
            var Entries = this.Service.GetEntries(Directory, false);
            Check(Entries.Count == 2, "Expected a directory and one file.");
            var FileEntry = Entries.Single(X => X.Name == "sample.txt");
            Check(!FileEntry.IsDirectory && FileEntry.Size == 5, "File metadata should expose the file size.");
            EqualPath(File, FileEntry.FullPath);
            Check(!string.IsNullOrWhiteSpace(FileEntry.TypeName), "File type should be displayable.");
            Check(!string.IsNullOrWhiteSpace(FileEntry.DisplayModified), "Modified date should be displayable.");
            var Folders = this.Service.GetDirectories(Directory, false);
            Check(Folders.Count == 1 && Folders[0].Name == "子文件夹", "Tree enumeration should include directories only.");
        });
        this.Test("Respect hidden-file and hidden-directory setting", () =>
        {
            var Directory = this.MakeDirectory("hidden");
            var File = System.IO.Path.Combine(Directory, "hidden.txt");
            System.IO.File.WriteAllText(File, "secret");
            var HiddenDirectory = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Directory, "hidden-folder")).FullName;
            System.IO.File.SetAttributes(File, System.IO.File.GetAttributes(File) | FileAttributes.Hidden);
            System.IO.File.SetAttributes(HiddenDirectory, System.IO.File.GetAttributes(HiddenDirectory) | FileAttributes.Hidden);
            Check(this.Service.GetEntries(Directory, false).Count == 0, "Hidden items must be excluded by default.");
            Check(this.Service.GetEntries(Directory, true).Count == 2, "Show hidden should reveal files and folders.");
            Check(this.Service.GetEntries(Directory, true).All(X => X.IsHidden), "Hidden flag should be reflected in metadata.");
            Check(this.Service.GetDirectories(Directory, false).Count == 0, "Hidden directories must be excluded from tree.");
            Check(this.Service.GetDirectories(Directory, true).Count == 1, "Show hidden should reveal tree directories.");
        });
        this.Test("Report missing-directory errors", () => Throws<IOException>(() => this.Service.GetEntries(System.IO.Path.Combine(this.Root, "missing"), false)));
        this.Test("Enumerate containing drive", () =>
        {
            var ExpectedRoot = System.IO.Path.GetPathRoot(this.Root)!;
            Check(this.Service.GetRoots().Any(X => SamePath(X.FullPath, ExpectedRoot)), "The fixture's drive must be listed.");
        });
        this.Test("Find parent directory", () =>
        {
            var Directory = this.MakeDirectory("parent", "child");
            EqualPath(System.IO.Path.GetDirectoryName(Directory)!, this.Service.GetParent(Directory)!);
        });
        this.Test("Create and rename Unicode folder", () =>
        {
            var Parent = this.MakeDirectory("create");
            var Created = this.Service.CreateFolder(Parent, "项目 2026");
            Check(System.IO.Directory.Exists(Created), "CreateFolder should create the requested folder.");
            var Renamed = this.Service.Rename(Created, "工作 文档");
            Check(System.IO.Directory.Exists(Renamed) && !System.IO.Directory.Exists(Created), "Rename should move the directory.");
            EqualPath(System.IO.Path.Combine(Parent, "工作 文档"), Renamed);
        });
        this.Test("Reject folder-name traversal and reserved names", () =>
        {
            var Parent = this.MakeDirectory("invalid-name");
            foreach (var Name in new[] { "..", ".", "a\\b", "a/b", "bad<name", "CON", "NUL.txt" })
                Throws<ArgumentException>(() => this.Service.CreateFolder(Parent, Name));
        });
        this.Test("Reject create-folder conflict", () =>
        {
            var Parent = this.MakeDirectory("create-conflict");
            this.Service.CreateFolder(Parent, "existing");
            Throws<IOException>(() => this.Service.CreateFolder(Parent, "existing"));
        });
        this.Test("Rename file and preserve content", () =>
        {
            var Source = this.MakeFile("rename\\old.txt", "rename contents");
            var Renamed = this.Service.Rename(Source, "新名字.txt");
            Check(!System.IO.File.Exists(Source), "Original filename should disappear.");
            Check(System.IO.File.ReadAllText(Renamed) == "rename contents", "Renaming should preserve content.");
        });
        this.Test("Support case-only file rename", () =>
        {
            var Source = this.MakeFile("case-rename\\report.txt", "case content");
            var Renamed = this.Service.Rename(Source, "REPORT.txt");
            Check(System.IO.Path.GetFileName(System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(Source)!).Single()) == "REPORT.txt", "Windows directory entry should use the requested casing.");
            Check(System.IO.File.ReadAllText(Renamed) == "case content", "Case-only rename should preserve contents.");
        });
        this.Test("Reject rename collision without overwriting", () =>
        {
            var Source = this.MakeFile("rename-conflict\\first.txt", "first");
            var Target = this.MakeFile("rename-conflict\\second.txt", "second");
            Throws<IOException>(() => this.Service.Rename(Source, "second.txt"));
            Check(System.IO.File.ReadAllText(Source) == "first" && System.IO.File.ReadAllText(Target) == "second", "Both original files should remain intact.");
        });
        this.Test("Copy file with Unicode name", () =>
        {
            var Source = this.MakeFile("copy-source\\报告 数据.txt", "copy me");
            var Destination = this.MakeDirectory("copy-target");
            this.Service.CopyItems(new[] { Source }, Destination, false);
            Check(System.IO.File.ReadAllText(System.IO.Path.Combine(Destination, System.IO.Path.GetFileName(Source))) == "copy me", "Copied file should have identical contents.");
            Check(System.IO.File.Exists(Source), "Copy must preserve source.");
        });
        this.Test("Preserve hidden read-only non-indexed attributes and NTFS metadata streams", () =>
        {
            var Source = this.MakeFile("metadata-source\\download.txt", "download content");
            var Destination = this.MakeDirectory("metadata-target");
            var Target = System.IO.Path.Combine(Destination, "download.txt");
            const string Zone = "[ZoneTransfer]\r\nZoneId=3\r\n";
            System.IO.File.WriteAllText(Source + ":Zone.Identifier", Zone);
            System.IO.File.SetAttributes(Source, System.IO.File.GetAttributes(Source) | FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.NotContentIndexed);
            try
            {
                this.Service.CopyItems(new[] { Source }, Destination, false);
                var Attributes = System.IO.File.GetAttributes(Target);
                Check((Attributes & FileAttributes.Hidden) != 0, "Copy should preserve hidden attribute.");
                Check((Attributes & FileAttributes.ReadOnly) != 0, "Copy should preserve read-only attribute.");
                Check((Attributes & FileAttributes.NotContentIndexed) != 0, "Copy should preserve indexing preferences so deletion backups remain verifiable.");
                Check(System.IO.File.ReadAllText(Target + ":Zone.Identifier") == Zone, "Copy should preserve NTFS streams, including download-origin metadata.");
                Check(System.IO.File.GetLastWriteTimeUtc(Source) == System.IO.File.GetLastWriteTimeUtc(Target), "Copy should preserve last-write timestamp.");
            }
            finally
            {
                System.IO.File.SetAttributes(Source, FileAttributes.Normal);
                if (System.IO.File.Exists(Target)) System.IO.File.SetAttributes(Target, FileAttributes.Normal);
            }
        });
        this.Test("Copy nested directory and empty folders", () =>
        {
            var Source = this.MakeDirectory("tree-source");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Source, "empty"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Source, "nested"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(Source, "nested", "file.txt"), "nested contents");
            var Destination = this.MakeDirectory("tree-target");
            this.Service.CopyItems(new[] { Source }, Destination, false);
            var Copied = System.IO.Path.Combine(Destination, "tree-source");
            Check(System.IO.Directory.Exists(System.IO.Path.Combine(Copied, "empty")), "Empty subdirectories should be copied.");
            Check(System.IO.File.ReadAllText(System.IO.Path.Combine(Copied, "nested", "file.txt")) == "nested contents", "Nested contents should be copied.");
        });
        this.Test("Reject copy conflict without overwriting", () =>
        {
            var Source = this.MakeFile("copy-conflict-source\\file.txt", "source");
            var Destination = this.MakeDirectory("copy-conflict-target");
            System.IO.File.WriteAllText(System.IO.Path.Combine(Destination, "file.txt"), "target");
            Throws<IOException>(() => this.Service.CopyItems(new[] { Source }, Destination, false));
            Check(System.IO.File.ReadAllText(System.IO.Path.Combine(Destination, "file.txt")) == "target", "Existing destination must not be overwritten.");
            Check(System.IO.File.ReadAllText(Source) == "source", "Source must remain intact.");
        });
        this.Test("Preflight batch conflicts before copying earlier entries", () =>
        {
            var First = this.MakeFile("batch-source\\first.txt", "first");
            var Second = this.MakeFile("batch-source\\second.txt", "second");
            var Destination = this.MakeDirectory("batch-target");
            System.IO.File.WriteAllText(System.IO.Path.Combine(Destination, "second.txt"), "existing");
            Throws<IOException>(() => this.Service.CopyItems(new[] { First, Second }, Destination, false));
            Check(!System.IO.File.Exists(System.IO.Path.Combine(Destination, "first.txt")), "A later conflict should be detected before creating an earlier item.");
            Check(System.IO.File.ReadAllText(System.IO.Path.Combine(Destination, "second.txt")) == "existing", "Existing target should remain unchanged.");
        });
        this.Test("Copy within the same directory creates numbered siblings without overwriting", () =>
        {
            var Source = this.MakeFile("same-directory\\file.txt", "original");
            var Parent = System.IO.Path.GetDirectoryName(Source)!;
            var FirstCopy = System.IO.Path.Combine(Parent, LanguageService.Text("CopyName", "file", ".txt"));
            var SecondCopy = System.IO.Path.Combine(Parent, LanguageService.Text("NumberedCopyName", "file", 2, ".txt"));
            this.Service.CopyItems(new[] { Source }, Parent, false);
            this.Service.CopyItems(new[] { Source }, Parent, false);
            Check(System.IO.File.ReadAllText(Source) == "original" && System.IO.File.ReadAllText(FirstCopy) == "original"
                && System.IO.File.ReadAllText(SecondCopy) == "original", "Repeated paste must preserve the original and create distinct copies with the extension intact.");
            Throws<IOException>(() => this.Service.CopyItems(new[] { Source }, Parent, true));
        });
        this.Test("Duplicate mixed folders and extensionless dotfiles in all supported languages", () =>
        {
            var InitialLanguage = LanguageService.CurrentLanguage;
            try
            {
                foreach (var Language in LanguageService.SupportedLanguages)
                {
                    LanguageService.SetLanguage(Language.Code);
                    var Parent = this.MakeDirectory("same-folder-" + Language.Code);
                    var Folder = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Parent, "Folder.with.dots")).FullName;
                    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Folder, "Empty"));
                    System.IO.File.WriteAllText(System.IO.Path.Combine(Folder, "Nested.txt"), "nested");
                    var DotFile = System.IO.Path.Combine(Parent, ".gitignore");
                    System.IO.File.WriteAllText(DotFile, "dotfile");
                    this.Service.CopyItems(new[] { Folder, DotFile }, Parent, false);
                    var FolderCopy = System.IO.Path.Combine(Parent, LanguageService.Text("CopyName", "Folder.with.dots", ""));
                    Check(System.IO.File.ReadAllText(System.IO.Path.Combine(FolderCopy, "Nested.txt")) == "nested"
                        && System.IO.Directory.Exists(System.IO.Path.Combine(FolderCopy, "Empty"))
                        && System.IO.File.ReadAllText(System.IO.Path.Combine(Parent, LanguageService.Text("CopyName", ".gitignore", ""))) == "dotfile",
                        "Mixed selection should duplicate recursively with localized names and preserve dotfiles and folder names.");
                }
            }
            finally { LanguageService.SetLanguage(InitialLanguage); }
        });
        this.Test("Duplicate names skip existing files and directories without overwriting", () =>
        {
            var Source = this.MakeFile("duplicate-collisions\\file.txt", "source");
            var Parent = System.IO.Path.GetDirectoryName(Source)!;
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Parent, LanguageService.Text("CopyName", "file", ".txt")));
            var Existing = System.IO.Path.Combine(Parent, LanguageService.Text("NumberedCopyName", "file", 2, ".txt"));
            System.IO.File.WriteAllText(Existing, "keep");
            this.Service.CopyItems(new[] { Source }, Parent, false);
            Check(System.IO.File.ReadAllText(Existing) == "keep"
                && System.IO.File.ReadAllText(System.IO.Path.Combine(Parent, LanguageService.Text("NumberedCopyName", "file", 3, ".txt"))) == "source",
                "Copy numbering should skip names occupied by either a file or directory.");
        });
        this.Test("Reject directory copy into itself or descendant", () =>
        {
            var Source = this.MakeDirectory("recursive-source");
            var Descendant = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Source, "child")).FullName;
            Throws<IOException>(() => this.Service.CopyItems(new[] { Source }, Source, false));
            Throws<IOException>(() => this.Service.CopyItems(new[] { Source }, Descendant, false));
            Check(!System.IO.Directory.Exists(System.IO.Path.Combine(Descendant, "recursive-source")), "Recursive target should not be created.");
        });
        this.Test("Allow sibling paths with the same name prefix", () =>
        {
            var Source = this.MakeDirectory("prefix", "data");
            System.IO.File.WriteAllText(System.IO.Path.Combine(Source, "file.txt"), "prefix safe");
            var Destination = this.MakeDirectory("prefix", "database");
            this.Service.CopyItems(new[] { Source }, Destination, false);
            Check(System.IO.File.ReadAllText(System.IO.Path.Combine(Destination, "data", "file.txt")) == "prefix safe", "A shared string prefix is not a descendant directory.");
        });
        this.Test("Move file and directory", () =>
        {
            var Source = this.MakeFile("move-source\\file.txt", "move content");
            var Directory = this.MakeDirectory("move-folder");
            System.IO.File.WriteAllText(System.IO.Path.Combine(Directory, "child.txt"), "child");
            var Destination = this.MakeDirectory("move-target");
            this.Service.CopyItems(new[] { Source, Directory }, Destination, true);
            Check(!System.IO.File.Exists(Source) && !System.IO.Directory.Exists(Directory), "Successful move should remove sources.");
            Check(System.IO.File.ReadAllText(System.IO.Path.Combine(Destination, "file.txt")) == "move content", "Moved file contents should match.");
            Check(System.IO.File.ReadAllText(System.IO.Path.Combine(Destination, "move-folder", "child.txt")) == "child", "Moved directory contents should match.");
        });
        this.Test("Respect cancellation before file operations", () =>
        {
            var Source = this.MakeFile("cancel-source\\file.txt", "unchanged");
            var Destination = this.MakeDirectory("cancel-target");
            using var Cancellation = new CancellationTokenSource();
            Cancellation.Cancel();
            Throws<OperationCanceledException>(() => this.Service.CopyItems(new[] { Source }, Destination, false, Cancellation.Token));
            Check(!System.IO.File.Exists(System.IO.Path.Combine(Destination, "file.txt")) && System.IO.File.Exists(Source), "Canceled operation should preserve source and leave no target.");
        });
        this.Test("Reject directory reparse points during recursive copy", () =>
        {
            var Source = this.MakeDirectory("link-source");
            var Destination = this.MakeDirectory("link-target");
            var Link = System.IO.Path.Combine(Source, "loop");
            try { System.IO.Directory.CreateSymbolicLink(Link, Source); }
            catch (System.Exception Ex) when (Ex is UnauthorizedAccessException or IOException)
            {
                CreateJunction(Link, Source, Ex);
            }
            try { Throws<IOException>(() => this.Service.CopyItems(new[] { Source }, Destination, false)); }
            finally { System.IO.Directory.Delete(Link, false); }
        });
    }

    private void RunAddressSegmentChecks()
    {
        this.Test("Address segments preserve drive roots Unicode spaces and exact ancestor destinations", () =>
        {
            var Segments = AddressSegment.Create(@"C:\路径 带空格\Child");
            Check(Segments.Select(Segment => Segment.Name).SequenceEqual(new[] { "C:", "路径 带空格", "Child" }), "Each folder should be a separate readable segment.");
            Check(Segments.Select(Segment => Segment.FullPath).SequenceEqual(new[] { @"C:\", @"C:\路径 带空格", @"C:\路径 带空格\Child" }), "Segment destinations must be cumulative absolute directories.");
            Check(Segments.Select(Segment => Segment.Separator).SequenceEqual(new[] { "", "\\", "\\" }), "Only non-root segments should have separators.");
            var Root = AddressSegment.Create(@"C:\");
            Check(Root.Count == 1 && Root[0] == new AddressSegment(@"C:\", @"C:\", ""), "A drive-only address must retain its root slash.");
        });
        this.Test("Address segments keep UNC server and share together without accessing the network", () =>
        {
            var Segments = AddressSegment.Create(@"\\unreachable-server\share name\目录\Child");
            Check(Segments.Count == 3 && Segments[0].Name == @"\\unreachable-server\share name", "UNC server/share must be one root segment.");
            Check(Segments[0].FullPath == @"\\unreachable-server\share name\", "UNC root destinations require a trailing slash.");
            Check(Segments[1].FullPath == @"\\unreachable-server\share name\目录"
                && Segments[2].FullPath == @"\\unreachable-server\share name\目录\Child", "UNC child destinations must stay inside the share.");
            var Root = AddressSegment.Create(@"\\unreachable-server\share name");
            Check(Root.Count == 1 && Root[0].Name == @"\\unreachable-server\share name\"
                && Root[0].FullPath == @"\\unreachable-server\share name\", "A share-only address should show the complete navigable root.");
        });
        this.Test("Address segments normalize trailing and mixed separators without empty clickable folders", () =>
        {
            var Segments = AddressSegment.Create("C:/Parent/Child/../Child/");
            Check(Segments.Select(Segment => Segment.Name).SequenceEqual(new[] { "C:", "Parent", "Child" }), "Trailing separators and dot components must not become breadcrumbs.");
            Check(Segments[^1].FullPath == @"C:\Parent\Child", "The final destination must be normalized.");
            Check(AddressSegment.Create(@"\\server\share\Child\").Count == 2, "UNC trailing separators must not create an empty folder.");
        });
        this.Test("Address segments leave blank addresses empty and reject drive-relative or rooted-relative paths", () =>
        {
            Check(AddressSegment.Create("").Count == 0 && AddressSegment.Create("   ").Count == 0, "An empty address must not synthesize a current-directory segment.");
            foreach (var Relative in new[] { "Child", @"C:Child", @"\Child", "." })
                Throws<ArgumentException>(() => AddressSegment.Create(Relative));
        });
    }

    private void RunLanguageChecks()
    {
        var InitialLanguage = LanguageService.CurrentLanguage;
        try
        {
            this.Test("Four embedded translations have complete keys and matching format arguments", () =>
            {
                var ResourceNames = new[] { "Chinese.json", "Japanese.json", "Korean.json", "English.json" };
                var Resources = ResourceNames.Select(ReadTranslation).ToArray();
                var ExpectedKeys = Resources[0].Keys.Order(StringComparer.Ordinal).ToArray();
                Check(ExpectedKeys.Length > 0, "Chinese resources must not be empty.");
                var RequiredKeys = new[] { "WindowTitle", "NameColumn", "NewFolder", "Rename", "CommonConfirm", "CommonCancel", "FavoriteHome", "TypeText", "ServicePathRequired", "BrowseHint", "FilteredItemsCount", "ItemsCount", "SelectedCount", "NoResults", "NoResultsHint" };
                Check(RequiredKeys.All(Resources[0].ContainsKey), "Resources must define the keys used by the language UI checks.");
                Check(LanguageService.SupportedLanguages.Select(Language => Language.Code).Order().SequenceEqual(new[] { "en-US", "ja-JP", "ko-KR", "zh-CN" }), "Exactly the four supported language codes should be exposed.");
                for (var I = 0; I < Resources.Length; I++)
                {
                    Check(Resources[I].Keys.Order(StringComparer.Ordinal).SequenceEqual(ExpectedKeys), ResourceNames[I] + " has missing or extra resource keys.");
                    foreach (var Key in ExpectedKeys)
                    {
                        var Template = Resources[I][Key];
                        Check(!string.IsNullOrWhiteSpace(Template), ResourceNames[I] + ": " + Key + " must not be blank.");
                        var Format = CompositeFormat.Parse(Template);
                        var Arguments = Enumerable.Repeat<object?>("Sample", Format.MinimumArgumentCount).ToArray();
                        _ = string.Format(CultureInfo.InvariantCulture, Format, Arguments);
                        Check(FormatSlots(Template).SequenceEqual(FormatSlots(Resources[0][Key])), ResourceNames[I] + ": " + Key + " changed its format placeholders.");
                    }
                }
            });
            this.RunEnglishTextCaseChecks();
            this.Test("Language codes normalize and unsupported codes fall back", () =>
            {
                Check(LanguageService.CurrentLanguage == "en-US", "The initial language should default to English.");
                LanguageService.SetLanguage(" EN-us ");
                Check(LanguageService.CurrentLanguage == "en-US", "Language code casing and whitespace should normalize.");
                LanguageService.SetLanguage("unsupported-language");
                Check(LanguageService.CurrentLanguage == "en-US", "An unsupported saved language should fall back to English.");
                LanguageService.SetLanguage(null);
                Check(LanguageService.CurrentLanguage == "en-US", "A missing saved language should fall back to English.");
            });
            this.Test("Cached file metadata refreshes in all four languages", () =>
            {
                var SamplePath = this.MakeFile("language-model\\sample.txt", "language sample");
                var Entry = this.Service.GetEntries(System.IO.Path.GetDirectoryName(SamplePath)!, true).Single();
                Check(Entry.TypeKey == "TypeText", "Text files should carry a stable translation key.");
                var Changes = new HashSet<string?>();
                Entry.PropertyChanged += (_, Event) => Changes.Add(Event.PropertyName);
                var Labels = new HashSet<string>(StringComparer.Ordinal);
                foreach (var Language in LanguageService.SupportedLanguages)
                {
                    LanguageService.SetLanguage(Language.Code);
                    Changes.Clear();
                    Entry.RefreshLanguage();
                    Check(Entry.TypeName == LanguageService.Text("TypeText"), "A cached type label should follow the active language.");
                    Check(Entry.DisplayModified == Entry.Modified.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture), "Modified dates must retain year/month/day order and seconds in every language.");
                    Check(new[] { nameof(FileEntry.TypeName), nameof(FileEntry.DisplaySize), nameof(FileEntry.DisplayModified) }.All(Changes.Contains), "Bindings must receive metadata refresh notifications.");
                    Labels.Add(Entry.TypeName);
                }
                Check(Labels.Count > 1, "Metadata should visibly change between languages.");
            });
            this.Test("Modified dates preserve seconds and distinguish midnight noon and afternoon in both clock formats", () =>
            {
                var Samples = new[]
                {
                    (Hour: 0, Expected24: "2026/01/02 00:04:07", Expected12: "2026/01/02 12:04:07 AM"),
                    (Hour: 1, Expected24: "2026/01/02 01:04:07", Expected12: "2026/01/02 01:04:07 AM"),
                    (Hour: 11, Expected24: "2026/01/02 11:04:07", Expected12: "2026/01/02 11:04:07 AM"),
                    (Hour: 12, Expected24: "2026/01/02 12:04:07", Expected12: "2026/01/02 12:04:07 PM"),
                    (Hour: 13, Expected24: "2026/01/02 13:04:07", Expected12: "2026/01/02 01:04:07 PM"),
                    (Hour: 23, Expected24: "2026/01/02 23:04:07", Expected12: "2026/01/02 11:04:07 PM")
                };
                foreach (var Language in LanguageService.SupportedLanguages)
                {
                    LanguageService.SetLanguage(Language.Code);
                    foreach (var Sample in Samples)
                    {
                        var Entry = new FileEntry { Modified = new DateTime(2026, 1, 2, Sample.Hour, 4, 7) };
                        Check(Entry.DisplayModified == Sample.Expected24, "Default timestamps must use 24-hour time with fixed separators and seconds.");
                        var Changes = new List<string?>();
                        Entry.PropertyChanged += (_, Event) => Changes.Add(Event.PropertyName);
                        Entry.Use12HourClock = true;
                        Check(Entry.DisplayModified == Sample.Expected12 && Changes.Contains(nameof(FileEntry.DisplayModified)),
                            "Changing the clock must notify displayed dates and disambiguate AM/PM.");
                        Entry.RefreshLanguage();
                        Check(Entry.DisplayModified == Sample.Expected12, "Language changes must preserve the selected clock and date order.");
                        Entry.Use12HourClock = false;
                        Check(Entry.DisplayModified == Sample.Expected24, "Switching back must restore 24-hour timestamps.");
                    }
                }
            });
            this.Test("Filesystem validation errors use the active language", () =>
            {
                foreach (var Language in LanguageService.SupportedLanguages)
                {
                    LanguageService.SetLanguage(Language.Code);
                    try { this.Service.NormalizePath(" "); }
                    catch (ArgumentException Error)
                    {
                        Check(Error.Message.StartsWith(LanguageService.Text("ServicePathRequired"), StringComparison.Ordinal), "Path validation should use the active language.");
                        continue;
                    }
                    throw new InvalidOperationException("Blank paths must be rejected in every language.");
                }
            });
        }
        finally { LanguageService.SetLanguage(InitialLanguage); }
    }

    private static Dictionary<string, string> ReadTranslation(string FileName)
    {
        var Assembly = typeof(LanguageService).Assembly;
        var ResourceName = Assembly.GetManifestResourceNames().Single(Name => Name.EndsWith(".Localization." + FileName, StringComparison.Ordinal));
        using var Stream = Assembly.GetManifestResourceStream(ResourceName) ?? throw new InvalidOperationException("Missing translation resource: " + FileName);
        using var Document = JsonDocument.Parse(Stream);
        var Properties = Document.RootElement.EnumerateObject().ToArray();
        Check(Properties.Select(Property => Property.Name).Distinct(StringComparer.Ordinal).Count() == Properties.Length, FileName + " contains duplicate resource keys.");
        return Properties.ToDictionary(Property => Property.Name, Property => Property.Value.GetString() ?? "", StringComparer.Ordinal);
    }

    private static int[] FormatSlots(string Template) => Regex.Matches(Template.Replace("{{", "").Replace("}}", ""), @"\{(\d+)[^{}]*\}")
        .Select(Match => int.Parse(Match.Groups[1].Value, CultureInfo.InvariantCulture)).Order().ToArray();

    private void Test(string Name, System.Action Body)
    {
        try { Body(); this.Passed++; Console.WriteLine("PASS " + Name); }
        catch (SkipTestException Ex) { this.Skipped++; Console.WriteLine("SKIP " + Name + " — " + Ex.Message); }
        catch (System.Exception Ex) { this.Failed++; Console.WriteLine("FAIL " + Name + " — " + Ex); }
        finally { Thread.Sleep(15); }
    }

    private string MakeDirectory(params string[] Segments)
    {
        var Path = Segments.Aggregate(this.Root, System.IO.Path.Combine);
        System.IO.Directory.CreateDirectory(Path);
        return Path;
    }

    private string MakeFile(string RelativePath, string Contents)
    {
        var Path = System.IO.Path.Combine(this.Root, RelativePath);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        System.IO.File.WriteAllText(Path, Contents);
        return Path;
    }

    private static void Check(bool Condition, string Message)
    {
        if (!Condition) throw new InvalidOperationException(Message);
    }

    private static bool SamePath(string Left, string Right) => string.Equals(
        System.IO.Path.GetFullPath(Left).TrimEnd(System.IO.Path.DirectorySeparatorChar),
        System.IO.Path.GetFullPath(Right).TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static void EqualPath(string Expected, string Actual) => Check(SamePath(Expected, Actual), $"Expected path '{Expected}', got '{Actual}'.");

    private static void Throws<TException>(System.Action Action) where TException : System.Exception
    {
        try { Action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + " but operation succeeded.");
    }

    private static void CreateJunction(string Link, string Target, System.Exception SymbolicLinkError)
    {
        // Junction creation does not need the symbolic-link privilege. Paths travel as
        // environment values, never as interpolated PowerShell program text.
        var Start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        Start.Environment["CDirTestLink"] = Link;
        Start.Environment["CDirTestTarget"] = Target;
        Start.ArgumentList.Add("-NoProfile");
        Start.ArgumentList.Add("-NonInteractive");
        Start.ArgumentList.Add("-Command");
        Start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:CDirTestLink -Target $env:CDirTestTarget -ErrorAction Stop | Out-Null");
        using System.Diagnostics.Process Process = System.Diagnostics.Process.Start(Start)!;
        var Output = Process.StandardOutput.ReadToEnd();
        var Error = Process.StandardError.ReadToEnd();
        Process.WaitForExit();
        if (Process.ExitCode != 0)
            throw new SkipTestException("Windows did not permit a temporary symbolic link or junction: " + SymbolicLinkError.Message + " " + Error + Output);
    }

    private void CleanupFixture()
    {
        var Resolved = System.IO.Path.GetFullPath(this.Root);
        var TempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!Resolved.StartsWith(TempRoot, StringComparison.OrdinalIgnoreCase) ||
            !System.IO.Path.GetFileName(Resolved).StartsWith("CDirTests", StringComparison.Ordinal) ||
            !System.IO.File.Exists(System.IO.Path.Combine(Resolved, ".TestFixture")))
            throw new InvalidOperationException("Refusing cleanup outside the owned test fixture.");
        foreach (var File in System.IO.Directory.EnumerateFiles(Resolved, "*", new System.IO.EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = System.IO.FileAttributes.ReparsePoint
        }))
            System.IO.File.SetAttributes(File, System.IO.File.GetAttributes(File) & ~System.IO.FileAttributes.ReadOnly);
        System.IO.Directory.Delete(Resolved, true);
    }
}

internal sealed class SkipTestException(string Message) : System.Exception(Message);
