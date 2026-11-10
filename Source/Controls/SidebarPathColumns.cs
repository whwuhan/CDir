using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.CompilerServices;

namespace CDir.Controls;

// Keep the metadata column in one sidebar coordinate system, even when folder
// headers have different indentation or live in the pinned overlay.
public static class SidebarPathColumns
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached("IsEnabled", typeof(bool),
        typeof(SidebarPathColumns), new PropertyMetadata(false, Changed));
    public static bool GetIsEnabled(DependencyObject Target) => (bool)Target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject Target, bool Value) => Target.SetValue(IsEnabledProperty, Value);

    public static readonly DependencyProperty NameColumnProperty = DependencyProperty.RegisterAttached("NameColumn", typeof(int),
        typeof(SidebarPathColumns), new PropertyMetadata(1));
    public static int GetNameColumn(DependencyObject Target) => (int)Target.GetValue(NameColumnProperty);
    public static void SetNameColumn(DependencyObject Target, int Value) => Target.SetValue(NameColumnProperty, Value);

    public static readonly DependencyProperty ReferenceScrollProperty = DependencyProperty.RegisterAttached("ReferenceScroll", typeof(ScrollViewer),
        typeof(SidebarPathColumns), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));
    public static ScrollViewer? GetReferenceScroll(DependencyObject Target) => (ScrollViewer?)Target.GetValue(ReferenceScrollProperty);
    public static void SetReferenceScroll(DependencyObject Target, ScrollViewer? Value) => Target.SetValue(ReferenceScrollProperty, Value);

    private static readonly ConditionalWeakTable<ScrollViewer, ColumnGroup> Groups = new();
    private sealed class ColumnGroup
    {
        private readonly Dictionary<LayoutState, double> Rows = [];
        internal double Maximum { get; private set; }
        internal void Report(LayoutState State, double? Right)
        {
            var Previous = this.Maximum;
            if (Right is { } Value)
            {
                this.Rows.TryGetValue(State, out var Old);
                this.Rows[State] = Value;
                this.Maximum = Value >= this.Maximum ? Value : Old >= this.Maximum ? this.Rows.Values.Max() : this.Maximum;
            }
            else if (this.Rows.Remove(State)) this.Maximum = this.Rows.Count == 0 ? 0 : this.Rows.Values.Max();
            if (Math.Abs(Previous - this.Maximum) > .25)
                foreach (var Row in this.Rows.Keys) Row.Invalidate();
        }
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(LayoutState), typeof(SidebarPathColumns));
    private static void Changed(DependencyObject Target, DependencyPropertyChangedEventArgs E)
    {
        if (Target is not Grid Row) return;
        if (Row.GetValue(StateProperty) is not LayoutState State)
        {
            State = new LayoutState(Row);
            Row.SetValue(StateProperty, State);
        }
        State.Update();
    }

    private sealed class LayoutState
    {
        private readonly Grid Row;
        private GridLength? OriginalNameWidth;
        private GridLength OriginalPathWidth;
        private bool Listening;
        private ScrollViewer? Viewport;
        private ColumnGroup? Group;
        private (string Text, Typeface Font, double Size, FlowDirection Direction, double Dpi, Thickness Margin)? MeasuredName;
        private double NaturalNameWidth;

        internal void Invalidate() => this.Row.InvalidateMeasure();

        internal LayoutState(Grid Row)
        {
            this.Row = Row;
            Row.Loaded += (_, _) => this.Update();
            Row.Unloaded += (_, _) => this.Stop();
        }

        internal void Update()
        {
            if (!GetIsEnabled(this.Row) || !this.Row.IsLoaded) { this.Stop(); return; }
            if (this.Row.DataContext is CDir.Models.FolderNode { IsPlaceholder: true }) return;
            var Index = GetNameColumn(this.Row);
            if (Index < 0 || Index + 1 >= this.Row.ColumnDefinitions.Count) return;
            if (this.OriginalNameWidth is null)
            {
                this.OriginalNameWidth = this.Row.ColumnDefinitions[Index].Width;
                this.OriginalPathWidth = this.Row.ColumnDefinitions[Index + 1].Width;
            }
            if (!this.Listening) { this.Row.LayoutUpdated += this.LayoutUpdated; this.Listening = true; }
            this.Apply();
        }

        private void LayoutUpdated(object? Sender, EventArgs E) => this.Apply();

        private void Apply()
        {
            if (this.Viewport is null)
                for (DependencyObject? Parent = this.Row; Parent is not null; Parent = VisualTreeHelper.GetParent(Parent))
                    if (Parent is UserControl Host && Host.FindName("SidebarScroll") is ScrollViewer Scroll) { this.Viewport = Scroll; break; }
            if (this.Viewport is not { ViewportWidth: > 0 } Viewport || this.Row.ActualWidth <= 0) return;
            var Index = GetNameColumn(this.Row);
            var Prefix = this.Row.ColumnDefinitions.Take(Index).Sum(Column => Column.ActualWidth);
            var Origin = this.Row.TranslatePoint(new Point(), Viewport).X + Prefix;
            // Keep column widths in content coordinates. Otherwise scrolling left
            // enlarges the name column by the same amount and pins the metadata.
            ScrollViewer? Category = null;
            for (DependencyObject? Parent = this.Row; Parent is not null && Parent != Viewport; Parent = VisualTreeHelper.GetParent(Parent))
                if (Parent is ScrollViewer Scroll)
                {
                    Origin += Scroll.HorizontalOffset;
                    if (Scroll.Name == "CategoryScroll" && Scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto) Category = Scroll;
                }
            var Reference = GetReferenceScroll(this.Row);
            if (Category is null && Reference is { HorizontalScrollBarVisibility: ScrollBarVisibility.Auto })
            {
                Category = Reference;
                Origin += Reference.HorizontalOffset;
            }
            var Group = Category is null ? null : Groups.GetOrCreateValue(Category);
            if (!ReferenceEquals(Group, this.Group)) { this.Group?.Report(this, null); this.Group = Group; }
            if (Group is not null && Reference is null)
            {
                // Measure the full name, including deeply indented descendants, so
                // scrolling can reveal every character without moving the path column per row.
                var Label = this.Row.Children.OfType<TextBlock>().FirstOrDefault(Text => Grid.GetColumn(Text) == Index);
                if (Label is not null)
                {
                    var Signature = (Label.Text, new Typeface(Label.FontFamily, Label.FontStyle, Label.FontWeight, Label.FontStretch),
                        Label.FontSize, Label.FlowDirection, VisualTreeHelper.GetDpi(Label).PixelsPerDip, Label.Margin);
                    if (this.MeasuredName is not { } Measured || !Measured.Equals(Signature))
                    {
                        this.MeasuredName = Signature;
                        this.NaturalNameWidth = new FormattedText(Label.Text, System.Globalization.CultureInfo.CurrentUICulture,
                            Label.FlowDirection, Signature.Item2, Label.FontSize, Label.Foreground, Signature.Item5).WidthIncludingTrailingWhitespace
                            + Label.Margin.Left + Label.Margin.Right;
                    }
                }
                Group.Report(this, this.Row.IsVisible ? Origin + (Label is null ? 0 : this.NaturalNameWidth) : null);
            }
            var Column = Math.Max(Viewport.ViewportWidth / 2 + 20, Group?.Maximum ?? 0);
            var Width = Math.Max(0, Column - Origin);
            var Name = this.Row.ColumnDefinitions[Index];
            if (!Name.Width.IsAbsolute || Math.Abs(Name.Width.Value - Width) > .25) Name.Width = new GridLength(Width);
            var Path = this.Row.ColumnDefinitions[Index + 1];
            if (!Path.Width.IsStar) Path.Width = new GridLength(1, GridUnitType.Star);
        }

        private void Stop()
        {
            this.Group?.Report(this, null);
            this.Group = null;
            if (this.Listening) { this.Row.LayoutUpdated -= this.LayoutUpdated; this.Listening = false; }
            if (this.OriginalNameWidth is { } Width)
            {
                var Index = GetNameColumn(this.Row);
                this.Row.ColumnDefinitions[Index].Width = Width;
                this.Row.ColumnDefinitions[Index + 1].Width = this.OriginalPathWidth;
                this.OriginalNameWidth = null;
            }
            this.Viewport = null;
        }
    }
}
