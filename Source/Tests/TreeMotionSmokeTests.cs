using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using CDir.Controls;

namespace CDir.UI;

public partial class ExplorerTab
{
    private async Task RunTreeMotionSmokeChecksAsync(List<string> Passed)
    {
        var Probe = new AnimatedDisclosure
        {
            IsExpanded = false, Child = new Border { Height = 180, Width = 200 },
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        AnimatedDisclosure.SetAnimationsEnabled(Probe, true);
        var Panel = (Grid)this.Content;
        Grid.SetRowSpan(Probe, 4);
        Panel.Children.Add(Probe);
        void Check(bool Condition, string Name)
        {
            if (!Condition) throw new InvalidOperationException("Tree motion UI check failed: " + Name);
            Passed.Add(Name);
        }
        async Task Settle(int Milliseconds = 0)
        {
            if (Milliseconds > 0) await Task.Delay(Milliseconds);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            this.UpdateLayout();
        }
        void Sample(AnimatedDisclosure Host, int Milliseconds)
        {
            // Seek the real WPF clock synchronously so desktop scheduling cannot
            // skip the short interval before its intermediate frame is inspected.
            if (Host.ActiveAnimation is { Controller: { } Controller })
            {
                Controller.Pause();
                Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(Milliseconds), TimeSeekOrigin.BeginTime);
            }
            this.UpdateLayout();
        }
        async Task Finish(AnimatedDisclosure Host)
        {
            if (Host.ActiveAnimation is { Controller: { } Controller })
            {
                Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(160), TimeSeekOrigin.BeginTime);
                Controller.Resume();
            }
            var End = DateTime.UtcNow.AddSeconds(3);
            while (Host.ActiveAnimation is not null && DateTime.UtcNow < End) await Settle(10);
            await Settle();
        }
        try
        {
            await Settle();
            Probe.IsExpanded = true;
            Sample(Probe, 35);
            Check(!SystemParameters.ClientAreaAnimation || Probe.Reveal is > 0 and < 1,
                $"Expansion reveals intermediate geometry during the short animation or honors reduced motion (loaded={Probe.IsLoaded}, reveal={Probe.Reveal}, enabled={AnimatedDisclosure.GetAnimationsEnabled(Probe)})");
            // Lazy-loaded descendants can change height during the reveal.
            ((Border)Probe.Child).Height = 240;
            await Finish(Probe);
            Check(Probe.Reveal == 1 && Math.Abs(Probe.ActualHeight - 240) < 1,
                "Expansion finishes at the latest lazy-loaded content height without clipping");
            Probe.IsExpanded = false;
            Sample(Probe, 35);
            Check(!SystemParameters.ClientAreaAnimation || Probe.Reveal is > 0 and < 1,
                "Collapse animates intermediate geometry instead of immediately hiding the subtree");
            await Finish(Probe);
            Check(!Probe.IsVisible && Probe.Reveal == 0,
                $"Collapse finishes fully hidden without retaining blank space (visible={Probe.IsVisible}, reveal={Probe.Reveal}, expanded={Probe.IsExpanded})");
            Probe.IsExpanded = true;
            Sample(Probe, 25);
            Probe.IsExpanded = false;
            Sample(Probe, 25);
            Probe.IsExpanded = true;
            await Finish(Probe);
            Check(Probe.IsVisible && Probe.Reveal == 1 && Math.Abs(Probe.ActualHeight - 240) < 1,
                "Rapid expansion and collapse finish in the last requested state");
            if (this.FolderTree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem Item)
            {
                var Host = (AnimatedDisclosure)Item.Template.FindName("ItemsReveal", Item);
                var Expanded = Item.IsExpanded;
                var WasSyncing = this.SyncingTree;
                this.SyncingTree = true;
                AnimatedDisclosure.SetAnimationsEnabled(Host, true);
                try
                {
                    Item.IsExpanded = true;
                    await Finish(Host);
                    Item.IsExpanded = false;
                    Sample(Host, 35);
                    Check(!SystemParameters.ClientAreaAnimation || Host.Reveal is > 0 and < 1,
                        "The real directory-tree template animates its child host when the folder model collapses");
                    Item.IsExpanded = true;
                    await Finish(Host);
                    Check(Host.IsExpanded && Host.Reveal == 1,
                        "The real directory tree restores its full subtree after animated expansion");
                }
                finally
                {
                    AnimatedDisclosure.SetAnimationsEnabled(Host, false);
                    Item.IsExpanded = Expanded;
                    this.SyncingTree = WasSyncing;
                }
            }
        }
        finally { Panel.Children.Remove(Probe); }
    }
}
