using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunTerminalSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialLanguage = LanguageService.CurrentLanguage;
        var FixtureId = Guid.NewGuid().ToString("N");
        var Fixture = Path.Combine(Path.GetTempPath(), "CDirTerminal-" + FixtureId);
        var First = Directory.CreateDirectory(Path.Combine(Fixture, "First 中文 & Folder")).FullName;
        var Second = Directory.CreateDirectory(Path.Combine(Fixture, "Second Folder")).FullName;
        var Marker = Path.Combine(Fixture, ".CDirTerminalOwner");
        File.WriteAllText(Marker, FixtureId);
        File.WriteAllText(Path.Combine(First, "Select All.txt"), "Owned main-panel selection fixture");
        CDir.Services.EmbeddedTerminalSession? Session = null;
        void Check(bool Condition, string Name)
        {
            if (!Condition)
            {
                if (AppMain.ScreenshotPath is { } FailureOutput) File.WriteAllText(FailureOutput + ".terminal.txt",
                    (this.TerminalSession is { } FailedSession
                        ? "Cursor: " + ((VirtualTerminal.TerminalDecoder)FailedSession.Decoder).CursorPosition
                            + " Input: [" + CurrentInput() + "]" + Environment.NewLine : "") + Screen());
                throw new InvalidOperationException("Terminal UI smoke test failed: " + Name);
            }
            Passed.Add(Name);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        async Task WaitUntil(Func<bool> Condition, string Name)
        {
            bool Ready()
            {
                try { return Condition(); }
                catch (IOException Error) when ((Error.HResult & 0xFFFF) is 32 or 33) { return false; }
            }
            var Deadline = Environment.TickCount64 + 15000;
            while (!Ready() && Environment.TickCount64 < Deadline) { await Task.Delay(40); await Settle(); }
            Check(Ready(), Name);
        }
        string Screen()
        {
            if (this.TerminalSession is not { } Active) return "";
            var Text = new StringBuilder();
            lock (Active.Buffer.SyncRoot)
                for (var Y = 0; Y < Active.Buffer.Rows; Y++)
                {
                    foreach (var Cell in Active.Buffer.GetRow(Y))
                        if (!Cell.Style.Continuation) Text.Append(Cell.Character.ToString());
                    Text.AppendLine();
                }
            return Text.ToString();
        }
        static string Quote(string Text) => "'" + Text.Replace("'", "''", StringComparison.Ordinal) + "'";
        bool ScreenContainsWrapped(string Text) => string.Concat(Screen().Replace("\r", "", StringComparison.Ordinal)
            .Split('\n').Select(Line => Line.TrimEnd())).Contains(Text, StringComparison.Ordinal);
        string CurrentInput()
        {
            var Active = this.TerminalSession!;
            var Text = new StringBuilder();
            lock (Active.Buffer.SyncRoot)
                for (var Y = 0; Y <= ((VirtualTerminal.TerminalDecoder)Active.Decoder).CursorPosition.Y; Y++)
                {
                    var Row = new StringBuilder();
                    foreach (var Cell in Active.Buffer.GetRow(Y))
                        if (!Cell.Style.Continuation) Row.Append(Cell.Character.ToString());
                    Text.Append(Row.ToString().TrimEnd());
                }
            var Snapshot = Text.ToString();
            var Prompt = Snapshot.LastIndexOf("PS ", StringComparison.Ordinal);
            var End = Prompt < 0 ? -1 : Snapshot.IndexOf('>', Prompt);
            return End < 0 ? "<no prompt>" : Snapshot[(End + 1)..].TrimStart();
        }
        void Send(string Text) => this.TerminalSession!.Write(Encoding.UTF8.GetBytes(Text + "\r"));
        void TypeText(string Text)
        {
            var Surface = this.TerminalSurface!;
            foreach (var Character in Text)
            {
                var Input = new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                    new TextComposition(InputManager.Current, Surface, Character.ToString()))
                    { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
                Surface.RaiseEvent(Input);
                if (!Input.Handled) throw new InvalidOperationException("The terminal did not handle typed text.");
            }
        }
        void PressKey(Key Key)
        {
            var Surface = this.TerminalSurface!;
            var Target = ((CDir.Controls.EmbeddedTerminalControl)Surface).InputEditor;
            var Input = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(Target), 0, Key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Target.RaiseEvent(Input);
            if (!Input.Handled) throw new InvalidOperationException("The terminal did not handle the key: " + Key);
        }
        void TypeCommand(string Text) { TypeText(Text); PressKey(Key.Enter); }
        async Task OpenAndCloseFileMenu()
        {
            var PreviousStaysOpen = this.HostWindow.FileMenu.StaysOpen;
            this.HostWindow.FileMenu.StaysOpen = true;
            try
            {
            var Invoker = (System.Windows.Automation.Provider.IInvokeProvider)
                new System.Windows.Automation.Peers.ButtonAutomationPeer(this.HostWindow.FileMenuButton)
                    .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);
            Invoker.Invoke();
            await Settle();
            Check(this.HostWindow.FileMenu.IsOpen && this.TerminalSession is { HasExited: false },
                "The File menu opens with a live terminal and an initialized accessibility peer");
            this.HostWindow.FileMenu.IsOpen = false;
            FocusManager.SetFocusedElement(this, this.TerminalSurface);
            await Settle();
            Check(this.TerminalSurface!.IsFocused && this.TerminalSurface.AllowDirectInput,
                "Closing the File menu allows focus to return to the terminal without activating the test window");
            }
            finally { this.HostWindow.FileMenu.IsOpen = false; this.HostWindow.FileMenu.StaysOpen = PreviousStaysOpen; }
        }
        bool ToggleIsAtBottomRight()
        {
            var Position = this.TerminalToggleButton.TranslatePoint(new Point(), this);
            var ContentBottom = this.RightPanel.TranslatePoint(new Point(0, this.RightPanel.ActualHeight), this).Y;
            return this.TerminalToggleButton.IsVisible && this.TerminalToggleButton.ActualWidth > 0
                && this.TerminalToggleButton.ActualHeight > 0 && Position.Y >= ContentBottom
                && Position.Y + this.TerminalToggleButton.ActualHeight <= this.ActualHeight
                && Position.X > this.ActualWidth / 2
                && Math.Abs(this.ActualWidth - Position.X - this.TerminalToggleButton.ActualWidth - 21) < 2;
        }
        bool ProcessExited(int Id)
        {
            try { using var Process = System.Diagnostics.Process.GetProcessById(Id); return Process.HasExited; }
            catch (ArgumentException) { return true; }
        }
        try
        {
            Check(this.TerminalPanel.Visibility == Visibility.Collapsed && this.TerminalSession is null && this.TerminalRow.Height.Value == 0,
                "The terminal starts hidden and does not launch any shell until requested");
            Check(KeyboardShortcut.Match(this.ShortcutBindings, Key.X, ModifierKeys.Alt)?.Id == "ToggleTerminal"
                && KeyboardShortcut.Match(this.ShortcutBindings, Key.X, ModifierKeys.Control)?.Id != "ToggleTerminal",
                "Alt+X is the default terminal toggle and does not interfere with Ctrl+X");
            Check(await this.NavigateAsync(First), "Open the terminal's owned starting directory");
            await Settle();
            Check(ToggleIsAtBottomRight(), "The terminal toggle is visible at the bottom right of the status bar while the terminal is hidden");
            var FullHeight = this.FileGrid.ActualHeight;
            var SidebarHeight = this.SidebarPanel.ActualHeight;
            var ToggleInput = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Alt), PresentationSource.FromVisual(this.SearchBox), 0, Key.X)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            this.SearchBox.RaiseEvent(ToggleInput);
            if (this.TerminalStartupTask is { } Startup) await Startup;
            Check(ToggleInput.Handled,
                "The terminal shortcut also works from an address or search text field");
            Session = this.TerminalSession;
            Check(Session is not null && this.TerminalSurface?.Session == Session && SamePath(Session.InitialDirectory, First)
                && this.TerminalPanel.IsVisible && this.TerminalSplitter.IsVisible,
                "First opening attaches a real ConPTY session in the directory displayed by the file list");
            await WaitUntil(() => Screen().Contains("PS ", StringComparison.Ordinal), "The embedded PowerShell displays an interactive prompt");
            var TerminalSelectSource = (UIElement?)FindVisualChild<TextBox>(this.TerminalSurface!) ?? this.TerminalSurface!;
            this.FileGrid.SelectedItems.Clear();
            var TerminalSelectAll = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control), PresentationSource.FromVisual(TerminalSelectSource), 0, Key.A)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            TerminalSelectSource.RaiseEvent(TerminalSelectAll);
            await Settle();
            Check(TerminalSelectAll.Handled && this.FileGrid.Items.Count > 0
                && this.FileGrid.SelectedItems.Count == this.FileGrid.Items.Count
                && ReferenceEquals(FocusManager.GetFocusedElement(this), this.FileGrid) && !Session!.HasExited,
                "Ctrl+A from the live terminal selects the visible file panel and preserves the shell process");
            this.TerminalSurface!.Focus();
            await Settle();
            var SidebarBeforeShortcut = this.Settings.ShowSidebar;
            foreach (var Expected in new[] { !SidebarBeforeShortcut, SidebarBeforeShortcut })
            {
                var SidebarKey = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Alt), PresentationSource.FromVisual(this.TerminalSurface!), 0, Key.D1)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                this.TerminalSurface!.RaiseEvent(SidebarKey);
                await Settle();
                Check(SidebarKey.Handled && this.Settings.ShowSidebar == Expected && !Session!.HasExited,
                    "Alt+1 toggles the sidebar from the live terminal while preserving its process");
            }
            var SearchKey = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Control), PresentationSource.FromVisual(this.TerminalSurface!), 0, Key.F)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            this.TerminalSurface!.RaiseEvent(SearchKey);
            Check(SearchKey.Handled && this.SearchBox.IsFocused, "Ctrl+F focuses the search field from the live terminal");
            var SearchEscape = new KeyEventArgs(this.UnmodifiedSmokeKeyboard, PresentationSource.FromVisual(this.SearchBox), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            this.SearchBox.RaiseEvent(SearchEscape);
            Check(SearchEscape.Handled && !this.SearchBox.IsFocused, "Escape releases search focus after leaving the live terminal");
            FocusManager.SetFocusedElement(this, this.TerminalSurface);
            var TerminalPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(this.TerminalSurface!);
            Check(TerminalPeer is not null, "Creating terminal accessibility after shell output preserves keyboard focus and menu access");
            Check(TerminalPeer!.GetPattern(System.Windows.Automation.Peers.PatternInterface.Text)
                is System.Windows.Automation.Provider.ITextProvider TextProvider
                && TextProvider.DocumentRange.GetText(-1).Contains("PS ", StringComparison.Ordinal),
                "Terminal accessibility retains the text pattern and the current PowerShell prompt");
            await OpenAndCloseFileMenu();
            var ProcessId = Session!.ProcessId;
            using (var Shell = System.Diagnostics.Process.GetProcessById(ProcessId))
                Check(Shell.MainWindowHandle == IntPtr.Zero && !Shell.HasExited,
                    "The terminal runs inside CDir without opening an external console window");
            await Settle();
            Check(ToggleIsAtBottomRight(), "The terminal toggle stays at the bottom right while the terminal is open");
            Check(this.FileGrid.ActualHeight < FullHeight - 100 && Math.Abs(this.SidebarPanel.ActualHeight - SidebarHeight) < 1
                && this.TerminalPanel.TranslatePoint(new Point(), this.RightPanel).Y >= this.MainPanelContent.ActualHeight,
                "The terminal occupies only the bottom of the right panel and keeps the sidebar at its full height");
            const string FirstCommand = "$global:CDirHistory='first'";
            const string SecondCommand = "$global:CDirHistory='second'";
            const string Draft = "$global:CDirHistory='草稿'";
            PressKey(Key.Up);
            PressKey(Key.Down);
            await Task.Delay(100);
            Check(CurrentInput() == "", "A fresh terminal has no external history or startup commands to recall");
            await this.RunTerminalRepaintSmokeChecksAsync(Passed);
            var ScrollbackReady = Path.Combine(Fixture, "scrollback-ready.txt");
            Send("1..1000 | ForEach-Object { [Console]::WriteLine(('scrollback '+$_+' '+('x'*80))) }; [IO.File]::WriteAllText(" + Quote(ScrollbackReady) + ",'ready')");
            await WaitUntil(() => File.Exists(ScrollbackReady) && CurrentInput() == "",
                "A real shell fills terminal scrollback before continuous editing checks");
            var EditingSurface = (CDir.Controls.EmbeddedTerminalControl)this.TerminalSurface!;
            await Settle();
            Check(this.TerminalScrollBar.IsVisible && this.TerminalScrollBar.IsEnabled
                && this.TerminalScrollBar.Maximum == EditingSurface.HistoryLines
                && this.TerminalScrollBar.Value == this.TerminalScrollBar.Maximum
                && this.TerminalScrollBar.TranslatePoint(new Point(), this.TerminalHost).X >= this.TerminalHost.ActualWidth,
                "The right-side terminal scrollbar reserves its own width and follows live output");
            this.TerminalScrollBar.Value = 0;
            await Settle();
            Check(EditingSurface.HistoryOffset == EditingSurface.HistoryLines
                && EditingSurface.GetVisibleScreenText().Contains("scrollback ", StringComparison.Ordinal),
                "Dragging the scrollbar to the top displays real terminal history");
            System.Windows.Controls.Primitives.ScrollBar.PageDownCommand.Execute(null, this.TerminalScrollBar);
            await Settle();
            Check(this.TerminalScrollBar.Value > 0 && EditingSurface.HistoryOffset
                == EditingSurface.HistoryLines - (int)Math.Round(this.TerminalScrollBar.Value),
                "Clicking the scrollbar track pages through the same terminal history");
            EditingSurface.ScrollToBottom();
            await Settle();
            var Wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            EditingSurface.RaiseEvent(Wheel);
            await Settle();
            Check(Wheel.Handled && EditingSurface.HistoryOffset == 3
                && this.TerminalScrollBar.Value == this.TerminalScrollBar.Maximum - 3,
                "Terminal wheel scrolling synchronizes the visible scrollbar");
            this.TerminalScrollBar.Value = this.TerminalScrollBar.Maximum;
            await Settle();
            Check(EditingSurface.HistoryOffset == 0, "Dragging the scrollbar to the bottom restores live terminal input");
            var EditorWidth = EditingSurface.InputEditor.Width;
            TypeText(new string('d', 160));
            await WaitUntil(() => CurrentInput() == new string('d', 160), "A wrapped draft reaches the real shell with populated scrollback");
            var DeleteTimer = Stopwatch.StartNew();
            for (var Index = 0; Index < 120; Index++)
            {
                PressKey(Key.Back);
                await Task.Delay(8);
            }
            await WaitUntil(() => CurrentInput() == new string('d', 40),
                "Continuous Backspace removes every requested character in order without losing input");
            DeleteTimer.Stop();
            Check(DeleteTimer.Elapsed < TimeSpan.FromSeconds(5),
                "Continuous deletion with populated scrollback settles within the responsiveness budget");
            Check(EditingSurface.InputEditor.Width == EditorWidth,
                "Echoed editing moves the IME host without resizing its hidden text editor");
            var AccessibleProvider = (System.Windows.Automation.Provider.ITextProvider)TerminalPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Text)!;
            var AccessibleDraft = new string(AccessibleProvider.DocumentRange.GetText(-1).Where(Character => !char.IsWhiteSpace(Character)).ToArray());
            Check(AccessibleDraft.Contains(new string('d', 40), StringComparison.Ordinal),
                "Accessibility explicitly requested after continuous deletion returns the current terminal text");
            PressKey(Key.Home);
            for (var Index = 0; Index < 20; Index++) PressKey(Key.Delete);
            await WaitUntil(() => CurrentInput() == new string('d', 20), "Repeated forward Delete edits the current draft without executing it");
            PressKey(Key.End);
            for (var Index = 0; Index < 20; Index++) PressKey(Key.Back);
            await WaitUntil(() => CurrentInput() == "", "Continuous editing leaves an empty prompt ready for the next command");
            if (AppMain.ScreenshotPath is { } PerformanceOutput)
                File.WriteAllText(PerformanceOutput + ".editing.json", System.Text.Json.JsonSerializer.Serialize(new
                    { backspaces = 120, cadence_ms = 8, elapsed_ms = DeleteTimer.Elapsed.TotalMilliseconds, scrollback_lines = 1000 }));
            var HistoryCleared = Path.Combine(Fixture, "history-cleared.txt");
            Send("[Microsoft.PowerShell.PSConsoleReadLine]::ClearHistory(); [IO.File]::WriteAllText(" + Quote(HistoryCleared) + ",'ready')");
            await WaitUntil(() => File.Exists(HistoryCleared) && CurrentInput() == "", "The performance fixture clears its own command history before history-navigation checks");
            TypeCommand(FirstCommand);
            await WaitUntil(() => ScreenContainsWrapped(FirstCommand) && CurrentInput() == "",
                "The first history command completes and returns to an empty prompt");
            TypeCommand(SecondCommand);
            await WaitUntil(() => ScreenContainsWrapped(SecondCommand) && CurrentInput() == "",
                "The second history command completes and returns to an empty prompt");
            await WaitUntil(() => !Screen().Contains(">>", StringComparison.Ordinal), "Enter submits one command without inserting a spurious continuation line");
            TypeText(Draft);
            await WaitUntil(() => CurrentInput() == Draft, "Unsubmitted Unicode input is visible before browsing history");
            PressKey(Key.Up);
            await WaitUntil(() => CurrentInput() == SecondCommand, "Up recalls the most recently submitted command");
            PressKey(Key.Up);
            await WaitUntil(() => CurrentInput() == FirstCommand, "Repeated Up recalls the preceding command");
            PressKey(Key.Up);
            await Task.Delay(100);
            Check(CurrentInput() == FirstCommand, "Up at the oldest command keeps that command selected");
            PressKey(Key.Down);
            await WaitUntil(() => CurrentInput() == SecondCommand, "Down advances to the next submitted command");
            PressKey(Key.Down);
            await WaitUntil(() => CurrentInput() == Draft, "Down past the newest command restores the unsubmitted Unicode draft");
            PressKey(Key.Down);
            await Task.Delay(100);
            Check(CurrentInput() == Draft, "Down at the draft leaves the unsubmitted text intact");
            foreach (var Character in Draft) PressKey(Key.Back);
            await WaitUntil(() => CurrentInput() == "", "The recalled draft can be cleared without executing it");
            PressKey(Key.Up);
            await WaitUntil(() => CurrentInput() == SecondCommand, "A cleared prompt can recall the newest command again");
            PressKey(Key.Down);
            await WaitUntil(() => CurrentInput() == "", "Down restores an originally empty input line");
            PressKey(Key.Up);
            await WaitUntil(() => CurrentInput() == SecondCommand, "A recalled command is available for editing");
            PressKey(Key.Back);
            PressKey(Key.Back);
            TypeText("D'");
            await WaitUntil(() => CurrentInput() == "$global:CDirHistory='seconD'", "Recalled history supports normal text editing");
            PressKey(Key.Enter);
            var HistoryResult = Path.Combine(Fixture, "history.txt");
            Send("Set-Content -LiteralPath " + Quote(HistoryResult) + " -Value $global:CDirHistory");
            await WaitUntil(() => File.Exists(HistoryResult) && File.ReadAllText(HistoryResult).Trim() == "seconD", "The edited history command executes in the real shell");
            Check(File.ReadAllText(HistoryResult).Trim() == "seconD", "Executing recalled history uses the edited command");
            TypeCommand("Write-Output ('multi'+");
            await WaitUntil(() => Screen().Contains(">>", StringComparison.Ordinal),
                "An incomplete expression still displays the genuine PowerShell continuation prompt");
            TypeCommand("'line')");
            await WaitUntil(() => Screen().Contains("multiline", StringComparison.Ordinal) && CurrentInput() == "",
                "Completing a multiline expression executes it and returns to a normal prompt");
            var Ready = Path.Combine(Fixture, "ready.txt");
            TypeText("x");
            PressKey(Key.Back);
            TypeCommand("$env:CDIR_TERMINAL_STATE='kept'; Set-Content -LiteralPath " + Quote(Ready) + " -Value ((Get-Location).Path+'|'+$env:CDIR_TERMINAL_STATE) -Encoding UTF8");
            await WaitUntil(() => File.Exists(Ready) && File.ReadAllText(Ready).Trim() == First + "|kept", "Typed text Backspace and Enter reach the real shell after closing the File menu");
            Check(File.ReadAllText(Ready).Trim() == First + "|kept", "The shell's actual current directory preserves spaces Unicode and ampersands");
            Send("[Console]::Write(([char]27)+'[32m'+('CO'+'LORED 中文')+([char]27)+'[0m'+[Environment]::NewLine)");
            await WaitUntil(() => Screen().Contains("COLORED 中文", StringComparison.Ordinal), "The terminal decodes ANSI colors and Unicode output");
            using (var Glyphs = new VirtualTerminal.Rendering.GlyphCache())
            {
                Glyphs.Configure(this.TerminalSurface!.FontFamily.Source, this.TerminalSurface.FontSize, 1,
                    ((VirtualTerminal.TerminalDecoder)Session.Decoder).Options.FontFallback);
                Check(Glyphs.GetGlyph(new Rune('中')).Item2 != 0 && Glyphs.GetGlyph(new Rune('文')).Item2 != 0,
                    "Terminal glyph fallback resolves Chinese characters instead of missing-glyph boxes");
            }
            var HideInput = new KeyEventArgs(new SmokeKeyboardDevice(ModifierKeys.Alt), PresentationSource.FromVisual(this.TerminalSurface!), 0, Key.X)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            this.TerminalSurface!.RaiseEvent(HideInput);
            Check(HideInput.Handled, "Alt+X routes from the embedded terminal through the main window");
            await Settle();
            Check(!this.TerminalPanel.IsVisible && ReferenceEquals(Session, this.TerminalSession) && !Session.HasExited
                && Math.Abs(this.FileGrid.ActualHeight - FullHeight) < 1,
                "Hiding the terminal restores the file list's height and retains the running shell session");
            var Hidden = Path.Combine(Fixture, "hidden.txt");
            Send("Set-Content -LiteralPath " + Quote(Hidden) + " -Value $env:CDIR_TERMINAL_STATE");
            await WaitUntil(() => File.Exists(Hidden), "Commands continue running while the terminal is hidden");
            Check(await this.NavigateAsync(Second), "Navigate the file list to another directory while the shell is hidden");
            this.TerminalToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (this.TerminalStartupTask is not null) await this.TerminalStartupTask;
            await Settle();
            Check(ReferenceEquals(Session, this.TerminalSession) && this.TerminalSession!.ProcessId == ProcessId
                && File.ReadAllText(Hidden).Trim() == "kept", "The status bar button restores the same process and environment instead of restarting it");
            await OpenAndCloseFileMenu();
            var Resumed = Path.Combine(Fixture, "resumed.txt");
            TypeCommand("Set-Content -LiteralPath " + Quote(Resumed) + " -Value (Get-Location).Path -Encoding UTF8");
            await WaitUntil(() => File.Exists(Resumed) && File.ReadAllText(Resumed).Trim() == First, "The restored shell accepts additional input");
            Check(File.ReadAllText(Resumed).Trim() == First && SamePath(this.CurrentPath, Second),
                "Later file navigation does not silently change an existing terminal's working directory");
            PressKey(Key.Up);
            // The newest command has a long owned fixture path and can wrap across terminal rows.
            await WaitUntil(() => CurrentInput().Contains("resumed.txt", StringComparison.Ordinal),
                "Hiding and restoring the terminal retains command history including wrapped input");
            PressKey(Key.Down);
            await WaitUntil(() => CurrentInput() == "", "Down after restoring the terminal returns to an empty prompt");
            await this.RunTerminalThemeSmokeChecksAsync(Passed);
            await this.RunTerminalInputSmokeChecksAsync(Passed);
            await this.RunTerminalClipboardSmokeChecksAsync(Passed);
            await this.RunTerminalPredictionSmokeChecksAsync(Passed);
            var Rows = Session.Buffer.Rows;
            this.TerminalRow.Height = new GridLength(160);
            await WaitUntil(() => Session.Buffer.Rows < Rows, "Changing panel height resizes the actual terminal grid");
            await WaitUntil(() => this.TerminalScrollBar.ViewportSize == Session.Buffer.Rows,
                "Resizing the terminal updates the scrollbar viewport size");
            Check(this.TerminalRow.ActualHeight >= 96 && this.MainPanelContent.ActualHeight >= 120,
                "Resizing retains usable minimum heights for both file browsing and terminal input");
            foreach (var Language in LanguageService.SupportedLanguages)
            {
                this.ChangeLanguage(Language.Code, Persist: false);
                await Settle();
                Check(this.TerminalToggleButton.Content?.ToString() == LanguageService.Text("Terminal")
                    && this.TerminalSurface!.ContextMenu.Items.OfType<MenuItem>().All(Item => Item.Header?.ToString() == LanguageService.Text((string)Item.Tag)),
                    Language.Code + " terminal captions tooltips and context actions use the selected application language");
            }
            this.ChangeLanguage(InitialLanguage, Persist: false);
            this.TerminalRow.Height = new GridLength(240);
            await Settle();
            await Task.Delay(250);
            if (AppMain.ScreenshotPath is { } Output)
            {
                var Bitmap = new RenderTargetBitmap((int)this.ActualWidth, (int)this.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(this);
                var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, "EmbeddedTerminal.png")); Encoder.Save(Stream);
            }
            Send("exit");
            await WaitUntil(() => Session.HasExited && this.TerminalExitedHint.IsVisible, "Exiting PowerShell leaves a readable terminal with a restart hint");
            Check(this.TerminalSurface?.AllowDirectInput == false && this.TerminalPanel.IsVisible,
                "An exited terminal keeps its panel visible and stops accepting dead-session input");
            this.RestartTerminalClick(this, new RoutedEventArgs(Button.ClickEvent));
            await this.TerminalStartupTask!;
            await Session.CleanupTask.WaitAsync(TimeSpan.FromSeconds(10));
            Check(this.TerminalSession is { } NewSession && NewSession.ProcessId != ProcessId && SamePath(NewSession.InitialDirectory, Second)
                && ProcessExited(ProcessId), "Restart opens a new terminal in the currently displayed directory and releases the old process");
            Session = this.TerminalSession;
            await WaitUntil(() => Screen().Contains("PS ", StringComparison.Ordinal), "The restarted terminal renders a new prompt");
            await Settle();
            Check(((CDir.Controls.EmbeddedTerminalControl)this.TerminalSurface!).HistoryOffset == 0
                && this.TerminalScrollBar.Maximum == Session!.Buffer.ScrollbackCount,
                "Restarting resets the scrollbar to the new session history");
            await OpenAndCloseFileMenu();
            PressKey(Key.Up);
            await Task.Delay(100);
            Check(CurrentInput() == "", "Restarting the terminal starts with an empty session history");
            var Restarted = Path.Combine(Fixture, "restarted.txt");
            TypeCommand("Set-Content -LiteralPath " + Quote(Restarted) + " -Value 'input works after restart'");
            await WaitUntil(() => File.Exists(Restarted), "Keyboard input still reaches the shell after restarting and reopening the File menu");
            Send("1..500 | ForEach-Object { [Console]::WriteLine(('x'*2000)) }");
            var FinalProcess = Session!.ProcessId;
            this.ReleaseTerminalSession();
            await Session.CleanupTask.WaitAsync(TimeSpan.FromSeconds(10));
            Check(ProcessExited(FinalProcess) && this.TerminalSession is null,
                "Releasing a terminal with pending output terminates only its owned process tree and drains ConPTY without deadlocking");
            this.HideTerminal();
        }
        finally
        {
            this.HostWindow.FileMenu.IsOpen = false;
            var Remaining = this.TerminalSession;
            this.ReleaseTerminalSession();
            if (Remaining is not null) await Remaining.CleanupTask.WaitAsync(TimeSpan.FromSeconds(10));
            else if (Session is not null) await Session.CleanupTask.WaitAsync(TimeSpan.FromSeconds(10));
            this.HideTerminal();
            this.ChangeLanguage(InitialLanguage, Persist: false);
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || !File.Exists(Marker) || File.ReadAllText(Marker) != FixtureId
                || (File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned terminal fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
