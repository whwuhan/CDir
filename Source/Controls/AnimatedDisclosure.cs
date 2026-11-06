using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace CDir.Controls;

/// <summary>Reveals a subtree over 130 ms, measuring live contents as lazy-loaded rows arrive.</summary>
public sealed class AnimatedDisclosure : Decorator
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool),
        typeof(AnimatedDisclosure), new FrameworkPropertyMetadata(true, ExpandedChanged));
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.RegisterAttached("AnimationsEnabled", typeof(bool),
        typeof(AnimatedDisclosure), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits,
            (Target, _) => { if (Target is AnimatedDisclosure Host) Host.ChangeExpansion(Host.IsExpanded); }));
    public static void SetAnimationsEnabled(DependencyObject Target, bool Value) => Target.SetValue(AnimationsEnabledProperty, Value);
    public static bool GetAnimationsEnabled(DependencyObject Target) => (bool)Target.GetValue(AnimationsEnabledProperty);

    private static readonly DependencyProperty RevealProperty = DependencyProperty.Register(nameof(Reveal), typeof(double),
        typeof(AnimatedDisclosure), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    internal double Reveal => (double)GetValue(RevealProperty);
    internal AnimationClock? ActiveAnimation { get; private set; }
    private int Transition;

    public AnimatedDisclosure() => this.ClipToBounds = true;

    private static void ExpandedChanged(DependencyObject Target, DependencyPropertyChangedEventArgs E)
        => ((AnimatedDisclosure)Target).ChangeExpansion((bool)E.NewValue);

    private void ChangeExpansion(bool Expanded)
    {
        var Target = Expanded ? 1d : 0d;
        var From = this.Reveal;
        var Version = ++this.Transition;
        this.ApplyAnimationClock(RevealProperty, null);
        this.ActiveAnimation = null;
        this.SetValue(RevealProperty, Target);
        if (!this.IsLoaded || !GetAnimationsEnabled(this) || !SystemParameters.ClientAreaAnimation || Math.Abs(From - Target) < .001)
        {
            this.Visibility = Expanded ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        this.Visibility = Visibility.Visible;
        var Animation = new DoubleAnimation(From, Target, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
        };
        var Clock = (AnimationClock)Animation.CreateClock(true);
        Clock.Completed += (_, _) =>
        {
            if (Version != this.Transition) return;
            this.ApplyAnimationClock(RevealProperty, null);
            this.ActiveAnimation = null;
            this.SetValue(RevealProperty, this.IsExpanded ? 1d : 0d);
            this.Visibility = this.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        };
        this.ActiveAnimation = Clock;
        this.ApplyAnimationClock(RevealProperty, Clock, HandoffBehavior.SnapshotAndReplace);
    }

    protected override Size MeasureOverride(Size Constraint)
    {
        if (this.Child is null) return new Size();
        this.Child.Measure(new Size(Constraint.Width, double.PositiveInfinity));
        return new Size(this.Child.DesiredSize.Width, this.Child.DesiredSize.Height * this.Reveal);
    }

    protected override Size ArrangeOverride(Size Size)
    {
        this.Child?.Arrange(new Rect(0, 0, Size.Width, this.Child.DesiredSize.Height));
        return Size;
    }
}
