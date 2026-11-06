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
    private InstanceWorkspaceService? InstanceService;
    private bool TrackInstances;
    private DateTimeOffset LastActivatedUtc = DateTimeOffset.UtcNow;
    internal bool MultiInstanceDetected;
    private bool? SaveOnExit;
    internal Func<MessageBoxResult>? ConfirmWorkspaceSave;
    private readonly Dictionary<string, string?> SharedStateBaselines = new(StringComparer.OrdinalIgnoreCase);
    private string? SharedSettingsPath;

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

    internal WorkspaceState? CaptureWorkspace()
    {
        if (this.RestoringWorkspace || this.WorkspaceClosed || this.Tabs.Count == 0 || this.Tabs.Any(Tab => !Tab.IsInitialLayoutReady)) return null;
        var Bounds = this.WindowState == WindowState.Normal ? new Rect(this.Left, this.Top, this.Width, this.Height) : this.RestoreBounds;
        return new WorkspaceState
        {
            LastActivatedUtc = this.LastActivatedUtc,
            Left = Bounds.Left, Top = Bounds.Top, Width = Bounds.Width, Height = Bounds.Height,
            Maximized = this.WindowState == WindowState.Maximized || this.WindowState == WindowState.Minimized && this.LastWorkspaceMaximized,
            ActiveTab = Math.Max(0, this.Tabs.IndexOf(this.ActiveTab!)), Tabs = this.Tabs.Select(Tab => Tab.CaptureWorkspaceTab()).ToList()
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

    private WorkspaceState? InitializeWorkspace(string? SettingsFile, bool Restore, WorkspaceState? InitialWorkspace)
    {
        this.SharedSettingsPath = SettingsFile ?? (AppMain.SmokeTest ? null
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CDir", "Settings.json"));
        if (this.SharedSettingsPath is { } SettingsPath) this.SharedStateBaselines[SettingsPath] = this.ReadSharedState(SettingsPath);
        if (!Restore || AppMain.SmokeTest && SettingsFile is null) return null;
        this.WorkspacePath = (SettingsFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CDir", "Settings.json")) + ".workspace.json";
        this.SharedStateBaselines[this.WorkspacePath] = this.ReadSharedState(this.WorkspacePath);
        this.RestoringWorkspace = true;
        if (!AppMain.SmokeTest && AppMain.StartupPath is not null) return null;
        var Saved = InitialWorkspace ?? (SettingsFile is null ? AppMain.StartupWorkspace ?? DurableJsonStore.Read<WorkspaceState>(this.WorkspacePath)
            : DurableJsonStore.Read<WorkspaceState>(this.WorkspacePath));
        return Saved is { Version: 1, Tabs.Count: > 0 } && Saved.Tabs.All(Tab => Tab is not null && Tab.Settings is not null) ? Saved : null;
    }

    private async Task RestoreWorkspaceAsync(WorkspaceState? Saved)
    {
        try
        {
            if (Saved is not null)
            {
                await this.ActiveTab!.RestoreWorkspaceTabAsync(Saved.Tabs[0]);
                foreach (var State in Saved.Tabs.Skip(1))
                {
                    if (this.WorkspaceClosed) return;
                    var Page = new ExplorerTab(this, InitialPath: State.Settings.LastPath, InitialSettings: State.Settings);
                    this.AttachTab(Page);
                    await Page.RestoreWorkspaceTabAsync(State);
                }
                this.SelectTab(this.Tabs[Math.Clamp(Saved.ActiveTab, 0, this.Tabs.Count - 1)]);
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
        if (this.WorkspacePath is null || this.RestoringWorkspace || this.WorkspaceClosed
            || this.Tabs.Count == 0 || this.Tabs.Any(Tab => !Tab.IsInitialLayoutReady)) return false;
        try
        {
            if (!this.CanPersistSharedState) return false;
            var State = this.CaptureWorkspace();
            if (State is null) return false;
            var Json = JsonSerializer.Serialize(State);
            if (Json != this.LastWorkspaceJson || this.SaveOnExit == true && this.ReadSharedState(this.WorkspacePath) != NormalizeSharedState(Json, Workspace: true))
            {
                DurableJsonStore.Write(this.WorkspacePath, Json);
                this.LastWorkspaceJson = Json;
                this.RecordSharedWrite(this.WorkspacePath, Json);
            }
            return true;
        }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException) { return false; }
    }
}
