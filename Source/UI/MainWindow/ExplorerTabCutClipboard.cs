using System.Windows;
using System.Windows.Threading;
using CDir.Localization;

namespace CDir.UI;

public partial class ExplorerTab
{
    private const string CutClipboardFormat = "CDir.CutSession";
    private sealed record CutClipboardSession(string Id, HashSet<string> Paths, uint Version,
        Func<uint> ReadVersion, Func<IDataObject?> ReadData, Action ClearData);
    private static CutClipboardSession? PendingCutClipboard;
    private static event Action? CutClipboardChanged;
    private static DispatcherTimer? CutClipboardTimer;

    private static void SetCutClipboard(CutClipboardSession? Session)
    {
        PendingCutClipboard = Session;
        if (Session is not null)
        {
            if (CutClipboardTimer is null)
            {
                CutClipboardTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
                CutClipboardTimer.Tick += (_, _) => CheckCutClipboardVersion();
            }
            CutClipboardTimer.Start();
        }
        else CutClipboardTimer?.Stop();
        CutClipboardChanged?.Invoke();
    }

    private static void CheckCutClipboardVersion()
    {
        if (PendingCutClipboard is { } Session && Session.ReadVersion() != Session.Version) SetCutClipboard(null);
    }

    private void RefreshCutFeedback()
    {
        foreach (var Entry in this.Entries) Entry.IsCut = PendingCutClipboard?.Paths.Contains(Entry.FullPath) == true;
    }

    private bool CancelCutClipboard()
    {
        if (PendingCutClipboard is not { } Session) return false;
        try
        {
            // Clear only the clipboard payload we created. A newer clipboard item belongs to its writer.
            if (Session.ReadVersion() == Session.Version && Session.ReadData()?.GetData(CutClipboardFormat) as string == Session.Id)
                Session.ClearData();
            SetCutClipboard(null);
            this.UpdateCounts();
        }
        catch (Exception Error) { this.ShowNotice(LanguageService.Text("ClipboardError", Error.Message), true); }
        return true;
    }
}
