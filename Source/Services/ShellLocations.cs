using System.Runtime.InteropServices;

namespace CDir.Services;

internal static class ShellLocations
{
    internal const string RecycleBin = "shell:RecycleBinFolder";
    internal const string Computer = "shell:MyComputerFolder";
    internal static bool IsComputer(string? Location) => string.Equals(Location, Computer, StringComparison.OrdinalIgnoreCase);
    internal static bool IsVirtual(string? Location) => IsRecycleBin(Location) || IsComputer(Location);
    internal static bool IsRecycleBin(string? Location) => string.Equals(Location, RecycleBin, StringComparison.OrdinalIgnoreCase);
    internal static IntPtr RecycleBinIdList()
        => KnownFolderIdList(new Guid("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC"));
    internal static IntPtr ComputerIdList()
        => KnownFolderIdList(new Guid("0AC0837C-BBF8-452A-850D-79D08E667CA7"));
    private static IntPtr KnownFolderIdList(Guid Id)
    {
        Marshal.ThrowExceptionForHR(SHGetKnownFolderIDList(ref Id, 0, IntPtr.Zero, out var Result));
        return Result;
    }
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderIDList(ref Guid Id, uint Flags, IntPtr Token, out IntPtr List);
}
