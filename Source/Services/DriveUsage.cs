using System.IO;
using System.Runtime.InteropServices;

namespace CDir.Services;

internal sealed record DriveUsage(string Root, long UsedBytes, long TotalBytes)
{
    internal double UsedGB => this.UsedBytes / 1073741824.0;
    internal double TotalGB => this.TotalBytes / 1073741824.0;
    internal double UsedFraction => this.TotalBytes > 0 ? Math.Clamp((double)this.UsedBytes / this.TotalBytes, 0, 1) : 0;

    internal static string GetRoot(string Location)
    {
        try { return Path.IsPathFullyQualified(Location) ? Path.GetPathRoot(Location) ?? "" : ""; }
        catch (ArgumentException) { return ""; }
    }

    internal static DriveUsage? Read(string Root)
    {
        var Directory = Path.EndsInDirectorySeparator(Root) ? Root : Root + Path.DirectorySeparatorChar;
        if (Root.Length == 0 || !GetDiskFreeSpaceEx(Directory, out _, out var Total, out var Free)
            || Total == 0 || Total > long.MaxValue) return null;
        return new(Root, (long)(Total - Math.Min(Free, Total)), (long)Total);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string DirectoryName, out ulong FreeAvailable, out ulong TotalBytes, out ulong TotalFreeBytes);
}
