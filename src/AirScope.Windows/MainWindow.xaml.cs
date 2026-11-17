using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirScope.Core;
using AirScope.Windows.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AirScope.Windows;

public partial class MainWindow : Window
{
    private readonly UserSettings _settings;
    private readonly bool _smokeTest;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TrafficMonitor _traffic = new();
    private readonly List<TrafficSample> _samples = [];
    private readonly List<SpeedResult> _speeds;
    private readonly List<LatencyResult> _latencies = [];
    private NativeWifi? _wifi;
    private Guid? _adapterId;
    private IReadOnlyList<AccessPoint> _networks = [];
    private Connection? _connection;
    private AdapterDetails? _hardware;
    private RealtimeQuality? _quality;
    private OptimisationPlan? _plan;
    private CancellationTokenSource? _test;
    private Forms.NotifyIcon? _tray;
    private SpeedWidget? _widget;
    private bool _ready, _adminBusy, _exiting, _exitAfterTest, _benchmark;
    private string _targetHex = "";
    private DateTimeOffset _lastScan = DateTimeOffset.MinValue;
    private int _ticks;
    private long _received, _sent, _previousRx = -1, _previousTx = -1;

    public MainWindow(bool smokeTest = false, WorkerConfiguration? worker = null)
    {
        _smokeTest = smokeTest;
        _settings = smokeTest ? new() : UserSettings.Load();
        _workerMode = worker is not null;
        if (worker is not null) { _settings.Automation = worker.Policy; _settings.TestOptions = worker.Options; _settings.AdapterId = worker.AdapterId; _settings.TargetSsid = ""; }
        _settings.ScanSeconds = Math.Clamp(_settings.ScanSeconds, 15, 120);
        _speeds = smokeTest ? [] : JsonStore.Read<List<SpeedResult>>("speed-history.json") ?? [];
        InitializeComponent();
        _adapterId = _settings.AdapterId;
        AutoScanSetting.IsChecked = _settings.AutoScan; TraySetting.IsChecked = _settings.MinimiseToTray;
        WidgetSetting.IsChecked = _settings.ShowWidget; DesktopSetting.IsChecked = _settings.DesktopTuning;
        TargetSsidBox.Text = _settings.TargetSsid; NetworkOwnOnly.IsChecked = _settings.OwnSsidOnly;
        InitialiseAdvanced();
        _timer.Tick += OnTick;
        _ready = true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_smokeTest) return;
        SetupTray(); UpdateWidget(); _timer.Start(); RefreshHistory(); RefreshRecovery();
        await ScanAsync();
        if (_adapterId is Guid id)
        {
            try { SetHardware(await HardwareService.ReadAsync(id)); }
            catch (Exception error) { HardwareText.Text = (_wifi?.Interfaces().FirstOrDefault(i => i.Id == id)?.Description ?? "Wi-Fi adapter") + "\n\n" + error.Message; }
        }
    }
    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (Pages is not null && sender is RadioButton button && int.TryParse(button.Tag?.ToString(), out int page)) Pages.SelectedIndex = page;
    }
    private void GoOptimiserClick(object sender, RoutedEventArgs e)
    {
        Pages.SelectedIndex = 4;
        var nav = FindNavigation(4); if (nav is not null) nav.IsChecked = true;
    }
    private RadioButton? FindNavigation(int index) => FindVisualChildren<RadioButton>(this).FirstOrDefault(r => r.Tag?.ToString() == index.ToString(CultureInfo.InvariantCulture));
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
    private void Status(string message) => StatusText.Text = message.Replace('\r', ' ').Replace('\n', ' ');
    private void Error(Exception error)
    {
        string message = error is Win32Exception { NativeErrorCode: 1223 } ? "Administrator request cancelled. No protected operation was started." : error.Message;
        Status(message); MessageBox.Show(this, message, "AirScope", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private async void ScanClick(object sender, RoutedEventArgs e) => await ScanAsync();
    private async Task ScanAsync()
    {
        if (_test is not null || _adminBusy || _experimentBusy || !await _scanGate.WaitAsync(0)) return;
        ScanButton.IsEnabled = false;
        try
        {
            _wifi ??= new NativeWifi();
            var interfaces = await Task.Run(_wifi.Interfaces);
            if (interfaces.Count == 0) { _connection = null; _networks = []; UpdateView(); Status("No Wi-Fi adapter found. Enable an adapter in Windows and scan again."); return; }
            if (_adapterId is null || !interfaces.Any(i => i.Id == _adapterId))
                _adapterId = interfaces.FirstOrDefault(i => i.State == 1)?.Id ?? interfaces[0].Id;
            var previous = AdapterPicker.ItemsSource as IReadOnlyList<WifiInterface>;
            if (previous is null || !previous.Select(p => p.Id).SequenceEqual(interfaces.Select(p => p.Id)))
            {
                AdapterPicker.ItemsSource = interfaces;
                AdapterPicker.SelectedItem = interfaces.FirstOrDefault(i => i.Id == _adapterId);
            }
            Guid id = _adapterId.Value;
            Status("Scanning nearby access points…");
            bool completed = await _wifi.ScanAsync(id, _shutdown.Token);
            var networks = await Task.Run(() => _wifi.Networks(id));
            var connection = await Task.Run(() => _wifi.Current(id));
            if (_adapterId != id) return;
            _networks = networks.Where(p => DateTimeOffset.Now - p.SeenAt < TimeSpan.FromSeconds(90)).ToArray();
            _connection = connection; _quality = _wifi.Quality(id); _lastScan = DateTimeOffset.Now;
            NoticeBorder.Visibility = Visibility.Collapsed;
            if (string.IsNullOrEmpty(_settings.TargetSsid) && connection is not null)
            {
                _settings.TargetSsid = connection.Ssid; TargetSsidBox.Text = connection.Ssid; SaveSettings();
            }
            _targetHex = _networks.FirstOrDefault(p => p.Ssid == _settings.TargetSsid)?.SsidHex
                ?? (connection?.Ssid == _settings.TargetSsid ? connection.SsidHex : "");
            UpdateView(); Status($"{(completed ? "Scan complete" : "Scan notification unavailable; showing dated observations")} · {_networks.Count} access points · {_lastScan:T}");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            _lastScan = DateTimeOffset.Now; _connection = null; _networks = []; UpdateView();
            NoticeText.Text = error is Win32Exception { NativeErrorCode: 5 }
                ? "Windows restricted Wi-Fi access. Enable Location services and allow desktop apps in Privacy & security → Location, then scan again."
                : "Wi-Fi scan unavailable: " + error.Message;
            NoticeBorder.Visibility = Visibility.Visible; Status(NoticeText.Text);
        }
        finally { ScanButton.IsEnabled = true; _scanGate.Release(); }
    }
    private void UpdateView()
    {
        var current = CurrentAp();
        HeaderSsid.Text = _connection?.Ssid ?? "Wi-Fi connection unavailable";
        HeaderConnection.Text = _connection is null ? "Check Wi-Fi and Windows location permission" : $"{_connection.Standard}  ·  {current?.BandLabel ?? "Band unreported"}  ·  {(_hardware?.Name ?? "Selected Wi-Fi adapter")}";
        SignalValue.Text = current?.Rssi.ToString(CultureInfo.CurrentCulture) ?? _connection?.SignalPercent.ToString(CultureInfo.CurrentCulture) ?? "—";
        SignalUnit.Text = current is null ? " %" : " dBm";
        SignalDescription.Text = current is not null ? $"{RadioMath.Signal(current.Rssi)} · {_connection?.SignalPercent}% quality" : "Driver signal quality";
        LinkValue.Text = _connection?.RxLinkMbps.ToString("N0") ?? "—";
        string? gateway = null;
        try { gateway = HardwareService.Gateway(_adapterId); } catch (System.Net.NetworkInformation.NetworkInformationException) { }
        ConnectionDetails.Text = _connection is null ? "Connection details are unavailable. Live traffic monitoring can continue if the adapter is up." :
            $"Band       {current?.BandLabel ?? "Not reported"}\nChannel    {current?.Channel.ToString() ?? "—"}\nSecurity   {_connection.Security}\nTransmit   {_connection.TxLinkMbps:N0} Mbps\nGateway    {gateway ?? "Not reported"}\nProfile    {_connection.Profile}";
        Band24Summary.Text = BandSummary(WifiBand.Band24); Band5Summary.Text = BandSummary(WifiBand.Band5); Band6Summary.Text = BandSummary(WifiBand.Band6);
        UpdateQualityView();
        ScanTimestamp.Text = _lastScan == DateTimeOffset.MinValue ? "Not scanned yet" : $"Last collection {_lastScan:T}";
        RefreshNetworks(); RefreshChannels();
            if (_plan is not null && _plan.AdapterId != _adapterId) InvalidatePlan();
    }
    private AccessPoint? CurrentAp() => _networks.FirstOrDefault(p => string.Equals(p.Bssid, _connection?.Bssid, StringComparison.OrdinalIgnoreCase));
    private string BandSummary(WifiBand band)
    {
        var own = _networks.Where(p => p.SsidHex == _targetHex && _targetHex.Length > 0 && p.Band == band).OrderByDescending(p => p.Rssi).ToArray();
        int others = _networks.Count(p => p.Band == band && p.SsidHex != _targetHex);
        var result = _speeds.FirstOrDefault(s => s.Ssid == _settings.TargetSsid && s.Band == band);
        if (own.Length == 0) return $"Your SSID not observed\n{others} neighbouring access points\nNo speed inferred";
        var best = own[0];
        bool active = own.Any(p => p.Bssid == _connection?.Bssid);
        return $"{(active ? "● Connected" : "Available")} · channel {best.Channel}\n{best.Rssi} dBm · AP advertises {best.WidthLabel}\n{(result is null ? "Not speed-tested" : $"Last test: {result.DownloadMbps:F0} Mbps ↓ · {result.Time:dd MMM HH:mm}")}";
    }
    private void OnTick(object? sender, EventArgs e)
    {
        _ticks++; FooterClock.Text = DateTime.Now.ToString("HH:mm:ss");
        try
        {
            var sample = _traffic.Sample(_adapterId);
            if (sample is not null)
            {
                _samples.Add(sample); _samples.RemoveAll(s => sample.Time - s.Time > TimeSpan.FromSeconds(120));
                DownloadValue.Text = sample.DownloadMbps.ToString("F1"); UploadValue.Text = sample.UploadMbps.ToString("F1");
                if (_previousRx >= 0 && sample.ReceivedBytes >= _previousRx) _received += sample.ReceivedBytes - _previousRx;
                if (_previousTx >= 0 && sample.SentBytes >= _previousTx) _sent += sample.SentBytes - _previousTx;
                _previousRx = sample.ReceivedBytes; _previousTx = sample.SentBytes;
                SessionUsage.Text = $"Session: ↓ {_received / 1_000_000d:F1} MB  ↑ {_sent / 1_000_000d:F1} MB";
                ActivityChart.Update(_samples.ToArray()); _widget?.Update(sample.DownloadMbps, sample.UploadMbps);
                if (_tray is not null) _tray.Text = $"AirScope · ↓ {sample.DownloadMbps:F1} / ↑ {sample.UploadMbps:F1} Mbps"[..Math.Min(127, $"AirScope · ↓ {sample.DownloadMbps:F1} / ↑ {sample.UploadMbps:F1} Mbps".Length)];
                LiveBadge.Text = "●  LIVE MONITOR";
            }
            else { DownloadValue.Text = "—"; UploadValue.Text = "—"; _widget?.Update(null, null); LiveBadge.Text = "○  WAITING FOR WI-FI"; _previousRx = _previousTx = -1; }
            if (_ticks % 5 == 0) RefreshRecovery();
            AdvancedTick(sample);
            if (_settings.AutoScan && DateTimeOffset.Now - _lastScan >= TimeSpan.FromSeconds(_settings.ScanSeconds)) _ = ScanAsync();
        }
        catch (Exception error) when (error is System.Net.NetworkInformation.NetworkInformationException or IOException or UnauthorizedAccessException)
        { Status("Monitoring temporarily unavailable: " + error.Message); }
    }
    private void SetTargetClick(object sender, RoutedEventArgs e)
    {
        _settings.TargetSsid = TargetSsidBox.Text;
        _targetHex = _networks.FirstOrDefault(p => p.Ssid == _settings.TargetSsid)?.SsidHex ?? "";
        InvalidatePlan(); SaveSettings(); UpdateView(); Status("Target SSID updated. Scan or build a fresh plan.");
    }
    private void RefreshNetworks()
    {
        if (!_ready) return;
        string query = NetworkSearch.Text.Trim();
        WifiBand? band = NetworkBand.SelectedIndex > 0 ? (WifiBand)NetworkBand.SelectedIndex : null;
        var selectedBssid = (NetworkTable.SelectedItem as AccessPoint)?.Bssid;
        var points = _networks.Where(p => (!band.HasValue || p.Band == band)
            && (NetworkOwnOnly.IsChecked != true || p.SsidHex == _targetHex && _targetHex.Length > 0)
            && (string.IsNullOrEmpty(query) || $"{p.Ssid} {p.Bssid} {p.Channel} {p.Security}".Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.Rssi).ToArray();
        NetworkTable.ItemsSource = points;
        NetworkTable.SelectedItem = points.FirstOrDefault(p => p.Bssid == selectedBssid);
        NetworkCount.Text = $"{points.Length} of {_networks.Count} access points · one row per BSSID · click column headers to sort";
    }
    private void NetworkFilterChanged(object sender, RoutedEventArgs e) { if (_ready) RefreshNetworks(); }
    private void NetworkBandChanged(object sender, SelectionChangedEventArgs e) { if (_ready) RefreshNetworks(); }
    private void NetworkSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NetworkTable.SelectedItem is not AccessPoint ap) return;
        SelectedNetworkDetails.Text = $"{ap.Bssid} · {ap.FrequencyMhz} MHz · {ap.SignalPercent}% signal quality · {(ap.StationCount is int count ? count + " stations" : "Station count not advertised")}\nSeen {ap.SeenAt.ToLocalTime():HH:mm:ss} · Regulatory flag: {(ap.InRegulatoryDomain ? "in domain" : "out of domain / driver unreported")}";
    }
    private void ChannelBandChanged(object sender, SelectionChangedEventArgs e) { if (_ready) RefreshChannels(); }
    private void ChannelFilterChanged(object sender, RoutedEventArgs e) { if (_ready) RefreshChannels(); }
    private void RefreshChannels()
    {
        if (!_ready) return;
        var band = (WifiBand)(ChannelBand.SelectedIndex + 1);
        var points = ChannelOwnOnly.IsChecked == true ? _networks.Where(p => p.SsidHex == _targetHex && _targetHex.Length > 0).ToArray() : _networks;
        Spectrum.Update(points, band, _targetHex);
        ChannelTable.ItemsSource = RadioMath.RateChannels(_networks, band, _targetHex);
    }
    private async void AdapterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || AdapterPicker.SelectedItem is not WifiInterface chosen || chosen.Id == _adapterId) return;
        if (_test is not null || _adminBusy) { AdapterPicker.SelectedItem = (AdapterPicker.ItemsSource as IReadOnlyList<WifiInterface>)?.FirstOrDefault(i => i.Id == _adapterId); return; }
        _adapterId = chosen.Id; _settings.AdapterId = chosen.Id; _hardware = null; _connection = null; _networks = [];
        _samples.Clear(); _previousRx = _previousTx = -1; PropertiesTable.ItemsSource = null; HardwareText.Text = chosen.Description;
        InvalidatePlan(); SaveSettings(); UpdateView(); await ScanAsync();
    }
    private async void ReadHardwareClick(object sender, RoutedEventArgs e)
    { try { if (_adapterId is not Guid id) throw new InvalidOperationException("Scan to select a Wi-Fi adapter first."); SetHardware(await HardwareService.ReadAsync(id)); } catch (Exception error) { Error(error); } }
    private async void ReadProtectedHardwareClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_adapterId is not Guid id) throw new InvalidOperationException("Scan to select a Wi-Fi adapter first.");
            var response = await AdminAsync(new("inspect", id));
            if (response.Data is JsonElement data) SetHardware(data.Deserialize<AdapterDetails>(JsonStore.Options) ?? throw new InvalidDataException("No hardware details returned."));
        }
        catch (Exception error) { Error(error); }
    }
    private void SetHardware(AdapterDetails hardware)
    {
        if (hardware.Id != _adapterId) return;
        _hardware = hardware; PropertiesTable.ItemsSource = hardware.Properties;
        HardwareText.Text = $"{hardware.Description}\nInterface: {hardware.Name}\nProvider: {hardware.Manufacturer}";
        DriverText.Text = $"Driver {hardware.DriverVersion}\nDriver date: {hardware.DriverDate}\n{hardware.Properties.Count} driver properties discovered";
        Status("Hardware read complete. Values come from the installed adapter driver."); UpdateView();
    }
    private async Task<AdminResponse> AdminAsync(AdminRequest request)
    {
        if (_adminBusy || _test is not null) throw new InvalidOperationException("Wait for the current test or protected operation to finish.");
        _adminBusy = true; PlanButton.IsEnabled = ApplyButton.IsEnabled = false;
        Status("Waiting for Windows administrator approval and the protected operation…");
        try
        {
            if (request.Operation == "apply") request = request with { ExpectedSsidHex = _connection?.SsidHex };
            var response = await ElevationService.ExecuteAsync(request);
            if (!response.Success) throw new InvalidOperationException(response.Message);
            Status(response.Message); return response;
        }
        finally { _adminBusy = false; PlanButton.IsEnabled = true; ApplyButton.IsEnabled = _plan?.Changes.Count > 0; RefreshRecovery(); }
    }
    private async void BuildPlanClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_connection is null || _adapterId is not Guid id || _targetHex.Length == 0 || _connection.SsidHex != _targetHex)
                throw new InvalidOperationException("Connect to your SSID, allow Wi-Fi access, and complete a scan first.");
            if (DateTimeOffset.Now - _lastScan > TimeSpan.FromMinutes(1)) await ScanAsync();
            if (_connection is null || _connection.SsidHex != _targetHex || _networks.Count == 0)
                throw new InvalidOperationException("The connected target SSID could not be verified. Scan again before tuning.");
            try { SetHardware(await HardwareService.ReadAsync(id)); }
            catch (InvalidOperationException)
            {
                var response = await AdminAsync(new("inspect", id));
                if (response.Data is JsonElement data) SetHardware(data.Deserialize<AdapterDetails>(JsonStore.Options) ?? throw new InvalidDataException("No hardware data."));
            }
            if (_hardware is null) throw new InvalidOperationException("Read the selected adapter hardware first.");
            _plan = OptimisationPlanner.Build(_hardware, _networks, _targetHex, _settings.DesktopTuning);
            PlanTable.ItemsSource = _plan.Changes; PlanAdvice.Text = string.Join("\n\n", _plan.Advice);
            PlanSummary.Text = _plan.Changes.Count == 0 ? "Your supported settings already match this plan, or no justified change is available. Review the guidance below." :
                $"{_plan.Changes.Count} supported changes for {_plan.AdapterName} · built {_plan.CreatedAt:T}. The trial may briefly disconnect Wi-Fi when the adapter restarts.";
            ApplyButton.IsEnabled = _plan.Changes.Count > 0;
            Status("Analysis complete. Review the proposed settings and their reasons.");
        }
        catch (Exception error) { Error(error); }
    }
    private void InvalidatePlan() { _plan = null; ApplyButton.IsEnabled = false; PlanTable.ItemsSource = null; PlanSummary.Text = "The setup changed. Build a fresh plan before tuning."; }
    private async void ApplyPlanClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var plan = _plan ?? throw new InvalidOperationException("Build a plan first.");
            OptimisationPlanner.Validate(plan);
            if (DateTimeOffset.Now - plan.CreatedAt > TimeSpan.FromMinutes(10)) { InvalidatePlan(); throw new InvalidOperationException("This plan has expired. Build a fresh plan."); }
            string summary = string.Join("\n", plan.Changes.Select(c => $"{c.Name}: {c.Before} → {c.After}"));
            if (MessageBox.Show(this, summary + "\n\nAirScope will snapshot these settings, verify a new Windows restore point, then restart the adapter. Wi-Fi may briefly disconnect.\n\nKeep the trial within 3 minutes in Recovery; otherwise Windows automatically restores the original settings. Apply this trial?", "Trial optimisation plan", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var response = await AdminAsync(new("apply", plan.AdapterId, plan));
            InvalidatePlan(); RecoveryNav.IsChecked = true; Pages.SelectedIndex = 6; RefreshRecovery();
            MessageBox.Show(this, response.Message, "Trial active", MessageBoxButton.OK, MessageBoxImage.Information);
            await ScanAsync();
        }
        catch (Exception error) { Error(error); }
    }
    private void RefreshRecovery()
    {
        try
        {
            var selected = (RecoveryTable.SelectedItem as RecoveryPoint)?.Id;
            var points = ElevationService.ListRecoveryPoints(); RecoveryTable.ItemsSource = points;
            RecoveryTable.SelectedItem = points.FirstOrDefault(p => p.Id == selected) ?? points.FirstOrDefault();
            var pending = points.FirstOrDefault(p => p.State is "Pending" or "Applying" or "RollbackFailed");
            TrialBanner.Visibility = pending is null ? Visibility.Collapsed : Visibility.Visible;
            if (pending is not null)
            {
                double remaining = Math.Max(0, (pending.RollbackAt - DateTimeOffset.Now)?.TotalSeconds ?? 0);
                TrialCountdown.Text = pending.State == "RollbackFailed" ? "Automatic recovery needs attention. Select this trial and restore it again." :
                    remaining > 0 ? $"Trial active · automatic rollback in {remaining:F0} seconds. Test your connection, then choose Keep or Restore." :
                    "The rollback deadline has passed. Windows may be restoring the adapter; refresh to check its state.";
            }
            KeepButton.IsEnabled = !_adminBusy && points.Any(p => p.State == "Pending" && p.RollbackAt > DateTimeOffset.Now);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Status("Recovery storage unavailable: " + error.Message); }
    }
    private void RefreshRecoveryClick(object sender, RoutedEventArgs e) => RefreshRecovery();
    private async void SnapshotClick(object sender, RoutedEventArgs e)
    {
        try { if (_adapterId is not Guid id) throw new InvalidOperationException("Select a Wi-Fi adapter first."); var response = await AdminAsync(new("snapshot", id)); Status(response.Message); }
        catch (Exception error) { Error(error); }
    }
    private async void KeepClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RecoveryTable.SelectedItem is not RecoveryPoint { State: "Pending" } point) throw new InvalidOperationException("Select an active pending trial.");
            if (_wifi is null || point.AdapterId is not Guid id || _wifi.Current(id) is null) throw new InvalidOperationException("Reconnect this trial's adapter to Wi-Fi before keeping it. Automatic rollback remains active.");
            var response = await AdminAsync(new("keep", id, RecoveryId: point.Id)); Status(response.Message); InvalidatePlan();
        }
        catch (Exception error) { Error(error); }
    }
    private async void RollbackClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RecoveryTable.SelectedItem is not RecoveryPoint point) throw new InvalidOperationException("Select a snapshot first.");
            if (MessageBox.Show(this, "Restore the adapter values captured in this snapshot? The adapter will restart and Wi-Fi may briefly disconnect.", "Restore original settings", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var response = await AdminAsync(new("rollback", Guid.Empty, RecoveryId: point.Id)); Status(response.Message); InvalidatePlan(); await ScanAsync();
        }
        catch (Exception error) { Error(error); }
    }
    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_test is not null || _adminBusy) throw new InvalidOperationException("Wait for the active operation to finish.");
            if (_wifi is null || _adapterId is not Guid id || NetworkTable.SelectedItem is not AccessPoint ap) throw new InvalidOperationException("Select an access point first.");
            string? profile = _connection?.SsidHex == ap.SsidHex ? _connection.Profile : _wifi.Profiles(id).FirstOrDefault(p => p == ap.Ssid);
            if (profile is null) { Open("ms-settings:network-wifi"); Status("Connect once in Windows to save a profile, then return to AirScope."); return; }
            if (MessageBox.Show(this, $"Connect the saved profile ‘{profile}’ to the selected {ap.BandLabel} access point? The current connection may drop briefly.", "Connect Wi-Fi", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await ConnectAndWaitAsync(id, profile, ap, _shutdown.Token); Status("Connected to the selected access point."); await ScanAsync();
        }
        catch (Exception error) { Error(error); }
    }
    private async Task<Connection> ConnectAndWaitAsync(Guid id, string profile, AccessPoint ap, CancellationToken token)
    {
        _wifi!.Connect(id, profile, ap.Bssid);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(25))
        {
            await Task.Delay(700, token);
            try
            {
                var current = _wifi.Current(id);
                if (current?.SsidHex == ap.SsidHex && string.Equals(current.Bssid, ap.Bssid, StringComparison.OrdinalIgnoreCase))
                { await Task.Delay(1200, token); if (_wifi.Current(id)?.Bssid == current.Bssid) return current; }
            }
            catch (Win32Exception error) when (error.NativeErrorCode is 5023 or 1168) { }
        }
        throw new TimeoutException($"Windows did not associate with the requested {ap.BandLabel} access point. No result was attributed to this band.");
    }
    private void SetTesting(bool running)
    {
        SpeedButton.IsEnabled = CompareButton.IsEnabled = LatencyButton.IsEnabled = !running;
        CancelButton.IsEnabled = running; ScanButton.IsEnabled = !running; AdapterPicker.IsEnabled = !running;
        PlanButton.IsEnabled = !running && !_adminBusy; ApplyButton.IsEnabled = !running && !_adminBusy && _plan?.Changes.Count > 0;
    }
    private async void SpeedClick(object sender, RoutedEventArgs e)
    {
        if (_test is not null || _adminBusy || _scanGate.CurrentCount == 0) { Status("Wait for the current operation or scan to finish."); return; }
        try
        {
            if (_connection is null || CurrentAp() is not AccessPoint ap) throw new InvalidOperationException("Complete a scan while connected to Wi-Fi first.");
            _test = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token); SetTesting(true);
            var connection = _wifi!.Current(_connection.InterfaceId) ?? throw new InvalidOperationException("The connection was lost.");
            await RunSpeedAsync(connection, ap.Band, _test.Token);
        }
        catch (OperationCanceledException) { TestProgress.Text = "Test cancelled or timed out. No incomplete download result was saved."; }
        catch (Exception error) { TestProgress.Text = error.Message; Error(error); }
        finally { FinishTest(); }
    }
    private async Task<SpeedResult> RunSpeedAsync(Connection connection, WifiBand band, CancellationToken token, ThroughputOptions? requestedOptions = null)
    {
        var progress = new Progress<string>(message => TestProgress.Text = $"{RadioMath.Label(band)} · {message}");
        var options = requestedOptions ?? _settings.TestOptions with { Upload = IncludeUpload.IsChecked == true };
        var result = await MeasureWithBudgetAsync(connection, band, options, progress, token);
        var after = _wifi!.Current(connection.InterfaceId);
        if (after?.Bssid != connection.Bssid || after.SsidHex != connection.SsidHex)
            throw new InvalidOperationException("The access point changed during the test. The result was discarded to avoid assigning it to the wrong band.");
        if (_smokeTest) { _speeds.Insert(0, result); if (_speeds.Count > 250) _speeds.RemoveRange(250, _speeds.Count - 250); }
        else { var saved = JsonStore.AppendSpeed(result); _speeds.Clear(); _speeds.AddRange(saved); }
        MeasuredDownload.Text = $"{result.DownloadMbps:F1} Mbps";
        MeasuredUpload.Text = result.UploadMbps is double up ? $"{up:F1} Mbps" : options.Upload ? "Unavailable" : "Not tested";
        TestProgress.Text = $"{RadioMath.Label(band)} · {result.Streams} streams · {result.DownloadSeconds:F1}s download · {(result.DataLimited ? "payload cap reached" : "duration complete")} · {result.DownloadBytes / 1_000_000d:F1} MB ↓. " + result.UploadError;
        AppendHistory(new(result.Time, "Speed test", connection.InterfaceId, connection.Ssid, connection.Bssid, band,
            DownloadMbps: result.DownloadMbps, UploadMbps: result.UploadMbps, LatencyMs: result.LoadedLatency?.AverageMs,
            Detail: $"{result.Transport}; {result.DownloadSeconds:F1}s; {result.Streams} streams; cap reached: {result.DataLimited}"));
        RefreshHistory(); UpdateView();
        return result;
    }
    private async void CompareBandsClick(object sender, RoutedEventArgs e)
    {
        if (_test is not null || _adminBusy || _scanGate.CurrentCount == 0) { Status("Wait for the current operation or scan to finish."); return; }
        Connection? original = null; AccessPoint? originalAp = null;
        try
        {
            if (_connection is null || _connection.SsidHex != _targetHex || _wifi is null || CurrentAp() is not AccessPoint current)
                throw new InvalidOperationException("Connect to your target SSID and scan it before comparing bands.");
            var candidates = _networks.Where(p => p.SsidHex == _targetHex && p.InRegulatoryDomain)
                .GroupBy(p => p.Band).Where(g => g.Key != WifiBand.Unknown).Select(g => g.OrderByDescending(p => p.Rssi).First()).OrderBy(p => p.Band).ToArray();
            if (candidates.Length < 2) throw new InvalidOperationException("At least two bands of your connected SSID must be observed in the current scan.");
            if (MessageBox.Show(this, $"Test {candidates.Length} available bands in turn? This can briefly interrupt Wi-Fi and use up to {candidates.Length * _settings.TestOptions.MaxBytes / 1_000_000} MB of payload, plus protocol overhead.\n\nAirScope will verify the actual BSSID before each test, then restore the original connection. The daily quota is enforced for every band. Keep the app open until it finishes.", "Compare Wi-Fi bands", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            original = _wifi.Current(_connection.InterfaceId) ?? throw new InvalidOperationException("The connection was lost."); originalAp = current;
            _test = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token); _benchmark = true; SetTesting(true);
            List<string> skipped = [];
            foreach (var candidate in candidates)
            {
                _test.Token.ThrowIfCancellationRequested();
                TestProgress.Text = $"Associating with {candidate.BandLabel}…";
                try
                {
                    var connected = await ConnectAndWaitAsync(original.InterfaceId, original.Profile, candidate, _test.Token);
                    await RunSpeedAsync(connected, candidate.Band, _test.Token);
                }
                catch (Exception error) when (error is not OperationCanceledException) { skipped.Add($"{candidate.BandLabel}: {error.Message}"); }
            }
            TestProgress.Text = skipped.Count == 0 ? "Band comparison complete. Restoring the original connection…" : "Some bands could not be measured: " + string.Join("; ", skipped);
        }
        catch (OperationCanceledException) { TestProgress.Text = "Comparison cancelled. Restoring your original connection…"; }
        catch (Exception error) { TestProgress.Text = error.Message; Error(error); }
        finally
        {
            if (original is not null && originalAp is not null)
            {
                try
                {
                    using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await ConnectAndWaitAsync(original.InterfaceId, original.Profile, originalAp, restoreTimeout.Token);
                    TestProgress.Text += " Original connection restored.";
                }
                catch (Exception error)
                {
                    TestProgress.Text += " Original connection could not be verified: " + error.Message;
                    try { _wifi?.Connect(original.InterfaceId, original.Profile); } catch (Win32Exception fallback) { Status("Windows fallback reconnection failed: " + fallback.Message); }
                }
            }
            _benchmark = false; FinishTest(); if (!_exiting) await ScanAsync();
        }
    }
    private async void LatencyClick(object sender, RoutedEventArgs e)
    {
        if (_test is not null || _adminBusy) return;
        try
        {
            _test = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token); SetTesting(true); _latencies.Clear();
            var targets = new[] { HardwareService.Gateway(_adapterId), "1.1.1.1", "8.8.8.8" }.Where(t => t is not null).Select(t => t!).Distinct();
            foreach (string target in targets)
            {
                _latencies.Add(await DiagnosticsService.PingAsync(target, 10, new Progress<string>(p => TestProgress.Text = p), _test.Token));
                LatencyText.Text = string.Join("\n\n", _latencies.Select(l => $"{l.Target}: {l.AverageMs?.ToString("F1") ?? "—"} ms mean · {l.JitterMs?.ToString("F1") ?? "—"} ms jitter · {l.LossPercent:F0}% loss")) + "\n\nICMP uses Windows routing; filtering may affect replies.";
            }
            TestProgress.Text = "Latency probes complete.";
        }
        catch (OperationCanceledException) { TestProgress.Text = "Probes cancelled."; }
        catch (Exception error) { Error(error); }
        finally { FinishTest(); }
    }
    private async void DnsClick(object sender, RoutedEventArgs e)
    {
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); DnsText.Text = await DiagnosticsService.DnsAsync("www.sky.com", timeout.Token); }
        catch (Exception error) { DnsText.Text = "DNS lookup failed: " + error.Message; }
    }
    private void CancelTestClick(object sender, RoutedEventArgs e) => _test?.Cancel();
    private void FinishTest()
    {
        _test?.Dispose(); _test = null; SetTesting(false);
        if (_exitAfterTest) { _exiting = true; Close(); }
    }
    private void RefreshHistory() => SpeedHistoryTable.ItemsSource = _speeds.ToArray();
    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.AutoScan = AutoScanSetting.IsChecked == true; _settings.MinimiseToTray = TraySetting.IsChecked == true;
        _settings.ShowWidget = WidgetSetting.IsChecked == true; _settings.DesktopTuning = DesktopSetting.IsChecked == true;
        InvalidatePlan(); SaveSettings(); UpdateWidget();
    }
    private void SaveSettings()
    {
        if (_smokeTest || _workerMode) return;
        _settings.AdapterId = _adapterId; _settings.OwnSsidOnly = NetworkOwnOnly.IsChecked == true;
        try { JsonStore.Write("settings.json", _settings); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Status("Settings could not be saved: " + error.Message); }
    }
    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon { Text = "AirScope · Wi-Fi intelligence", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Information, Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMain);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open AirScope", null, (_, _) => Dispatcher.Invoke(ShowMain));
        menu.Items.Add("Toggle speed widget", null, (_, _) => Dispatcher.Invoke(() => WidgetSetting.IsChecked = !_settings.ShowWidget));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(() => { _exiting = true; Close(); })); _tray.ContextMenuStrip = menu;
    }
    private void ShowMain() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void UpdateWidget()
    {
        if (_smokeTest) return;
        if (_settings.ShowWidget && _widget is null)
        {
            _widget = new(ShowMain, () => WidgetSetting.IsChecked = false); _widget.Show();
        }
        else if (!_settings.ShowWidget && _widget is not null) { _widget.Close(); _widget = null; }
    }
    private void OnStateChanged(object? sender, EventArgs e)
    { if (WindowState == WindowState.Minimized && _settings.MinimiseToTray && _tray is not null) Hide(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_smokeTest && !_exiting && !_benchmark && !_adminBusy && _settings.Automation.BackgroundMode) { e.Cancel = true; Hide(); return; }
        if (_adminBusy) { e.Cancel = true; Status("Wait for the protected operation to finish before exiting."); return; }
        if (_benchmark && !_exiting) { e.Cancel = true; _exitAfterTest = true; _test?.Cancel(); Status("Cancelling the comparison and restoring the original connection before exiting…"); return; }
        _exiting = true; _timer.Stop(); _test?.Cancel(); _shutdown.Cancel();
        SaveSettings(); _tray?.Dispose(); _widget?.Close(); _wifi?.Dispose();
        Advanced.Dispose();
    }
    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    private void LocationClick(object sender, RoutedEventArgs e) => Open("ms-settings:privacy-location");
    private void SystemProtectionClick(object sender, RoutedEventArgs e) => Open("SystemPropertiesProtection.exe");
    private void SkyHelpClick(object sender, RoutedEventArgs e) => Open(Product.SkyHelp);
    private void RouterClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string gateway = HardwareService.Gateway(_adapterId) ?? throw new InvalidOperationException("No IPv4 gateway is available on the selected Wi-Fi adapter.");
            if (!System.Net.IPAddress.TryParse(gateway, out _)) throw new InvalidOperationException("Invalid gateway address.");
            Open("http://" + gateway);
        }
        catch (Exception error) { Error(error); }
    }
    private void OpenDataClick(object sender, RoutedEventArgs e) { Directory.CreateDirectory(JsonStore.Root); Open(JsonStore.Root); }
    private bool? IncludeIdentifiers()
    {
        var choice = MessageBox.Show(this, "Include SSIDs and access-point MAC addresses in this export?\n\nChoose No for a report with network and AP labels instead of identifiers.", "Report privacy", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
        return choice == MessageBoxResult.Cancel ? null : choice == MessageBoxResult.Yes;
    }
    private void ExportReportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            bool? identifiers = IncludeIdentifiers(); if (identifiers is null) return;
            var dialog = new SaveFileDialog { Filter = "HTML report (*.html)|*.html", FileName = $"AirScope-{DateTime.Now:yyyy-MM-dd-HHmm}.html" };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, ReportService.Html(_networks, _connection, _hardware, _speeds, _latencies, identifiers.Value), new UTF8Encoding(false));
            Status("Report exported. Open the HTML in a browser to print or save as PDF.");
        }
        catch (Exception error) { Error(error); }
    }
    private void ExportCsvClick(object sender, RoutedEventArgs e)
    {
        try
        {
            bool? identifiers = IncludeIdentifiers(); if (identifiers is null) return;
            var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"AirScope-access-points-{DateTime.Now:yyyy-MM-dd}.csv" };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, ReportService.Csv(_networks, identifiers.Value), new UTF8Encoding(true)); Status("Access points exported.");
        }
        catch (Exception error) { Error(error); }
    }

    public async Task SmokeTestAsync(bool live)
    {
        var args = Environment.GetCommandLineArgs();
        string output = args.SkipWhile(a => a != "--output").Skip(1).FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "AirScope-smoke.json");
        string directory = Path.GetDirectoryName(Path.GetFullPath(output))!; Directory.CreateDirectory(directory);
        var bindingErrors = new StringBuilder();
        using var listener = new TextWriterTraceListener(new StringWriter(bindingErrors));
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            if (live)
            {
                await ScanAsync();
                if (_adapterId is Guid id)
                {
                    try { SetHardware(await HardwareService.ReadAsync(id)); } catch (Exception error) { HardwareText.Text = error.Message; }
                    _traffic.Sample(id); await Task.Delay(1200); OnTick(null, EventArgs.Empty);
                    await Task.Delay(1200); OnTick(null, EventArgs.Empty);
                    if (args.Contains("--speed-test") && _connection is not null && CurrentAp() is AccessPoint ap)
                    {
                        int capArg = Array.IndexOf(args, "--test-mb");
                        if (capArg >= 0 && capArg + 1 < args.Length && int.TryParse(args[capArg + 1], out int mb) && mb is >= 1 and <= 256)
                            _settings.TestOptions = _settings.TestOptions with { MaxBytes = mb * 1_000_000L, SecondsPerDirection = 3 };
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
                        await RunSpeedAsync(_connection, ap.Band, timeout.Token);
                    }
                }
            }
            foreach (int page in Enumerable.Range(0, Pages.Items.Count))
            {
                var nav = FindNavigation(page); if (nav is not null) nav.IsChecked = true;
                Pages.SelectedIndex = page; UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render); await Task.Delay(80);
                var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                image.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(directory, $"page-{page}.private.png")); encoder.Save(file);
            }
            Pages.SelectedIndex = 7; UpdateLayout(); await Advanced.RenderSmokePagesAsync(directory); Pages.SelectedIndex = 0;
            var summary = new { success = true, pages = Pages.Items.Count, toolPages = Advanced.PageCount, live, accessPoints = _networks.Count, connected = _connection is not null,
                hardwareProperties = _hardware?.Properties.Count ?? 0, speedTests = _speeds.Count,
                realtimeSupported = _quality?.Supported, mlo = _quality?.Supported == true ? _quality.IsMlo : (bool?)null, connectedLinks = _quality?.Links.Count,
                realtimeDetail = _quality?.UnavailableReason, links = _quality?.Links,
                lastDownloadMbps = _speeds.FirstOrDefault()?.DownloadMbps, lastUploadMbps = _speeds.FirstOrDefault()?.UploadMbps,
                downloadSeconds = _speeds.FirstOrDefault()?.DownloadSeconds, downloadBytes = _speeds.FirstOrDefault()?.DownloadBytes,
                uploadBytes = _speeds.FirstOrDefault()?.UploadBytes, dataLimited = _speeds.FirstOrDefault()?.DataLimited,
                bindingErrors = bindingErrors.ToString() };
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(summary, JsonStore.Options));
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
    }
}
