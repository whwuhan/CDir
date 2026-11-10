using System.IO;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class MainWindow
{
    private readonly Dictionary<string, FileViewMode> FolderViews = new(StringComparer.OrdinalIgnoreCase);
    private bool FolderViewsLoaded;

    private readonly Dictionary<string, FileViewMode> FolderViewDefaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> FolderIconSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> FolderDefaultIconSizes = new(StringComparer.OrdinalIgnoreCase);
    private static int ValidIconSize(int Size) => Size is >= 16 and <= 256 ? Size : 0;

    private static string? FolderViewKey(string? Path)
    {
        if (string.IsNullOrWhiteSpace(Path)) return null;
        if (ShellLocations.IsVirtual(Path)) return Path;
        try
        {
            return System.IO.Path.IsPathFullyQualified(Path)
                ? System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Path)) : null;
        }
        catch (Exception Error) when (Error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    internal void RegisterFolderViews(AppSettings Settings)
    {
        // Every snapshot contains the complete registry. Pending tabs must not
        // reintroduce old descendant overrides after a recursive Apply or Reset.
        if (this.FolderViewsLoaded) return;
        this.FolderViewsLoaded = true;
        foreach (var Entry in Settings.FolderViews ?? [])
            if (Enum.IsDefined(Entry.Value) && FolderViewKey(Entry.Key) is { } Key)
                this.FolderViews.TryAdd(Key, Entry.Value);
        foreach (var Entry in Settings.FolderViewDefaults ?? [])
            if (Enum.IsDefined(Entry.Value) && FolderViewKey(Entry.Key) is { } Key && !ShellLocations.IsVirtual(Key))
                this.FolderViewDefaults.TryAdd(Key, Entry.Value);
        foreach (var Entry in Settings.FolderViewIconSizes ?? [])
            if (FolderViewKey(Entry.Key) is { } Key && this.FolderViews.ContainsKey(Key))
                this.FolderIconSizes[Key] = ValidIconSize(Entry.Value);
        foreach (var Entry in Settings.FolderViewDefaultIconSizes ?? [])
            if (FolderViewKey(Entry.Key) is { } Key && this.FolderViewDefaults.ContainsKey(Key))
                this.FolderDefaultIconSizes[Key] = ValidIconSize(Entry.Value);
        // Preserve the current view when reading settings from before folder-level storage.
        if (Enum.IsDefined(Settings.FileView) && FolderViewKey(Settings.LastPath) is { } Current)
        {
            this.FolderViews.TryAdd(Current, Settings.FileView);
            this.FolderIconSizes.TryAdd(Current, ValidIconSize(Settings.FileViewIconSize));
        }
    }

    internal Dictionary<string, FileViewMode> CaptureFolderViews() => new(this.FolderViews, StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, FileViewMode> CaptureFolderViewDefaults() => new(this.FolderViewDefaults, StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, int> CaptureFolderIconSizes() => new(this.FolderIconSizes, StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, int> CaptureFolderDefaultIconSizes() => new(this.FolderDefaultIconSizes, StringComparer.OrdinalIgnoreCase);
    private static bool WithinFolder(string Path, string Root) => string.Equals(Path, Root, StringComparison.OrdinalIgnoreCase)
        || Path.StartsWith(Root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal void ApplyFolderViewRecursively(string Path, FileViewMode Mode, int IconSize = 0)
    {
        if (!Enum.IsDefined(Mode) || FolderViewKey(Path) is not { } Root || ShellLocations.IsVirtual(Root)) return;
        foreach (var Key in this.FolderViews.Keys.Where(Key => WithinFolder(Key, Root)).ToArray()) this.FolderViews.Remove(Key);
        foreach (var Key in this.FolderViewDefaults.Keys.Where(Key => WithinFolder(Key, Root)).ToArray()) this.FolderViewDefaults.Remove(Key);
        foreach (var Key in this.FolderIconSizes.Keys.Where(Key => WithinFolder(Key, Root)).ToArray()) this.FolderIconSizes.Remove(Key);
        foreach (var Key in this.FolderDefaultIconSizes.Keys.Where(Key => WithinFolder(Key, Root)).ToArray()) this.FolderDefaultIconSizes.Remove(Key);
        this.FolderViews[Root] = this.FolderViewDefaults[Root] = Mode;
        this.FolderIconSizes[Root] = this.FolderDefaultIconSizes[Root] = ValidIconSize(IconSize);
        foreach (var Tab in this.Tabs)
            if (FolderViewKey(Tab.TabPath) is { } Key && WithinFolder(Key, Root)) Tab.ApplyRememberedFolderView(Mode, ValidIconSize(IconSize));
    }

    internal FileViewMode FolderViewFor(string Path)
    {
        if (FolderViewKey(Path) is not { } Key) return FileViewMode.Details;
        if (!this.FolderViews.TryGetValue(Key, out var Mode))
        {
            var Rule = this.FolderViewDefaults.Where(Entry => WithinFolder(Key, Entry.Key)).OrderByDescending(Entry => Entry.Key.Length).FirstOrDefault();
            this.FolderViews[Key] = Mode = Rule.Key is null ? FileViewMode.Details : Rule.Value;
            this.FolderIconSizes[Key] = Rule.Key is null ? 0 : this.FolderDefaultIconSizes.GetValueOrDefault(Rule.Key);
        }
        return Mode;
    }

    internal int FolderIconSizeFor(string Path)
    {
        this.FolderViewFor(Path);
        return FolderViewKey(Path) is { } Key ? this.FolderIconSizes.GetValueOrDefault(Key) : 0;
    }

    internal void RememberFolderView(string Path, FileViewMode Mode, int IconSize = 0)
    {
        if (!Enum.IsDefined(Mode) || FolderViewKey(Path) is not { } Key) return;
        this.FolderViews[Key] = Mode;
        this.FolderIconSizes[Key] = ValidIconSize(IconSize);
        foreach (var Tab in this.Tabs)
            if (string.Equals(FolderViewKey(Tab.TabPath), Key, StringComparison.OrdinalIgnoreCase))
                Tab.ApplyRememberedFolderView(Mode, ValidIconSize(IconSize));
    }
}
