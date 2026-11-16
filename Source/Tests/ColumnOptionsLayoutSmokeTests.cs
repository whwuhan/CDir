using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Models;
using CDir.UI;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunColumnOptionsLayoutSmokeAsync(List<string> Passed)
    {
        void Check(bool Value, string Message)
        {
            if (!Value) throw new InvalidOperationException("Column options: " + Message);
            Passed.Add(Message);
        }
        await this.RunPreferencesSmokeChecksAsync(Passed, InspectShortcuts: async Dialog =>
        {
            Dialog.Categories.SelectedItem = Dialog.UICategory;
            var OriginalWidth = Dialog.Width;
            var OriginalFont = Dialog.FontSize;
            async Task Settle()
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Dialog.UpdateLayout();
            }
            int ColumnsInFirstRow()
            {
                var FirstY = Dialog.ColumnChecks.Values.First().TranslatePoint(new Point(), Dialog.ColumnChoices).Y;
                return Dialog.ColumnChecks.Values.Count(Choice => Math.Abs(Choice.TranslatePoint(new Point(), Dialog.ColumnChoices).Y - FirstY) < .5);
            }
            void Capture(string Name, Visual Visual, double Width, double Height)
            {
                if (AppMain.ScreenshotPath is not { } Output) return;
                var Bitmap = new RenderTargetBitmap((int)Math.Ceiling(Width), (int)Math.Ceiling(Height), 96, 96, PixelFormats.Pbgra32);
                Bitmap.Render(Visual);
                var Encoder = new PngBitmapEncoder(); Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Output)!, Name + ".png")); Encoder.Save(Stream);
            }
            try
            {
                Dialog.FontSize = 13;
                Dialog.Width = 1280;
                await Settle();
                var WideColumns = ColumnsInFirstRow();
                var Keys = Dialog.ColumnChoices.Children.OfType<CheckBox>().Select(Choice => (string)Choice.Tag).ToArray();
                Check(WideColumns >= 3 && Keys.SequenceEqual(this.GetColumnPreferences().Select(Option => Option.Key)),
                    "A wide Preferences window shows at least three choices per row in the existing reading and keyboard order");
                Dialog.ColumnChecks.Values.First().BringIntoView();
                await Settle();
                Capture("PreferencesColumnsWide", Dialog, Dialog.ActualWidth, Dialog.ActualHeight);
                Dialog.Width = 800;
                await Settle();
                Check(ColumnsInFirstRow() < WideColumns && Dialog.ColumnChecks.Values.All(Choice =>
                {
                    var Point = Choice.TranslatePoint(new Point(), Dialog.ColumnChoices);
                    return Point.X >= -.5 && Point.X + Choice.ActualWidth <= Dialog.ColumnChoices.ActualWidth + .5;
                }), "Narrowing Preferences automatically reduces columns without horizontal clipping");
                Capture("PreferencesColumnsNarrow", Dialog, Dialog.ActualWidth, Dialog.ActualHeight);
                Dialog.FontSize = 24;
                await Settle();
                Check(ColumnsInFirstRow() == 1 && Dialog.ColumnChecks.Values.All(Choice => Choice.FontSize == 24
                    && FindVisualChild<TextBlock>(Choice) is { TextWrapping: TextWrapping.Wrap }),
                    "Large configured text keeps its size and wraps into a single column when needed");
                Capture("PreferencesColumnsLargeFont", Dialog, Dialog.ActualWidth, Dialog.ActualHeight);
                Dialog.FontSize = 13;
                Dialog.Width = 1280;
                await Settle();
                var Recycle = Dialog.ColumnChecks[nameof(FileEntry.OriginalLocation)];
                Recycle.BringIntoView();
                await Settle();
                Check(Recycle.ToolTip?.ToString() == LanguageService.Text("OriginalLocationColumnHint")
                    && Dialog.ColumnChecks[nameof(FileEntry.DateDeleted)].ToolTip?.ToString() == LanguageService.Text("DateDeletedColumnHint")
                    && Dialog.ColumnChecks["System.DateCreated"].ToolTip?.ToString() == LanguageService.Text("DateCreatedColumnHint"),
                    "The three illustrated choices distinguish Recycle Bin-only properties from ordinary creation time");
                var Tip = new ToolTip { Content = Recycle.ToolTip, PlacementTarget = Recycle, StaysOpen = true,
                    MaxWidth = 360, ContentTemplate = Recycle.ContentTemplate };
                try
                {
                    Tip.IsOpen = true;
                    await Settle();
                    Check(Tip.IsVisible && Tip.ActualWidth > 0 && Tip.ActualHeight > 0,
                        "The applicability text opens as a real tooltip without moving the desktop pointer");
                    Capture("PreferencesColumnTooltip", Tip, Tip.ActualWidth, Tip.ActualHeight);
                    Capture("PreferencesColumnsSpecial", Dialog, Dialog.ActualWidth, Dialog.ActualHeight);
                }
                finally { Tip.IsOpen = false; }
            }
            finally { Dialog.Width = OriginalWidth; Dialog.FontSize = OriginalFont; await Settle(); }
        });
    }
}
