using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace CDir.UI;

public partial class MainWindow
{
    internal void SetTabPinned(ExplorerTab Page, bool Pinned)
    {
        if (!this.Tabs.Contains(Page) || Page.IsTabPinned == Pinned) return;
        // Saved pane memberships use indices into the saved tab order. Restore
        // pin flags without moving those indices while tabs are being loaded.
        if (this.RestoringWorkspace) { Page.SetTabPinned(Pinned); this.RefreshTabHeaders(); return; }
        var Destination = this.PaneTabs(this.PaneFor(Page)).Count(Tab => Tab.IsTabPinned && !ReferenceEquals(Tab, Page));
        this.MoveTab(Page, Pinned, Destination);
    }

    private bool MoveTab(ExplorerTab Page, bool Pinned, int Destination)
    {
        var Pane = this.PaneFor(Page);
        var Local = this.PaneTabs(Pane).ToList();
        var OldIndex = Local.IndexOf(Page);
        if (OldIndex < 0 || Destination < 0 || Destination >= Local.Count) return false;
        if (Page.IsTabPinned == Pinned && OldIndex == Destination) return false;
        if (Page.IsTabPinned != Pinned) Page.SetTabPinned(Pinned);
        Local.Remove(Page); Local.Insert(Destination, Page);
        var Slots = this.Tabs.Select((Tab, Index) => (Tab, Index)).Where(Item => ReferenceEquals(Item.Tab.Parent, Pane.TabPages)).Select(Item => Item.Index).ToArray();
        for (var Index = 0; Index < Slots.Length; Index++)
        {
            this.Tabs.Move(this.Tabs.IndexOf(Local[Index]), Slots[Index]);
            this.Headers.Move(this.Headers.IndexOf(this.Headers.Single(Header => ReferenceEquals(Header.Page, Local[Index]))), Slots[Index]);
        }
        this.RefreshTabHeaders();
        this.PersistWorkspaceNow();
        var Control = this.SeparatePinnedTabs && Pinned ? Pane.PinnedTabHeaders : Pane.TabHeaders;
        Control.UpdateLayout();
        (Control.ItemContainerGenerator.ContainerFromItem(this.Headers.Single(Header => ReferenceEquals(Header.Page, Page))) as FrameworkElement)?.BringIntoView();
        return true;
    }

    internal void UpdatePinnedTabLayout(bool Separate)
    {
        this.SeparatePinnedTabs = Separate;
        this.RefreshTabHeaders();
    }

    private void RefreshTabHeaders()
    {
        foreach (var Pane in this.Panes)
        {
            var Local = this.Headers.Where(Header => ReferenceEquals(Header.Page.Parent, Pane.TabPages)).ToArray();
            Synchronize(Pane.VisibleHeaders, Local.Where(Header => !this.SeparatePinnedTabs || !Header.Page.IsTabPinned).ToArray());
            Synchronize(Pane.PinnedHeaders, Local.Where(Header => this.SeparatePinnedTabs && Header.Page.IsTabPinned).ToArray());
            Pane.PinnedTabScroll.Visibility = Pane.PinnedHeaders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Pane.PinnedTabRow.Visibility = Pane.PinnedTabScroll.Visibility;
        }

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
