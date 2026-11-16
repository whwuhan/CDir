namespace CDir.Models;

public sealed record ColumnPreference(string Key, string ResourceKey, bool Enabled, bool SearchOnly, string? DisplayName = null,
    string? HintResourceKey = null);
