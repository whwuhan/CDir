using System.ComponentModel;
using System.Globalization;
using CDir.Localization;

namespace CDir.Models;

public sealed class FileEntry : INotifyPropertyChanged
{
    private bool CurrentIsRenaming;
    public bool IsRenaming
    {
        get => this.CurrentIsRenaming;
        set { if (this.CurrentIsRenaming == value) return; this.CurrentIsRenaming = value; PropertyChanged?.Invoke(this, new(nameof(IsRenaming))); }
    }
    private bool CurrentIsCut;
    public bool IsCut
    {
        get => this.CurrentIsCut;
        set { if (this.CurrentIsCut == value) return; this.CurrentIsCut = value; PropertyChanged?.Invoke(this, new(nameof(IsCut))); }
    }
    private VersionStatus CurrentVersionStatus = VersionStatus.None;
    public VersionStatus VersionStatus
    {
        get => this.CurrentVersionStatus;
        set { if (this.CurrentVersionStatus == value) return; this.CurrentVersionStatus = value; PropertyChanged?.Invoke(this, new(nameof(VersionStatus))); }
    }
    private string OriginalTypeName = string.Empty;
    private string? CachedTypeLanguage;
    private string? CachedTypeName;
    public string ViewGroup { get; set; } = "";
    internal long ViewGroupOrder { get; set; }
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public bool IsDrive { get; init; }
    public string Extension { get; init; } = string.Empty;
    public string? TypeKey { get; init; }
    public object?[] TypeArguments { get; init; } = [];
    public string TypeName
    {
        get
        {
            if (this.TypeKey is not { } Key) return this.OriginalTypeName;
            var Language = LanguageService.CurrentLanguage;
            if (this.CachedTypeLanguage != Language)
            {
                this.CachedTypeName = LanguageService.Text(Key, this.TypeArguments);
                this.CachedTypeLanguage = Language;
            }
            return this.CachedTypeName!;
        }
        init => this.OriginalTypeName = value;
    }
    private long? CurrentSize;
    public long? Size { get => this.CurrentSize; init => this.CurrentSize = value; }
    internal bool FolderSizeComplete { get; private set; }
    private bool FolderSizePartial;
    internal void UpdateFolderSize(long Bytes, bool Complete)
    {
        if (!this.IsDirectory) return;
        this.CurrentSize = Bytes;
        this.FolderSizeComplete = Complete;
        this.FolderSizePartial = !Complete;
        PropertyChanged?.Invoke(this, new(nameof(Size)));
        PropertyChanged?.Invoke(this, new(nameof(DisplaySize)));
    }
    internal void ResetFolderSize()
    {
        if (!this.IsDirectory || this.CurrentSize is null && !this.FolderSizeComplete && !this.FolderSizePartial) return;
        this.CurrentSize = null;
        this.FolderSizeComplete = this.FolderSizePartial = false;
        PropertyChanged?.Invoke(this, new(nameof(Size)));
        PropertyChanged?.Invoke(this, new(nameof(DisplaySize)));
    }
    private DateTime CurrentModified;
    public DateTime Modified { get => this.CurrentModified; init => this.CurrentModified = value; }
    private bool CurrentIsHidden;
    public bool IsHidden { get => this.CurrentIsHidden; init => this.CurrentIsHidden = value; }

    // Filesystem notifications often only change metadata (for example while
    // generating IDE projects). Keep the bound item and its realized icon alive.
    internal void UpdateMetadata(FileEntry Updated)
    {
        if (this.Modified != Updated.Modified)
        {
            this.CurrentModified = Updated.Modified;
            PropertyChanged?.Invoke(this, new(nameof(Modified)));
            PropertyChanged?.Invoke(this, new(nameof(DisplayModified)));
            if (this.IsDirectory) this.ResetFolderSize();
        }
        if (!this.IsDirectory && this.Size != Updated.Size)
        {
            this.CurrentSize = Updated.Size;
            PropertyChanged?.Invoke(this, new(nameof(Size)));
            PropertyChanged?.Invoke(this, new(nameof(DisplaySize)));
        }
        if (this.IsHidden != Updated.IsHidden)
        {
            this.CurrentIsHidden = Updated.IsHidden;
            PropertyChanged?.Invoke(this, new(nameof(IsHidden)));
        }
    }
    public string? OriginalLocation { get; init; }
    public DateTime? DateDeleted { get; init; }
    internal byte[]? ShellId { get; init; }
    public string DisplayDateDeleted => this.DateDeleted?.ToString(
        this.Use12HourClock ? "yyyy/MM/dd hh:mm:ss tt" : "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "—";
    private bool CurrentUse12HourClock;
    public bool Use12HourClock
    {
        get => this.CurrentUse12HourClock;
        set
        {
            if (this.CurrentUse12HourClock == value) return;
            this.CurrentUse12HourClock = value;
            PropertyChanged?.Invoke(this, new(nameof(Use12HourClock)));
            PropertyChanged?.Invoke(this, new(nameof(DisplayModified)));
            PropertyChanged?.Invoke(this, new(nameof(DisplayDateDeleted)));
        }
    }
    private long CachedSizeValue;
    private bool CachedSizePartial;
    private CultureInfo? CachedSizeCulture;
    private string? CachedSizeText;
    private DateTime CachedModifiedValue;
    private bool CachedModifiedClock;
    private string? CachedModifiedText;
    public string DisplaySize
    {
        get
        {
            if (this.Size is not { } ByteCount) return "—";
            var Culture = LanguageService.Culture;
            if (this.CachedSizeText is null || this.CachedSizeValue != ByteCount || this.CachedSizePartial != this.FolderSizePartial
                || !ReferenceEquals(this.CachedSizeCulture, Culture))
            {
                this.CachedSizeText = (this.FolderSizePartial ? "≥ " : "") + FormatSize(ByteCount, Culture);
                this.CachedSizeValue = ByteCount; this.CachedSizePartial = this.FolderSizePartial; this.CachedSizeCulture = Culture;
            }
            return this.CachedSizeText;
        }
    }
    public string DisplayModified
    {
        get
        {
            if (this.IsDrive) return "—";
            if (this.CachedModifiedText is null || this.CachedModifiedValue != this.Modified || this.CachedModifiedClock != this.Use12HourClock)
            {
                this.CachedModifiedText = this.Modified.ToString(this.Use12HourClock ? "yyyy/MM/dd hh:mm:ss tt" : "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
                this.CachedModifiedValue = this.Modified; this.CachedModifiedClock = this.Use12HourClock;
            }
            return this.CachedModifiedText;
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshLanguage()
    {
        this.CachedTypeLanguage = null;
        PropertyChanged?.Invoke(this, new(nameof(TypeName)));
        PropertyChanged?.Invoke(this, new(nameof(DisplaySize)));
        PropertyChanged?.Invoke(this, new(nameof(DisplayModified)));
        PropertyChanged?.Invoke(this, new(nameof(DisplayDateDeleted)));
    }

    private static readonly string[] SizeUnits = ["KB", "MB", "GB", "TB", "PB", "EB"];
    private static string FormatSize(long Bytes, CultureInfo Culture)
    {
        if (Bytes < 1024) return Bytes.ToString("N0", Culture) + " B";
        double Value = Bytes;
        var Unit = -1;
        do { Value /= 1024; Unit++; } while (Value >= 1024 && Unit < SizeUnits.Length - 1);
        return Value.ToString("0.#", Culture) + " " + SizeUnits[Unit];
    }
}

public sealed record DriveEntry
{
    private readonly string OriginalName;
    private readonly string OriginalDescription;
    public string FullPath { get; }
    public string? DescriptionKey { get; init; }
    public bool LocalizeName { get; init; }
    public string Description => this.DescriptionKey is { } Key ? LanguageService.Text(Key) : this.OriginalDescription;
    public string Name => this.LocalizeName
        ? LanguageService.Text("DriveLabel", this.Description, this.FullPath.TrimEnd('\\', '/'))
        : this.OriginalName;

    public DriveEntry(string Name, string FullPath, string Description)
    {
        this.OriginalName = Name;
        this.FullPath = FullPath;
        this.OriginalDescription = Description;
    }
}
public sealed record DirectoryEntry(string Name, string FullPath);
