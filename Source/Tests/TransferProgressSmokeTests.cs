using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CDir.Localization;
using CDir.Services;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunTransferProgressSmokeChecksAsync(List<string> Passed, string Fixture)
    {
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Windows transfer check failed: " + Name);
            Passed.Add(Name);
        }
        var Source = Directory.CreateDirectory(Path.Combine(Fixture, "ProgressSource")).FullName;
        var Target = Directory.CreateDirectory(Path.Combine(Fixture, "ProgressTarget")).FullName;
        var Large = Path.Combine(Source, "Large.bin");
        using (var FileStream = File.Create(Large)) FileStream.SetLength(64L * 1024 * 1024);
        var OriginalTransfer = this.TransferShellItems;
        var OriginalHistory = this.Files.OperationHistory;
        var OriginalPath = this.CurrentPath;
        var History = new FileOperationHistory(Path.Combine(Fixture, "NativeAudit"));
        try
        {
            this.Files.OperationHistory = History;
            var Transfer = this.TransferFilesAsync([Large], Target, Move: false);
            Check(this.TransferProgressPanel.Visibility == Visibility.Collapsed && this.TransferProgressText.Text.Length == 0,
                "Windows owns preparation and file-count progress without a competing CDir progress panel");
            Check(await Transfer && new FileInfo(Path.Combine(Target, "Large.bin")).Length == 64L * 1024 * 1024
                && !Directory.EnumerateDirectories(History.JournalRoot, "Data", SearchOption.AllDirectories).Any(),
                "A native 64 MiB copy finishes without recursively hashing or backing up its source");
            foreach (var Result in new[] { unchecked((int)0x80004004), unchecked((int)0x800704C7), unchecked((int)0x80270000) })
            {
                var Cancelled = false;
                try { ShellContextMenu.CompleteShellOperation(Result, false); }
                catch (OperationCanceledException) { Cancelled = true; }
                Check(Cancelled, "Windows cancellation HRESULT is normalized to cancellation: " + Result.ToString("X8"));
            }
            await this.NavigateAsync(Target, RecordHistory: false);
            this.TransferShellItems = _ => throw new COMException("Native cancellation", unchecked((int)0x800704C7));
            Check(!await this.TransferFilesAsync([Large], Target, Move: false)
                && !this.OperationBusy && this.LoadingBar.Visibility == Visibility.Collapsed
                && this.TransferProgressPanel.Visibility == Visibility.Collapsed && this.TransferProgressText.Text.Length == 0
                && this.StatusText.Text == LanguageService.Text("OperationCancelled"),
                "Cancelling the Windows dialog immediately clears progress and replaces the stale copying message");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(this.TransferProgressPanel.Visibility == Visibility.Collapsed && this.TransferProgressText.Text.Length == 0,
                "No delayed progress callback resurrects the cancelled operation");
            var Partial = Path.Combine(Source, "Partial.txt");
            File.WriteAllText(Partial, "A completed item before cancellation");
            this.TransferShellItems = Request =>
            {
                File.Copy(Partial, Path.Combine(Request.Destination, "Partial.txt"));
                throw new OperationCanceledException();
            };
            Check(!await this.TransferFilesAsync([Partial], Target, Move: false)
                && this.Entries.Any(Entry => Entry.Name == "Partial.txt") && !this.OperationBusy,
                "A cancelled native batch still refreshes files completed before cancellation");
            this.TransferShellItems = OriginalTransfer;
            using var Cancellation = new CancellationTokenSource();
            Cancellation.Cancel();
            this.TransferCancellation = Cancellation.Token;
            Check(!await this.TransferFilesAsync([Large], Target, Move: false)
                && !this.OperationBusy && this.TransferProgressPanel.Visibility == Visibility.Collapsed
                && this.StatusText.Text == LanguageService.Text("OperationCancelled"),
                "Native cancellation tokens release the same UI busy and progress state");
            this.TransferCancellation = default;
            Check(!await this.TransferFilesAsync([Path.Combine(Source, "Missing.bin")], Target, Move: false)
                && !this.OperationBusy && this.TransferProgressPanel.Visibility == Visibility.Collapsed,
                "Native failures also clear progress and leave the panel usable");
        }
        finally
        {
            this.TransferShellItems = OriginalTransfer;
            this.TransferCancellation = default;
            this.Files.OperationHistory = OriginalHistory;
            await this.NavigateAsync(OriginalPath, RecordHistory: false);
        }
    }
}
