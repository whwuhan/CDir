namespace CDir.Models;

public sealed record FavoriteLocation(string Name, string FullPath, string? NameKey = null)
{
    public FolderNode Node { get; } = new(Name, FullPath, NameKey: NameKey);
    public bool IsVirtual => CDir.Services.ShellLocations.IsVirtual(this.FullPath);
}
