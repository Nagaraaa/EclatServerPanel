using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EclatServerPanel;

internal static class Motion
{
    public static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction InOut = new CubicEase { EasingMode = EasingMode.EaseInOut };

    public static void To(this UIElement element, DependencyProperty property, double value, double milliseconds, IEasingFunction? easing = null)
        => element.BeginAnimation(property, new DoubleAnimation(value, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = easing ?? Out });

    // Pages and toasts enter by fading in while rising a few pixels.
    public static void Enter(FrameworkElement element, double distance = 14, double milliseconds = 260)
    {
        var move = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = move;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = Out });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = Out });
    }

    public static Brush Hex(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
}

// A number that counts up to its new value instead of jumping.
internal sealed class CountUpText : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(CountUpText),
        new PropertyMetadata(0d, (d, _) => ((CountUpText)d).Render()));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Format { get; set; } = "0";
    public string Suffix { get; set; } = "";
    bool known;

    public void Show(double? value)
    {
        if (value is null) { BeginAnimation(ValueProperty, null); known = false; Text = "—"; return; }
        known = true;
        BeginAnimation(ValueProperty, new DoubleAnimation(value.Value, TimeSpan.FromMilliseconds(900)) { EasingFunction = Motion.Out });
    }

    void Render() { if (known) Text = Value.ToString(Format, CultureInfo.GetCultureInfo("fr-FR")) + Suffix; }
}

// Circular gauge: a track and a crystal-gradient arc that sweeps to Value (0..1).
internal sealed class ArcGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ArcGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Stroke { get; set; } = Motion.Hex("#5EE6FF");
    public double Thickness { get; set; } = 10;

    public void Animate(double value) => BeginAnimation(ValueProperty, new DoubleAnimation(Math.Clamp(value, 0, 1), TimeSpan.FromMilliseconds(1100)) { EasingFunction = Motion.Out });

    protected override void OnRender(DrawingContext context)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Thickness) / 2;
        const double start = 135, sweep = 270;
        context.DrawGeometry(null, new Pen(Motion.Hex("#222D58"), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(center, radius, start, sweep));
        var value = Math.Clamp(Value, 0, 1);
        if (value > 0.001)
            context.DrawGeometry(null, new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(center, radius, start, sweep * value));
    }

    static Geometry Arc(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        Point At(double degrees) { var r = degrees * Math.PI / 180; return new Point(center.X + radius * Math.Cos(r), center.Y + radius * Math.Sin(r)); }
        var figure = new PathFigure { StartPoint = At(startDegrees), IsClosed = false };
        figure.Segments.Add(new ArcSegment(At(startDegrees + sweepDegrees), new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }
}

// History curve with a soft filled area; it redraws itself left to right.
internal sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Color Color { get; set; } = Color.FromRgb(0x5E, 0xE6, 0xFF);
    IReadOnlyList<double> points = Array.Empty<double>();

    public void Show(IReadOnlyList<double> values)
    {
        points = values.ToArray();
        BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)) { EasingFunction = Motion.Out });
    }

    protected override void OnRender(DrawingContext context)
    {
        if (points.Count < 2 || ActualWidth < 4 || ActualHeight < 4) return;
        double min = points.Min(), max = points.Max();
        if (max - min < 1e-9) { max += 1; min -= 1; }
        var visible = Math.Max(2, (int)Math.Ceiling(points.Count * Math.Clamp(Progress, 0, 1)));
        Point At(int i) => new(i * ActualWidth / (points.Count - 1), ActualHeight - 3 - (points[i] - min) / (max - min) * (ActualHeight - 6));
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var draw = line.Open()) { draw.BeginFigure(At(0), false, false); for (var i = 1; i < visible; i++) draw.LineTo(At(i), true, true); }
        using (var draw = area.Open()) {
            draw.BeginFigure(new Point(0, ActualHeight), true, true);
            for (var i = 0; i < visible; i++) draw.LineTo(At(i), true, true);
            draw.LineTo(new Point(At(visible - 1).X, ActualHeight), true, true);
        }
        var fill = new LinearGradientBrush(Color.FromArgb(90, Color.R, Color.G, Color.B), Color.FromArgb(0, Color.R, Color.G, Color.B), 90);
        context.DrawGeometry(fill, null, area);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color), 2) { LineJoin = PenLineJoin.Round }, line);
        var last = At(visible - 1);
        context.DrawEllipse(new SolidColorBrush(Color), null, last, 3.5, 3.5);
    }
}
