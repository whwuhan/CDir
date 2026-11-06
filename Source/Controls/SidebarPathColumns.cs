using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
            var Width = Math.Clamp(Viewport.ViewportWidth / 2 + 20 - Origin, 0, Math.Max(0, this.Row.ActualWidth - Prefix));
            var Name = this.Row.ColumnDefinitions[Index];
            if (!Name.Width.IsAbsolute || Math.Abs(Name.Width.Value - Width) > .25) Name.Width = new GridLength(Width);
            var Path = this.Row.ColumnDefinitions[Index + 1];
            if (!Path.Width.IsStar) Path.Width = new GridLength(1, GridUnitType.Star);
        }

        private void Stop()
        {
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
