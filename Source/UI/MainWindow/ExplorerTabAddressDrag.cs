using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private SidebarFolderAddSource? AddressFolderDragCandidate;
    private Point AddressFolderDragOrigin;
    private bool AddressFolderDragActive;

    private bool PrepareAddressFolderDrag(DependencyObject? Source, int ClickCount, Point Origin)
    {
        this.AddressFolderDragCandidate = null;
        if (this.WindowClosed || this.AddressEditing || this.IsRecycleBin || this.OperationBusy || this.Loading || ClickCount != 1)
            return false;
        FrameworkElement? Element = null;
        string? FolderPath = null;
        if (ReferenceEquals(Source, this.AddressIcon) || Source is not null && this.AddressIcon.IsAncestorOf(Source))
        {
            Element = this.AddressIcon;
            FolderPath = this.CurrentPath;
        }
        else if (FindAncestor<Button>(Source) is { Tag: string Path } Button && this.AddressBreadcrumbs.IsAncestorOf(Button))
        {
            Element = Button;
            FolderPath = Path;
        }
        if (Element is null || string.IsNullOrWhiteSpace(FolderPath)) return false;
        this.AddressFolderDragOrigin = Origin;
        this.AddressFolderDragCandidate = new(new FileEntry { FullPath = FolderPath, IsDirectory = true }, Element);
        return true;
    }

    private void AddressFolderDragMouseMove(object Sender, MouseEventArgs E)
    {
        if (E.LeftButton != MouseButtonState.Pressed) { this.AddressFolderDragCandidate = null; return; }
        var Position = E.GetPosition(this.AddressBar);
        if (this.AddressFolderDragCandidate is null || this.AddressFolderDragActive
            || Math.Abs(Position.X - this.AddressFolderDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(Position.Y - this.AddressFolderDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        E.Handled = true;
        this.StartAddressFolderDrag();
    }

    private void StartAddressFolderDrag()
    {
        var Source = this.AddressFolderDragCandidate;
        this.AddressFolderDragCandidate = null;
        if (Source is null || this.WindowClosed || this.AddressEditing || this.IsRecycleBin || this.OperationBusy || this.Loading) return;
        this.CancelAddressNavigation();
        this.AddressFolderDragActive = true;
        try { this.StartSidebarFolderAddDrag(Source); }
        finally
        {
            this.AddressFolderDragActive = false;
            this.CancelAddressNavigation();
        }
    }

    private void AddressFolderDragMouseUp(object Sender, MouseButtonEventArgs E)
    {
        var Source = this.AddressFolderDragCandidate;
        this.AddressFolderDragCandidate = null;
        // Retain the icon's ordinary click-to-edit behavior when no drag started.
        if (Source?.Element == this.AddressIcon && !this.AddressFolderDragActive)
        {
            E.Handled = true;
            this.BeginAddressEdit();
        }
    }
}
