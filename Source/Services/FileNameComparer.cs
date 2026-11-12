using System.Runtime.InteropServices;

namespace CDir.Services;

/// <summary>Windows logical name order: embedded digit runs compare as numbers.</summary>
internal sealed class FileNameComparer : IComparer<string>
{
    internal static FileNameComparer Instance { get; } = new();

    public int Compare(string? Left, string? Right)
    {
        if (ReferenceEquals(Left, Right)) return 0;
        if (Left is null) return -1;
        if (Right is null) return 1;
        return StrCmpLogicalW(Left, Right);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string Left, string Right);
}
