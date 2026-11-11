using System.Windows;
using System.Windows.Input;

namespace CDir.UI.Dialogs;

internal abstract class DialogWindow : Window
{
    protected DialogWindow()
    {
        this.SetResourceReference(FontWeightProperty, "UIFontWeight");
        this.SetResourceReference(BackgroundProperty, "SurfaceMiddle");
        this.Closed += (_, _) =>
        {
            if (this.Owner is CDir.UI.MainWindow Owner)
                Owner.Dispatcher.BeginInvoke(() => Owner.ActiveTab?.FocusMainFilePanel());
        };
    }

    protected override void OnSourceInitialized(EventArgs E)
    {
        base.OnSourceInitialized(E);
        WindowAppearance.UseSquareCorners(this);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs E)
    {
        if (E.Key == Key.Escape)
        {
            E.Handled = true;
            this.Close();
            return;
        }
        base.OnPreviewKeyDown(E);
    }
}
