using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunAddressDropSmokeChecksAsync(List<string> Passed)
    {
        var InitialPath = this.CurrentPath;
        var InitialCopy = this.Settings.CopyOnDrop;
        var Fixture = Path.Combine(AppMain.SmokeRoot!, "AddressDrop-" + Guid.NewGuid().ToString("N"));
        var Target = Directory.CreateDirectory(Path.Combine(Fixture, "目标 With Spaces")).FullName;
        var Child = Directory.CreateDirectory(Path.Combine(Target, "Current Child")).FullName;
        var Source = Directory.CreateDirectory(Path.Combine(Fixture, "Source")).FullName;
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Address drop UI check failed: " + Name);
            Passed.Add(Name);
        }
        Button SegmentButton(string FullPath)
        {
            this.AddressBreadcrumbs.UpdateLayout();
            var Segment = this.AddressBreadcrumbs.Items.Cast<AddressSegment>().Single(Item => SamePath(Item.FullPath, FullPath));
            var Container = (DependencyObject)this.AddressBreadcrumbs.ItemContainerGenerator.ContainerFromItem(Segment);
            return FindVisualChild<Button>(Container)!;
        }
        DragEventArgs Route(UIElement Element, RoutedEvent Event, IDataObject Data, DragDropEffects Allowed = DragDropEffects.Copy | DragDropEffects.Move)
        {
            var Input = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [Data, DragDropKeyStates.None, Allowed, Element, new Point()], culture: null)!;
            Input.RoutedEvent = Event;
            Element.RaiseEvent(Input);
            return Input;
        }
        static DataObject FileData(params string[] Paths)
        {
            var Data = new DataObject();
            Data.SetData(DataFormats.FileDrop, Paths);
            return Data;
        }
        try
        {
            await this.NavigateAsync(Child, RecordHistory: false);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
            var History = this.History.ToArray();
            foreach (var Copy in new[] { false, true })
            {
                this.ChangeCopyOnDrop(Copy);
                var Name = Copy ? "Copy" : "Move";
                var File = Path.Combine(Source, Name + " 中文.txt");
                var Folder = Directory.CreateDirectory(Path.Combine(Source, Name + " Folder", "Nested")).Parent!.FullName;
                System.IO.File.WriteAllText(File, Name + " file contents");
                System.IO.File.WriteAllText(Path.Combine(Folder, "Nested", "Child.txt"), Name + " folder contents");
                var Data = FileData(File, Folder);
                var Button = SegmentButton(Target);
                var Effect = Copy ? DragDropEffects.Copy : DragDropEffects.Move;
                var Enter = Route(Button, DragDrop.PreviewDragEnterEvent, Data);
                Check(Enter.Handled && Enter.Effects == Effect && this.AddressDropTargetHighlight.IsVisible
                    && this.FileDropTargetHighlight.Visibility == Visibility.Collapsed && this.FileDropHint.IsVisible
                    && this.FileDropHint.Text.Contains(Target)
                    && this.AddressDropTargetHighlight.Width == Button.ActualWidth,
                    "Address ancestor drag entry highlights the exact directory and previews " + Name);
                this.CaptureAddressBreadcrumbPreview("AddressDrop" + Name + "Preview.png");
                var Over = Route(Button, DragDrop.PreviewDragOverEvent, Data);
                Check(Over.Handled && Over.Effects == Effect, "Address ancestor drag-over retains " + Name + " mode");
                var Drop = Route(Button, DragDrop.PreviewDropEvent, Data);
                Check(Drop.Handled && Drop.Effects == Effect && !this.OperationBusy
                    && System.IO.File.Exists(File) == Copy && Directory.Exists(Folder) == Copy
                    && System.IO.File.ReadAllText(Path.Combine(Target, Path.GetFileName(File))) == Name + " file contents"
                    && System.IO.File.ReadAllText(Path.Combine(Target, Path.GetFileName(Folder), "Nested", "Child.txt")) == Name + " folder contents"
                    && SamePath(this.CurrentPath, Child) && this.History.SequenceEqual(History)
                    && this.AddressDropTargetHighlight.Visibility == Visibility.Collapsed && this.FileDropHint.Visibility == Visibility.Collapsed,
                    "The routed address drop completes a mixed file and folder " + Name + " without navigating");
            }
            var Remaining = Path.Combine(Source, "Copy 中文.txt");
            var RemainingData = FileData(Remaining);
            var Current = SegmentButton(Child);
            Check(Route(Current, DragDrop.PreviewDragOverEvent, RemainingData).Effects == DragDropEffects.Copy
                && this.FileTransferTarget(Current, false)?.Path == Child,
                "The final address segment accepts drops into the current directory");
            Check(Route(SegmentButton(Target), DragDrop.PreviewDragOverEvent, RemainingData, DragDropEffects.Move).Effects == DragDropEffects.None
                && this.AddressDropTargetHighlight.Visibility == Visibility.Collapsed && this.FileDropHint.Visibility == Visibility.Collapsed,
                "Address drops reject a source that does not allow the configured copy mode and clear feedback");
            Check(Route(this.AddressDisplay, DragDrop.PreviewDropEvent, RemainingData).Effects == DragDropEffects.None
                && System.IO.File.Exists(Remaining) && !System.IO.File.Exists(Path.Combine(Child, Path.GetFileName(Remaining))),
                "Blank address space does not silently transfer files into the current directory");
            Check(Route(Current, DragDrop.PreviewDropEvent, FileData(Child)).Effects == DragDropEffects.None
                && Route(Current, DragDrop.PreviewDropEvent, FileData(Target)).Effects == DragDropEffects.None
                && Route(Current, DragDrop.PreviewDropEvent, FileData(Path.Combine(Child, "Already Here.txt"))).Effects == DragDropEffects.None
                && Route(Current, DragDrop.PreviewDropEvent, new DataObject(DataFormats.Text, Remaining)).Effects == DragDropEffects.None,
                "Address drops reject self targets, recursive targets, same-directory items and text data");
            Check(Route(Current, DragDrop.PreviewDropEvent, RemainingData).Effects == DragDropEffects.Copy
                && System.IO.File.Exists(Remaining)
                && System.IO.File.ReadAllText(Path.Combine(Child, Path.GetFileName(Remaining))) == "Copy file contents",
                "Dropping onto the final address segment copies into the current directory");
            this.OperationBusy = true;
            // The completed copy refreshed and regenerated the breadcrumb buttons.
            Current = SegmentButton(Child);
            var BusyDrop = Route(Current, DragDrop.PreviewDropEvent, RemainingData);
            Check(BusyDrop.Handled && BusyDrop.Effects == DragDropEffects.None,
                "A busy application rejects a second operation through the address bar");
            this.OperationBusy = false;
        }
        finally
        {
            this.OperationBusy = false;
            this.ClearFileTransferFeedback();
            this.ChangeCopyOnDrop(InitialCopy);
            await this.NavigateAsync(InitialPath, RecordHistory: false);
            if (!AppMain.IsInsideSmokeScope(Fixture) || (System.IO.File.GetAttributes(Fixture) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the owned address-drop fixture.");
            Directory.Delete(Fixture, true);
        }
    }
}
