using System.Runtime.InteropServices;
using System.Windows;
using NativeDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace CDir.Services;

internal static class DropDataSnapshot
{
    // HDROP can be delay-rendered: archive managers extract here. Marshal the
    // source to an STA worker while the drop handler continues pumping WPF input.
    internal static Task<IDataObject> ReadAsync(IDataObject Data)
    {
        var Native = Data as NativeDataObject ?? (NativeDataObject)new DataObject(Data);
        var Id = typeof(NativeDataObject).GUID;
        Marshal.ThrowExceptionForHR(CoMarshalInterThreadInterfaceInStream(ref Id, Native, out var Stream));
        var Completion = new TaskCompletionSource<IDataObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var Worker = new Thread(() =>
        {
            NativeDataObject? Source = null;
            try
            {
                Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(Stream, ref Id, out Source));
                var Input = new DataObject(Source);
                var Snapshot = new DataObject();
                if (Input.GetDataPresent(DataFormats.FileDrop, true) && Input.GetData(DataFormats.FileDrop, true) is string[] Paths)
                    Snapshot.SetData(DataFormats.FileDrop, Paths.ToArray());
                if (ShellDropData.Read(Input) is { } Ids)
                    Snapshot.SetData(ShellDropData.Format, new System.IO.MemoryStream(ShellDropData.Create(Ids)));
                Completion.TrySetResult(Snapshot);
            }
            catch (Exception Error) { Completion.TrySetException(Error); }
            finally { if (Source is not null && Marshal.IsComObject(Source)) Marshal.ReleaseComObject(Source); }
        }) { IsBackground = true, Name = "CDir incoming drop data" };
        Worker.SetApartmentState(ApartmentState.STA);
        try { Worker.Start(); }
        catch { Marshal.Release(Stream); throw; }
        return Completion.Task;
    }

    [DllImport("ole32.dll")] private static extern int CoMarshalInterThreadInterfaceInStream(ref Guid Id, [MarshalAs(UnmanagedType.Interface)] NativeDataObject Data, out IntPtr Stream);
    [DllImport("ole32.dll")] private static extern int CoGetInterfaceAndReleaseStream(IntPtr Stream, ref Guid Id, [MarshalAs(UnmanagedType.Interface)] out NativeDataObject Data);
}
