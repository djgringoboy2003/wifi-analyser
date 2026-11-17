using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AirScope.Core;

namespace AirScope.Windows.Services;

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirScope");
    public static T? Read<T>(string file)
    {
        try { return ReadPath<T>(Path.Combine(Root, file)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return default; }
    }
    private static T? ReadPath<T>(string path)
    {
        if (!File.Exists(path)) return default;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, Options);
    }
    public static void Write<T>(string file, T value) => UpdatePath<T>(Path.Combine(Root, file), _ => value, readExisting: false);
    public static T Update<T>(string file, Func<T?, T> update) => UpdatePath(Path.Combine(Root, file), update);
    internal static T UpdatePath<T>(string path, Func<T?, T> update, bool readExisting = true)
    {
        using var transaction = LocalFileTransaction.Acquire(path);
        T value = update(readExisting ? ReadPath<T>(path) : default); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, Options)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return value;
    }
    public static IReadOnlyList<SpeedResult> AppendSpeed(SpeedResult result) => Update<List<SpeedResult>>("speed-history.json",
        history => new[] { result }.Concat(history ?? []).DistinctBy(s => (s.Time, s.Ssid, s.Bssid, s.Transport, s.Endpoint)).OrderByDescending(s => s.Time).Take(250).ToList());
}

internal static class LocalFileTransaction
{
    public static FileStream Acquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(3)) { Thread.Sleep(10); }
        }
    }
}

public sealed class UserSettings
{
    public static UserSettings Load()
    {
        var settings = JsonStore.Read<UserSettings>("settings.json") ?? new();
        settings.TestOptions ??= new(); settings.Automation ??= new();
        try { settings.TestOptions.Validate(); } catch (ArgumentException) { settings.TestOptions = new(); }
        try { settings.Automation.Validate(); } catch (ArgumentException) { settings.Automation = new(); }
        settings.RetentionDays = Math.Clamp(settings.RetentionDays, 1, 365); settings.ScanSeconds = Math.Clamp(settings.ScanSeconds, 15, 120);
        return settings;
    }
    public bool AutoScan { get; set; } = true;
    public bool MinimiseToTray { get; set; } = true;
    public bool ShowWidget { get; set; }
    public bool DesktopTuning { get; set; } = true;
    public bool OwnSsidOnly { get; set; }
    public Guid? AdapterId { get; set; }
    public string TargetSsid { get; set; } = "";
    public int ScanSeconds { get; set; } = 20;
    public ThroughputOptions TestOptions { get; set; } = new();
    public AutomationPolicy Automation { get; set; } = new();
    public int RetentionDays { get; set; } = 30;
    public double? ServiceMbps { get; set; }
    public string? UpdatePublisherThumbprint { get; set; }
}

public static class PowerShellRunner
{
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    public static async Task<string> RunAsync(string script, TimeSpan? timeout = null, CancellationToken token = default)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand");
        string preamble = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new(); ";
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::InputEncoding=[Text.UTF8Encoding]::new(); & ([ScriptBlock]::Create([Console]::In.ReadToEnd()))")));
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Windows PowerShell could not start.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(25));
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var errors = process.StandardError.ReadToEndAsync(limit.Token);
        string wrapped = "try { & { " + preamble + script + " } } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        await process.StandardInput.WriteAsync(wrapped.AsMemory(), limit.Token);
        process.StandardInput.Close();
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        string result = await output, error = await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Length > 0 ? CleanError(error) : $"Windows command exited with code {process.ExitCode}.");
        return result.Trim();
    }
    private static string CleanError(string error)
    {
        if (error.Contains("Access", StringComparison.OrdinalIgnoreCase) && error.Contains("denied", StringComparison.OrdinalIgnoreCase))
            return "Windows denied access. Use ‘Read protected hardware’ to request administrator access.";
        if (error.Contains("#< CLIXML"))
        {
            try
            {
                int start = error.IndexOf("<Objs", StringComparison.Ordinal);
                string plain = (start >= 0 ? error[..start] : error).Replace("#< CLIXML", "").Trim();
                if (plain.Length > 0) return plain.Length <= 1200 ? plain : plain[..1200];
                string xml = error[start..];
                var doc = System.Xml.Linq.XDocument.Parse(xml);
                string decoded = string.Join(" ", doc.Descendants().Where(e => e.Name.LocalName == "S" && e.Attribute("S")?.Value == "Error").Select(e => System.Xml.XmlConvert.DecodeName(e.Value)));
                if (!string.IsNullOrWhiteSpace(decoded)) return decoded.Length <= 1200 ? decoded : decoded[..1200];
            }
            catch (Exception e) when (e is System.Xml.XmlException or ArgumentOutOfRangeException) { }
        }
        return error.Length <= 700 ? error : error[..700];
    }
}

public static class HardwareService
{
    public static string Script(Guid id) => $$"""
        $id=[guid]'{{id:D}}';
        $adapter=Get-NetAdapter | Where-Object { [guid]$_.InterfaceGuid -eq $id } | Select-Object -First 1;
        if (!$adapter) { throw 'The selected adapter is no longer available.' };
        $properties=@(Get-NetAdapterAdvancedProperty -Name ([System.Management.Automation.WildcardPattern]::Escape($adapter.Name)) | ForEach-Object {
          [pscustomobject]@{DisplayName=$_.DisplayName;DisplayValue=$_.DisplayValue;RegistryKeyword=$_.RegistryKeyword;
          RegistryValue=@($_.RegistryValue | ForEach-Object { [string]$_ });ValidDisplayValues=@($_.ValidDisplayValues);
          ValidRegistryValues=@($_.ValidRegistryValues | ForEach-Object { [string]$_ })}
        });
        [pscustomobject]@{Id=$id.ToString('D');Name=$adapter.Name;Description=$adapter.InterfaceDescription;
        DriverVersion=[string]$adapter.DriverVersion;DriverDate=[string]$adapter.DriverDate;Manufacturer=[string]$adapter.DriverProvider;
        Properties=$properties} | ConvertTo-Json -Depth 6 -Compress
        """;
    public static async Task<AdapterDetails> ReadAsync(Guid id) => JsonSerializer.Deserialize<AdapterDetails>(await PowerShellRunner.RunAsync(Script(id)), JsonStore.Options)
        ?? throw new InvalidDataException("No adapter data was returned.");
    public static NetworkInterface? Find(Guid? id) => NetworkInterface.GetAllNetworkInterfaces()
        .FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && (id is null || Guid.TryParse(n.Id, out var value) && value == id));
    public static string? Gateway(Guid? id) => Find(id)?.GetIPProperties().GatewayAddresses
        .Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))?.ToString();
}

public sealed class TrafficMonitor
{
    private readonly TrafficCalculator _calculator = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public TrafficSample? Sample(Guid? id)
    {
        var adapter = HardwareService.Find(id);
        if (adapter is null || adapter.OperationalStatus != OperationalStatus.Up) return null;
        var stats = adapter.GetIPStatistics();
        return _calculator.Add(adapter.Id, stats.BytesReceived, stats.BytesSent, _clock.Elapsed.TotalSeconds, DateTimeOffset.Now);
    }
}

public static class DiagnosticsService
{
    public static async Task<LatencyResult> PingAsync(string target, int count, IProgress<string>? progress, CancellationToken token)
    {
        List<double?> values = [];
        using var ping = new Ping();
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(1200), new byte[32], null, token);
                values.Add(reply.Status == IPStatus.Success ? reply.RoundtripTime : null);
            }
            catch (PingException) { values.Add(null); }
            progress?.Report($"Latency probe {i + 1}/{count} · {target}");
            if (i + 1 < count) await Task.Delay(250, token);
        }
        return LatencyMath.Summarise(target, values);
    }
    public static async Task<string> DnsAsync(string host, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var addresses = await Dns.GetHostAddressesAsync(host, token);
        return $"{host} · {watch.Elapsed.TotalMilliseconds:F0} ms · {string.Join(", ", addresses.Select(a => a.ToString()))}";
    }
}
