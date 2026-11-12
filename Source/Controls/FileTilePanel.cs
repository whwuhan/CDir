using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CDir.Models;

namespace CDir.Controls;

// Fixed-size cells allow selection, keyboard navigation and virtualization to use
// the same geometry, including cells that have not been realized yet.
public sealed class FileTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty GroupedProperty = DependencyProperty.Register(nameof(Grouped), typeof(bool), typeof(FileTilePanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public bool Grouped { get => (bool)GetValue(GroupedProperty); set => SetValue(GroupedProperty, value); }
    private sealed record ItemGroup(string Label, int Start, int Count)
    {
        public double Top { get; set; }
        public double Bottom { get; set; }
    }
    private readonly List<ItemGroup> ItemGroups = [];
    private bool GroupsDirty = true;
    private readonly List<(string Label, double Top)> GroupHeaders = [];
    public static readonly DependencyProperty CellWidthProperty = DependencyProperty.Register(nameof(CellWidth), typeof(double), typeof(FileTilePanel), new FrameworkPropertyMetadata(112d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double CellWidth { get => (double)GetValue(CellWidthProperty); set => SetValue(CellWidthProperty, value); }
    public static readonly DependencyProperty CellHeightProperty = DependencyProperty.Register(nameof(CellHeight), typeof(double), typeof(FileTilePanel), new FrameworkPropertyMetadata(112d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double CellHeight { get => (double)GetValue(CellHeightProperty); set => SetValue(CellHeightProperty, value); }
    public static readonly DependencyProperty FillDownProperty = DependencyProperty.Register(nameof(FillDown), typeof(bool), typeof(FileTilePanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public bool FillDown { get => (bool)GetValue(FillDownProperty); set => SetValue(FillDownProperty, value); }
    public static readonly DependencyProperty FullWidthProperty = DependencyProperty.Register(nameof(FullWidth), typeof(bool), typeof(FileTilePanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public bool FullWidth { get => (bool)GetValue(FullWidthProperty); set => SetValue(FullWidthProperty, value); }
    private int Across = 1;
    private int Down = 1;
    private int Count;
    private int ResizeAnchor = -1;
    public void RevealAfterResize(int Index) { this.ResizeAnchor = Index; InvalidateMeasure(); }
    public Rect ItemBounds(int Index)
    {
        if (this.Grouped && Index >= 0 && Index < Count)
        {
            var Low = 0;
            var High = this.ItemGroups.Count - 1;
            while (Low <= High)
            {
                var Middle = Low + (High - Low) / 2;
                var Group = this.ItemGroups[Middle];
                if (Index < Group.Start) High = Middle - 1;
                else if (Index >= Group.Start + Group.Count) Low = Middle + 1;
                else return new Rect((Index - Group.Start) % Across * ActualCellWidth,
                    Group.Top + (Index - Group.Start) / Across * CellHeight, ActualCellWidth, CellHeight);
            }
        }
        return new Rect((FillDown ? Index / Down : Index % Across) * this.ActualCellWidth,
            (FillDown ? Index % Down : Index / Across) * CellHeight, this.ActualCellWidth, CellHeight);
    }
    private double ActualCellWidth => FullWidth ? Math.Max(1, ViewportWidth) : CellWidth;

    protected override Size MeasureOverride(Size Available)
    {
        var Owner = ItemsControl.GetItemsOwner(this);
        if (Owner is null) return new();
        ViewportWidth = double.IsFinite(Available.Width) ? Available.Width : 800;
        ViewportHeight = double.IsFinite(Available.Height) ? Available.Height : 600;
        if (Count != Owner.Items.Count) this.GroupsDirty = true;
        Count = Owner.Items.Count;
        Across = FullWidth ? 1 : Math.Max(1, (int)(ViewportWidth / CellWidth));
        Down = Math.Max(1, (int)(ViewportHeight / CellHeight));
        ExtentWidth = FillDown ? Math.Ceiling((double)Count / Down) * CellWidth : ViewportWidth;
        ExtentHeight = FillDown ? ViewportHeight : Math.Ceiling((double)Count / Across) * CellHeight;
        this.GroupHeaders.Clear();
        if (this.Grouped)
        {
            // Membership changes with the collection, not with zoom or scrolling.
            // Keep one range per group instead of allocating a rectangle per file.
            if (this.GroupsDirty)
            {
                this.ItemGroups.Clear();
                for (var Start = 0; Start < Count;)
                {
                    var Label = (Owner.Items[Start] as FileEntry)?.ViewGroup ?? "";
                    var End = Start + 1;
                    while (End < Count && (Owner.Items[End] as FileEntry)?.ViewGroup == Label) End++;
                    this.ItemGroups.Add(new ItemGroup(Label, Start, End - Start));
                    Start = End;
                }
                this.GroupsDirty = false;
            }
            var Top = 0d;
            var HeaderHeight = Math.Max(28, (Owner as Control)?.FontSize + 12 ?? 28);
            foreach (var Group in this.ItemGroups)
            {
                this.GroupHeaders.Add(($"{Group.Label} ({Group.Count})", Top)); Top += HeaderHeight;
                Group.Top = Top;
                Top += Math.Ceiling((double)Group.Count / Across) * CellHeight;
                Group.Bottom = Top;
            }
            ExtentWidth = ViewportWidth;
            ExtentHeight = Top;
            InvalidateVisual();
        }
        // Resolve the zoom anchor using the new geometry before realizing items.
        // Otherwise a deep folder first realizes the old offset, discards those
        // controls, then realizes the anchor in a second synchronous layout.
        if (this.ResizeAnchor >= 0 && this.ResizeAnchor < Count)
        {
            var Bounds = this.ItemBounds(this.ResizeAnchor);
            HorizontalOffset = Math.Max(Bounds.Right - ViewportWidth, Math.Min(HorizontalOffset, Bounds.Left));
            VerticalOffset = Math.Max(Bounds.Bottom - ViewportHeight, Math.Min(VerticalOffset, Bounds.Top));
        }
        this.ResizeAnchor = -1;
        HorizontalOffset = Math.Clamp(HorizontalOffset, 0, Math.Max(0, ExtentWidth - ViewportWidth));
        VerticalOffset = Math.Clamp(VerticalOffset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        ScrollOwner?.InvalidateScrollInfo();
        var First = FillDown ? (int)(HorizontalOffset / CellWidth) * Down : (int)(VerticalOffset / CellHeight) * Across;
        var Last = Math.Min(Count - 1, FillDown
            ? ((int)Math.Ceiling((HorizontalOffset + ViewportWidth) / CellWidth) + 1) * Down - 1
            : ((int)Math.Ceiling((VerticalOffset + ViewportHeight) / CellHeight) + 1) * Across - 1);
        if (this.Grouped)
        {
            First = Count;
            Last = -1;
            foreach (var Group in this.ItemGroups)
            {
                if (Group.Bottom < VerticalOffset) continue;
                if (Group.Top > VerticalOffset + ViewportHeight + CellHeight) break;
                var FirstRow = Math.Max(0, (int)Math.Ceiling((VerticalOffset - Group.Top) / CellHeight) - 1);
                var LastRow = (int)Math.Floor((VerticalOffset + ViewportHeight + CellHeight - Group.Top) / CellHeight);
                First = Math.Min(First, Group.Start + FirstRow * Across);
                Last = Math.Min(Group.Start + Group.Count - 1, Group.Start + (LastRow + 1) * Across - 1);
            }
        }
        // Accessing InternalChildren connects the panel to its items generator.
        _ = this.InternalChildren;
        var Generator = this.ItemContainerGenerator;
        for (var ChildIndex = InternalChildren.Count - 1; ChildIndex >= 0; ChildIndex--)
        {
            var ItemIndex = Generator.IndexFromGeneratorPosition(new GeneratorPosition(ChildIndex, 0));
            if (ItemIndex >= First && ItemIndex <= Last) continue;
            Generator.Remove(new GeneratorPosition(ChildIndex, 0), 1);
            RemoveInternalChildRange(ChildIndex, 1);
        }
        if (Count > 0 && First <= Last)
        {
            var Position = Generator.GeneratorPositionFromIndex(First);
            var ChildIndex = Position.Offset == 0 ? Position.Index : Position.Index + 1;
            using (Generator.StartAt(Position, GeneratorDirection.Forward, true))
                for (var Index = First; Index <= Last; Index++, ChildIndex++)
                {
                    var Child = (UIElement)Generator.GenerateNext(out var New);
                    if (New)
                    {
                        InsertInternalChild(ChildIndex, Child);
                        Generator.PrepareItemContainer(Child);
                    }
                    Child.Measure(new Size(ActualCellWidth, CellHeight));
                }
        }
        return new Size(ViewportWidth, ViewportHeight);
    }

    protected override Size ArrangeOverride(Size FinalSize)
    {
        for (var Index = 0; Index < InternalChildren.Count; Index++)
        {
            var Item = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(Index, 0));
            var Bounds = ItemBounds(Item);
            Bounds.Offset(-HorizontalOffset, -VerticalOffset);
            InternalChildren[Index].Arrange(Bounds);
        }
        return FinalSize;
    }
    protected override void OnRender(DrawingContext Context)
    {
        base.OnRender(Context);
        if (ItemsControl.GetItemsOwner(this) is not Control Owner) return;
        foreach (var (Label, Top) in this.GroupHeaders)
        {
            var Y = Top - VerticalOffset + 6;
            if (Y < -40 || Y > ViewportHeight) continue;
            var Text = new FormattedText(Label, CDir.Localization.LanguageService.Culture, FlowDirection.LeftToRight,
                new Typeface(Owner.FontFamily, FontStyles.Normal, (FontWeight)Owner.FindResource("UIHeadingFontWeight"), FontStretches.Normal), Owner.FontSize, Owner.Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            Context.DrawText(Text, new Point(8, Y));
            Context.DrawLine(new Pen(SystemColors.ControlLightBrush, 1), new Point(Math.Min(ViewportWidth, Text.Width + 20), Y + Text.Height / 2), new Point(ViewportWidth, Y + Text.Height / 2));
        }
    }
    protected override void OnItemsChanged(object Sender, ItemsChangedEventArgs Args)
    {
        this.GroupsDirty = true;
        this.ResizeAnchor = -1;
        // Collection-view refreshes can invalidate all previously generated positions.
        if (Args.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Remove
            or System.Collections.Specialized.NotifyCollectionChangedAction.Replace
            or System.Collections.Specialized.NotifyCollectionChangedAction.Move)
            RemoveInternalChildRange(Args.Position.Index, Args.ItemUICount);
        InvalidateMeasure();
    }
    public void Reveal(int Index)
    {
        var Bounds = ItemBounds(Index);
        if (Bounds.Left < HorizontalOffset) SetHorizontalOffset(Bounds.Left);
        else if (Bounds.Right > HorizontalOffset + ViewportWidth) SetHorizontalOffset(Bounds.Right - ViewportWidth);
        if (Bounds.Top < VerticalOffset) SetVerticalOffset(Bounds.Top);
        else if (Bounds.Bottom > VerticalOffset + ViewportHeight) SetVerticalOffset(Bounds.Bottom - ViewportHeight);
    }
    protected override void BringIndexIntoView(int Index) => Reveal(Index);
    public Rect MakeVisible(Visual Visual, Rect Rectangle)
    {
        var Owner = ItemsControl.GetItemsOwner(this);
        if (Owner is not null && ItemsControl.ContainerFromElement(Owner, Visual) is { } Container)
            Reveal(Owner.ItemContainerGenerator.IndexFromContainer(Container));
        return Rectangle;
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth { get; private set; }
    public double ExtentHeight { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }
    public double HorizontalOffset { get; private set; }
    public double VerticalOffset { get; private set; }
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetHorizontalOffset(double Offset) { HorizontalOffset = Math.Clamp(Offset, 0, Math.Max(0, ExtentWidth - ViewportWidth)); InvalidateMeasure(); ScrollOwner?.InvalidateScrollInfo(); }
    public void SetVerticalOffset(double Offset) { VerticalOffset = Math.Clamp(Offset, 0, Math.Max(0, ExtentHeight - ViewportHeight)); InvalidateMeasure(); ScrollOwner?.InvalidateScrollInfo(); }
    public void LineUp() => SetVerticalOffset(VerticalOffset - CellHeight);
    public void LineDown() => SetVerticalOffset(VerticalOffset + CellHeight);
    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - CellWidth);
    public void LineRight() => SetHorizontalOffset(HorizontalOffset + CellWidth);
    public void MouseWheelUp() { if (FillDown) LineLeft(); else SetVerticalOffset(VerticalOffset - 3 * CellHeight); }
    public void MouseWheelDown() { if (FillDown) LineRight(); else SetVerticalOffset(VerticalOffset + 3 * CellHeight); }
    public void MouseWheelLeft() => LineLeft();
    public void MouseWheelRight() => LineRight();
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - ViewportWidth);
    public void PageRight() => SetHorizontalOffset(HorizontalOffset + ViewportWidth);
}
