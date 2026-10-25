using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CorePilot.Controls;

/// <summary>
/// Lightweight oscilloscope-style line chart: one polyline per core,
/// auto-scaled to the window max, with phosphor-instrument styling.
/// </summary>
public sealed class MetricChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty =
        DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<double[]>), typeof(MetricChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(MetricChart),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(MetricChart),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightedSeriesProperty =
        DependencyProperty.Register(nameof(HighlightedSeries), typeof(IReadOnlyList<bool>), typeof(MetricChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double[]>? Series
    {
        get => (IReadOnlyList<double[]>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public IReadOnlyList<bool>? HighlightedSeries
    {
        get => (IReadOnlyList<bool>?)GetValue(HighlightedSeriesProperty);
        set => SetValue(HighlightedSeriesProperty, value);
    }

    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(40, 140, 180, 160)), 1);
    private static readonly SolidColorBrush LabelBrush = new(Color.FromRgb(0x6E, 0x8B, 0x82));
    private static readonly Typeface LabelTypeface = new("Bahnschrift");

    static MetricChart()
    {
        GridPen.Freeze();
        LabelBrush.Freeze();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 30) return;

        const double leftPad = 44, topPad = 22, bottomPad = 6, rightPad = 8;
        var plotW = w - leftPad - rightPad;
        var plotH = h - topPad - bottomPad;

        // Title
        DrawText(dc, Title.ToUpperInvariant(), 11, new Point(0, 0), LabelBrush, FontWeights.SemiBold);

        var series = Series;
        if (series is null || series.Count == 0)
        {
            DrawText(dc, "WAITING FOR SAMPLES", 10, new Point(leftPad, topPad + plotH / 2), LabelBrush, FontWeights.Normal);
            return;
        }

        var maxLen = series.Max(s => s?.Length ?? 0);
        var maxVal = Math.Max(1.0, series.SelectMany(s => s ?? Array.Empty<double>()).DefaultIfEmpty(0).Max());
        maxVal = NiceCeiling(maxVal);

        // Grid lines: 4 horizontal
        for (var i = 0; i <= 4; i++)
        {
            var y = topPad + plotH - plotH * i / 4.0;
            dc.DrawLine(GridPen, new Point(leftPad, y), new Point(leftPad + plotW, y));
            var label = FormatValue(maxVal * i / 4.0);
            DrawText(dc, label, 9.5, new Point(0, y - 7), LabelBrush, FontWeights.Normal);
        }
        DrawText(dc, Unit, 9.5, new Point(2, topPad - 14), LabelBrush, FontWeights.Normal);

        for (var s = 0; s < series.Count; s++)
        {
            var data = series[s];
            if (data is null || data.Length < 2) continue;

            var highlighted = HighlightedSeries is not null && s < HighlightedSeries.Count && HighlightedSeries[s];
            var color = SeriesColor(s);
            var brush = new SolidColorBrush(highlighted ? color : Dim(color, 0.45));
            var pen = new Pen(brush, highlighted ? 2.0 : 1.0);
            brush.Freeze();
            pen.Freeze();

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                var stepX = maxLen > 1 ? plotW / (maxLen - 1) : plotW;
                var offsetX = leftPad + plotW - stepX * (data.Length - 1);
                ctx.BeginFigure(Map(offsetX, data[0]), isFilled: false, isClosed: false);
                for (var i = 1; i < data.Length; i++)
                    ctx.LineTo(Map(offsetX + stepX * i, data[i]), isStroked: true, isSmoothJoin: false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);

            Point Map(double x, double v)
            {
                var y = topPad + plotH - (v / maxVal) * plotH;
                return new Point(x, Math.Clamp(y, topPad, topPad + plotH));
            }
        }
    }

    private static double NiceCeiling(double value)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(value, 1))));
        var normalized = value / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    private static string FormatValue(double v) =>
        v >= 1000 ? $"{v / 1000:0.#}k" : v >= 10 ? $"{v:0}" : $"{v:0.#}";

    private void DrawText(DrawingContext dc, string text, double size, Point origin, Brush brush, FontWeight weight)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelTypeface, size, brush, dpi) { MaxTextWidth = ActualWidth };
        dc.DrawText(formatted, origin);
    }

    internal static Color SeriesColor(int index)
    {
        // Evenly spaced hues, phosphor-instrument saturation.
        var hue = (index * 137.508) % 360; // golden angle spread
        return HslToRgb(hue, 0.72, 0.58);
    }

    internal static Color LoadColor(double loadScore)
    {
        // Green (idle) -> amber -> red (saturated at 25% combined interrupt+DPC time).
        var t = Math.Clamp(loadScore / 25.0, 0, 1);
        var hue = 120.0 * (1 - t);
        return HslToRgb(hue, 0.75, 0.16 + 0.30 * t);
    }

    private static Color Dim(Color c, double factor) =>
        Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

    private static Color HslToRgb(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
