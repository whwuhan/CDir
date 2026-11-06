using System.IO;
using System.Text.Json;

namespace CDir.Models;

internal sealed class DefaultSettings
{
    internal static DefaultSettings Current { get; } = Load();

    public required bool ShowHidden { get; init; }
    public required bool ShowSidebar { get; init; }
    public required double SidebarWidth { get; init; }
    public required bool ShowGridLines { get; init; }
    public required bool Use12HourClock { get; init; }
    public required bool ExitOnClose { get; init; }
    public required bool CopyOnDrop { get; init; }
    public required bool CalculateFolderSizes { get; init; }
    public required bool ShowSidebarDriveUsage { get; init; }
    public required bool SeparatePinnedTabs { get; init; }
    public required bool ShowBookmarkPaths { get; init; }
    public required IReadOnlyList<string> ColumnOrder { get; init; }
    public required IReadOnlyList<string> HiddenColumns { get; init; }
    public required IReadOnlyDictionary<string, double> FontSizes { get; init; }
    public required int TitleBarFontRevision { get; init; }
    public required IReadOnlyDictionary<string, string> Shortcuts { get; init; }
    public required double TerminalFontSize { get; init; }
    public required string TerminalBackgroundColor { get; init; }
    public string? TerminalForegroundColor { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required bool Maximized { get; init; }
    public required string Language { get; init; }
    public required bool FavoritesExpanded { get; init; }
    public required bool DirectoryTreeExpanded { get; init; }
    public required IReadOnlyList<string> SidebarOrder { get; init; }

    private static DefaultSettings Load()
    {
        // Embed the source file so packaged applications use the same defaults.
        using var Stream = typeof(DefaultSettings).Assembly.GetManifestResourceStream("CDir.Config.DefaultSettings.json")
            ?? throw new InvalidDataException("Default settings resource is missing.");
        return JsonSerializer.Deserialize<DefaultSettings>(Stream)
            ?? throw new InvalidDataException("Default settings resource is empty.");
    }
}
