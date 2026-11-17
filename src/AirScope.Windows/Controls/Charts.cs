using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AirScope.Core;

namespace AirScope.Windows.Controls;

public abstract class ChartBase : FrameworkElement
{
    protected static readonly SolidColorBrush Muted = Brush("#8196B0");
    protected static readonly SolidColorBrush Mint = Brush("#51E0C4");
    protected static readonly SolidColorBrush Purple = Brush("#AC9CFF");
    protected static readonly SolidColorBrush GridBrush = Brush("#243449");
    protected static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush;
    }
    protected void Label(DrawingContext dc, string value, double x, double y, System.Windows.Media.Brush? color = null, double size = 10)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size,
            color ?? Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(x, y));
    }
}

public sealed class TrafficChart : ChartBase
{
    private IReadOnlyList<TrafficSample> _samples = [];
    public void Update(IReadOnlyList<TrafficSample> samples) { _samples = samples; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double left = 42, top = 12, right = Math.Max(left + 1, ActualWidth - 6), bottom = Math.Max(top + 1, ActualHeight - 23);
        double max = Math.Max(1, _samples.Select(p => Math.Max(p.DownloadMbps, p.UploadMbps)).DefaultIfEmpty().Max() * 1.2);
        max = Math.Ceiling(max / (max > 100 ? 100 : max > 10 ? 10 : 1)) * (max > 100 ? 100 : max > 10 ? 10 : 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = top + (bottom - top) * i / 4;
            dc.DrawLine(new Pen(GridBrush, 1), new(left, y), new(right, y));
            Label(dc, (max * (4 - i) / 4).ToString(max < 10 ? "0.0" : "0"), 0, y - 6);
        }
        Label(dc, "−120s", left, bottom + 7); Label(dc, "−60s", (left + right) / 2 - 12, bottom + 7); Label(dc, "now", right - 20, bottom + 7);
        if (_samples.Count < 2) { Label(dc, "Your live traffic history will appear here", left + 35, (top + bottom) / 2, Muted, 12); return; }
        DateTimeOffset now = _samples[^1].Time;
        DrawSeries(s => s.DownloadMbps, Mint);
        DrawSeries(s => s.UploadMbps, Purple);
        void DrawSeries(Func<TrafficSample, double> value, SolidColorBrush brush)
        {
            var points = _samples.Where(s => (now - s.Time).TotalSeconds <= 120).Select(s => new Point(
                right - (right - left) * (now - s.Time).TotalSeconds / 120, bottom - (bottom - top) * Math.Clamp(value(s) / max, 0, 1))).ToArray();
            if (points.Length < 2) return;
            var area = new StreamGeometry();
            using (var context = area.Open())
            {
                context.BeginFigure(new(points[0].X, bottom), true, true);
                context.PolyLineTo(points, true, false); context.LineTo(new(points[^1].X, bottom), true, false);
            }
            dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(38, brush.Color.R, brush.Color.G, brush.Color.B), Colors.Transparent, 90), null, area);
            var line = new StreamGeometry();
            using (var context = line.Open()) { context.BeginFigure(points[0], false, false); context.PolyLineTo(points.Skip(1).ToArray(), true, false); }
            dc.DrawGeometry(null, new Pen(brush, 2), line);
        }
    }
}

public sealed class SpectrumChart : ChartBase
{
    private IReadOnlyList<AccessPoint> _points = [];
    private string _target = "";
    private WifiBand _band = WifiBand.Band6;
    public void Update(IReadOnlyList<AccessPoint> points, WifiBand band, string target)
    { _points = points; _band = band; _target = target; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double left = 42, top = 20, right = Math.Max(left + 1, ActualWidth - 10), bottom = Math.Max(top + 1, ActualHeight - 28);
        var (min, max) = _band switch { WifiBand.Band24 => (2395d, 2490d), WifiBand.Band5 => (5150d, 5840d), _ => (5935d, 6450d) };
        double X(double mhz) => left + (right - left) * (mhz - min) / (max - min);
        double Y(double dbm) => bottom - (bottom - top) * (Math.Clamp(dbm, -100, -20) + 100) / 80;
        for (int level = -100; level <= -20; level += 20)
        {
            double y = Y(level); dc.DrawLine(new Pen(GridBrush, 1), new(left, y), new(right, y)); Label(dc, level.ToString(), 0, y - 6);
        }
        int[] ticks = _band switch { WifiBand.Band24 => [1, 3, 6, 9, 11, 13], WifiBand.Band5 => [36, 52, 64, 100, 116, 132, 149, 165], _ => [1, 17, 33, 49, 65, 81, 93] };
        foreach (int ch in ticks)
        {
            int mhz = (_band == WifiBand.Band24 ? 2407 : _band == WifiBand.Band5 ? 5000 : 5950) + ch * 5;
            Label(dc, ch.ToString(), X(mhz) - 5, bottom + 8);
        }
        var visible = _points.Where(p => p.Band == _band).OrderBy(p => p.Rssi).TakeLast(50)
            .OrderBy(p => p.SsidHex == _target ? 1 : 0).ToArray();
        dc.PushClip(new RectangleGeometry(new(left, 0, right - left, bottom + 1)));
        foreach (var ap in visible)
        {
            bool own = !string.IsNullOrEmpty(_target) && ap.SsidHex == _target;
            var brush = own ? Mint : _band == WifiBand.Band24 ? Brush("#DDA86C") : Purple;
            double center = ap.CenterFrequencyMhz ?? ap.FrequencyMhz;
            double halfWidth = (ap.WidthMhz ?? 20) / 2d;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new(X(center - halfWidth), bottom), true, false);
                for (int i = 1; i <= 40; i++)
                {
                    double f = i / 40d;
                    double y = bottom - (bottom - Y(ap.Rssi)) * Math.Sin(Math.PI * f);
                    context.LineTo(new(X(center - halfWidth + f * halfWidth * 2), y), true, false);
                }
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(own ? (byte)50 : (byte)13, brush.Color.R, brush.Color.G, brush.Color.B)), new Pen(brush, own ? 2.3 : 1), geometry);
            if (own || visible.Length <= 8)
            {
                string label = ap.Name.Length > 20 ? ap.Name[..18] + "…" : ap.Name;
                Label(dc, label, Math.Clamp(X(center) - 25, left + 2, Math.Max(left + 2, right - 110)), Y(ap.Rssi) - 18, brush, own ? 11 : 9);
            }
        }
        dc.Pop();
        if (visible.Length == 0) Label(dc, "No access points observed on this band", left + 35, (top + bottom) / 2, Muted, 13);
    }
}
