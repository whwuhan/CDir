using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace CDir.UI;

public partial class MainWindow
{
    internal void SetTabPinned(ExplorerTab Page, bool Pinned)
    {
        if (!this.Tabs.Contains(Page) || Page.IsTabPinned == Pinned) return;
        var Destination = this.Tabs.Count(Tab => Tab.IsTabPinned && !ReferenceEquals(Tab, Page));
        this.MoveTab(Page, Pinned, Destination);
    }

    private bool MoveTab(ExplorerTab Page, bool Pinned, int Destination)
    {
        var OldIndex = this.Tabs.IndexOf(Page);
        if (OldIndex < 0 || Destination < 0 || Destination >= this.Tabs.Count) return false;
        if (Page.IsTabPinned == Pinned && OldIndex == Destination) return false;
        if (Page.IsTabPinned != Pinned) Page.SetTabPinned(Pinned);
        this.Tabs.Move(OldIndex, Destination);
        this.Headers.Move(OldIndex, Destination);
        this.RefreshTabHeaders();
        this.PersistWorkspaceNow();
        var Control = this.SeparatePinnedTabs && Pinned ? this.PinnedTabHeaders : this.TabHeaders;
        Control.UpdateLayout();
        (Control.ItemContainerGenerator.ContainerFromItem(this.Headers[Destination]) as FrameworkElement)?.BringIntoView();
        return true;
    }

    internal void UpdatePinnedTabLayout(bool Separate)
    {
        this.SeparatePinnedTabs = Separate;
        this.RefreshTabHeaders();
    }

    private void RefreshTabHeaders()
    {
        Synchronize(this.VisibleHeaders, this.Headers.Where(Header => !this.SeparatePinnedTabs || !Header.Page.IsTabPinned).ToArray());
        Synchronize(this.PinnedHeaders, this.Headers.Where(Header => this.SeparatePinnedTabs && Header.Page.IsTabPinned).ToArray());
        this.PinnedTabScroll.Visibility = this.PinnedHeaders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        this.PinnedTabRow.Visibility = this.PinnedTabScroll.Visibility;

        static void Synchronize(ObservableCollection<TabHeader> Target, TabHeader[] Desired)
        {
            foreach (var Header in Target.Where(Header => !Desired.Contains(Header)).ToArray()) Target.Remove(Header);
            for (var Index = 0; Index < Desired.Length; Index++)
            {
                var Previous = Target.IndexOf(Desired[Index]);
                if (Previous < 0) Target.Insert(Index, Desired[Index]);
                else if (Previous != Index) Target.Move(Previous, Index);
            }
        }
    }

    private void PinTabClick(object Sender, RoutedEventArgs E)
    {
        E.Handled = true;
        if (Sender is FrameworkElement { DataContext: TabHeader Header })
            this.SetTabPinned(Header.Page, !Header.Page.IsTabPinned);
    }
}
