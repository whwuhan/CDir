using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

internal sealed partial class PreferencesWindow
{
    internal ListBoxItem ShortcutsCategory { get; private set; } = null!;
    internal FrameworkElement ShortcutsPanel { get; private set; } = null!;
    internal TextBox ShortcutSearch { get; private set; } = null!;
    internal TextBlock ShortcutEmptyHint { get; private set; } = null!;
    internal TextBlock ShortcutError { get; private set; } = null!;
    internal Dictionary<string, TextBox> ShortcutEditors { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, FrameworkElement> ShortcutRows { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, Button> ShortcutClearButtons { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, Button> ShortcutResetButtons { get; } = new(StringComparer.Ordinal);
    private Dictionary<string, string> PreferenceShortcuts = [];
    private Func<string, string, bool> OnShortcutChanged = null!;
    private readonly KeyboardShortcut[] EditableShortcuts = KeyboardShortcut.All.Where(Shortcut => !Shortcut.IsFixed)
        .OrderBy(Shortcut => LanguageService.EnglishText(Shortcut.ResourceKey), StringComparer.OrdinalIgnoreCase)
        .ThenBy(Shortcut => Shortcut.Id, StringComparer.Ordinal).ToArray();

    private void BuildShortcuts(IReadOnlyDictionary<string, string> Shortcuts, Func<string, string, bool> OnChanged)
    {
        this.PreferenceShortcuts = new(Shortcuts, StringComparer.Ordinal);
        this.OnShortcutChanged = OnChanged;
        var Panel = new StackPanel { Visibility = Visibility.Collapsed };
        this.ShortcutsPanel = Panel;
        var Heading = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) };
        Heading.SetResourceReference(TextBlock.TextProperty, "TextShortcutsCategory");
        Heading.SetResourceReference(FontSizeProperty, "UIPreferencesHeadingSize");
        Panel.Children.Add(Heading);
        var SearchLabel = new Label { Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 6) };
        SearchLabel.SetResourceReference(ContentProperty, "TextSearchShortcuts");
        this.ShortcutSearch = new TextBox { Name = "ShortcutSearch", Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 12) };
        this.ShortcutSearch.SetResourceReference(AutomationProperties.NameProperty, "TextSearchShortcuts");
        SearchLabel.Target = this.ShortcutSearch;
        Panel.Children.Add(SearchLabel);
        Panel.Children.Add(this.ShortcutSearch);
        var Hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)this.FindResource("Muted"), Margin = new Thickness(0, 0, 0, 12) };
        Hint.SetResourceReference(TextBlock.TextProperty, "TextShortcutHint");
        Hint.SetResourceReference(FontSizeProperty, "UIPreferencesHintSize");
        Panel.Children.Add(Hint);
        this.ShortcutError = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetLiveSetting(this.ShortcutError, System.Windows.Automation.AutomationLiveSetting.Polite);
        Panel.Children.Add(this.ShortcutError);
        this.ShortcutEmptyHint = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)this.FindResource("Muted") };
        this.ShortcutEmptyHint.SetResourceReference(TextBlock.TextProperty, "TextNoShortcutResults");
        Panel.Children.Add(this.ShortcutEmptyHint);
        foreach (var Shortcut in this.EditableShortcuts)
        {
            var Row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            Row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            Row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var Label = new Label { VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0), Margin = new Thickness(0, 0, 8, 0) };
            var Caption = new TextBlock { TextWrapping = TextWrapping.Wrap };
            Caption.SetResourceReference(TextBlock.TextProperty, "Text" + Shortcut.ResourceKey);
            Label.Content = Caption;
            var Editor = new TextBox
            {
                IsReadOnly = true, IsReadOnlyCaretVisible = false, Padding = new Thickness(6, 5, 6, 5),
                VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                ContextMenu = new ContextMenu(), Tag = Shortcut.Id
            };
            Label.Target = Editor;
            Editor.SetResourceReference(AutomationProperties.NameProperty, "Text" + Shortcut.ResourceKey);
            Editor.SetResourceReference(ToolTipProperty, "TextShortcutRecordHint");
            Editor.PreviewKeyDown += (_, E) =>
            {
                var PressedKey = E.Key == Key.System ? E.SystemKey : E.Key;
                var Modifiers = E.KeyboardDevice.Modifiers;
                if (PressedKey is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                    || PressedKey == Key.Tab && (Modifiers & ModifierKeys.Control) == 0) return;
                E.Handled = true;
                if (E.IsRepeat) return;
                if (!KeyboardShortcut.IsValid(PressedKey, Modifiers)) { this.ShowShortcutError(LanguageService.Text("ShortcutInvalid")); return; }
                this.SetShortcut(Shortcut.Id, KeyboardShortcut.Format(PressedKey, Modifiers));
            };
            Editor.PreviewMouseWheel += (_, E) =>
            {
                if (this.RecordShortcutWheel(Shortcut.Id, E.Delta, Keyboard.Modifiers)) E.Handled = true;
            };
            Button MakeButton(string Resource)
            {
                var Button = new Button { Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                Button.SetResourceReference(ContentProperty, "Text" + Resource);
                Button.SetResourceReference(AutomationProperties.NameProperty, "Text" + Resource);
                return Button;
            }
            var Clear = MakeButton("ClearShortcut");
            var Reset = MakeButton("ResetShortcut");
            Clear.Click += (_, _) => this.SetShortcut(Shortcut.Id, "");
            Reset.Click += (_, _) => this.SetShortcut(Shortcut.Id, Shortcut.DefaultGesture);
            Grid.SetColumn(Editor, 1);
            Grid.SetColumn(Clear, 2);
            Grid.SetColumn(Reset, 3);
            Row.Children.Add(Label);
            Row.Children.Add(Editor);
            Row.Children.Add(Clear);
            Row.Children.Add(Reset);
            Panel.Children.Add(Row);
            this.ShortcutRows.Add(Shortcut.Id, Row);
            this.ShortcutEditors.Add(Shortcut.Id, Editor);
            this.ShortcutClearButtons.Add(Shortcut.Id, Clear);
            this.ShortcutResetButtons.Add(Shortcut.Id, Reset);
        }
        this.ShortcutSearch.TextChanged += (_, _) => this.FilterShortcuts();
        this.RefreshShortcuts();
    }

    internal bool RecordShortcutWheel(string Id, int Delta, ModifierKeys Modifiers)
    {
        if (Delta == 0 || !KeyboardShortcut.All.Any(Item => Item.Id == Id && !Item.IsFixed && Item.DefaultGesture.Contains("Wheel", StringComparison.Ordinal))
            || !this.ShortcutEditors.TryGetValue(Id, out var Editor) || !Editor.IsFocused
            || (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0) return false;
        this.SetShortcut(Id, KeyboardShortcut.FormatWheel(Delta, Modifiers));
        return true;
    }

    internal bool SetShortcut(string Id, string Gesture)
    {
        if (!this.PreferenceShortcuts.ContainsKey(Id) || !KeyboardShortcut.All.Any(Item => Item.Id == Id && !Item.IsFixed)) return false;
        if (Gesture.Length > 0)
        {
            var Definition = KeyboardShortcut.All.Single(Item => Item.Id == Id);
            if (!KeyboardShortcut.TryNormalize(Gesture, Definition.DefaultGesture.Contains("Wheel", StringComparison.Ordinal), out Gesture)) { this.ShowShortcutError(LanguageService.Text("ShortcutInvalid")); return false; }
            var Conflict = KeyboardShortcut.All.FirstOrDefault(Item => Item.Id != Id && this.PreferenceShortcuts[Item.Id] == Gesture);
            if (Conflict is not null) { this.ShowShortcutError(LanguageService.Text("ShortcutConflict", LanguageService.Text(Conflict.ResourceKey))); return false; }
        }
        if (!this.OnShortcutChanged(Id, Gesture)) { this.ShowShortcutError(LanguageService.Text("ShortcutSaveError")); return false; }
        this.PreferenceShortcuts[Id] = Gesture;
        this.ShowShortcutError(null);
        this.RefreshShortcuts();
        return true;
    }

    private void ShowShortcutError(string? Message)
    {
        this.ShortcutError.Text = Message ?? "";
        this.ShortcutError.Visibility = Message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshShortcuts()
    {
        foreach (var Shortcut in this.EditableShortcuts)
        {
            var Gesture = this.PreferenceShortcuts[Shortcut.Id];
            this.ShortcutEditors[Shortcut.Id].Text = Gesture.Length == 0 ? LanguageService.Text("ShortcutUnassigned") : Gesture;
            this.ShortcutClearButtons[Shortcut.Id].IsEnabled = Gesture.Length > 0;
            this.ShortcutResetButtons[Shortcut.Id].IsEnabled = Gesture != Shortcut.DefaultGesture;
        }
        this.FilterShortcuts();
    }

    private void FilterShortcuts()
    {
        var Terms = this.ShortcutSearch.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var Visible = 0;
        foreach (var Shortcut in this.EditableShortcuts)
        {
            var SearchText = LanguageService.Text(Shortcut.ResourceKey) + " " + Shortcut.Id + " " + this.PreferenceShortcuts[Shortcut.Id];
            var Matches = Terms.All(Term => SearchText.Contains(Term, StringComparison.CurrentCultureIgnoreCase)
                || SearchText.Replace("+", "").Contains(Term.Replace("+", ""), StringComparison.CurrentCultureIgnoreCase));
            this.ShortcutRows[Shortcut.Id].Visibility = Matches ? Visibility.Visible : Visibility.Collapsed;
            if (Matches) Visible++;
        }
        this.ShortcutEmptyHint.Visibility = Visible == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
