using System.IO;
using System.Text.Json.Serialization;

namespace CDir.Models;

public sealed class CategoryFolder
{
    private FolderNode? FolderTree;
    public string FullPath { get; set; } = "";
    [JsonIgnore]
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(this.FullPath)) is { Length: > 0 } Name ? Name : this.FullPath;
    [JsonIgnore]
    public FolderNode Node => this.FolderTree ??= new FolderNode(this.Name, this.FullPath);
}
