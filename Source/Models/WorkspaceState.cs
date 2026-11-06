namespace CDir.Models;

internal sealed class WorkspaceState
{
    public int Version { get; set; } = 1;
    public DateTimeOffset LastActivatedUtc { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 820;
    public bool Maximized { get; set; }
    public int ActiveTab { get; set; }
    public List<WorkspaceTabState> Tabs { get; set; } = [];
}

internal sealed class WorkspaceTabState
{
    public AppSettings Settings { get; set; } = new();
    public string Alias { get; set; } = "";
    public string Search { get; set; } = "";
    public string Sort { get; set; } = nameof(FileEntry.Name);
    public bool Descending { get; set; }
    public List<string> History { get; set; } = [];
    public List<string> HistoryTreeKeys { get; set; } = [];
    public int HistoryIndex { get; set; }
    public Dictionary<string, double> ColumnWidths { get; set; } = [];
    public List<string> SelectedPaths { get; set; } = [];
    public double ScrollOffset { get; set; }
    public bool TerminalVisible { get; set; }
    public double TerminalHeight { get; set; } = 240;
}
