using System.Windows.Input;

namespace CDir.Models;

internal sealed record KeyboardShortcut(string Id, string ResourceKey, bool WorksInText = false, bool FileListOnly = false, bool TerminalOnly = false, string? FixedGesture = null)
{
    internal bool IsFixed => this.FixedGesture is not null;
    internal string DefaultGesture => this.FixedGesture ?? DefaultSettings.Current.Shortcuts[this.Id];

    internal static IReadOnlyList<KeyboardShortcut> All { get; } = Array.AsReadOnly<KeyboardShortcut>(
    [
        new("NewWindow", "NewWindow", true, FixedGesture: "Ctrl+N"),
        new("NewTab", "NewTab", true),
        new("ToggleTabPin", "ToggleTabPin", true),
        new("CloseTab", "CloseTab", true),
        new("NextTab", "NextTab", true),
        new("PreviousTab", "PreviousTab", true),
        new("EditPath", "EditPath", true),
        new("EditPathAlternate", "EditPathAlternate", true),
        new("Search", "SearchShortcutsFolder", true),
        new("CopyCurrentFolderPath", "CopyCurrentFolderPath", true),
        new("Back", "Back", true),
        new("Forward", "Forward", true),
        new("Up", "Up", true),
        new("Refresh", "Refresh", true),
        new("NewFolder", "NewFolder"),
        new("Copy", "Copy", FixedGesture: "Ctrl+C"),
        new("Cut", "Cut", FixedGesture: "Ctrl+X"),
        new("Paste", "Paste", FixedGesture: "Ctrl+V"),
        new("Undo", "Undo", true, FixedGesture: "Ctrl+Z"),
        new("Redo", "Redo", true, FixedGesture: "Ctrl+Y"),
        new("SelectAll", "SelectAll", WorksInText: true, FixedGesture: "Ctrl+A"),
        new("Rename", "Rename", FileListOnly: true),
        new("Delete", "Recycle", FileListOnly: true),
        new("Open", "Open", FileListOnly: true),
        new("ToggleTerminal", "Terminal", true),
        new("ToggleSidebar", "ToggleSidebar", true),
        new("TerminalFontIncrease", "TerminalFontIncrease", TerminalOnly: true),
        new("TerminalFontDecrease", "TerminalFontDecrease", TerminalOnly: true),
        new("TerminalFontIncreaseWheel", "TerminalFontIncreaseWheel", TerminalOnly: true, FixedGesture: "Ctrl+WheelUp"),
        new("TerminalFontDecreaseWheel", "TerminalFontDecreaseWheel", TerminalOnly: true, FixedGesture: "Ctrl+WheelDown"),
        new("TerminalFontReset", "TerminalFontReset", TerminalOnly: true)
    ]);

    internal static string Format(Key Key, ModifierKeys Modifiers)
    {
        if (Key is Key.Add or Key.OemPlus)
        {
            Key = Key.OemPlus;
            Modifiers &= ~ModifierKeys.Shift;
        }
        if (Key == Key.Subtract) Key = Key.OemMinus;
        if (Key == Key.NumPad0) Key = Key.D0;
        return ModifierPrefix(Modifiers)
            + (Key == Key.OemPlus ? "+" : Key == Key.OemMinus ? "-" : Key == Key.D0 ? "0" : Key == Key.Enter ? "Enter" : new KeyConverter().ConvertToInvariantString(Key));
    }

    private static string ModifierPrefix(ModifierKeys Modifiers) =>
        ((Modifiers & ModifierKeys.Control) != 0 ? "Ctrl+" : "")
        + ((Modifiers & ModifierKeys.Alt) != 0 ? "Alt+" : "")
        + ((Modifiers & ModifierKeys.Shift) != 0 ? "Shift+" : "");

    internal static bool IsValid(Key Key, ModifierKeys Modifiers) =>
        Enum.IsDefined(Key) && Key is > Key.None and < Key.DeadCharProcessed
        && Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.Escape or Key.Capital or Key.NumLock or Key.Scroll)
        && (Key != Key.Tab || Modifiers == ModifierKeys.Control || Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        && (Modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) == 0
        && !(Modifiers == ModifierKeys.Alt && Key == Key.F4)
        && ((Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0
            || Key is >= Key.F1 and <= Key.F24 || Key is Key.Enter or Key.Delete or Key.Insert or Key.Home or Key.End or Key.PageUp or Key.PageDown);

    internal static bool TryParse(string? Text, out Key Key, out ModifierKeys Modifiers)
    {
        Key = Key.None;
        Modifiers = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(Text)) return false;
        if (Text.EndsWith("++", StringComparison.Ordinal)) Text = Text[..^1] + "OemPlus";
        var Parts = Text.Split('+');
        foreach (var Part in Parts[..^1])
        {
            var Modifier = Part.Trim().ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift,
                _ => ModifierKeys.Windows
            };
            if ((Modifiers & Modifier) != 0 || Modifier == ModifierKeys.Windows) return false;
            Modifiers |= Modifier;
        }
        try { Key = Parts[^1].Trim() == "-" ? Key.OemMinus : (Key)new KeyConverter().ConvertFromInvariantString(Parts[^1].Trim())!; }
        catch (Exception Error) when (Error is NotSupportedException or ArgumentException or FormatException) { return false; }
        return IsValid(Key, Modifiers);
    }

    internal static string FormatWheel(int Delta, ModifierKeys Modifiers) => ModifierPrefix(Modifiers) + (Delta > 0 ? "WheelUp" : "WheelDown");

    internal static bool TryParseWheel(string? Text, out int Delta, out ModifierKeys Modifiers)
    {
        Delta = 0;
        Modifiers = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(Text)) return false;
        var Last = Text.LastIndexOf('+');
        var Direction = Text[(Last + 1)..];
        Delta = Direction.Equals("WheelUp", StringComparison.OrdinalIgnoreCase) ? 1
            : Direction.Equals("WheelDown", StringComparison.OrdinalIgnoreCase) ? -1 : 0;
        if (Delta == 0 || Last < 0 || !TryParse(Text[..(Last + 1)] + "F24", out _, out Modifiers)) return false;
        return (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0;
    }

    internal static bool TryNormalize(string? Text, bool AllowWheel, out string Gesture)
    {
        Gesture = "";
        if (TryParse(Text, out var Key, out var Modifiers)) { Gesture = Format(Key, Modifiers); return true; }
        if (AllowWheel && TryParseWheel(Text, out var Delta, out Modifiers)) { Gesture = FormatWheel(Delta, Modifiers); return true; }
        return false;
    }

    internal static Dictionary<string, string> Resolve(IReadOnlyDictionary<string, string>? Overrides)
    {
        var Result = All.ToDictionary(Item => Item.Id, Item => Item.DefaultGesture, StringComparer.Ordinal);
        foreach (var Item in All)
        {
            if (Item.IsFixed || Overrides is null || !Overrides.TryGetValue(Item.Id, out var Value)) continue;
            if (Value == "") Result[Item.Id] = "";
            else if (TryNormalize(Value, Item.DefaultGesture.Contains("Wheel", StringComparison.Ordinal), out var Gesture)) Result[Item.Id] = Gesture;
        }
        // Damaged or manually edited settings must not leave commands competing for a key.
        // A newly introduced default must not take over an existing personal binding.
        foreach (var NewCommand in new[] { "ToggleSidebar", "ToggleTabPin" })
            if (Overrides is not null && !Overrides.ContainsKey(NewCommand)
                && Result.Any(Pair => Pair.Key != NewCommand && Pair.Value == Result[NewCommand]))
                Result[NewCommand] = "";
        if (Result.Values.Where(Value => Value.Length > 0).GroupBy(Value => Value).Any(Group => Group.Count() > 1))
            return All.ToDictionary(Item => Item.Id, Item => Item.DefaultGesture, StringComparer.Ordinal);
        return Result;
    }

    internal static KeyboardShortcut? Match(IReadOnlyDictionary<string, string> Bindings, Key Key, ModifierKeys Modifiers) =>
        All.FirstOrDefault(Item => Bindings[Item.Id] == Format(Key, Modifiers));
}
