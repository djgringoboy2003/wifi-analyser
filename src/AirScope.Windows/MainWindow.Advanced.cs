using System.Net;
using System.Text.Json;
using System.Windows;
using AirScope.Core;
using AirScope.Windows.Services;
using Forms = System.Windows.Forms;

namespace AirScope.Windows;

public partial class MainWindow
{
    private readonly HistoryService _historyStore = new();
    private readonly UsageLedger _usage = new();
    private bool _experimentBusy, _automationRunning, _workerMode;
    private string? _lastIdentity, _lastDriver;
    private int? _lastChannel;
    private DateTimeOffset _lastAlert = DateTimeOffset.MinValue;
    internal UserSettings Settings => _settings;
    internal Connection? Connected => _connection;
    internal IReadOnlyList<AccessPoint> AccessPoints => _networks;
    internal RealtimeQuality? LinkQuality => _quality;
    internal AdapterDetails? Hardware => _hardware;
    internal IReadOnlyList<SpeedResult> Measurements => _speeds;
    internal IReadOnlyList<LatencyResult> Latencies => _latencies;
    internal bool Busy => _test is not null || _adminBusy || _experimentBusy || _scanGate.CurrentCount == 0;
    internal HistoryService History => _historyStore;
    internal UsageLedger Usage => _usage;
    internal bool IsSmoke => _smokeTest;

    private void InitialiseAdvanced()
    {
        TestSeconds.Text = _settings.TestOptions.SecondsPerDirection.ToString(); TestStreams.Text = _settings.TestOptions.Streams.ToString();
        TestCap.Text = (_settings.TestOptions.MaxBytes / 1_000_000).ToString(); TestIpv6.IsChecked = _settings.TestOptions.PreferIpv6;
        TestProgress.Text = "Parallel interface-bound tests · configurable payload cap · idle and loaded TCP latency. Review the options and daily budget before starting.";
        Advanced.Attach(this);
        if (!_smokeTest) _historyStore.Prune(Math.Clamp(_settings.RetentionDays, 1, 365));
    }
    private void SaveTestOptionsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(TestSeconds.Text, out int seconds) || !int.TryParse(TestStreams.Text, out int streams) || !long.TryParse(TestCap.Text, out long cap))
                throw new ArgumentException("Enter whole numbers for seconds, streams and the payload cap.");
            var options = _settings.TestOptions with { SecondsPerDirection = seconds, Streams = streams, MaxBytes = checked(cap * 1_000_000), PreferIpv6 = TestIpv6.IsChecked == true };
            options.Validate(); _settings.TestOptions = options; SaveSettings(); Status("Test options saved. The daily payload quota also applies to manual tests.");
        }
        catch (Exception error) { Error(error); }
    }
    private void UpdateQualityView()
    {
        MloText.Text = _quality is { Supported: true } q
            ? $"Windows real-time telemetry: {(q.IsMlo ? "MLO active" : "MLO not active")}\n" + string.Join("\n", q.Links.Select(l => $"Link {l.LinkId}: {l.BandLabel}, width {l.WidthLabel}, {l.Rssi} dBm"))
            : "Real-time link/MLO telemetry unavailable: " + (_quality?.UnavailableReason ?? "not queried");
    }
    private void AppendHistory(HistoryEntry entry)
    {
        if (_smokeTest) return;
        try { _historyStore.Append(entry); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Status("History could not be saved: " + error.Message); }
    }
    private void AdvancedTick(TrafficSample? sample)
    {
        if (_wifi is not null)
            foreach (var notification in _wifi.DrainEvents().Where(n => n.Kind != "Scan complete"))
                AppendHistory(new(notification.Time, notification.Kind, notification.InterfaceId, Detail: notification.Message));
        if (_ticks % 5 == 0 && _wifi is not null && _adapterId is Guid id)
        { _quality = _wifi.Quality(id); UpdateQualityView(); }
        string identity = $"{_connection?.SsidHex}/{_connection?.Bssid}";
        int? channel = CurrentAp()?.Channel;
        if (_lastIdentity is not null && identity != _lastIdentity)
            AppendHistory(new(DateTimeOffset.Now, _connection is null ? "Outage" : "Association changed", _adapterId ?? Guid.Empty,
                _connection?.Ssid, _connection?.Bssid, CurrentAp()?.Band ?? WifiBand.Unknown));
        if (_lastChannel.HasValue && channel.HasValue && channel != _lastChannel)
            AppendHistory(new(DateTimeOffset.Now, "Channel changed", _adapterId ?? Guid.Empty, _connection?.Ssid, _connection?.Bssid,
                CurrentAp()?.Band ?? WifiBand.Unknown, Detail: $"Primary channel {_lastChannel} → {channel}"));
        if (_lastDriver is not null && _hardware?.DriverVersion is string driver && driver != _lastDriver)
            AppendHistory(new(DateTimeOffset.Now, "Driver changed", _adapterId ?? Guid.Empty, Detail: $"{_lastDriver} → {driver}"));
        _lastIdentity = identity; _lastChannel = channel; _lastDriver = _hardware?.DriverVersion;
        if (_ticks % 10 == 0 && sample is not null)
        {
            var recent = _samples.Where(s => sample.Time - s.Time <= TimeSpan.FromSeconds(10)).ToArray();
            AppendHistory(new(sample.Time, "Sample", _adapterId ?? Guid.Empty, _connection?.Ssid, _connection?.Bssid,
                CurrentAp()?.Band ?? WifiBand.Unknown, _quality?.Supported == true ? _quality.Links.Select(l => (int?)l.Rssi).Max() : CurrentAp()?.Rssi,
                _connection?.RxLinkMbps, recent.Average(s => s.DownloadMbps), recent.Average(s => s.UploadMbps)));
        }
        if (_ticks % 30 == 0 && !_smokeTest) _ = RunScheduledCheckAsync();
    }
    private async Task<SpeedResult> MeasureWithBudgetAsync(Connection connection, WifiBand band, ThroughputOptions options,
        IProgress<string> progress, CancellationToken token)
    {
        options.Validate(); var started = DateTimeOffset.Now;
        string? reservation = _smokeTest ? null : _usage.Reserve(options.MaxBytes, _settings.Automation.DailyByteLimit, started);
        long bytes = 0;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        var midnightLocal = started.LocalDateTime.Date.AddDays(1);
        var midnight = new DateTimeOffset(midnightLocal, TimeZoneInfo.Local.GetUtcOffset(midnightLocal));
        limit.CancelAfter(midnight - started);
        try { return await new SpeedTestService().RunAsync(connection, band, options, progress, limit.Token, n => Interlocked.Add(ref bytes, n)); }
        finally { if (reservation is not null) _usage.Complete(reservation, Interlocked.Read(ref bytes), started); }
    }
    internal async Task<SpeedResult> MeasureAsync(ThroughputOptions options, CancellationToken token)
    {
        if (Busy) throw new InvalidOperationException("Wait for the current scan, experiment or protected operation.");
        if (_wifi is null || _adapterId is not Guid id || _wifi.Current(id) is not Connection connection || CurrentAp() is not AccessPoint ap)
            throw new InvalidOperationException("Connect and scan the selected Wi-Fi adapter first.");
        _test = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token); SetTesting(true);
        try { return await RunSpeedAsync(connection, ap.Band, _test.Token, options); }
        finally { FinishTest(); }
    }
    internal async Task<OptimisationPlan> PreparePlanAsync()
    {
        if (Busy) throw new InvalidOperationException("Wait for the current operation.");
        if (_adapterId is not Guid id || _connection is null || _connection.SsidHex != _targetHex || DateTimeOffset.Now - _lastScan > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("Complete a recent scan while connected to your target SSID.");
        try { SetHardware(await HardwareService.ReadAsync(id)); }
        catch (InvalidOperationException)
        {
            var response = await AdminAsync(new("inspect", id));
            if (response.Data is JsonElement data) SetHardware(data.Deserialize<AdapterDetails>(JsonStore.Options) ?? throw new InvalidDataException("Hardware read failed."));
        }
        var plan = OptimisationPlanner.Build(_hardware!, _networks, _targetHex, _settings.DesktopTuning);
        _plan = plan; PlanTable.ItemsSource = plan.Changes; PlanAdvice.Text = string.Join("\n\n", plan.Advice);
        PlanSummary.Text = $"{plan.Changes.Count} supported changes. Experiments trial only one selected setting."; ApplyButton.IsEnabled = plan.Changes.Count > 0;
        return plan;
    }
    internal void PersistSettings() { SaveSettings(); Advanced.RefreshSummary(); }
    internal async Task<RecoveryPreflight> ProtectedPreflightAsync()
    {
        if (_adapterId is not Guid id) throw new InvalidOperationException("Select and scan a Wi-Fi adapter first.");
        var response = await AdminAsync(new("preflight", id));
        return response.Data?.Deserialize<RecoveryPreflight>(JsonStore.Options) ?? throw new IOException("No preflight data returned.");
    }
    internal async Task<string> ConfigureWorkerAsync(bool remove)
    {
        if (remove) return (await AdminAsync(new("worker-remove", Guid.Empty))).Message;
        if (_connection is null) throw new InvalidOperationException("Connect and scan the intended SSID first.");
        if (string.IsNullOrEmpty(_settings.UpdatePublisherThumbprint)) throw new InvalidOperationException("Set a trusted signing-certificate thumbprint in Updates first. Unsigned builds cannot install a privileged worker.");
        var config = new WorkerConfiguration(Guid.NewGuid().ToString("D"), "", _connection.InterfaceId, _connection.SsidHex,
            _settings.Automation, _settings.TestOptions, _settings.UpdatePublisherThumbprint);
        return (await AdminAsync(new("worker-install", _connection.InterfaceId, Worker: config))).Message;
    }
    internal void ExitForUpdate() { _exiting = true; Close(); }
    internal async Task RunProtectedMaintenanceAsync(WorkerConfiguration config, CancellationToken token)
    {
        await ScanAsync();
        if (_connection?.InterfaceId != config.AdapterId || _connection.SsidHex != config.ExpectedSsidHex)
            throw new InvalidOperationException("The protected worker is waiting for its configured adapter and SSID.");
        _hardware = await HardwareService.ReadAsync(config.AdapterId);
        await RunScheduledTrialAsync(config.Policy);
        token.ThrowIfCancellationRequested();
    }
    internal void DisposeWorkerWindow()
    {
        _shutdown.Cancel(); _wifi?.Dispose(); Advanced.Dispose(); _timer.Stop();
    }
    internal DiagnosticEvidence Evidence() => new(_connection, CurrentAp(), _quality,
        _latencies.FirstOrDefault(l => l.Target == HardwareService.Gateway(_adapterId)), _latencies.FirstOrDefault(l => l.Target == "1.1.1.1"),
        _speeds.FirstOrDefault(s => s.Transport == "HTTPS" && s.Ssid == _connection?.Ssid && s.Bssid == _connection.Bssid && DateTimeOffset.Now - s.Time < TimeSpan.FromHours(24)),
        _speeds.FirstOrDefault(s => s.Transport == "LAN TCP" && s.Ssid == _connection?.Ssid && s.Bssid == _connection.Bssid && DateTimeOffset.Now - s.Time < TimeSpan.FromHours(24)), _settings.ServiceMbps);
    private async Task RunScheduledCheckAsync()
    {
        if (_automationRunning) return;
        _automationRunning = true;
        try
        {
            var policy = _settings.Automation; var day = _usage.Today(DateTimeOffset.Now);
            var decision = AutomationRules.Evaluate(policy, DateTimeOffset.Now, DesktopIntegration.IdleTime(), day.LastTest, day.LastDisruption,
                day.UsedBytes, _settings.TestOptions.MaxBytes, Busy, _connection is not null);
            Advanced.AutomationStatus(decision.Reason);
            if (!decision.Run) return;
            var result = await MeasureAsync(_settings.TestOptions, _shutdown.Token);
            var comparable = _speeds.Where(s => s.Ssid == result.Ssid && s.Bssid == result.Bssid && s.Endpoint == result.Endpoint && s.Streams == result.Streams && s.DownloadSeconds >= 2).Take(8).ToArray();
            bool degraded = comparable.Length >= 6 && comparable.Take(3).All(s => s.DownloadMbps < EvidenceEngine.Median(comparable.Skip(3).Select(s => s.DownloadMbps)) * 0.65);
            if (policy.NotifyDegradation && degraded && DateTimeOffset.Now - _lastAlert > TimeSpan.FromHours(1))
            {
                _lastAlert = DateTimeOffset.Now;
                _tray?.ShowBalloonTip(8000, "AirScope: sustained performance needs checking", "The scheduled result is below recent comparable tests. Open History & tools to review evidence.", Forms.ToolTipIcon.Warning);
                AppendHistory(new(DateTimeOffset.Now, "Degradation alert", _adapterId ?? Guid.Empty, _connection?.Ssid, Detail: "Scheduled throughput fell below 65% of the recent comparable median."));
            }
            if (policy.AllowDisruptiveTrials) await RunScheduledTrialAsync(policy);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Advanced.AutomationStatus("Scheduled work deferred: " + error.Message); AppendHistory(new(DateTimeOffset.Now, "Automation deferred", _adapterId ?? Guid.Empty, Detail: error.Message)); }
        finally { _automationRunning = false; }
    }
}
