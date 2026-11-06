using System.Runtime.InteropServices;
using CDir.Services;
using Microsoft.Win32.SafeHandles;

internal sealed partial class RegressionRunner
{
    private void RunHistoryBackupChecks()
    {
        this.Test("Non-indexed source files retain their attributes in deletion backups", () =>
        {
            var Source = this.MakeDirectory("HistoryNonIndexed/Source");
            var FilePath = System.IO.Path.Combine(Source, "Tracked.txt");
            File.WriteAllText(FilePath, "original source contents");
            File.SetAttributes(FilePath, File.GetAttributes(FilePath) | FileAttributes.NotContentIndexed);
            var History = new FileOperationHistory(this.MakeDirectory("HistoryNonIndexedJournal"));
            var Backup = History.Backup(Source);
            Check(File.GetAttributes(System.IO.Path.Combine(Backup, "Tracked.txt")).HasFlag(FileAttributes.NotContentIndexed),
                "The backup must retain NotContentIndexed instead of misreporting a changed source.");
        });
        this.Test("History compares portable attributes and detects real changes despite identical timestamps", () =>
        {
            var Source = this.MakeFile("HistoryAttributes/Source.txt", "original");
            var History = new FileOperationHistory(this.MakeDirectory("HistoryAttributeJournal"));
            var Stamp = File.GetLastWriteTimeUtc(Source);
            var Snapshot = History.Snapshot(Source);
            File.SetAttributes(Source, File.GetAttributes(Source) ^ FileAttributes.Archive);
            Check(History.Snapshot(Source) == Snapshot, "The archive bit does not change backed-up contents.");
            var Backup = History.Backup(Source);
            Check(File.ReadAllText(Backup) == "original", "A cleared archive bit must not block a verified backup.");
            File.SetAttributes(Source, File.GetAttributes(Source) | FileAttributes.Hidden);
            Check(History.Snapshot(Source) != Snapshot, "User-visible file attributes still participate in verification.");
            File.SetAttributes(Source, File.GetAttributes(Source) & ~FileAttributes.Hidden);
            File.WriteAllText(Source, "modified");
            File.SetLastWriteTimeUtc(Source, Stamp);
            Check(History.Snapshot(Source) != Snapshot, "Same-length edits with restored timestamps must still be detected.");
        });
        this.Test("Compressed directory deletion backups verify across different storage attributes", () =>
        {
            var Source = this.MakeDirectory("HistoryCompressed/Source");
            CompressHistoryFixture(Source);
            var Child = Directory.CreateDirectory(System.IO.Path.Combine(Source, "Child")).FullName;
            File.WriteAllText(System.IO.Path.Combine(Child, "Tracked.txt"), "Compressed source contents");
            Check(File.GetAttributes(Source).HasFlag(FileAttributes.Compressed), "The fixture must actually be compressed.");
            var History = new FileOperationHistory(this.MakeDirectory("HistoryUncompressedJournal"));
            var Backup = History.Backup(Source);
            Check(!File.GetAttributes(Backup).HasFlag(FileAttributes.Compressed), "The backup must exercise a different directory storage layout.");
            var Comparison = new FileOperationHistory(System.IO.Path.Combine(this.Root, "HistoryComparisonJournal"));
            Check(History.Snapshot(Source) == Comparison.Snapshot(Backup), "Storage flags must not cause a false changed-source error.");
            using (History.Begin("Delete"))
            {
                Directory.Delete(Source, true);
                History.RecordDeletion(Source, Backup, Path => Directory.Delete(Path, true));
            }
            History.Replay(false);
            Check(File.ReadAllText(System.IO.Path.Combine(Source, "Child", "Tracked.txt")) == "Compressed source contents", "Delete undo restores the verified tree.");
            History.Replay(true);
            Check(!Directory.Exists(Source), "Delete redo accepts the restored tree.");
        });
    }

    private static void CompressHistoryFixture(string Path)
    {
        using var Handle = OpenHistoryFixture(Path, 0xC0000000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        ushort Format = 2;
        if (Handle.IsInvalid || !CompressHistoryFixtureNative(Handle, 0x0009C040, ref Format, 2, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new SkipTestException("Owned test volume does not support NTFS compression: " + Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenHistoryFixture(string Path, uint Access, uint Share, IntPtr Security, uint Creation, uint Flags, IntPtr Template);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CompressHistoryFixtureNative(SafeFileHandle Handle, uint Code, ref ushort Input, uint InputSize,
        IntPtr Output, uint OutputSize, out uint Returned, IntPtr Overlapped);
}
