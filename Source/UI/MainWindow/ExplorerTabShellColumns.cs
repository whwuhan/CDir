using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private static readonly string[] BasicColumnKeys = [nameof(FileEntry.Name), nameof(FileEntry.Modified), nameof(FileEntry.TypeName), nameof(FileEntry.Size), nameof(FileEntry.FullPath)];
    private static readonly Lazy<IReadOnlyList<ShellDetailColumns.Column>> RecycleColumns = new(() => ShellDetailColumns.ReadCatalog(AppMain.SmokeRoot ?? AppContext.BaseDirectory, true));
    private static readonly Lazy<IReadOnlyList<ShellDetailColumns.Column>> WindowsColumns = new(() =>
        ShellDetailColumns.ReadCatalog(AppMain.SmokeRoot ?? AppContext.BaseDirectory)
            .Concat(RecycleColumns.Value)
            .DistinctBy(Column => Column.Key).ToArray());
    private static readonly Lazy<string[]> AllColumnKeys = new(() => BasicColumnKeys.Concat(WindowsColumns.Value.Select(Column => Column.Key)).Distinct().ToArray());
    private string[] ColumnKeys => AllColumnKeys.Value;
    private bool ColumnAvailable(string Key) => !IsRecycleColumn(Key)
        && WindowsColumns.Value.FirstOrDefault(Column => Column.Key == Key)?.RecycleOnly != true || this.IsRecycleBin;

    private void InitializeShellColumns()
    {
        foreach (var Column in this.FileGrid.Columns) Column.MinWidth = 0;
        foreach (var Definition in WindowsColumns.Value)
        {
            if (this.FileGrid.Columns.Any(Column => Column.SortMemberPath == Definition.Key)) continue;
            var Binding = Definition.Key switch
            {
                nameof(FileEntry.OriginalLocation) => new Binding(nameof(FileEntry.OriginalLocation)),
                nameof(FileEntry.DateDeleted) => new Binding(nameof(FileEntry.DisplayDateDeleted)),
                _ => new Binding("[" + Definition.Key + "]")
            };
            this.FileGrid.Columns.Add(new DataGridTextColumn { Header = CDir.Localization.LanguageService.GetTexts().ContainsKey(ColumnResourceKey(Definition.Key))
                    ? CDir.Localization.LanguageService.Text(ColumnResourceKey(Definition.Key)) : Definition.Header, SortMemberPath = Definition.Key,
                Binding = Binding, Width = 180, MinWidth = 0, Visibility = Visibility.Collapsed,
                ElementStyle = (Style)this.FindResource("FileMetadataText") });
        }
    }

    private CancellationTokenSource? ShellColumnsCancellation;
    private Task ShellColumnsTask = Task.CompletedTask;
    private void QueueShellColumnValues()
    {
        this.ShellColumnsCancellation?.Cancel();
        var Keys = this.FileGrid.Columns.Where(Column => Column.Visibility == Visibility.Visible && !BasicColumnKeys.Contains(Column.SortMemberPath)
            && !IsRecycleColumn(Column.SortMemberPath)).Select(Column => Column.SortMemberPath).ToHashSet();
        if (Keys.Count == 0 || this.Entries is null || this.WindowClosed) return;
        var Definitions = (this.IsRecycleBin ? RecycleColumns.Value : WindowsColumns.Value).Where(Column => Keys.Contains(Column.Key)).ToArray();
        var Items = this.Entries.ToArray();
        var Cancellation = new CancellationTokenSource();
        this.ShellColumnsCancellation = Cancellation;
        var Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var Previous = this.ShellColumnsTask;
        var Version = this.NavigationVersion;
        var Worker = new Thread(() =>
        {
            try
            {
                foreach (var Entry in Items)
                {
                    if (Cancellation.IsCancellationRequested) break;
                    if (AppMain.SmokeTest && !AppMain.IsInsideSmokeScope(Entry.FullPath)) continue;
                    IReadOnlyDictionary<string, (string Text, object? Value)> Values;
                    try { Values = ShellDetailColumns.ReadValues(Entry, Definitions); }
                    catch (Exception Error) when (Error is COMException or System.IO.IOException or UnauthorizedAccessException) { continue; }
                    Dispatcher.Invoke(() =>
                    {
                        if (!Cancellation.IsCancellationRequested && !this.WindowClosed && this.NavigationVersion == Version)
                            Entry.SetShellDetails(Values);
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
                Dispatcher.Invoke(() =>
                {
                    if (Cancellation.IsCancellationRequested || this.WindowClosed || this.NavigationVersion != Version || !Keys.Contains(this.SortProperty)) return;
                    var Viewer = FindVisualChild<ScrollViewer>(this.ActiveFileControl);
                    var Vertical = Viewer?.VerticalOffset ?? 0; var Horizontal = Viewer?.HorizontalOffset ?? 0;
                    this.View?.Refresh(); this.ActiveFileControl.UpdateLayout();
                    Viewer?.ScrollToVerticalOffset(Vertical); Viewer?.ScrollToHorizontalOffset(Horizontal);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception Error) when (Error is TaskCanceledException or OperationCanceledException or InvalidOperationException)
            { /* Closing the tab can shut down its dispatcher while a property handler completes. */ }
            finally { Completion.TrySetResult(); }
        }) { IsBackground = true, Name = "CDir detail properties" };
        Worker.SetApartmentState(ApartmentState.STA);
        this.ShellColumnsTask = StartWorkerAsync();
        async Task StartWorkerAsync()
        {
            await Previous;
            if (Cancellation.IsCancellationRequested) return;
            Worker.Start();
            await Completion.Task;
        }
    }
}
