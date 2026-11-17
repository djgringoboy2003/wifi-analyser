using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using AirScope.Core;
using AirScope.Windows.Services;
using Microsoft.Win32;

namespace AirScope.Windows.Controls;

public sealed partial class AdvancedWorkspace
{
    private readonly CoverageMap _map = new() { Height = 340, Margin = new(0, 8, 0, 8) };
    private readonly TextBox _surveyName = Input("Survey name", "Home coverage"), _room = Input("Room or location label", "Room"), _retention = Input("History retention days", "30"), _service = Input("Subscribed service Mbps (optional)");
    private readonly ComboBox _surveyBand = Select("All bands", "2.4 GHz", "5 GHz", "6 GHz");
    private readonly DataGrid _surveyTable = Table(("Location", "Room"), ("Time", "Time"), ("Band", "Band"), ("Signal dBm", "Rssi"), ("AP width MHz", "AdvertisedWidthMhz"), ("Connected MHz", "ConnectedWidthMhz"), ("Measured Mbps", "DownloadMbps"));
    private CoverageSurvey _survey = new();
    private (double X, double Y)? _location;
    private readonly CheckBox _enabled = new() { Content = "Enable scheduled diagnostics" }, _signIn = new() { Content = "Start lightweight monitoring at sign-in" },
        _background = new() { Content = "Keep monitoring in the tray when the window closes" }, _disruptive = new() { Content = "Allow protected one-setting trials in the maintenance window" },
        _autoKeep = new() { Content = "Automatically keep only trials that pass all evidence and health checks" }, _alerts = new() { Content = "Notify me about sustained degradation" };
    private readonly TextBox _interval = Input("Scheduled test interval minutes", "60"), _idle = Input("Minimum idle minutes", "5"), _start = Input("Maintenance window start hour", "1"), _end = Input("Maintenance window end hour", "6"),
        _daily = Input("Daily payload budget MB", "1000"), _cooldown = Input("Disruptive trial cooldown minutes", "240");
    private readonly TextBlock _autoStatus = Text("Scheduled testing is off until you enable and save it."), _recoveryStatus = Text("Preflight checks are read-only. Actual restore-point creation is verified again for every protected trial.");
    private readonly DataGrid _preflight = Table(("Check", "Name"), ("Passed", "Passed"), ("Detail", "Detail"));
    private readonly UpdateService _updates = new();
    private readonly TextBox _publisher = Input("Trusted publisher certificate thumbprint (optional)");
    private readonly TextBlock _updateState = Text("Updates are checked only when you request them. Applying an update requires a valid signature from your pinned publisher.");
    private ReleaseManifest? _release; private StagedUpdate? _staged;

    private FrameworkElement CoveragePage()
    {
        _map.LocationSelected += (x, y) => { _location = (x, y); _feedback.Text = $"Location selected: {x:P0}, {y:P0}. Enter a room name and capture your SSID."; };
        _surveyBand.SelectionChanged += (_, _) => RefreshSurvey();
        return Page(Card("A map of measured coverage", Row(Labeled("Survey name", _surveyName), Labeled("View", _surveyBand)),
            Row(SyncButton("Import floor plan", ImportFloorPlan), SyncButton("Load survey", LoadSurvey), SyncButton("Save survey", SaveSurvey), SyncButton("Export map (SVG)", ExportMap)),
            Text("Click the map, label the location, then capture your connected SSID. Coloured markers are measurements at those locations; unmeasured space is not estimated."),
            _map, Row(Labeled("Location / room", _room), SyncButton("Capture this location", CaptureLocation)), Text("Mint ≥ −55 dBm · blue ≥ −67 · amber ≥ −75 · red weaker. Speed is attached only to a recent test of that exact AP.")),
            Card("Survey observations", _surveyTable));
    }
    private void RefreshSurvey()
    {
        if (_surveyTable is null || _map is null) return;
        WifiBand? band = _surveyBand.SelectedIndex > 0 ? (WifiBand)_surveyBand.SelectedIndex : null;
        var points = _survey.Observations.Where(o => band is null || o.Band == band).ToArray();
        _map.Update(points); _surveyTable.ItemsSource = points.OrderByDescending(p => p.Time).ToArray();
    }
    private void ImportFloorPlan()
    {
        var dialog = new OpenFileDialog { Filter = "Floor plan image|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (new FileInfo(dialog.FileName).Length > 20_000_000) throw new IOException("Choose an image smaller than 20 MB.");
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1600; bitmap.UriSource = new(dialog.FileName); bitmap.EndInit(); bitmap.Freeze();
        _map.FloorPlan = bitmap;
        if (!Owner.IsSmoke)
        {
            string directory = Path.Combine(JsonStore.Root, "surveys"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, _survey.Id + ".png");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file); _survey.FloorPlanPath = path;
        }
        _map.InvalidateVisual();
    }
    private void CaptureLocation()
    {
        if (_location is not { } location || Owner.Connected is not Connection current) throw new InvalidOperationException("Select a map location while connected to Wi-Fi first.");
        var points = Owner.AccessPoints.Where(p => p.SsidHex == current.SsidHex).GroupBy(p => p.Band).Select(g => g.OrderByDescending(p => p.Rssi).First()).ToArray();
        if (points.Length == 0 || points.All(p => DateTimeOffset.Now - p.SeenAt > TimeSpan.FromSeconds(45))) throw new InvalidOperationException("Scan recently at this location before capturing it.");
        foreach (var p in points.Where(p => DateTimeOffset.Now - p.SeenAt <= TimeSpan.FromSeconds(45)))
        {
            var speed = Owner.Measurements.FirstOrDefault(s => s.Bssid == p.Bssid && DateTimeOffset.Now - s.Time < TimeSpan.FromMinutes(2));
            var link = p.Bssid == current.Bssid ? Owner.LinkQuality?.Links.FirstOrDefault(l => l.Band == p.Band) : null;
            _survey.Observations.Add(new(DateTimeOffset.Now, location.X, location.Y, _room.Text.Trim(), p.Ssid, p.Bssid, p.Band, p.Rssi,
                p.WidthMhz, speed?.DownloadMbps, speed?.LoadedLatency?.AverageMs, link?.WidthMhz));
        }
        RefreshSurvey(); SaveSurvey();
    }
    private void SaveSurvey()
    {
        if (Owner.IsSmoke) return;
        _survey.Name = _surveyName.Text.Trim(); Directory.CreateDirectory(Path.Combine(JsonStore.Root, "surveys"));
        JsonStore.Write("surveys/" + _survey.Id + ".json", _survey); _feedback.Text = "Survey saved locally with its floor plan and measured locations.";
    }
    private void LoadSurvey()
    {
        var dialog = new OpenFileDialog { Filter = "AirScope survey (*.json)|*.json", InitialDirectory = Path.Combine(JsonStore.Root, "surveys") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (new FileInfo(dialog.FileName).Length > 5_000_000) throw new IOException("Survey is too large.");
        var survey = JsonSerializer.Deserialize<CoverageSurvey>(File.ReadAllText(dialog.FileName), JsonStore.Options) ?? throw new InvalidDataException("Invalid survey.");
        if (!Guid.TryParseExact(survey.Id, "D", out _) || survey.Observations.Count > 10000
            || survey.Observations.Any(o => o.X is < 0 or > 1 || o.Y is < 0 or > 1 || !double.IsFinite(o.X) || !double.IsFinite(o.Y) || o.Rssi is < -127 or > 0))
            throw new InvalidDataException("Survey contains invalid identifiers, coordinates or signal values.");
        string local = Path.GetFullPath(Path.Combine(JsonStore.Root, "surveys", survey.Id + ".json"));
        bool trustedLocal = Path.GetFullPath(dialog.FileName).Equals(local, StringComparison.OrdinalIgnoreCase);
        survey.FloorPlanPath = trustedLocal ? Path.Combine(JsonStore.Root, "surveys", survey.Id + ".png") : null;
        if (!trustedLocal) survey.Id = Guid.NewGuid().ToString("D");
        _survey = survey; _surveyName.Text = survey.Name; _map.FloorPlan = null;
        if (survey.FloorPlanPath is string image && File.Exists(image))
        { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new(image); bitmap.EndInit(); bitmap.Freeze(); _map.FloorPlan = bitmap; }
        RefreshSurvey();
    }
    private void ExportMap()
    {
        var dialog = new SaveFileDialog { Filter = "SVG map (*.svg)|*.svg", FileName = "AirScope-coverage.svg" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        XNamespace svg = "http://www.w3.org/2000/svg";
        var image = new XElement(svg + "svg", new XAttribute("viewBox", "0 0 1000 650"), new XAttribute("width", 1000), new XAttribute("height", 650),
            new XElement(svg + "rect", new XAttribute("width", 1000), new XAttribute("height", 650), new XAttribute("fill", "#101b2a")),
            new XElement(svg + "text", new XAttribute("x", 30), new XAttribute("y", 40), new XAttribute("fill", "white"), new XAttribute("font-size", 22), _survey.Name));
        if (_map.FloorPlan is BitmapSource plan)
        { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(plan)); using var memory = new MemoryStream(); encoder.Save(memory); image.Add(new XElement(svg + "image", new XAttribute("x", 30), new XAttribute("y", 70), new XAttribute("width", 940), new XAttribute("height", 500), new XAttribute("preserveAspectRatio", "none"), new XAttribute("href", "data:image/png;base64," + Convert.ToBase64String(memory.ToArray())))); }
        foreach (var p in _survey.Observations.Where(o => _surveyBand.SelectedIndex == 0 || o.Band == (WifiBand)_surveyBand.SelectedIndex))
        {
            double x = 30 + p.X * 940, y = 70 + p.Y * 500;
            image.Add(new XElement(svg + "circle", new XAttribute("cx", x), new XAttribute("cy", y), new XAttribute("r", 10), new XAttribute("fill", CoverageMap.SignalColor(p.Rssi))));
            image.Add(new XElement(svg + "text", new XAttribute("x", x + 14), new XAttribute("y", y), new XAttribute("fill", "white"), new XAttribute("font-size", 12), $"{p.Room} · {RadioMath.Label(p.Band)} · {p.Rssi} dBm"));
        }
        image.Add(new XElement(svg + "text", new XAttribute("x", 30), new XAttribute("y", 610), new XAttribute("fill", "white"), "Measured locations only. Includes room labels and the imported floor plan; SSIDs and MAC addresses are omitted."));
        new XDocument(image).Save(dialog.FileName); _feedback.Text = "Coverage SVG exported. Room labels and floor plan are included; network identifiers are omitted.";
    }
    private FrameworkElement AutomationPage() => Page(
        Card("Run on your terms", _enabled, _signIn, _background, _alerts,
            Row(Labeled("Interval minutes", _interval), Labeled("Idle minutes", _idle), Labeled("Daily payload MB", _daily)),
            Row(Labeled("Window start (0–23)", _start), Labeled("Window end (0–23)", _end), Labeled("Trial cooldown minutes", _cooldown)),
            _disruptive, _autoKeep, Text("Equal start/end hours means all day. Internet tests reserve quota before they run. Disruptive trials also require a signed, protected worker, a fresh restore point and the independent rollback task."),
            Row(SyncButton("Save automation policy", SavePolicy), AsyncButton("Configure protected worker", ConfigureWorkerAsync), AsyncButton("Remove protected worker", RemoveWorkerAsync)), _autoStatus),
        Card("History and service context", Row(Labeled("Retain days (1–365)", _retention), Labeled("Service speed Mbps (optional)", _service)), SyncButton("Save history settings", SaveHistorySettings)),
        Card("Recovery readiness", Row(AsyncButton("Read preflight", PreflightAsync), AsyncButton("Read protected preflight", ProtectedPreflightAsync)), _recoveryStatus, _preflight));
    private void LoadPolicy()
    {
        var p = Owner.Settings.Automation; _enabled.IsChecked = p.Enabled; _signIn.IsChecked = p.RunAtSignIn; _background.IsChecked = p.BackgroundMode;
        _profile.SelectedItem = p.Profile.ToString();
        _alerts.IsChecked = p.NotifyDegradation; _disruptive.IsChecked = p.AllowDisruptiveTrials; _autoKeep.IsChecked = p.AutoKeepSupportedTrials;
        _interval.Text = p.IntervalMinutes.ToString(); _idle.Text = p.MinimumIdleMinutes.ToString(); _daily.Text = (p.DailyByteLimit / 1_000_000).ToString();
        _start.Text = p.WindowStartHour.ToString(); _end.Text = p.WindowEndHour.ToString(); _cooldown.Text = p.CooldownMinutes.ToString();
        _retention.Text = Owner.Settings.RetentionDays.ToString(); _service.Text = Owner.Settings.ServiceMbps?.ToString(CultureInfo.CurrentCulture) ?? "";
        _publisher.Text = Owner.Settings.UpdatePublisherThumbprint ?? "";
    }
    private void SavePolicy()
    {
        var policy = new AutomationPolicy { Enabled = _enabled.IsChecked == true, RunAtSignIn = _signIn.IsChecked == true, BackgroundMode = _background.IsChecked == true,
            NotifyDegradation = _alerts.IsChecked == true, AllowDisruptiveTrials = _disruptive.IsChecked == true, AutoKeepSupportedTrials = _autoKeep.IsChecked == true,
            IntervalMinutes = int.Parse(_interval.Text), MinimumIdleMinutes = int.Parse(_idle.Text), DailyByteLimit = checked(long.Parse(_daily.Text) * 1_000_000),
            WindowStartHour = int.Parse(_start.Text), WindowEndHour = int.Parse(_end.Text), CooldownMinutes = int.Parse(_cooldown.Text), Profile = Enum.Parse<UsageProfile>(Selection(_profile)) };
        policy.Validate(); if (policy.RunAtSignIn != Owner.Settings.Automation.RunAtSignIn && !Owner.IsSmoke) DesktopIntegration.SetStartAtSignIn(policy.RunAtSignIn);
        Owner.Settings.Automation = policy; Owner.PersistSettings(); _autoStatus.Text = "Policy saved. Protected worker configuration is a separate explicit administrator action.";
    }
    private void SaveHistorySettings()
    {
        int days = int.Parse(_retention.Text); if (days is < 1 or > 365) throw new ArgumentException("Retention must be 1–365 days.");
        double? speed = string.IsNullOrWhiteSpace(_service.Text) ? null : double.Parse(_service.Text);
        if (speed is <= 0 || speed > 100000 || speed.HasValue && !double.IsFinite(speed.Value)) throw new ArgumentException("Enter a positive service speed up to 100000 Mbps, or leave blank.");
        Owner.Settings.RetentionDays = days; Owner.Settings.ServiceMbps = speed; Owner.PersistSettings(); if (!Owner.IsSmoke) Owner.History.Prune(days);
    }
    private async Task PreflightAsync()
    {
        if (Owner.Connected is not Connection c) throw new InvalidOperationException("Connect and scan Wi-Fi first.");
        var report = await RecoveryHealthService.PreflightAsync(c.InterfaceId); ShowPreflight(report);
    }
    private async Task ProtectedPreflightAsync() => ShowPreflight(await Owner.ProtectedPreflightAsync());
    private void ShowPreflight(RecoveryPreflight report) { _preflight.ItemsSource = report.Checks; _recoveryStatus.Text = report.Ready ? "Preflight passed. Actual restore-point creation and health are verified again before applying." : "Some prerequisites are unavailable. No adapter settings were changed."; }
    private async Task ConfigureWorkerAsync() { SavePolicy(); _autoStatus.Text = await Owner.ConfigureWorkerAsync(remove: false); }
    private async Task RemoveWorkerAsync() => _autoStatus.Text = await Owner.ConfigureWorkerAsync(remove: true);

    private FrameworkElement UpdatesPage() => Page(Card("Trusted updates", Labeled("Pinned publisher certificate thumbprint", _publisher),
        Row(SyncButton("Save publisher pin", SavePublisher), AsyncButton("Check GitHub release", CheckUpdateAsync), AsyncButton("Download and verify", DownloadUpdateAsync), AsyncButton("Apply verified update", ApplyUpdateAsync)),
        _updateState, Text("SHA-256 detects download corruption against the release manifest. Automatic application additionally requires a trusted Authenticode signature matching your saved publisher pin. An unsigned build cannot satisfy that check.")),
        Card("Installation and accessibility", Text("The release includes per-user installation/uninstallation tools. Windows sign-in and background monitoring are optional. Keyboard focus, labelled inputs and per-monitor DPI scaling are supported."),
            Row(SyncButton("Larger text", () => Window.GetWindow(this).FontSize = 16), SyncButton("Default text", () => Window.GetWindow(this).FontSize = 13))));
    private void SavePublisher()
    {
        string pin = _publisher.Text.Replace(" ", "").Trim();
        if (pin.Length != 0 && (pin.Length != 40 || !pin.All(Uri.IsHexDigit))) throw new ArgumentException("Enter the 40-character Windows certificate thumbprint, or leave empty.");
        Owner.Settings.UpdatePublisherThumbprint = pin.Length == 0 ? null : pin.ToUpperInvariant(); Owner.PersistSettings();
    }
    private async Task CheckUpdateAsync() { _release = await _updates.CheckAsync(CancellationToken.None); _updateState.Text = _release is null ? "No newer release with an update manifest is available." : $"Version {_release.Version}: {_release.Notes}"; }
    private async Task DownloadUpdateAsync()
    {
        if (_release is null) throw new InvalidOperationException("Check for a newer release first.");
        using var stop = BeginOperation(); _staged = await _updates.DownloadAsync(_release, Owner.Settings.UpdatePublisherThumbprint, new Progress<string>(s => _updateState.Text = s), stop.Token);
        _updateState.Text = _staged.Detail;
    }
    private async Task ApplyUpdateAsync()
    {
        if (_staged is null || !_staged.PublisherVerified) throw new InvalidOperationException("Download an update with a verified pinned publisher first.");
        if (Owner.Busy || ElevationService.ListRecoveryPoints().Any(p => p.State is "Pending" or "Applying" or "RollbackFailed")) throw new InvalidOperationException("Resolve active tests and recovery trials before updating.");
        if (MessageBox.Show(Window.GetWindow(this), "Close AirScope and apply the verified update? The previous executable is retained as a fallback.", "Apply update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        string script = await UpdateService.PrepareApplyScriptAsync(_staged, Owner.Settings.UpdatePublisherThumbprint!);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"" };
        Process.Start(start); Owner.ExitForUpdate();
    }
}
