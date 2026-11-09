namespace CDir.Models;

public sealed record FavoriteLocation(string Name, string FullPath)
{
    public FolderNode Node { get; } = new(Name, FullPath);
    public bool IsVirtual => CDir.Services.ShellLocations.IsVirtual(this.FullPath);
}
