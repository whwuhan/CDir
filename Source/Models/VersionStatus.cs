using CDir.Localization;

namespace CDir.Models;

public enum VersionState { None, Clean, Modified, Added, Deleted, Renamed, Conflict, Untracked, Ignored, Unknown }

public sealed record VersionStatus(VersionState State, string Provider = "", bool ContainsChanges = false, string? Error = null)
{
    public static VersionStatus None { get; } = new(VersionState.None);
    public string Description => this.State == VersionState.None ? "" : this.State == VersionState.Unknown
        ? LanguageService.Text("VcsUnavailable", this.Provider, this.Error ?? LanguageService.Text("VcsChecking"))
        : LanguageService.Text(this.ContainsChanges ? "VcsContains" : "VcsStatus", this.Provider, LanguageService.Text("Vcs" + this.State));
}
