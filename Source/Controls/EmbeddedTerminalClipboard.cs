using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using VirtualTerminal;
using VirtualTerminal.Input;

namespace CDir.Controls;

internal sealed partial class EmbeddedTerminalControl
{
    internal Func<string> ReadClipboardText { get; set; } = () => Clipboard.GetText(TextDataFormat.UnicodeText);
    internal Action<Exception>? ClipboardFailed { get; set; }

    private void InitializeClipboardRouting()
    {
        CommandManager.AddPreviewExecutedHandler(this, (_, E) =>
        {
            if (E.Command != ApplicationCommands.Paste && E.Command != PasteCommand) return;
            E.Handled = true;
            this.PasteClipboard();
        });
        CommandManager.AddPreviewCanExecuteHandler(this, (_, E) =>
        {
            if (E.Command != ApplicationCommands.Paste && E.Command != PasteCommand) return;
            E.CanExecute = this.AllowDirectInput && this.Session is not null;
            E.Handled = true;
        });
        DataObject.AddPastingHandler(this, (_, E) =>
        {
            // TextBox paste does not raise TextInput. Redirect its payload before
            // it can be inserted into the transparent IME editor.
            E.CancelCommand();
            if (E.DataObject.GetDataPresent(DataFormats.UnicodeText))
                this.PasteText(E.DataObject.GetData(DataFormats.UnicodeText) as string ?? "");
            else if (E.DataObject.GetDataPresent(DataFormats.Text))
                this.PasteText(E.DataObject.GetData(DataFormats.Text) as string ?? "");
        });
    }

    internal bool PasteClipboard()
    {
        if (!this.AllowDirectInput || this.Session is null) return false;
        try { return this.PasteText(this.ReadClipboardText()); }
        catch (Exception Error) when (Error is ExternalException or InvalidOperationException)
        {
            this.ClipboardFailed?.Invoke(Error);
            return false;
        }
    }

    internal bool HandlePasteShortcut(Key Key, ModifierKeys Modifiers)
    {
        if (!((Key == Key.V && (Modifiers == ModifierKeys.Control || Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
            || (Key == Key.Insert && Modifiers == ModifierKeys.Shift))) return false;
        this.PasteClipboard();
        return true;
    }

    internal bool PasteText(string Text)
    {
        if (!this.AllowDirectInput || this.Session is not { } Session || Text.Length == 0) return false;
        this.IsComposing = false;
        this.SuppressEditorInput = true;
        try { this.InputEditor?.Clear(); }
        finally { this.SuppressEditorInput = false; }
        if (this.InputEditor is not null) this.InputEditor.Opacity = 0;
        this.ScrollToBottom();
        Text = Text.ReplaceLineEndings("\r");
        var Bracketed = Session.Decoder is TerminalDecoder Decoder && Decoder.State.Modes.BracketedPaste;
        Session.Write(Encoding.UTF8.GetBytes(KeyboardEncoder.WrapBracketedPaste(Text, Bracketed)));
        return true;
    }
}
