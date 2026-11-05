using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace CDir.Localization;

public sealed record LanguageOption(string Code, string NativeName);

/// <summary>Language resources and formatting shared by the desktop UI and filesystem models.</summary>
public static class LanguageService
{
    public const string DefaultLanguage = "en-US";

    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } = Array.AsReadOnly<LanguageOption>(
    [
        new("en-US", "English"),
        new("zh-CN", "简体中文"),
        new("ja-JP", "日本語"),
        new("ko-KR", "한국어")
    ]);

    private static readonly Dictionary<string, string> EnglishTexts = ReadResource("English.json");
    private static LanguageState State = LoadState(DefaultLanguage);
    public static string CurrentLanguage => Volatile.Read(ref State).Code;
    public static CultureInfo Culture => Volatile.Read(ref State).Culture;
    public static event EventHandler? LanguageChanged;

    public static void SetLanguage(string? Code)
    {
        var Selected = SupportedLanguages.FirstOrDefault(Language =>
            string.Equals(Language.Code, Code?.Trim(), StringComparison.OrdinalIgnoreCase))?.Code ?? DefaultLanguage;
        var Previous = Volatile.Read(ref State);
        var Next = Previous.Code == Selected ? Previous : LoadState(Selected);
        Volatile.Write(ref State, Next);
        CultureInfo.CurrentCulture = Next.Culture;
        CultureInfo.CurrentUICulture = Next.Culture;
        CultureInfo.DefaultThreadCurrentCulture = Next.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Next.Culture;
        if (Previous.Code != Next.Code) LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Text(string Key, params object?[] Arguments)
    {
        ArgumentNullException.ThrowIfNull(Key);
        var Snapshot = Volatile.Read(ref State);
        var Template = Snapshot.Texts.TryGetValue(Key, out var Value) ? Value : Key;
        if (Arguments.Length == 0) return Template;
        try { return string.Format(Snapshot.Culture, Template, Arguments); }
        catch (FormatException) { return Template; }
    }

    public static IReadOnlyDictionary<string, string> GetTexts() => Volatile.Read(ref State).Texts;

    internal static IReadOnlyDictionary<string, string> GetTexts(string Code) => LoadState(Code).Texts;

    internal static string EnglishText(string Key) => EnglishTexts.TryGetValue(Key, out var Text) ? Text : Key;

    private static LanguageState LoadState(string Code)
    {
        var Texts = new Dictionary<string, string>(EnglishTexts, StringComparer.Ordinal);
        var FileName = Code switch
        {
            "ja-JP" => "Japanese.json",
            "ko-KR" => "Korean.json",
            "zh-CN" => "Chinese.json",
            _ => "English.json"
        };
        if (FileName != "English.json")
            foreach (var Pair in ReadResource(FileName)) Texts[Pair.Key] = Pair.Value;
        return new LanguageState(Code, CultureInfo.GetCultureInfo(Code), new ReadOnlyDictionary<string, string>(Texts));
    }

    private static Dictionary<string, string> ReadResource(string FileName)
    {
        var Assembly = typeof(LanguageService).Assembly;
        var ResourceName = Assembly.GetManifestResourceNames().FirstOrDefault(Name =>
            Name.EndsWith(".Localization." + FileName, StringComparison.OrdinalIgnoreCase)
            || Name.Equals(FileName, StringComparison.OrdinalIgnoreCase));
        if (ResourceName is null) return new(StringComparer.Ordinal);
        try
        {
            using var Stream = Assembly.GetManifestResourceStream(ResourceName);
            if (Stream is null) return new(StringComparer.Ordinal);
            var Values = JsonSerializer.Deserialize<Dictionary<string, string?>>(Stream);
            return Values?.Where(Pair => Pair.Value is not null).ToDictionary(Pair => Pair.Key, Pair => Pair.Value!, StringComparer.Ordinal)
                ?? new(StringComparer.Ordinal);
        }
        catch (Exception Error) when (Error is IOException or JsonException or NotSupportedException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private sealed record LanguageState(string Code, CultureInfo Culture, IReadOnlyDictionary<string, string> Texts);
}
