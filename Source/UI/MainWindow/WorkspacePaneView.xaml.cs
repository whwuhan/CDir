using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CDir.UI;

public partial class WorkspacePaneView : UserControl
{
    public ObservableCollection<MainWindow.TabHeader> VisibleHeaders { get; } = [];
    public ObservableCollection<MainWindow.TabHeader> PinnedHeaders { get; } = [];
    internal MainWindow Host = null!;
    internal ExplorerTab? Selected;
    internal ExplorerTab? Presented;

    public WorkspacePaneView() => InitializeComponent();

    private void TabBarPreviewMouseDown(object Sender, MouseButtonEventArgs E)
    { this.Host.ActivatePane(this); this.Host.TabBarPreviewMouseDown(Sender, E); }
    private void NewTabClick(object Sender, RoutedEventArgs E)
    { this.Host.ActivatePane(this); this.Host.AddTab(); }
    private void TabRowDragOver(object Sender, DragEventArgs E)
    { this.Host.ActivatePane(this); this.Host.TabRowDragOver(Sender, E); }
    private void TabRowDrop(object Sender, DragEventArgs E)
    { this.Host.ActivatePane(this); this.Host.TabRowDrop(Sender, E); }
    private void TabRowDragLeave(object Sender, DragEventArgs E) => this.Host.TabRowDragLeave(Sender, E);
}
