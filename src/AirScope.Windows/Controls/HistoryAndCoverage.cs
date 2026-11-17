using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AirScope.Core;

namespace AirScope.Windows.Controls;

public sealed class HistoryChart : ChartBase
{
    private IReadOnlyList<HistoryEntry> _entries = [];
    private double _bucketSeconds = 10;
    public void Update(IReadOnlyList<HistoryEntry> entries, double bucketSeconds = 10) { _entries = entries.OrderBy(e => e.Time).ToArray(); _bucketSeconds = bucketSeconds; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        double left = 40, right = Math.Max(left + 1, ActualWidth - 12), top = 16, bottom = Math.Max(top + 1, ActualHeight - 26);
        foreach (int rssi in new[] { -100, -80, -60, -40, -20 })
        { double y = bottom - (rssi + 100) / 80d * (bottom - top); dc.DrawLine(new Pen(GridBrush, 1), new(left, y), new(right, y)); Label(dc, rssi.ToString(), 0, y - 6); }
        var points = _entries.Where(e => e.Rssi.HasValue).ToArray();
        if (points.Length < 2) { Label(dc, "Signal history builds while monitoring is running · dBm", left + 20, (top + bottom) / 2, Muted, 12); return; }
        double seconds = Math.Max(1, (points[^1].Time - points[0].Time).TotalSeconds);
        foreach (var series in points.GroupBy(e => e.Ssid))
        {
            var values = series.OrderBy(e => e.Time).ToArray(); Point? previous = null; DateTimeOffset? previousTime = null;
            foreach (var item in values)
            {
                var point = new Point(left + (item.Time - points[0].Time).TotalSeconds / seconds * (right - left), bottom - (Math.Clamp(item.Rssi!.Value, -100, -20) + 100) / 80d * (bottom - top));
                if (previous.HasValue && item.Time - previousTime <= TimeSpan.FromSeconds(Math.Max(35, _bucketSeconds * 2.1))) dc.DrawLine(new Pen(Mint, 1.7), previous.Value, point);
                dc.DrawEllipse(Mint, null, point, 1.8, 1.8); previous = point; previousTime = item.Time;
            }
        }
        Label(dc, points[0].Time.ToLocalTime().ToString("dd MMM HH:mm"), left, bottom + 7);
        Label(dc, points[^1].Time.ToLocalTime().ToString("dd MMM HH:mm"), Math.Max(left, right - 95), bottom + 7);
    }
}

public sealed class CoverageMap : ChartBase
{
    private IReadOnlyList<SurveyObservation> _points = [];
    private Rect _imageRect;
    private Point? _selection;
    public BitmapSource? FloorPlan { get; set; }
    public event Action<double, double>? LocationSelected;
    public CoverageMap()
    {
        Focusable = true; Cursor = Cursors.Cross;
        System.Windows.Automation.AutomationProperties.SetName(this, "Coverage map; use arrow keys to select a position and Enter to confirm");
    }
    public void Update(IReadOnlyList<SurveyObservation> points) { _points = points; InvalidateVisual(); }
    public static string SignalColor(int signal) => signal >= -55 ? "#51e0c4" : signal >= -67 ? "#81b7ff" : signal >= -75 ? "#ebc582" : "#ff7d89";
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Brush("#0A1420"), new Pen(GridBrush, 1), new(0, 0, ActualWidth, ActualHeight), 10, 10);
        _imageRect = new(12, 12, Math.Max(1, ActualWidth - 24), Math.Max(1, ActualHeight - 24));
        if (FloorPlan is { } bitmap)
        {
            double scale = Math.Min(_imageRect.Width / bitmap.PixelWidth, _imageRect.Height / bitmap.PixelHeight);
            double w = bitmap.PixelWidth * scale, h = bitmap.PixelHeight * scale;
            _imageRect = new((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h); dc.DrawImage(bitmap, _imageRect);
        }
        else
        {
            for (int i = 1; i < 10; i++)
            { double x = _imageRect.X + _imageRect.Width * i / 10, y = _imageRect.Y + _imageRect.Height * i / 10; dc.DrawLine(new Pen(GridBrush, 0.5), new(x, _imageRect.Top), new(x, _imageRect.Bottom)); dc.DrawLine(new Pen(GridBrush, 0.5), new(_imageRect.Left, y), new(_imageRect.Right, y)); }
            if (_points.Count == 0) Label(dc, "Import a floor plan or click this grid to mark a room", 30, 25, Muted, 13);
        }
        foreach (var point in _points.GroupBy(p => (p.X, p.Y)).Select(g => g.OrderByDescending(p => p.Rssi).First()))
        {
            var p = new Point(_imageRect.X + point.X * _imageRect.Width, _imageRect.Y + point.Y * _imageRect.Height);
            dc.DrawEllipse(Brush(SignalColor(point.Rssi)), new Pen(Brush("#091019"), 2), p, 9, 9);
            Label(dc, point.Room + " " + point.Rssi + " dBm", Math.Clamp(p.X + 12, 0, Math.Max(0, ActualWidth - 130)), Math.Max(0, p.Y - 8), Brush("#EDF4FC"), 11);
        }
        if (_selection is Point selection)
            dc.DrawEllipse(null, new Pen(Brush("#FFFFFF"), 2), new(_imageRect.X + selection.X * _imageRect.Width, _imageRect.Y + selection.Y * _imageRect.Height), 14, 14);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus(); var point = e.GetPosition(this); if (!_imageRect.Contains(point)) return;
        _selection = new((point.X - _imageRect.X) / _imageRect.Width, (point.Y - _imageRect.Y) / _imageRect.Height);
        LocationSelected?.Invoke(_selection.Value.X, _selection.Value.Y); InvalidateVisual();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var point = _selection ?? new Point(0.5, 0.5);
        point = e.Key switch { Key.Left => new(Math.Max(0, point.X - 0.02), point.Y), Key.Right => new(Math.Min(1, point.X + 0.02), point.Y), Key.Up => new(point.X, Math.Max(0, point.Y - 0.02)), Key.Down => new(point.X, Math.Min(1, point.Y + 0.02)), _ => point };
        if (e.Key is Key.Enter or Key.Left or Key.Right or Key.Up or Key.Down) { _selection = point; LocationSelected?.Invoke(point.X, point.Y); InvalidateVisual(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
