using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AirScope.Core;
using Forms = System.Windows.Forms;

namespace AirScope.Windows.Services;

public sealed class BackgroundMonitor : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _stop = new();
    private readonly HistoryService _history = new(); private readonly UsageLedger _usage = new(); private readonly TrafficMonitor _traffic = new();
    private readonly Forms.NotifyIcon _tray; private readonly WorkerConfiguration? _worker; private readonly bool _smoke;
    private NativeWifi? _wifi; private Connection? _connection; private RealtimeQuality? _quality; private Guid? _id;
    private Task? _scheduled; private int _ticks; private string? _identity; private bool _disposed;
    private DateTimeOffset _lastError = DateTimeOffset.MinValue;
    public BackgroundMonitor(WorkerConfiguration? worker = null, bool smoke = false)
    {
        _worker = worker; _smoke = smoke;
        _tray = new Forms.NotifyIcon { Visible = !smoke, Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Information,
            Text = worker is null ? "AirScope · lightweight monitor" : "AirScope · protected monitor" };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(worker is null ? "Open AirScope" : "Show protected monitor", null, async (_, _) => await OpenAsync());
        menu.Items.Add("Exit monitor", null, async (_, _) => { await StopAsync(); System.Windows.Application.Current.Shutdown(); });
        _tray.ContextMenuStrip = menu; _tray.DoubleClick += async (_, _) => await OpenAsync();
        _timer.Tick += Tick; _timer.Start();
    }
    private void Record(HistoryEntry entry) { if (!_smoke) _history.Append(entry); }
    private void Tick(object? sender, EventArgs e)
    {
        if (_disposed) return; _ticks++;
        try
        {
            if (_ticks % 15 == 1)
            {
                _wifi ??= new NativeWifi();
                var interfaces = _wifi.Interfaces(); _id = _worker?.AdapterId ?? interfaces.FirstOrDefault(i => i.State == 1)?.Id;
                _connection = _id is Guid id ? _wifi.Current(id) : null; _quality = _id is Guid key ? _wifi.Quality(key) : null;
                string identity = _connection?.SsidHex + "/" + _connection?.Bssid;
                if (_identity is not null && _identity != identity) Record(new(DateTimeOffset.Now, _connection is null ? "Outage" : "Association changed", _id ?? Guid.Empty, _connection?.Ssid, _connection?.Bssid));
                _identity = identity;
            }
            var traffic = _traffic.Sample(_id);
            if (traffic is not null)
            {
                string text = $"AirScope · ↓ {traffic.DownloadMbps:F1} / ↑ {traffic.UploadMbps:F1} Mbps"; _tray.Text = text.Length > 127 ? text[..127] : text;
                if (_ticks % 10 == 0)
                {
                    var best = _quality?.Links.OrderByDescending(l => l.Rssi).FirstOrDefault();
                    Record(new(traffic.Time, "Sample", _id ?? Guid.Empty, _connection?.Ssid, _connection?.Bssid,
                        _quality?.IsMlo == true ? WifiBand.Unknown : best?.Band ?? WifiBand.Unknown, best?.Rssi, _connection?.RxLinkMbps,
                        traffic.DownloadMbps, traffic.UploadMbps, Detail: _quality?.IsMlo == true ? "MLO aggregate traffic; signal is the best connected link." : "Lightweight background observation."));
                }
            }
            if (_wifi is not null)
                foreach (var item in _wifi.DrainEvents().Where(n => n.Kind != "Scan complete")) Record(new(item.Time, item.Kind, item.InterfaceId, Detail: item.Message));
            if (!_smoke && _ticks % 30 == 0 && (_scheduled is null || _scheduled.IsCompleted)) _scheduled = RunScheduledAsync();
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or JsonException)
        {
            _connection = null; _tray.Text = "AirScope · waiting for Wi-Fi access";
            if (!_smoke && DateTimeOffset.Now - _lastError > TimeSpan.FromMinutes(5)) { _lastError = DateTimeOffset.Now; try { Record(new(DateTimeOffset.Now, "Background monitoring unavailable", _id ?? Guid.Empty, Detail: error.Message)); } catch (IOException) { } }
        }
    }
    private async Task RunScheduledAsync()
    {
        try
        {
            var settings = UserSettings.Load();
            var policy = _worker?.Policy ?? settings.Automation; var options = _worker?.Options ?? settings.TestOptions;
            if (_worker is not null && (_connection?.InterfaceId != _worker.AdapterId || _connection.SsidHex != _worker.ExpectedSsidHex)) return;
            var now = DateTimeOffset.Now; var day = _usage.Today(now);
            var decision = AutomationRules.Evaluate(policy, now, DesktopIntegration.IdleTime(), day.LastTest, day.LastDisruption,
                day.UsedBytes, options.MaxBytes, false, _connection is not null);
            if (!decision.Run || _connection is null) return;
            var connection = _connection;
            string reservation = _usage.Reserve(options.MaxBytes, policy.DailyByteLimit, now); long used = 0;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var midnight = now.LocalDateTime.Date.AddDays(1); limit.CancelAfter(new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight)) - now);
            try
            {
                var band = _quality?.IsMlo == true ? WifiBand.Unknown : _quality?.Links.FirstOrDefault()?.Band ?? WifiBand.Unknown;
                var result = await new SpeedTestService().RunAsync(connection, band, options, new Progress<string>(), limit.Token, n => Interlocked.Add(ref used, n));
                if (_wifi?.Current(connection.InterfaceId)?.Bssid != connection.Bssid) throw new IOException("AP changed; background result discarded.");
                Record(new(result.Time, "Scheduled speed test", connection.InterfaceId, connection.Ssid, connection.Bssid, band,
                    DownloadMbps: result.DownloadMbps, UploadMbps: result.UploadMbps, LatencyMs: result.LoadedLatency?.AverageMs,
                    Detail: $"{result.Transport}; {result.DownloadSeconds:F1}s; {result.Streams} streams"));
                var history = JsonStore.Read<List<SpeedResult>>("speed-history.json") ?? [];
                var comparable = new[] { result }.Concat(history).Where(s => s.Ssid == result.Ssid && s.Bssid == result.Bssid && s.Endpoint == result.Endpoint && s.Streams == result.Streams && s.DownloadSeconds >= 2).Take(8).ToArray();
                if (policy.NotifyDegradation && comparable.Length >= 6 && comparable.Take(3).All(s => s.DownloadMbps < EvidenceEngine.Median(comparable.Skip(3).Select(s => s.DownloadMbps)) * 0.65))
                    _tray.ShowBalloonTip(7000, "AirScope: performance needs review", "The scheduled result is below recent comparable tests. Open History & tools to review evidence.", Forms.ToolTipIcon.Warning);
                JsonStore.AppendSpeed(result);
            }
            finally { _usage.Complete(reservation, Interlocked.Read(ref used), now); }
            if (_worker is not null && policy.AllowDisruptiveTrials && !_stop.IsCancellationRequested)
            {
                var preflight = await RecoveryHealthService.PreflightAsync(_worker.AdapterId);
                if (!preflight.Ready) { Record(new(DateTimeOffset.Now, "Protected trial deferred", _worker.AdapterId, Detail: "Recovery preflight prerequisites are not met.")); return; }
                var window = new MainWindow(worker: _worker);
                try { await window.RunProtectedMaintenanceAsync(_worker, _stop.Token); }
                finally { window.DisposeWorkerWindow(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { try { Record(new(DateTimeOffset.Now, "Scheduled work deferred", _id ?? Guid.Empty, Detail: error.Message)); } catch (IOException) { } }
    }
    private async Task OpenAsync()
    {
        var config = _worker; await StopAsync();
        var window = new MainWindow(worker: config); System.Windows.Application.Current.MainWindow = window;
        System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
        if (config is not null) window.Title = "AirScope · protected monitor";
        window.Show(); window.Activate();
    }
    private async Task StopAsync()
    {
        _timer.Stop(); _stop.Cancel();
        if (_scheduled is not null) await _scheduled;
        Dispose();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _timer.Stop(); _stop.Cancel(); _tray.Dispose(); _wifi?.Dispose(); _stop.Dispose();
    }
}
