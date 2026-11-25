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
    private static readonly HashSet<string> BasicColumnKeySet = new(BasicColumnKeys, StringComparer.Ordinal);
    private static readonly Lazy<IReadOnlyList<ShellDetailColumns.Column>> RecycleColumns = new(() => ShellDetailColumns.ReadCatalog(AppMain.SmokeRoot ?? AppContext.BaseDirectory, true));
    private static readonly Lazy<IReadOnlyList<ShellDetailColumns.Column>> WindowsColumns = new(() =>
        ShellDetailColumns.ReadCatalog(AppMain.SmokeRoot ?? AppContext.BaseDirectory)
            .Concat(RecycleColumns.Value)
            .DistinctBy(Column => Column.Key).ToArray());
    private static readonly Lazy<Dictionary<string, ShellDetailColumns.Column>> WindowsColumnsByKey = new(() =>
        WindowsColumns.Value.ToDictionary(Column => Column.Key, StringComparer.Ordinal));
    private static readonly Lazy<Dictionary<string, ShellDetailColumns.Column>> RecycleColumnsByKey = new(() =>
        RecycleColumns.Value.ToDictionary(Column => Column.Key, StringComparer.Ordinal));
    private static readonly Lazy<string[]> AllColumnKeys = new(() => BasicColumnKeys.Concat(WindowsColumns.Value.Select(Column => Column.Key)).Distinct().ToArray());
    private static readonly Lazy<HashSet<string>> AllColumnKeySet = new(() => new(AllColumnKeys.Value, StringComparer.Ordinal));
    private string[] ColumnKeys => AllColumnKeys.Value;
    private readonly Dictionary<string, DataGridColumn> ColumnsByKey = new(StringComparer.Ordinal);
    private bool ShellColumnsInitialized;
    private bool ColumnAvailable(string Key) => this.IsRecycleBin
        || !IsRecycleColumn(Key) && !(WindowsColumnsByKey.Value.TryGetValue(Key, out var Definition) && Definition.RecycleOnly);

    private void InitializeShellColumns()
    {
        if (this.ShellColumnsInitialized) return;
        this.ShellColumnsInitialized = true;
        foreach (var Column in this.FileGrid.Columns)
        {
            Column.MinWidth = 0;
            this.ColumnsByKey[Column.SortMemberPath] = Column;
        }
        var Texts = CDir.Localization.LanguageService.GetTexts();
        var MetadataStyle = (Style)this.FindResource("FileMetadataText");
        foreach (var Definition in WindowsColumns.Value)
        {
            if (this.ColumnsByKey.ContainsKey(Definition.Key)) continue;
            var Binding = Definition.Key switch
            {
                nameof(FileEntry.OriginalLocation) => new Binding(nameof(FileEntry.OriginalLocation)),
                nameof(FileEntry.DateDeleted) => new Binding(nameof(FileEntry.DisplayDateDeleted)),
                _ => new Binding("[" + Definition.Key + "]")
            };
            var ResourceKey = ColumnResourceKey(Definition.Key);
            var Column = new DataGridTextColumn
            {
                Header = Texts.ContainsKey(ResourceKey) ? CDir.Localization.LanguageService.Text(ResourceKey) : Definition.Header,
                SortMemberPath = Definition.Key,
                Binding = Binding, Width = 180, MinWidth = 0, Visibility = Visibility.Collapsed,
                ElementStyle = MetadataStyle
            };
            this.FileGrid.Columns.Add(Column);
            this.ColumnsByKey[Definition.Key] = Column;
        }
    }

    private CancellationTokenSource? ShellColumnsCancellation;
    private Task ShellColumnsTask = Task.CompletedTask;
    private void QueueShellColumnValues()
    {
        this.ShellColumnsCancellation?.Cancel();
        var Keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var Column in this.FileGrid.Columns)
        {
            var Key = Column.SortMemberPath;
            if (Column.Visibility == Visibility.Visible && !BasicColumnKeySet.Contains(Key) && !IsRecycleColumn(Key))
                Keys.Add(Key);
        }
        // A hidden column can still order or group the view; load the values it compares.
        if (this.SortProperty.Length > 0 && !BasicColumnKeySet.Contains(this.SortProperty) && !IsRecycleColumn(this.SortProperty))
            Keys.Add(this.SortProperty);
        if (this.FileGroupProperty.Length > 0 && !BasicColumnKeySet.Contains(this.FileGroupProperty) && !IsRecycleColumn(this.FileGroupProperty))
            Keys.Add(this.FileGroupProperty);
        if (Keys.Count == 0 || this.Entries is null || this.WindowClosed) return;
        var Catalog = this.IsRecycleBin ? RecycleColumnsByKey.Value : WindowsColumnsByKey.Value;
        var Definitions = Keys.Where(Catalog.ContainsKey).Select(Key => Catalog[Key]).ToArray();
        if (Definitions.Length == 0) return;
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
                using var Session = ShellDetailColumns.OpenSession();
                var Batch = new List<(FileEntry Entry, IReadOnlyDictionary<string, (string Text, object? Value)> Values)>(32);
                var BatchTimer = System.Diagnostics.Stopwatch.StartNew();
                var FirstFlush = true;
                void Flush()
                {
                    if (Batch.Count == 0 || Cancellation.IsCancellationRequested) return;
                    var Snapshot = Batch.ToArray();
                    Batch.Clear();
                    BatchTimer.Restart();
                    FirstFlush = false;
                    Dispatcher.Invoke(() =>
                    {
                        if (Cancellation.IsCancellationRequested || this.WindowClosed || this.NavigationVersion != Version) return;
                        foreach (var Item in Snapshot) Item.Entry.SetShellDetails(Item.Values);
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
                foreach (var Entry in Items)
                {
                    if (Cancellation.IsCancellationRequested) break;
                    if (AppMain.SmokeTest && !AppMain.IsInsideSmokeScope(Entry.FullPath)) continue;
                    IReadOnlyDictionary<string, (string Text, object? Value)> Values;
                    try { Values = Session.ReadValues(Entry, Definitions); }
                    catch (Exception Error) when (Error is COMException or System.IO.IOException or UnauthorizedAccessException) { continue; }
                    Batch.Add((Entry, Values));
                    if (Batch.Count >= (FirstFlush ? 8 : 32) || BatchTimer.ElapsedMilliseconds >= 40) Flush();
                }
                Flush();
                if (!Cancellation.IsCancellationRequested) Dispatcher.Invoke(() =>
                {
                    var Regroup = Keys.Contains(this.FileGroupProperty);
                    if (Cancellation.IsCancellationRequested || this.WindowClosed || this.NavigationVersion != Version || !Keys.Contains(this.SortProperty) && !Regroup) return;
                    var Viewer = FindVisualChild<ScrollViewer>(this.ActiveFileControl);
                    var Vertical = Viewer?.VerticalOffset ?? 0; var Horizontal = Viewer?.HorizontalOffset ?? 0;
                    if (Regroup) { var Today = DateTime.Today; foreach (var Entry in this.Entries) this.AssignFileGroup(Entry, Today); }
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
