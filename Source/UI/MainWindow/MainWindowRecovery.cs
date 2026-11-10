using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CDir.Models;
using CDir.Services;

namespace CDir.UI;

public partial class MainWindow
{
    private string? WorkspacePath;
    private SessionWorkspaceStore? SessionStore;
    private bool SessionIsEnding;
    private InstanceWorkspaceService? InstanceService;
    private bool TrackInstances;
    private DateTimeOffset LastActivatedUtc = DateTimeOffset.UtcNow;
    internal bool MultiInstanceDetected;
    private bool? SaveOnExit;
    internal Func<MessageBoxResult>? ConfirmWorkspaceSave;
    private readonly Dictionary<string, string?> SharedStateBaselines = new(StringComparer.OrdinalIgnoreCase);
    private string? SharedSettingsPath;
    internal int WorkspaceWriteCount { get; private set; }
    internal int WorkspaceCaptureCount { get; private set; }

    private static string NormalizeSharedState(string Json, bool Workspace)
    {
        object Value;
        if (Workspace)
        {
            var State = JsonSerializer.Deserialize<WorkspaceState>(Json) ?? throw new JsonException();
            State.LastActivatedUtc = default; // Activation is coordination metadata, not a layout edit.
            Value = State;
        }
        else Value = JsonSerializer.Deserialize<AppSettings>(Json) ?? throw new JsonException();
        using var Document = JsonDocument.Parse(JsonSerializer.Serialize(Value));
        using var Stream = new MemoryStream();
        using (var Writer = new Utf8JsonWriter(Stream)) Write(Document.RootElement, Writer);
        return System.Text.Encoding.UTF8.GetString(Stream.ToArray());

        static void Write(JsonElement Element, Utf8JsonWriter Writer)
        {
            if (Element.ValueKind == JsonValueKind.Object)
            {
                Writer.WriteStartObject();
                foreach (var Property in Element.EnumerateObject().OrderBy(Property => Property.Name, StringComparer.Ordinal))
                { Writer.WritePropertyName(Property.Name); Write(Property.Value, Writer); }
                Writer.WriteEndObject();
            }
            else if (Element.ValueKind == JsonValueKind.Array)
            {
                Writer.WriteStartArray();
                foreach (var Item in Element.EnumerateArray()) Write(Item, Writer);
                Writer.WriteEndArray();
            }
            else Element.WriteTo(Writer);
        }
    }

    private string? ReadSharedState(string Path)
    {
        try { return NormalizeSharedState(DurableJsonStore.ReadText(Path), string.Equals(Path, this.WorkspacePath, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException) { return null; }
    }

    internal void RecordSharedWrite(string Path, string Json)
    {
        if (this.SharedStateBaselines.ContainsKey(Path))
            this.SharedStateBaselines[Path] = NormalizeSharedState(Json, string.Equals(Path, this.WorkspacePath, StringComparison.OrdinalIgnoreCase));
    }

    private bool HasSharedStateConflict()
    {
        var Pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (this.SharedSettingsPath is { } SettingsPath && this.ActiveTab is { } Tab)
            Pending[SettingsPath] = NormalizeSharedState(JsonSerializer.Serialize(Tab.CaptureSaveSettings()), Workspace: false);
        if (this.WorkspacePath is { } Path && this.CaptureWorkspace() is { } State)
            Pending[Path] = NormalizeSharedState(JsonSerializer.Serialize(State), Workspace: true);
        return Pending.Any(Item => this.SharedStateBaselines.TryGetValue(Item.Key, out var Baseline)
            && this.ReadSharedState(Item.Key) is { } Disk && Disk != Baseline && Disk != Item.Value);
    }

    internal bool CanPersistSharedState
    {
        get
        {
            if (this.SaveOnExit is { } Save) return Save;
            if (this.TrackInstances && InstanceWorkspaceService.Peers().Length > 0) this.MultiInstanceDetected = true;
            return !this.MultiInstanceDetected;
        }
    }

    private Rect LastFinitePersistenceBounds = Rect.Empty;
    internal bool PersistenceMaximized => this.WindowState == WindowState.Maximized
        || this.WindowState == WindowState.Minimized && this.LastWorkspaceMaximized;

    internal Rect CapturePersistenceBounds()
    {
        var Bounds = this.WindowState == WindowState.Normal ? new Rect(this.Left, this.Top, this.Width, this.Height) : this.RestoreBounds;
        if (!Bounds.IsEmpty && double.IsFinite(Bounds.Left) && double.IsFinite(Bounds.Top)
            && double.IsFinite(Bounds.Width) && double.IsFinite(Bounds.Height) && Bounds.Width > 0 && Bounds.Height > 0)
            this.LastFinitePersistenceBounds = Bounds;
        if (!this.LastFinitePersistenceBounds.IsEmpty) return this.LastFinitePersistenceBounds;
        // Unshown/tearing-down WPF windows may have NaN coordinates or no restore
        // rectangle. Keep persisted geometry finite even before the first capture.
        static double Dimension(double Requested, double Actual, double Default) => double.IsFinite(Requested) && Requested > 0 ? Requested
            : double.IsFinite(Actual) && Actual > 0 ? Actual : Default;
        return new Rect(double.IsFinite(this.Left) ? this.Left : 0, double.IsFinite(this.Top) ? this.Top : 0,
            Dimension(this.Width, this.ActualWidth, DefaultSettings.Current.Width),
            Dimension(this.Height, this.ActualHeight, DefaultSettings.Current.Height));
    }

    internal WorkspaceState? CaptureWorkspace()
    {
        if (this.ClosingTabBatch || this.RestoringWorkspace || this.WorkspaceClosed || this.Tabs.Count == 0 || this.Tabs.Any(Tab => !Tab.IsInitialLayoutReady)
            || this.Panes.Any(Pane => Pane.Selected is null || !ReferenceEquals(Pane.Selected.Parent, Pane.TabPages))) return null;
        this.WorkspaceCaptureCount++;
        var Bounds = this.CapturePersistenceBounds();
        return new WorkspaceState
        {
            LastActivatedUtc = this.LastActivatedUtc,
            Left = Bounds.Left, Top = Bounds.Top, Width = Bounds.Width, Height = Bounds.Height,
            Maximized = this.PersistenceMaximized,
            ActiveTab = Math.Max(0, this.Tabs.IndexOf(this.ActiveTab!)), Tabs = this.Tabs.Select(Tab => Tab.CaptureWorkspaceTab()).ToList(),
            Layout = this.CapturePane(this.LayoutRoot)
        };
    }

    private bool ChooseExitPersistence()
    {
        if (this.CanPersistSharedState) return true;
        if (!this.HasSharedStateConflict()) { this.SaveOnExit = true; return true; }
        var Choice = this.ConfirmWorkspaceSave?.Invoke() ?? MessageBox.Show(this,
            CDir.Localization.LanguageService.Text("MultiInstanceSaveQuestion"), CDir.Localization.LanguageService.Text("SaveWorkspaceTitle"),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
        if (Choice == MessageBoxResult.Cancel) return false;
        this.SaveOnExit = Choice == MessageBoxResult.Yes;
        return true;
    }
    private bool RestoringWorkspace;
    private bool WorkspaceClosed;
    private bool LastWorkspaceMaximized;
    private string? LastWorkspaceJson;
    private DispatcherTimer? WorkspaceTimer;
    internal Task WorkspaceRestored { get; private set; } = Task.CompletedTask;
    // Keep directory/tree restoration bounded while still allowing input and
    // painting to proceed between the construction of individual workspaces.
    internal readonly SemaphoreSlim InitialPageReadGate = new(3);

    private WorkspaceState? InitializeWorkspace(string? SettingsFile, bool Restore, WorkspaceState? InitialWorkspace)
    {
        this.SharedSettingsPath = SettingsFile ?? (AppMain.SmokeTest ? null
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CDir", "Settings.json"));
        if (this.SharedSettingsPath is { } SettingsPath) this.SharedStateBaselines[SettingsPath] = this.ReadSharedState(SettingsPath);
        if (!Restore || AppMain.SmokeTest && SettingsFile is null) return null;
        this.WorkspacePath = (SettingsFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CDir", "Settings.json")) + ".workspace.json";
        this.SessionStore = new SessionWorkspaceStore(this.WorkspacePath);
        this.SharedStateBaselines[this.WorkspacePath] = this.ReadSharedState(this.WorkspacePath);
        this.RestoringWorkspace = true;
        if (!AppMain.SmokeTest && AppMain.StartupPath is not null) return null;
        var Saved = InitialWorkspace ?? (SettingsFile is null ? AppMain.StartupWorkspace : null)
            ?? this.SessionStore.ReadLatest() ?? DurableJsonStore.Read<WorkspaceState>(this.WorkspacePath);
        return Saved is { Version: 1, Tabs.Count: > 0 } && Saved.Tabs.All(Tab => Tab is not null && Tab.Settings is not null) ? Saved : null;
    }

    private async Task RestoreWorkspaceAsync(WorkspaceState? Saved)
    {
        try
        {
            if (Saved is not null)
            {
                var ActiveIndex = Math.Clamp(Saved.ActiveTab, 0, Saved.Tabs.Count - 1);
                var Initial = this.ActiveTab!;
                var StateGate = new SemaphoreSlim(3);
                async Task RestoreState(ExplorerTab Page, WorkspaceTabState State)
                {
                    await StateGate.WaitAsync();
                    try { if (!this.WorkspaceClosed) await Page.RestoreWorkspaceTabAsync(State); }
                    finally { StateGate.Release(); }
                }
                var Pending = new List<Task> { RestoreState(Initial, Saved.Tabs[ActiveIndex]) };
                await Initial.WaitUntilReadyAsync();
                if (this.WorkspaceClosed) return;
                var Roots = Initial.CloneRootEntries();
                var Indices = new Dictionary<ExplorerTab, int> { [Initial] = ActiveIndex };
                var Created = new Dictionary<int, ExplorerTab> { [ActiveIndex] = Initial };
                var Visible = new List<int>();
                var Owners = new Dictionary<int, int>();
                var CanPreparePanes = Saved.Layout?.Split is not null && ValidPaneLayout(Saved.Layout, Saved.Tabs.Count);
                var PanesPrepared = false;
                void VisibleTabs(WorkspacePaneState? Node)
                {
                    if (Node is null) return;
                    if (Node.Split is null)
                    {
                        if (Node.ActiveTab >= 0 && Node.ActiveTab < Saved.Tabs.Count) Visible.Add(Node.ActiveTab);
                        if (CanPreparePanes) foreach (var Index in Node.Tabs) Owners[Index] = Node.ActiveTab;
                        return;
                    }
                    VisibleTabs(Node.First); VisibleTabs(Node.Second);
                }
                VisibleTabs(Saved.Layout);
                WorkspacePaneState ReadyLayout(WorkspacePaneState Node)
                {
                    if (Node.Split is null) return new WorkspacePaneState
                    {
                        Tabs = Node.Tabs.Where(Created.ContainsKey).Select(Index => this.Tabs.IndexOf(Created[Index])).ToList(),
                        ActiveTab = this.Tabs.IndexOf(Created[Node.ActiveTab])
                    };
                    return new WorkspacePaneState { Split = Node.Split, Ratio = Node.Ratio,
                        First = ReadyLayout(Node.First!), Second = ReadyLayout(Node.Second!) };
                }
                var Order = Visible.Concat(Enumerable.Range(0, Saved.Tabs.Count)).Distinct().Where(Index => Index != ActiveIndex);
                foreach (var Index in Order)
                {
                    // A constructor touches WPF controls. Yield before the next
                    // one instead of monopolizing the UI thread for a whole session.
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                    if (this.WorkspaceClosed) return;
                    var State = Saved.Tabs[Index];
                    var Page = new ExplorerTab(this, InitialPath: State.Settings.LastPath, InitialSettings: State.Settings,
                        InitialRoots: Roots.Select(Root => Root.CloneRootEntry()).ToArray(), RefreshInheritedRoots: false);
                    this.ApplyCurrentUIPreferences(Page);
                    Page.SetTabAlias(State.Alias ?? ""); Page.SetTabPinned(State.IsPinned);
                    var Slot = this.Tabs.Count(Tab => Indices.TryGetValue(Tab, out var Position) && Position < Index);
                    Indices[Page] = Index;
                    Created[Index] = Page;
                    var Owner = PanesPrepared ? Created[Owners[Index]] : null;
                    var Destination = Owner is not null && this.Tabs.Contains(Owner) ? this.PaneFor(Owner) : null;
                    this.AttachTab(Page, Activate: false, InsertAt: Slot, Destination: Destination);
                    Pending.Add(RestoreState(Page, State));
                    if (CanPreparePanes && !PanesPrepared && Visible.All(Created.ContainsKey))
                    {
                        // Build the saved pane layout using its selected tabs first.
                        // Remaining tab headers can be inserted into their own panes
                        // without dismantling and reparenting the finished layout.
                        PanesPrepared = this.RestorePanes(ReadyLayout(Saved.Layout!));
                        if (PanesPrepared)
                        {
                            this.SelectTab(Initial);
                            // Let selected panes finish their first layout and
                            // paint before allocating the inactive tab controls.
                            await Task.WhenAll(Visible.Distinct().Select(VisibleIndex => Created[VisibleIndex].WaitUntilReadyAsync()));
                            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                        }
                    }
                }
                if (this.WorkspaceClosed) return;
                // Pane topology and selected tabs can be shown before all of the
                // independent directory reads have completed.
                if (!PanesPrepared)
                {
                    this.RestorePanes(Saved.Layout);
                    this.SelectTab(Initial);
                }
                await Task.WhenAll(Pending);
                if (this.WorkspaceClosed) return;
                this.ActiveTab!.ActivateRecoveredPreferences();
            }
            else if (this.ActiveTab is { } Initial) await Initial.WaitUntilReadyAsync();
        }
        finally
        {
            this.RestoringWorkspace = false;
            if (this.WorkspacePath is not null && !this.WorkspaceClosed)
            {
                this.WorkspaceTimer = new DispatcherTimer(DispatcherPriority.Background, this.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
                this.WorkspaceTimer.Tick += (_, _) => this.PersistWorkspaceNow();
                this.WorkspaceTimer.Start();
                this.PersistWorkspaceNow();
            }
        }
    }

    internal bool PersistWorkspaceNow()
    {
        if (this.SessionIsEnding || this.ClosingTabBatch || this.WorkspacePath is null || this.RestoringWorkspace || this.WorkspaceClosed
            || this.Tabs.Count == 0 || this.Tabs.Any(Tab => !Tab.IsInitialLayoutReady)) return false;
        try
        {
            if (!this.CanPersistSharedState)
            {
                if (this.SaveOnExit is null && this.CaptureWorkspace() is { } PrivateState) this.SessionStore?.Save(PrivateState);
                return false;
            }
            var State = this.CaptureWorkspace();
            if (State is null) return false;
            var Json = JsonSerializer.Serialize(State);
            if (Json != this.LastWorkspaceJson || this.SaveOnExit == true && this.ReadSharedState(this.WorkspacePath) != NormalizeSharedState(Json, Workspace: true))
            {
                DurableJsonStore.Write(this.WorkspacePath, Json);
                this.WorkspaceWriteCount++;
                this.LastWorkspaceJson = Json;
                this.RecordSharedWrite(this.WorkspacePath, Json);
            }
            return true;
        }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException) { return false; }
    }

    internal bool PersistSessionEndingWorkspace()
    {
        // Do not enter tray, busy-operation or conflict UI during Windows shutdown.
        this.SessionIsEnding = true;
        this.SaveOnExit = false;
        this.WorkspaceTimer?.Stop();
        try { return this.CaptureWorkspace() is { } State && this.SessionStore?.Save(State) == true; }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException) { return false; }
    }
}
