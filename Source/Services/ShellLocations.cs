using System.Runtime.InteropServices;

namespace CDir.Services;

internal static class ShellLocations
{
    internal const string RecycleBin = "shell:RecycleBinFolder";
    internal static bool IsRecycleBin(string? Location) => string.Equals(Location, RecycleBin, StringComparison.OrdinalIgnoreCase);
    internal static IntPtr RecycleBinIdList()
    {
        var Id = new Guid("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
        Marshal.ThrowExceptionForHR(SHGetKnownFolderIDList(ref Id, 0, IntPtr.Zero, out var Result));
        return Result;
    }
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderIDList(ref Guid Id, uint Flags, IntPtr Token, out IntPtr List);
}
