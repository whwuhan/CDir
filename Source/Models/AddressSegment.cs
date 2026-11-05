using System.IO;

namespace CDir.Models;

/// <summary>One clickable directory in an absolute Windows address; constructing it never performs filesystem I/O.</summary>
public sealed record AddressSegment(string Name, string FullPath, string Separator)
{
    public static IReadOnlyList<AddressSegment> Create(string Path)
    {
        if (string.IsNullOrWhiteSpace(Path)) return [];
        if (!System.IO.Path.IsPathFullyQualified(Path))
            throw new ArgumentException("An address breadcrumb requires a fully qualified path.", nameof(Path));

        var Absolute = System.IO.Path.GetFullPath(Path);
        var Root = System.IO.Path.GetPathRoot(Absolute);
        if (string.IsNullOrEmpty(Root))
            throw new ArgumentException("The address does not contain a filesystem root.", nameof(Path));

        var Parts = Absolute[Root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var RootPath = System.IO.Path.EndsInDirectorySeparator(Root) ? Root : Root + System.IO.Path.DirectorySeparatorChar;
        var Segments = new List<AddressSegment>(Parts.Length + 1)
        {
            new(Parts.Length == 0 ? RootPath : Root.TrimEnd('\\', '/'), RootPath, "")
        };
        var Current = RootPath;
        foreach (var Part in Parts)
        {
            Current = System.IO.Path.Combine(Current, Part);
            Segments.Add(new(Part, Current, "\\"));
        }
        return Segments.AsReadOnly();
    }
}
