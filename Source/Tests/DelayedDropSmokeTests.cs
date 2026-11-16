using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CDir.Models;

namespace CDir.UI;

public partial class ExplorerTab
{
    private sealed class DelayedArchiveData(string[] Paths, int UiThread) : IDataObject
    {
        internal readonly ManualResetEventSlim Started = new();
        internal readonly ManualResetEventSlim Release = new();
        internal int Reads;
        internal ApartmentState ReaderApartment;
        internal bool Fail;
        public object GetData(string Format, bool AutoConvert)
        {
            if (!this.GetDataPresent(Format, AutoConvert)) return null!;
            Interlocked.Increment(ref this.Reads);
            if (Environment.CurrentManagedThreadId == UiThread) throw new InvalidOperationException("Delayed archive contents were requested on the UI thread.");
            this.ReaderApartment = Thread.CurrentThread.GetApartmentState();
            this.Started.Set();
            if (!this.Release.Wait(TimeSpan.FromSeconds(12))) throw new TimeoutException("Delayed archive test was not released by the UI.");
            if (this.Fail) throw new COMException("Owned archive extraction cancelled.", unchecked((int)0x80004004));
            return Paths;
        }
        public object GetData(string Format) => this.GetData(Format, true);
        public object GetData(Type Format) => this.GetData(Format.FullName!);
        public bool GetDataPresent(string Format, bool AutoConvert) => Format == DataFormats.FileDrop;
        public bool GetDataPresent(string Format) => this.GetDataPresent(Format, true);
        public bool GetDataPresent(Type Format) => this.GetDataPresent(Format.FullName!);
        public string[] GetFormats(bool AutoConvert) => [DataFormats.FileDrop];
        public string[] GetFormats() => this.GetFormats(true);
        public void SetData(string Format, object Data, bool AutoConvert) => throw new NotSupportedException();
        public void SetData(string Format, object Data) => throw new NotSupportedException();
        public void SetData(Type Format, object Data) => throw new NotSupportedException();
        public void SetData(object Data) => throw new NotSupportedException();
    }

    private async Task RunDelayedDropSmokeAsync(List<string> Passed, string Root)
    {
        var Initial = this.CurrentPath;
        var Mode = this.Settings.FileView;
        var Target = Directory.CreateDirectory(Path.Combine(Root, "Delayed archive target")).FullName;
        var Other = Directory.CreateDirectory(Path.Combine(Root, "Browse while extracting")).FullName;
        var Source = Path.Combine(Root, "Delayed archive file.txt");
        File.WriteAllText(Source, "Owned delayed archive contents");
        var Data = new DelayedArchiveData([Source], Environment.CurrentManagedThreadId);
        var OleData = new DataObject(Data);
        void Check(bool Value, string Message)
        {
            if (!Value) throw new InvalidOperationException("Delayed archive drop: " + Message);
            Passed.Add(Message);
        }
        DragEventArgs Route(IDataObject Input, RoutedEvent Event)
        {
            var Args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [Input, DragDropKeyStates.None, DragDropEffects.Copy | DragDropEffects.Move, this.FileGrid, new Point(8, 8)], null)!;
            Args.RoutedEvent = Event;
            this.FileGrid.RaiseEvent(Args);
            return Args;
        }
        var Timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
        var Ticks = 0;
        var Navigated = false;
        var RejectedNested = false;
        Exception? UiFailure = null;
        Timer.Tick += async (_, _) =>
        {
            Ticks++;
            if (!Data.Started.IsSet || Ticks < 4) return;
            Timer.Stop();
            try
            {
                this.BeginAddressEdit();
                Check(this.AddressEditing, "The address editor responds while archive contents are still being rendered");
                this.CancelAddressEdit();
                RejectedNested = Route(new DataObject(DataFormats.FileDrop, new[] { Source }), DragDrop.PreviewDropEvent).Effects == DragDropEffects.None;
                Navigated = await this.NavigateAsync(Other);
            }
            catch (Exception Error) { UiFailure = Error; }
            finally { Data.Release.Set(); }
        };
        try
        {
            await this.NavigateAsync(Target);
            this.ChangeCopyOnDrop(true);
            var Hover = Route(OleData, DragDrop.PreviewDragOverEvent);
            Check(Hover.Effects == DragDropEffects.Copy && Data.Reads == 0,
                "Hovering an archive drop checks formats without starting extraction or reading HDROP");
            Timer.Start();
            var Drop = Route(OleData, DragDrop.PreviewDropEvent);
            if (UiFailure is not null) throw UiFailure;
            Check(Ticks >= 4 && Navigated && SamePath(this.CurrentPath, Other) && RejectedNested
                && Data.ReaderApartment == ApartmentState.STA && Data.Reads > 0,
                "A delayed OLE provider runs on an STA worker while navigation remains responsive and nested drops are rejected");
            Check(Drop.Effects == DragDropEffects.Copy && File.Exists(Source)
                && File.ReadAllText(Path.Combine(Target, Path.GetFileName(Source))) == "Owned delayed archive contents"
                && !this.ReceivingFileDrop && !this.OperationBusy,
                "The drop retains its original destination and source lifetime until extraction and transfer finish");
            Data.Fail = true;
            var Cancelled = Route(OleData, DragDrop.PreviewDropEvent);
            Check(Cancelled.Effects == DragDropEffects.None && !this.ReceivingFileDrop && !this.OperationBusy,
                "Cancelled archive data leaves the browser ready for another operation");
        }
        finally
        {
            Timer.Stop(); Data.Release.Set();
            await this.NavigateAsync(Initial); this.ChangeFileView(Mode);
            Data.Started.Dispose(); Data.Release.Dispose();
        }
    }
}
