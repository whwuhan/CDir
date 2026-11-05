using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunSidebarVisibilitySmokeChecksAsync(List<string> Passed)
    {
        var InitialWidth = this.SidebarColumn.ActualWidth;
        var InitialPath = this.CurrentPath;
        var InitialFavorites = this.FavoritesSection.IsExpanded;
        var InitialTree = this.DirectoryTreeSection.IsExpanded;
        var InitialLanguage = LanguageService.CurrentLanguage;
        var InitialWindowWidth = this.HostWindow.Width;
        var SettingsFile = System.IO.Path.Combine(AppMain.SmokeRoot!, "SidebarVisibilitySettings.json");
        var Invoker = (IInvokeProvider)new ButtonAutomationPeer(this.SidebarToggleButton).GetPattern(PatternInterface.Invoke);
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Sidebar visibility UI smoke test failed: " + Name);
            Passed.Add(Name);
        }
        async Task Toggle()
        {
            Invoker.Invoke();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        void Capture(string Name)
        {
            if (AppMain.ScreenshotPath is null) return;
            var Bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            Bitmap.Render(this);
            var Encoder = new PngBitmapEncoder();
            Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
            using var Stream = System.IO.File.Create(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AppMain.ScreenshotPath)!, Name + ".png"));
            Encoder.Save(Stream);
        }
        try
        {
            Check(this.SidebarToggleButton.IsVisible && this.SidebarToggleButton.Focusable
                && this.SidebarToggleButton.TranslatePoint(new Point(), this.ContentPanels).X >= InitialWidth,
                "Directory tree toggle is keyboard accessible in the sidebar divider");
            await Toggle();
            Check(!this.Settings.ShowSidebar && !this.SidebarPanel.IsVisible && !this.SidebarSplitter.IsVisible
                && this.SidebarColumn.ActualWidth < 0.1 && this.SidebarToggleButton.IsVisible,
                "Toggle hides the entire sidebar and retains the restore button");
            Check(Math.Abs(this.FileGrid.TranslatePoint(new Point(), this.ContentPanels).X - this.ContentPanels.ColumnDefinitions[1].ActualWidth) < 0.1
                && Math.Abs(this.MainPanelColumn.ActualWidth + this.ContentPanels.ColumnDefinitions[1].ActualWidth - this.ContentPanels.ActualWidth) < 0.1,
                "Hidden directory tree releases all sidebar space to the file panel");
            Check(this.CurrentPath == InitialPath && this.FavoritesSection.IsExpanded == InitialFavorites
                && this.DirectoryTreeSection.IsExpanded == InitialTree,
                "Hiding the sidebar preserves navigation and section expansion");
            Check(this.SaveSettings(SettingsFile) && !ReadSettings(SettingsFile).ShowSidebar,
                "Hidden sidebar preference survives settings serialization");
            Capture("SidebarHidden");
            foreach (var Language in LanguageService.SupportedLanguages)
            {
                this.ChangeLanguage(Language.Code, Persist: false);
                Check(this.SidebarToggleButton.ToolTip?.ToString() == LanguageService.Text("ShowSidebarTooltip")
                    && AutomationProperties.GetName(this.SidebarToggleButton) == LanguageService.Text("ShowSidebarTooltip"),
                    Language.Code + " hidden sidebar restore action updates its tooltip and accessible name");
            }
            this.ChangeLanguage(InitialLanguage, Persist: false);
            await Toggle();
            Check(this.Settings.ShowSidebar && this.SidebarPanel.IsVisible && this.SidebarSplitter.IsVisible
                && Math.Abs(this.SidebarColumn.ActualWidth - InitialWidth) < 0.1,
                "Restore button reveals all sidebar contents at the previous dragged width");
            Check(this.SidebarToggleButton.ToolTip?.ToString() == LanguageService.Text("HideSidebarTooltip")
                && this.SaveSettings(SettingsFile) && ReadSettings(SettingsFile).ShowSidebar,
                "Visible sidebar action and saved preference return to the shown state");
            Capture("SidebarVisible");
            await Toggle();
            this.HostWindow.Width = this.HostWindow.MinWidth;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Toggle();
            Check(this.SidebarColumn.ActualWidth >= 190 && this.MainPanelColumn.ActualWidth >= 9.9,
                "Restoring after a hidden window resize respects both panel minimum widths");
        }
        finally
        {
            this.HostWindow.Width = InitialWindowWidth;
            this.ChangeLanguage(InitialLanguage, Persist: false);
            this.Settings.ShowSidebar = true;
            this.ExpandedSidebarWidth = new GridLength(InitialWidth);
            this.ApplySidebarVisibility();
            this.UpdateLayout();
            if (System.IO.File.Exists(SettingsFile)) System.IO.File.Delete(SettingsFile);
        }
    }
}
