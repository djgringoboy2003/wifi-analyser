using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AirScope.Core;

namespace AirScope.Windows.Services;

public sealed class SpeedTestService
{
    public async Task<SpeedResult> RunAsync(Connection connection, WifiBand band, bool upload, IProgress<string> progress, CancellationToken token)
    {
        using var http = CreateClient(connection.InterfaceId);
        // Bounded transfers: one 32 MB warmup/probe, then a larger sample only if useful.
        // Maximum download: 128 MB. Maximum upload: 16 MB.
        progress.Report("Download · measuring HTTPS payload throughput…");
        long received = 0;
        double seconds = 0;
        foreach (int size in new[] { 32_000_000, 96_000_000 })
        {
            var sample = await DownloadAsync(http, size, progress, token);
            received += sample.Bytes; seconds += sample.Seconds;
            if (seconds >= 10) break;
        }
        double? up = null;
        string? upError = null;
        long uploaded = 0;
        if (upload)
        {
            progress.Report("Upload · sending 16 MB of random test data…");
            try
            {
                var payload = new byte[16_000_000];
                System.Security.Cryptography.RandomNumberGenerator.Fill(payload);
                using var body = new ByteArrayContent(payload);
                body.Headers.ContentType = new("application/octet-stream");
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(45));
                var clock = Stopwatch.StartNew();
                using var result = await http.PostAsync("https://speed.cloudflare.com/__up", body, limit.Token);
                result.EnsureSuccessStatusCode();
                uploaded = payload.Length;
                up = uploaded * 8d / clock.Elapsed.TotalSeconds / 1_000_000;
            }
            catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException && !token.IsCancellationRequested)
            { upError = "Upload measurement unavailable: " + e.Message; }
        }
        return new(DateTimeOffset.Now, connection.Ssid, connection.Bssid, band, received * 8d / Math.Max(seconds, 0.001) / 1_000_000,
            up, received, uploaded, "speed.cloudflare.com (HTTPS payload)", upError);
    }
    private static async Task<(long Bytes, double Seconds)> DownloadAsync(HttpClient http, int size, IProgress<string> progress, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(40));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://speed.cloudflare.com/__down?bytes={size}&r={Guid.NewGuid():N}");
        request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
        byte[] buffer = new byte[131072];
        long bytes = 0;
        var clock = Stopwatch.StartNew();
        double lastUpdate = 0;
        while (bytes < size)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - bytes)), limit.Token);
            if (read == 0) break;
            bytes += read;
            if (clock.Elapsed.TotalSeconds - lastUpdate > 0.3)
            {
                lastUpdate = clock.Elapsed.TotalSeconds;
                progress.Report($"Download · {bytes / 1_000_000d:F1} MB · {bytes * 8d / lastUpdate / 1_000_000:F1} Mbps");
            }
        }
        if (bytes == 0) throw new IOException("The speed-test server returned no payload.");
        return (bytes, Math.Max(0.001, clock.Elapsed.TotalSeconds));
    }
    private static HttpClient CreateClient(Guid adapterId)
    {
        var source = HardwareService.Find(adapterId)?.GetIPProperties().UnicastAddresses
            .Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        if (source is null) throw new InvalidOperationException("The selected Wi-Fi adapter has no usable IPv4 address. Internet speed testing requires one.");
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = false, MaxConnectionsPerServer = 4, UseProxy = false,
            ConnectCallback = async (context, cancellation) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation);
                Exception? lastError = null;
                foreach (var remote in addresses.Where(a => a.AddressFamily == source.AddressFamily))
                {
                    var socket = new Socket(source.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        socket.Bind(new IPEndPoint(source, 0));
                        await socket.ConnectAsync(new IPEndPoint(remote, context.DnsEndPoint.Port), cancellation);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception e) when (e is SocketException or OperationCanceledException) { socket.Dispose(); cancellation.ThrowIfCancellationRequested(); lastError = e; }
                }
                throw new IOException("Could not reach the test endpoint through the selected Wi-Fi interface.", lastError);
            }
        };
        return new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
