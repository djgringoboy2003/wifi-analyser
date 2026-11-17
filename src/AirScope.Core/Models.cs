namespace AirScope.Core;

public enum WifiBand { Unknown, Band24, Band5, Band6 }

public sealed record AccessPoint
{
    public string Ssid { get; init; } = "";
    public string SsidHex { get; init; } = "";
    public string Bssid { get; init; } = "";
    public Guid InterfaceId { get; init; }
    public int FrequencyMhz { get; init; }
    public int Rssi { get; init; }
    public int SignalPercent { get; init; }
    public int? WidthMhz { get; init; }
    public int? CenterFrequencyMhz { get; init; }
    public string Standard { get; init; } = "Unknown";
    public string Security { get; init; } = "Unknown";
    public double? UtilisationPercent { get; init; }
    public int? StationCount { get; init; }
    public bool InRegulatoryDomain { get; init; }
    public DateTimeOffset SeenAt { get; init; } = DateTimeOffset.Now;
    public WifiBand Band => RadioMath.Band(FrequencyMhz);
    public int Channel => RadioMath.Channel(FrequencyMhz);
    public string BandLabel => RadioMath.Label(Band);
    public string WidthLabel => WidthMhz is int width ? $"{width} MHz" : "Not reported";
    public string LoadLabel => UtilisationPercent is double load ? $"{load:F0}%" : "Not advertised";
    public string Name => string.IsNullOrEmpty(Ssid) ? "Hidden network" : Ssid;
}

public sealed record WifiInterface(Guid Id, string Description, int State);
public sealed record Connection(Guid InterfaceId, string Profile, string Ssid, string SsidHex,
    string Bssid, int SignalPercent, double RxLinkMbps, double TxLinkMbps, string Standard, string Security);
public sealed record ScanResult(IReadOnlyList<WifiInterface> Interfaces, IReadOnlyList<AccessPoint> Networks,
    Connection? Connection, DateTimeOffset Timestamp, string? Error = null, bool PermissionDenied = false);
public sealed record ChannelRating(int Channel, int FrequencyMhz, WifiBand Band, double OverlapScore,
    int NearbyNetworks, bool Observed, string Note)
{
    public string ScoreLabel => $"{OverlapScore:F0}";
}
public sealed record TrafficSample(DateTimeOffset Time, double DownloadMbps, double UploadMbps, long ReceivedBytes, long SentBytes);
public sealed record LatencyResult(string Target, int Sent, int Received, double? AverageMs, double? JitterMs,
    double? MinMs, double? MaxMs)
{
    public double LossPercent => Sent == 0 ? 0 : 100d * (Sent - Received) / Sent;
}
public sealed record SpeedResult(DateTimeOffset Time, string Ssid, string Bssid, WifiBand Band, double DownloadMbps,
    double? UploadMbps, long DownloadBytes, long UploadBytes, string Endpoint, string? UploadError = null,
    double DownloadSeconds = 0, double UploadSeconds = 0, int Streams = 1, bool DataLimited = false,
    LatencyResult? IdleLatency = null, LatencyResult? LoadedLatency = null, string Transport = "HTTPS",
    long WarmupBytes = 0)
{
    public string BandLabel => RadioMath.Label(Band);
}
public sealed record AdvancedProperty(string DisplayName, string DisplayValue, string RegistryKeyword,
    string[] RegistryValue, string[] ValidDisplayValues, string[] ValidRegistryValues);
public sealed record AdapterDetails(Guid Id, string Name, string Description, string DriverVersion,
    string DriverDate, string Manufacturer, IReadOnlyList<AdvancedProperty> Properties);
public sealed record SettingChange(string Keyword, string Name, string Before, string After, string[] BeforeValues,
    string[] AfterValues, string Reason);
public sealed record OptimisationPlan(Guid AdapterId, string AdapterName, IReadOnlyList<SettingChange> Changes,
    IReadOnlyList<string> Advice, DateTimeOffset CreatedAt);
public sealed record RecoveryPoint(string Id, DateTimeOffset CreatedAt, string Adapter, string State,
    string Description, DateTimeOffset? RollbackAt = null, Guid? AdapterId = null);

public static class Product
{
    public const string Name = "AirScope";
    public const string Version = "0.2.1";
    public const string SkyHelp = "https://www.sky.com/help/articles/change-wifi-max-hub-settings";
    public const string SkyHub = "Sky Gigafast+ Hub · XER10";
}
