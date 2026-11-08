using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace PhotoImporter.App.Controls;

/// <summary>
/// Circular progress indicator in the style of WinUI's ProgressRing (WPF has none).
/// Determinate: an arc proportional to <see cref="Value"/> (0..1) with the percentage in the middle.
/// Indeterminate: a rotating quarter arc.
/// </summary>
public sealed class ProgressRing : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing), new PropertyMetadata(0.0, (d, _) => ((ProgressRing)d).Update()));

    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(ProgressRing), new PropertyMetadata(false, (d, _) => ((ProgressRing)d).Update()));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing), new PropertyMetadata(6.0, (d, _) => ((ProgressRing)d).Update()));

    private readonly Ellipse _track = new();
    private readonly Path _arc = new() { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly TextBlock _percent = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
    private readonly RotateTransform _rotation = new();
    private readonly DoubleAnimation _spin = new(0, 360, new Duration(TimeSpan.FromSeconds(1.2))) { RepeatBehavior = RepeatBehavior.Forever };
    private bool _spinning;

    public ProgressRing()
    {
        _track.SetResourceReference(Shape.StrokeProperty, "ControlStrongStrokeColorDefaultBrush");
        _track.Opacity = 0.35;
        _arc.SetResourceReference(Shape.StrokeProperty, "AccentFillColorDefaultBrush");
        _arc.RenderTransform = _rotation;
        Children.Add(_track);
        Children.Add(_arc);
        Children.Add(_percent);
        SizeChanged += (_, _) => Update();
        IsVisibleChanged += (_, _) => Update();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    private void Update()
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;

        _track.StrokeThickness = _arc.StrokeThickness = Thickness;
        _percent.FontSize = Math.Max(10, size * 0.2);
        var radius = (size - Thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        _rotation.CenterX = center.X;
        _rotation.CenterY = center.Y;

        var spin = IsIndeterminate && IsVisible;
        if (spin != _spinning)
        {
            _spinning = spin;
            _rotation.BeginAnimation(RotateTransform.AngleProperty, spin ? _spin : null);
            if (!spin) _rotation.Angle = 0;
        }

        var fraction = IsIndeterminate ? 0.25 : Math.Clamp(Value, 0, 1);
        _percent.Text = IsIndeterminate ? "" : ((int)Math.Round(fraction * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        _arc.Data = ArcGeometry(center, radius, fraction);
    }

    /// <summary>Arc starting at 12 o'clock, clockwise, covering <paramref name="fraction"/> of the circle.</summary>
    private static Geometry ArcGeometry(Point center, double radius, double fraction)
    {
        if (fraction <= 0) return Geometry.Empty;
        if (fraction >= 0.9999) return new EllipseGeometry(center, radius, radius);

        var angle = fraction * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}
