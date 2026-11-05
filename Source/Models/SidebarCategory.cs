using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;

namespace CDir.Models;

public sealed class SidebarCategory : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    private string CategoryName = "";
    public string Name
    {
        get => this.CategoryName;
        set
        {
            if (this.CategoryName == value) return;
            this.CategoryName = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }
    public bool IsExpanded { get; set; } = true;
    public ObservableCollection<CategoryFolder> Folders { get; set; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    public SidebarCategory Clone()
    {
        var Copy = new SidebarCategory { Id = this.Id, Name = this.Name.Trim(), IsExpanded = this.IsExpanded };
        var Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var Folder in this.Folders ?? [])
        {
            try
            {
                if (Folder is null || string.IsNullOrWhiteSpace(Folder.FullPath) || !Path.IsPathFullyQualified(Folder.FullPath)) continue;
                var FullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Folder.FullPath));
                if (Paths.Add(FullPath)) Copy.Folders.Add(new CategoryFolder { FullPath = FullPath });
            }
            catch (Exception Error) when (Error is ArgumentException or NotSupportedException or IOException) { }
        }
        return Copy;
    }
}
