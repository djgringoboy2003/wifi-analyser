namespace AirScope.Core;

public sealed record ConnectedLink(int LinkId, int CenterFrequencyMhz, int? WidthMhz, int Rssi, uint? ReportedWidthCode = null)
{
    public WifiBand Band => RadioMath.Band(CenterFrequencyMhz);
    public string BandLabel => RadioMath.Label(Band);
    public string WidthLabel => WidthMhz.HasValue ? WidthMhz + " MHz" : "Not reported in recognised units";
}
public sealed record RealtimeQuality(bool Supported, bool IsMlo, int SignalPercent, double RxMbps, double TxMbps,
    string Standard, IReadOnlyList<ConnectedLink> Links, string? UnavailableReason = null);
public sealed record WifiEvent(DateTimeOffset Time, Guid InterfaceId, string Kind, string Message, uint? ReasonCode = null);

public enum UsageProfile { Balanced, Gaming, VideoCalls, Throughput }
public sealed record ThroughputOptions
{
    public int SecondsPerDirection { get; init; } = 10;
    public int Streams { get; init; } = 4;
    public long MaxBytes { get; init; } = 256_000_000;
    public bool Upload { get; init; } = true;
    public bool PreferIpv6 { get; init; }
    public string DownloadUrl { get; init; } = "https://speed.cloudflare.com/__down";
    public string UploadUrl { get; init; } = "https://speed.cloudflare.com/__up";
    public string? LanEndpoint { get; init; }
    public string? LanToken { get; init; }
    public void Validate()
    {
        if (SecondsPerDirection is < 2 or > 30 || Streams is < 1 or > 8 || MaxBytes is < 1_000_000 or > 4_000_000_000)
            throw new ArgumentException("Choose 2–30 seconds, 1–8 streams and a total data cap between 1 MB and 4 GB.");
        if (LanEndpoint is not null)
        {
            if (string.IsNullOrWhiteSpace(LanToken) || LanToken.Length != 32 || !LanToken.All(Uri.IsHexDigit))
                throw new ArgumentException("LAN peer tokens must contain 32 hexadecimal characters.");
        }
        else foreach (string url in new[] { DownloadUrl, UploadUrl })
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("Internet test endpoints must be HTTPS URLs without embedded credentials.");
        }
    }
}

public sealed record MeasurementProgress(string Phase, long Bytes, double ElapsedSeconds, double Mbps, long RemainingBytes);
public sealed record HistoryEntry(DateTimeOffset Time, string Kind, Guid AdapterId, string? Ssid = null,
    string? Bssid = null, WifiBand Band = WifiBand.Unknown, int? Rssi = null, double? RxLinkMbps = null,
    double? DownloadMbps = null, double? UploadMbps = null, double? LatencyMs = null, string? Detail = null);
public sealed record DiagnosticFinding(string Area, string Status, string Confidence, string Evidence, string NextAction);
public sealed record DiagnosticEvidence(Connection? Connection, AccessPoint? AccessPoint, RealtimeQuality? Quality,
    LatencyResult? Gateway, LatencyResult? Internet, SpeedResult? InternetSpeed, SpeedResult? LanSpeed, double? PlanMbps = null);
public sealed record ComparisonResult(string Verdict, bool RecommendKeep, double MedianBefore, double MedianAfter,
    double GainPercent, double LowerGainPercent, double UpperGainPercent, string Explanation);
public sealed record TrialMeasurement(SpeedResult Result, UsageProfile Profile, string DriverVersion, string ConfigurationHash);
public sealed record ExperimentRecord(string Id, DateTimeOffset CreatedAt, Guid AdapterId, string Ssid,
    string Bssid, string DriverVersion, UsageProfile Profile, SettingChange Change,
    IReadOnlyList<SpeedResult> Baseline, IReadOnlyList<SpeedResult> Trial, string? RecoveryId,
    string State, ComparisonResult? Comparison = null, ThroughputOptions? Options = null, string? ConfigurationHash = null);

public sealed class AutomationPolicy
{
    public bool Enabled { get; set; }
    public bool RunAtSignIn { get; set; }
    public bool BackgroundMode { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    public int MinimumIdleMinutes { get; set; } = 5;
    public int WindowStartHour { get; set; } = 1;
    public int WindowEndHour { get; set; } = 6;
    public long DailyByteLimit { get; set; } = 1_000_000_000;
    public bool AllowDisruptiveTrials { get; set; }
    public bool AutoKeepSupportedTrials { get; set; }
    public int CooldownMinutes { get; set; } = 240;
    public bool NotifyDegradation { get; set; } = true;
    public UsageProfile Profile { get; set; } = UsageProfile.Balanced;
    public void Validate()
    {
        if (IntervalMinutes is < 15 or > 1440 || MinimumIdleMinutes is < 1 or > 240 || WindowStartHour is < 0 or > 23
            || WindowEndHour is < 0 or > 23 || DailyByteLimit is < 16_000_000 or > 20_000_000_000 || CooldownMinutes is < 60 or > 10080)
            throw new ArgumentException("Automation policy is outside the supported interval, idle, time-window, quota or cooldown range.");
    }
}
public sealed record AutomationDecision(bool Run, string Reason, long RemainingBytes);
public sealed record RecoveryCheck(string Name, bool Passed, string Detail);
public sealed record RecoveryPreflight(IReadOnlyList<RecoveryCheck> Checks, DateTimeOffset Time)
{
    public bool Ready => Checks.Count > 0 && Checks.All(c => c.Passed);
}
public sealed record SurveyObservation(DateTimeOffset Time, double X, double Y, string Room, string Ssid,
    string Bssid, WifiBand Band, int Rssi, int? AdvertisedWidthMhz, double? DownloadMbps = null,
    double? LatencyMs = null, int? ConnectedWidthMhz = null);
public sealed class CoverageSurvey
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = "Home coverage";
    public string? FloorPlanPath { get; set; }
    public List<SurveyObservation> Observations { get; set; } = [];
}
public sealed record ReleaseManifest(string Version, string Url, string Sha256, string FileName,
    string? PublisherThumbprint = null, string? Notes = null);
