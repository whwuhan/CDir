using CDir.UI.Dialogs;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RespondSmokeCategoryDeleteDialogAsync(Func<Task> Action, string ExpectedName, bool Confirm = true,
        bool CloseWindow = false, System.Action<CategoryDeleteDialog>? Inspect = null, bool Escape = false)
    {
        Exception? Failure = null;
        var Responded = false;
        CategoryDeleteDialog? FindDialog() => Application.Current.Windows.OfType<CategoryDeleteDialog>().SingleOrDefault(Dialog => ReferenceEquals(Dialog.Owner, this.HostWindow));
        var Timeout = new DispatcherTimer(DispatcherPriority.Send, Dispatcher) { Interval = TimeSpan.FromSeconds(8) };
        Timeout.Tick += (_, _) =>
        {
            Timeout.Stop();
            Failure = new TimeoutException("Timed out waiting for the category deletion confirmation.");
            if (FindDialog() is { } Dialog) Dialog.DialogResult = false;
        };
        var Pending = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new System.Action(() =>
        {
            CategoryDeleteDialog? Dialog = null;
            try
            {
                Dialog = FindDialog() ?? throw new InvalidOperationException("Category deletion did not show a confirmation dialog.");
                Dialog.UpdateLayout();
                CheckSquareWindowCorners(Dialog);
                if (!Dialog.IsVisible || Dialog.Title != LanguageService.Text("DeleteCategory")
                    || Dialog.Question.Text != LanguageService.Text("DeleteCategoryQuestion", ExpectedName)
                    || Dialog.ConfirmButton.Content?.ToString() != LanguageService.Text("CommonConfirm")
                    || Dialog.CancelButton.Content?.ToString() != LanguageService.Text("CommonCancel")
                    || !Dialog.CancelButton.IsCancel || !Dialog.CancelButton.IsDefault || Dialog.ConfirmButton.IsDefault
                    || !ReferenceEquals(Dialog.CancelButton.Style, Dialog.FindResource("PrimaryButton"))
                    || ReferenceEquals(Dialog.ConfirmButton.Style, Dialog.FindResource("PrimaryButton")))
                    throw new InvalidOperationException("Category confirmation does not show the expected name localized actions and cancellation default.");
                Inspect?.Invoke(Dialog);
                if (AppMain.ScreenshotPath is { } Screenshot && ExpectedName == "Renamed 工作" && !Confirm && !CloseWindow)
                {
                    var Bitmap = new RenderTargetBitmap((int)Dialog.ActualWidth, (int)Dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    Bitmap.Render(Dialog);
                    var Encoder = new PngBitmapEncoder();
                    Encoder.Frames.Add(BitmapFrame.Create(Bitmap));
                    using var Stream = File.Create(Path.Combine(Path.GetDirectoryName(Screenshot)!, "DeleteCategory-" + LanguageService.CurrentLanguage + ".png"));
                    Encoder.Save(Stream);
                }
                if (Escape) CloseSmokeDialogWithEscape(Dialog, Dialog.ConfirmButton);
                else if (CloseWindow) Dialog.Close();
                else (Confirm ? Dialog.ConfirmButton : Dialog.CancelButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Responded = true;
            }
            catch (Exception Error)
            {
                Failure = Error;
                if (Dialog is { IsVisible: true }) Dialog.DialogResult = false;
            }
            finally { Timeout.Stop(); }
        }));
        Timeout.Start();
        try { await Action(); }
        finally
        {
            Timeout.Stop();
            if (Pending.Status == DispatcherOperationStatus.Pending) Pending.Abort();
        }
        if (Failure is not null) throw new InvalidOperationException("Could not verify the real category confirmation dialog.", Failure);
        if (!Responded) throw new InvalidOperationException("Category deletion returned without showing its confirmation dialog.");
    }
}
