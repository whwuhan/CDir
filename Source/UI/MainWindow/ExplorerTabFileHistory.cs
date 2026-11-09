using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDir.Localization;
using CDir.Services;
using CDir.UI.Dialogs;

namespace CDir.UI;

public partial class ExplorerTab
{
    private static FileOperationHistory SharedFileHistory => HistoryInstance ??= new FileOperationHistory(
        AppMain.SmokeTest ? Path.Combine(AppMain.SmokeRoot!, "OperationHistory")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CDir", "OperationHistory"));
    private static FileOperationHistory? HistoryInstance;
    private Action<IntPtr, string, bool> ReplayShellOperation = ShellContextMenu.Replay;
    internal Task HandleHistoryCommandAsync(bool Redo) => this.ReplayFileOperationAsync(Redo);

    private async Task ReplayFileOperationAsync(bool Redo)
    {
        if (this.HostWindow.Tabs.Any(Tab => Tab.OperationBusy) || this.Loading) return;
        var Owner = new System.Windows.Interop.WindowInteropHelper(this.HostWindow).Handle;
        var Folder = this.IsVirtualLocation ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : this.CurrentPath;
        await this.RunOperationAsync(LanguageService.Text(Redo ? "Redoing" : "Undoing"),
            () => this.ReplayShellOperation(Owner, Folder, Redo), LanguageService.Text(Redo ? "RedoComplete" : "UndoComplete"), RequiresSta: true);
        foreach (var Tab in this.HostWindow.Tabs.Where(Tab => !ReferenceEquals(Tab, this))) Tab.ScheduleRefresh();
        this.FocusMainFilePanel();
    }

    internal void FocusMainFilePanel()
    {
        if (this.WindowClosed || !this.IsTabSelected) return;
        var Target = this.FileGrid;
        FocusManager.SetFocusedElement(this, Target);
        Target.Focus();
    }

    internal void SelectAllVisibleFiles()
    {
        if (this.WindowClosed || !this.IsTabSelected) return;
        if (this.AddressEditing) this.CancelAddressEdit();
        this.DismissSearchTips();
        if (this.MarqueeOrigin is not null) this.EndMarqueeSelection(Cancel: true);
        this.FocusMainFilePanel();
        this.FileGrid.SelectAll();
    }

    internal async void ShowFileHistory()
    {
        try
        {
            var Journal = await Task.Run(SharedFileHistory.ReadJournal);
            var Lines = new List<string>();
            foreach (var Line in Journal.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var Document = JsonDocument.Parse(Line);
                    var Entry = Document.RootElement;
                    var Operation = Entry.GetProperty("Operation").GetString()!;
                    var Status = Entry.GetProperty("Status").GetString()!;
                    Lines.Add($"{Entry.GetProperty("Time").GetDateTimeOffset().ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}  "
                        + LanguageService.Text(Operation) + " · " + LanguageService.Text("History" + Status));
                    Lines.AddRange(Entry.GetProperty("Paths").EnumerateArray().Select(Path => "    " + Path.GetString()));
                    if (Entry.GetProperty("Error").GetString() is { } Error) Lines.Add("    " + Error);
                    Lines.Add("");
                }
                catch (JsonException) { Lines.Add(Line); }
            }
            var Window = new FileHistoryWindow(this.HostWindow, string.Join(Environment.NewLine, Lines));
            Window.ShowDialog();
        }
        catch (Exception Error) when (Error is IOException or UnauthorizedAccessException)
        { this.ShowNotice(LanguageService.Text("OperationIncomplete", FriendlyError(Error)), true); }
    }

    private sealed class FileHistoryWindow : DialogWindow
    {
        internal FileHistoryWindow(Window Owner, string Content)
        {
            this.Owner = Owner;
            this.Title = LanguageService.Text("OperationHistory");
            this.Width = Math.Min(900, SystemParameters.WorkArea.Width);
            this.Height = Math.Min(600, SystemParameters.WorkArea.Height);
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            this.ShowActivated = !AppMain.SmokeTest;
            this.SetResourceReference(FontFamilyProperty, "InterfaceFont");
            this.SetResourceReference(FontSizeProperty, "UIFontMenus");
            var Panel = new DockPanel { Margin = new Thickness(12) };
            var Hint = new TextBlock { Text = LanguageService.Text("HistoryHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(Hint, Dock.Top);
            Panel.Children.Add(Hint);
            Panel.Children.Add(new TextBox { Text = Content.Length == 0 ? LanguageService.Text("HistoryEmpty") : Content,
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
            this.Content = Panel;
        }
    }
}
