using System.Windows.Controls;
using System.Windows.Threading;

namespace CDir.UI;

public partial class ExplorerTab
{
    private sealed record BrowseState(string Path, string Search, string[] Selected, double Vertical, double Horizontal);
    private readonly Dictionary<int, BrowseState> BrowseStates = [];

    private void RememberBrowseState()
    {
        if (this.HistoryIndex < 0 || this.Loading) return;
        var Viewer = FindVisualChild<ScrollViewer>(this.ActiveFileControl);
        this.BrowseStates[this.HistoryIndex] = new(this.CurrentPath, this.SearchBox.Text,
            this.SelectedEntries().Select(Entry => Entry.FullPath).ToArray(), Viewer?.VerticalOffset ?? 0, Viewer?.HorizontalOffset ?? 0);
    }

    private async Task RestoreBrowseStateAsync(int Index, int Version)
    {
        if (!this.BrowseStates.TryGetValue(Index, out var State) || !SamePath(State.Path, this.CurrentPath)) return;
        bool Current() => !this.WindowClosed && this.HistoryIndex == Index && this.NavigationVersion == Version;
        if (!Current()) return;
        this.SearchBox.Text = State.Search;
        await this.SearchTask;
        if (!Current()) return;
        this.ClearFileSelection();
        foreach (var Path in State.Selected) this.SelectPath(Path, Append: true, Reveal: false);
        // Realize the restored view before applying offsets; selection must never scroll it again.
        this.UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (!Current()) return;
        var Viewer = FindVisualChild<ScrollViewer>(this.ActiveFileControl);
        Viewer?.ScrollToVerticalOffset(State.Vertical);
        Viewer?.ScrollToHorizontalOffset(State.Horizontal);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }
}
