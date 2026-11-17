using System.Net;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AirScope.Core;
using AirScope.Windows.Services;

namespace AirScope.Windows.Controls;

public sealed partial class AdvancedWorkspace : UserControl, IDisposable
{
    private MainWindow? _owner;
    private readonly TabControl _tabs = new() { Background = Brushes.Transparent, BorderThickness = new(0), Foreground = Brushes.White };
    private readonly TextBlock _feedback = Text("Choose a tool. Measurements and changes start only when requested.");
    private readonly DataGrid _history = Table(("Time", "Time"), ("Event", "Kind"), ("Band", "Band"), ("Signal dBm", "Rssi"), ("Down Mbps", "DownloadMbps"), ("Up Mbps", "UploadMbps"), ("Details", "Detail"));
    private readonly DataGrid _findings = Table(("Area", "Area"), ("Status", "Status"), ("Confidence", "Confidence"), ("Evidence", "Evidence"), ("Next action", "NextAction"));
    private readonly ComboBox _days = Select("1", "7", "30", "90"), _historyKind = Select("Events and tests", "Signal samples", "Everything");
    private readonly CheckBox _historyOwn = new() { Content = "Current SSID only", IsChecked = true };
    private readonly HistoryChart _historyChart = new() { Height = 180 };
    private readonly TextBox _downUrl = Input("HTTPS download endpoint"), _upUrl = Input("HTTPS upload endpoint"), _lanEndpoint = Input("Private LAN peer IP:port"), _lanToken = Input("LAN peer token");
    private readonly TextBox _peerBind = Input("LAN peer listen IP", "127.0.0.1"), _peerPort = Input("LAN peer listen port", "49826"), _peerToken = Input("LAN peer access token", Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
    private readonly TextBlock _peerState = Text("Peer stopped. Run the peer on a suitably fast wired device to isolate Wi-Fi performance.");
    private readonly TextBlock _experimentStatus = Text("Load a plan, select one change, then record three baseline samples.");
    private readonly ComboBox _changes = new() { Width = 400, DisplayMemberPath = "Name" }, _profile = Select("Balanced", "Gaming", "VideoCalls", "Throughput");
    private readonly DataGrid _experimentRuns = Table(("Time", "Time"), ("Down Mbps", "DownloadMbps"), ("Up Mbps", "UploadMbps"), ("Download seconds", "DownloadSeconds"), ("Streams", "Streams"), ("Data-limited", "DataLimited"));
    private readonly TextBlock _quota = Text("");
    private CancellationTokenSource? _operation;
    private LanPeerService? _peer;
    private ExperimentRecord? _experiment;
    public int PageCount => _tabs.Items.Count;
    internal MainWindow Owner => _owner ?? throw new InvalidOperationException("The workspace is not attached.");

    public AdvancedWorkspace()
    {
        Foreground = Brushes.White;
        var root = new DockPanel { Margin = new(28, 22, 28, 20) };
        var heading = new StackPanel(); heading.Children.Add(Text("Understand. Compare. Improve.", 26)); heading.Children.Add(_quota);
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var feedback = new Border { Padding = new(12), Background = ColorBrush("#142A32"), CornerRadius = new(7), Margin = new(0, 10, 0, 0), Child = _feedback };
        DockPanel.SetDock(feedback, Dock.Bottom); root.Children.Add(feedback); root.Children.Add(_tabs); Content = root;
        _tabs.Items.Add(new TabItem { Header = "History", Content = HistoryPage() });
        _tabs.Items.Add(new TabItem { Header = "Diagnosis & LAN", Content = DiagnosticsPage() });
        _tabs.Items.Add(new TabItem { Header = "Experiments", Content = ExperimentsPage() });
        _tabs.Items.Add(new TabItem { Header = "Coverage", Content = CoveragePage() });
        _tabs.Items.Add(new TabItem { Header = "Automation", Content = AutomationPage() });
        _tabs.Items.Add(new TabItem { Header = "Updates", Content = UpdatesPage() });
    }
    internal void Attach(MainWindow owner)
    {
        _owner = owner; _days.SelectedIndex = 1; _downUrl.Text = owner.Settings.TestOptions.DownloadUrl; _upUrl.Text = owner.Settings.TestOptions.UploadUrl;
        LoadPolicy(); RefreshSummary();
    }
    internal void RefreshSummary()
    {
        if (_owner is null) return;
        if (Owner.IsSmoke) { _quota.Text = "Local history · reproducible comparisons · controlled automation"; return; }
        try { var usage = Owner.Usage.Today(DateTimeOffset.Now); _quota.Text = $"Today's test payload: {usage.UsedBytes / 1_000_000d:F1} / {Owner.Settings.Automation.DailyByteLimit / 1_000_000d:F0} MB · abandoned reservations stay charged until midnight"; }
        catch (Exception error) { _quota.Text = "Quota unavailable; scheduled tests are stopped: " + error.Message; }
    }
    internal void AutomationStatus(string message) => _autoStatus.Text = message;
    private FrameworkElement HistoryPage() => Page(
        Card("Connection history", Row(Labeled("Days", _days), Labeled("View", _historyKind), _historyOwn, AsyncButton("Refresh history", RefreshHistoryAsync)),
            Text("Signal plot averages up to 1,500 time buckets across the selected period. Table shows the latest 5,000 matching records; event filtering happens before that limit. Original ten-second samples remain in the local history files."), _historyChart),
        Card("Recorded observations", _history));
    private async Task RefreshHistoryAsync()
    {
        if (Owner.IsSmoke) return;
        int days = int.Parse(Selection(_days)), kind = _historyKind.SelectedIndex; string? ssid = _historyOwn.IsChecked == true ? Owner.Connected?.Ssid ?? Owner.Settings.TargetSsid : null;
        var records = await Task.Run(() => Owner.History.Read(days, predicate: r => (ssid is null || r.Ssid is null || r.Ssid == ssid)
            && (kind == 2 || (kind == 1 ? r.Kind == "Sample" : r.Kind != "Sample"))));
        _history.ItemsSource = records;
        _historyChart.Update(await Task.Run(() => Owner.History.SignalBuckets(days, ssid)), HistoryService.SignalBucketSeconds(days)); RefreshSummary();
    }
    private FrameworkElement DiagnosticsPage() => Page(
        Card("Explain the evidence", Row(SyncButton("Build diagnosis", () => _findings.ItemsSource = EvidenceEngine.Diagnose(Owner.Evidence())),
                AsyncButton("Run current-link test", () => RunMeasurementAsync(false))),
            Text("Findings describe likely causes and their confidence. A single short speed test cannot establish your connection's ceiling."), _findings),
        Card("Internet test endpoints", Labeled("HTTPS download URL (Cloudflare-compatible bytes parameter, or {bytes} placeholder)", _downUrl),
            Labeled("HTTPS upload URL (raw POST payload)", _upUrl), SyncButton("Save endpoints", SaveEndpoints)),
        Card("Test against a LAN peer", Row(Labeled("Peer IP:port", _lanEndpoint), Labeled("Token", _lanToken), AsyncButton("Test LAN connection", () => RunMeasurementAsync(true))),
            Text("Use a wired peer with enough Ethernet/CPU capacity. LAN tests use synthetic TCP payload, not internet data, but still share the app's test budget.")),
        Card("Run a peer on this computer", Row(Labeled("Listen IP", _peerBind), Labeled("Port", _peerPort)), Labeled("32-character access token", _peerToken),
            Row(AsyncButton("Start private peer", StartPeerAsync), AsyncButton("Stop peer", StopPeerAsync)), _peerState,
            Text("Private or loopback addresses only. The peer stops after 15 minutes and enforces a 4 GB payload budget. No firewall rule is added automatically; allow only the intended private network if Windows asks.")));
    private void SaveEndpoints()
    {
        var options = Owner.Settings.TestOptions with { DownloadUrl = _downUrl.Text.Trim(), UploadUrl = _upUrl.Text.Trim(), LanEndpoint = null, LanToken = null };
        options.Validate(); Owner.Settings.TestOptions = options; Owner.PersistSettings(); _feedback.Text = "HTTPS test endpoints saved.";
    }
    private async Task RunMeasurementAsync(bool lan)
    {
        using var stop = BeginOperation();
        var options = Owner.Settings.TestOptions;
        if (lan) options = options with { LanEndpoint = _lanEndpoint.Text.Trim(), LanToken = _lanToken.Text.Trim() };
        var result = await Owner.MeasureAsync(options, stop.Token);
        _feedback.Text = $"{result.Transport}: {result.DownloadMbps:F1} Mbps ↓, {result.UploadMbps?.ToString("F1") ?? "—"} Mbps ↑. Loaded TCP RTT: {result.LoadedLatency?.AverageMs?.ToString("F1") ?? "—"} ms.";
        _findings.ItemsSource = EvidenceEngine.Diagnose(Owner.Evidence()); RefreshSummary();
    }
    private Task StartPeerAsync()
    {
        if (_peer is not null) throw new InvalidOperationException("Stop the active peer first.");
        if (!IPAddress.TryParse(_peerBind.Text, out var bind) || !int.TryParse(_peerPort.Text, out int port)) throw new ArgumentException("Enter a private IP address and port.");
        _peer = new(bind, port, _peerToken.Text.Trim()); _peer.Start();
        _peerState.Text = "Listening at " + _peer.Endpoint + ". Share this endpoint and token only with the device you intend to test."; return Task.CompletedTask;
    }
    private async Task StopPeerAsync() { if (_peer is not null) await _peer.DisposeAsync(); _peer = null; _peerState.Text = "Peer stopped."; }
    private FrameworkElement ExperimentsPage() => Page(
        Card("One change, repeated evidence", Text("A controlled experiment needs six runs: three before and three during a protected trial. The daily budget must cover all six. Use at most 15 seconds per direction; short or unstable samples remain inconclusive."),
            Row(AsyncButton("Load planned changes", async () => { var plan = await Owner.PreparePlanAsync(); _changes.ItemsSource = plan.Changes; _changes.SelectedIndex = 0; _feedback.Text = $"{plan.Changes.Count} supported changes available."; }), Labeled("Objective", _profile)),
            Labeled("Setting to trial", _changes), Row(AsyncButton("1. Measure baseline", BaselineAsync), AsyncButton("2. Trial selected change", TrialAsync), SyncButton("Cancel measurements", CancelOperation)), _experimentStatus),
        Card("Experiment measurements", _experimentRuns));
    private async Task BaselineAsync()
    {
        if (_changes.SelectedItem is not SettingChange change) throw new InvalidOperationException("Load a plan and select a change first.");
        using var stop = BeginOperation();
        _experiment = await Owner.BaselineAsync(change, Enum.Parse<UsageProfile>(Selection(_profile)), stop.Token);
        _experimentStatus.Text = _experiment.State + ". Three trial runs are still required before a recommendation.";
        _experimentRuns.ItemsSource = _experiment.Baseline; RefreshSummary();
    }
    private async Task TrialAsync()
    {
        if (_experiment is null) throw new InvalidOperationException("Measure a baseline first.");
        using var stop = BeginOperation(); _experiment = await Owner.TrialAsync(_experiment, false, stop.Token);
        _experimentRuns.ItemsSource = _experiment.Baseline.Concat(_experiment.Trial).ToArray();
        _experimentStatus.Text = _experiment.State + ". " + _experiment.Comparison?.Explanation
            + (_experiment.Comparison is { } result ? $"\nMedian change {result.GainPercent:F1}%; descriptive 95% resampled range {result.LowerGainPercent:F1}% to {result.UpperGainPercent:F1}%." : "");
        RefreshSummary();
    }
    private CancellationTokenSource BeginOperation()
    {
        if (_operation is not null)
        {
            try { _ = _operation.Token; throw new InvalidOperationException("Wait for the active workspace operation to finish, or cancel it."); }
            catch (ObjectDisposedException) { }
        }
        _operation = new(); return _operation;
    }
    private void CancelOperation() { try { _operation?.Cancel(); } catch (ObjectDisposedException) { } }
    private Button AsyncButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new(0, 5, 9, 5) };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) { _feedback.Text = "Operation cancelled. Protected trials retain independent rollback."; }
            catch (Exception error) { _feedback.Text = error.Message; }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
    private Button SyncButton(string label, Action action) => AsyncButton(label, () => { action(); return Task.CompletedTask; });
    private static TextBlock Text(string value, double size = 13) => new() { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size, Foreground = ColorBrush(size >= 20 ? "#EDF4FC" : "#A7BCD3"), Margin = new(0, 4, 0, 8), LineHeight = size >= 20 ? 34 : 21 };
    private static SolidColorBrush ColorBrush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    private static TextBox Input(string name, string value = "")
    { var input = new TextBox { Text = value, MinWidth = 110, MaxWidth = 900, Margin = new(0, 0, 8, 8) }; AutomationProperties.SetName(input, name); input.ToolTip = name; return input; }
    private static ComboBox Select(params string[] values) => new() { ItemsSource = values, SelectedIndex = 0, Margin = new(0, 0, 10, 8), MinWidth = 150 };
    private static string Selection(ComboBox combo) => combo.SelectedItem?.ToString() ?? "";
    private static FrameworkElement Labeled(string label, FrameworkElement input)
    { var panel = new StackPanel { Margin = new(0, 0, 10, 0) }; panel.Children.Add(Text(label, 11)); panel.Children.Add(input); return panel; }
    private static WrapPanel Row(params UIElement[] children) { var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center }; foreach (var child in children) row.Children.Add(child); return row; }
    private static Border Card(string title, params UIElement[] children)
    {
        var stack = new StackPanel(); stack.Children.Add(Text(title, 18)); foreach (var child in children) stack.Children.Add(child);
        return new() { Background = ColorBrush("#101B2A"), BorderBrush = ColorBrush("#233247"), BorderThickness = new(1), CornerRadius = new(12), Padding = new(18), Margin = new(0, 14, 0, 0), Child = stack };
    }
    private static ScrollViewer Page(params UIElement[] children)
    { var stack = new StackPanel { Margin = new(0, 4, 8, 8) }; foreach (var child in children) stack.Children.Add(child); return new() { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; }
    private static DataGrid Table(params (string Header, string Binding)[] columns)
    {
        var table = new DataGrid { Height = 250, AutoGenerateColumns = false, IsReadOnly = true, RowHeight = double.NaN, MinRowHeight = 42 };
        var text = new Style(typeof(TextBlock)); text.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); text.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.White));
        foreach (var (header, binding) in columns) table.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(binding), Width = new(1, DataGridLengthUnitType.Star), ElementStyle = text });
        return table;
    }
    internal async Task RenderSmokePagesAsync(string directory)
    {
        for (int i = 0; i < _tabs.Items.Count; i++)
        {
            _tabs.SelectedIndex = i; UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(this); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(directory, $"tools-{i}.private.png")); encoder.Save(file);
        }
        _tabs.SelectedIndex = 0;
    }
    public void Dispose() { try { _operation?.Cancel(); } catch (ObjectDisposedException) { } _operation?.Dispose(); if (_peer is not null) _peer.DisposeAsync().AsTask().GetAwaiter().GetResult(); _updates.Dispose(); }
}
