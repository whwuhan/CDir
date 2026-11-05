using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CDir.Localization;
using CDir.Services;

namespace CDir.Models;

public sealed class FolderNode : INotifyPropertyChanged
{
    private VersionStatus CurrentVersionStatus = VersionStatus.None;
    public VersionStatus VersionStatus
    {
        get => this.CurrentVersionStatus;
        set { if (this.CurrentVersionStatus == value) return; this.CurrentVersionStatus = value; this.Changed(); }
    }
    private bool Expanded;
    private bool Selected;
    private readonly string OriginalName;
    private readonly string? NameKey;
    private readonly Func<string>? NameProvider;
    private DriveUsage? CurrentDriveUsage;
    public bool HasDriveUsage => this.CurrentDriveUsage is { TotalBytes: > 0 };
    public string DriveCapacityText => this.HasDriveUsage
        ? LanguageService.Text("DriveUsageCompact", this.CurrentDriveUsage!.UsedGB, this.CurrentDriveUsage.TotalGB) : "—";
    public double DriveUsagePercent => (this.CurrentDriveUsage?.UsedFraction ?? 0) * 100;
    public string DriveUsagePercentageText => this.HasDriveUsage
        ? this.CurrentDriveUsage!.UsedFraction.ToString("P1", LanguageService.Culture) : "";
    public string DriveUsageBand => this.DriveUsagePercent >= 90 ? "Critical" : this.DriveUsagePercent >= 70 ? "Warning" : "Low";
    public string DriveUsageTooltip => this.HasDriveUsage
        ? LanguageService.Text("DriveUsageTooltip", this.FullPath.TrimEnd('\\', '/'), this.CurrentDriveUsage!.UsedGB,
            this.CurrentDriveUsage.TotalGB, this.CurrentDriveUsage.UsedFraction)
        : LanguageService.Text("DriveUsageUnavailableTooltip");

    internal void UpdateDriveUsage(DriveUsage? Usage)
    {
        if (Equals(this.CurrentDriveUsage, Usage)) return;
        this.CurrentDriveUsage = Usage;
        this.NotifyDriveUsage();
    }

    private void NotifyDriveUsage()
    {
        foreach (var Property in new[] { nameof(HasDriveUsage), nameof(DriveCapacityText), nameof(DriveUsagePercent),
            nameof(DriveUsagePercentageText), nameof(DriveUsageBand), nameof(DriveUsageTooltip) }) this.Changed(Property);
    }
    public string Name => this.NameProvider?.Invoke()
        ?? (this.NameKey is { } Key ? LanguageService.Text(Key) : this.OriginalName);
    public string FullPath { get; }
    public bool IsDrive { get; }
    public bool IsPlaceholder => this.FullPath.Length == 0;
    public bool IsLoaded { get; set; }
    public int LoadVersion { get; set; }
    public Task? LoadTask { get; set; }
    public ObservableCollection<FolderNode> Children { get; } = [];
    public bool IsExpanded
    {
        get => this.Expanded;
        set
        {
            if (this.Expanded == value) return;
            this.Expanded = value;
            this.Changed();
        }
    }
    public bool IsSelected
    {
        get => this.Selected;
        set
        {
            if (this.Selected == value) return;
            this.Selected = value;
            this.Changed();
        }
    }
    public FolderNode(string Name, string FullPath, bool IsDrive = false, string? NameKey = null)
    {
        this.OriginalName = Name;
        this.NameKey = NameKey;
        this.FullPath = FullPath;
        this.IsDrive = IsDrive;
        if (!this.IsPlaceholder) this.Children.Add(new FolderNode("", "", NameKey: "TreeExpandHint"));
    }
    public FolderNode(DriveEntry Drive) : this(Drive.Name, Drive.FullPath, true) => this.NameProvider = () => Drive.Name;
    // Reuse completed directory reads, keeping mutable nodes and in-flight loads independent.
    private FolderNode(FolderNode Source) : this(Source.OriginalName, Source.FullPath, Source.IsDrive, Source.NameKey)
    {
        this.NameProvider = Source.NameProvider;
        this.VersionStatus = Source.VersionStatus;
        this.CurrentDriveUsage = Source.CurrentDriveUsage;
        if (!Source.IsLoaded) return;
        this.Children.Clear();
        foreach (var Child in Source.Children) this.Children.Add(new FolderNode(Child));
        this.IsLoaded = true;
        this.IsExpanded = Source.IsExpanded;
    }
    internal FolderNode CloneLoadedTree() => new(this);
    public void RefreshLanguage()
    {
        this.Changed(nameof(Name));
        this.NotifyDriveUsage();
        foreach (var Child in this.Children) Child.RefreshLanguage();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? Name = null) => PropertyChanged?.Invoke(this, new(Name));
}

