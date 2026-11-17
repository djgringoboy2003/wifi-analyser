using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AirScope.Core;

namespace AirScope.Windows.Services;

public interface IThroughputTransport : IDisposable
{
    string Name { get; }
    string Endpoint { get; }
    Task DownloadAsync(int count, Action<int> received, CancellationToken token);
    Task UploadAsync(int count, Action<int> sent, CancellationToken token);
}

public sealed class SpeedTestService
{
    public Task<SpeedResult> RunAsync(Connection connection, WifiBand band, bool upload, IProgress<string> progress, CancellationToken token) =>
        RunAsync(connection, band, new ThroughputOptions { Upload = upload }, progress, token);

    public async Task<SpeedResult> RunAsync(Connection connection, WifiBand band, ThroughputOptions options,
        IProgress<string> progress, CancellationToken token, Action<long>? accountUsage = null)
    {
        options.Validate();
        using IThroughputTransport transport = options.LanEndpoint is not null
            ? new LanTransport(connection.InterfaceId, options.LanEndpoint, options.LanToken!, options.PreferIpv6)
            : new HttpsTransport(connection.InterfaceId, options);
        string host; int port;
        if (options.LanEndpoint is string lan) { var endpoint = LanPeerService.ParsePrivateEndpoint(lan); host = endpoint.Address.ToString(); port = endpoint.Port; }
        else { var uri = new Uri(options.DownloadUrl); host = uri.Host; port = uri.Port; }
        var source = options.LanEndpoint is not null ? InterfaceNetwork.SourceForPeer(connection.InterfaceId, LanPeerService.ParsePrivateEndpoint(options.LanEndpoint)) : InterfaceNetwork.SourceAddress(connection.InterfaceId, options.PreferIpv6);
        var remote = (await Dns.GetHostAddressesAsync(host, token)).FirstOrDefault(a => a.AddressFamily == source.AddressFamily)
            ?? throw new IOException("The endpoint has no address compatible with the selected Wi-Fi IP family.");
        var idle = await InterfaceNetwork.ProbeTcpAsync(source, new(remote, port), 5, token);
        List<double?> loaded = [];
        using var loadedStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task loadTask = Task.CompletedTask;
        long used = 0;
        void Account(int bytes) { Interlocked.Add(ref used, bytes); accountUsage?.Invoke(bytes); }
        try
        {
            var budget = new TransferBudget(options.MaxBytes);
            int warmupAllowance = budget.Reserve(262_144); long warmup = 0;
            var warmClock = Stopwatch.StartNew();
            using (var warmLimit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                warmLimit.CancelAfter(TimeSpan.FromSeconds(10));
                progress.Report("Warming up the selected interface and test endpoint…");
                await transport.DownloadAsync(warmupAllowance, n => { warmup += n; Account(n); }, warmLimit.Token);
            }
            int block = (int)Math.Clamp(warmup / Math.Max(0.05, warmClock.Elapsed.TotalSeconds) * 0.3, 262_144, 8_000_000);
            loadTask = SampleLoadedAsync();
            long available = budget.Remaining;
            long downAllowance = options.Upload ? available * 3 / 4 : available;
            var down = await RunPhaseAsync(transport, false, downAllowance, options, block, progress, Account, token);
            (long Bytes, long Acknowledged, double Seconds, bool Capped) up = default;
            string? uploadError = null;
            if (options.Upload)
            {
                try { up = await RunPhaseAsync(transport, true, available - downAllowance, options, block, progress, Account, token); }
                catch (Exception error) when (error is HttpRequestException or IOException or SocketException)
                { uploadError = error.Message; up = (Math.Max(0, Interlocked.Read(ref used) - warmup - down.Bytes), 0, 0, false); }
            }
            loadedStop.Cancel(); await loadTask;
            var loadedResult = LatencyMath.Summarise($"{host}:{port} · selected-interface TCP", loaded.ToArray());
            return new(DateTimeOffset.Now, connection.Ssid, connection.Bssid, band, down.Acknowledged * 8d / Math.Max(down.Seconds, 0.001) / 1_000_000,
                options.Upload && uploadError is null && up.Acknowledged > 0 ? up.Acknowledged * 8d / Math.Max(up.Seconds, 0.001) / 1_000_000 : null,
                down.Bytes, up.Bytes, transport.Endpoint, uploadError, down.Seconds, up.Seconds, options.Streams,
                down.Capped || up.Capped, idle, loadedResult, transport.Name, warmup);
        }
        finally { loadedStop.Cancel(); await loadTask; }

        async Task SampleLoadedAsync()
        {
            try
            {
                while (!loadedStop.IsCancellationRequested)
                {
                    var sample = await InterfaceNetwork.ProbeTcpAsync(source, new(remote, port), 1, loadedStop.Token);
                    loaded.Add(sample.AverageMs);
                    await Task.Delay(400, loadedStop.Token);
                }
            }
            catch (OperationCanceledException) when (loadedStop.IsCancellationRequested) { }
        }
    }

    public static async Task<(long Bytes, long Acknowledged, double Seconds, bool Capped)> RunPhaseAsync(
        IThroughputTransport transport, bool upload, long cap, ThroughputOptions options, int block,
        IProgress<string> progress, Action<int> account, CancellationToken token)
    {
        var budget = new TransferBudget(cap); long bytes = 0, acknowledged = 0;
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(token);
        phase.CancelAfter(TimeSpan.FromSeconds(options.SecondsPerDirection));
        var clock = Stopwatch.StartNew();
        using var ticker = new System.Threading.Timer(_ => progress.Report($"{(upload ? "Upload" : "Download")} · {Interlocked.Read(ref bytes) / 1_000_000d:F1}/{cap / 1_000_000d:F0} MB · {Interlocked.Read(ref acknowledged) * 8d / Math.Max(0.001, clock.Elapsed.TotalSeconds) / 1_000_000:F1} Mbps · {options.Streams} streams"), null, 0, 250);
        Exception? failure = null;
        await Task.WhenAll(Enumerable.Range(0, options.Streams).Select(async _ =>
        {
            while (!phase.IsCancellationRequested)
            {
                int request = budget.Reserve(block); if (request == 0) break;
                long transferred = 0;
                void Count(int n) { Interlocked.Add(ref bytes, n); account(n); if (!upload) Interlocked.Add(ref acknowledged, n); transferred += n; }
                try
                {
                    if (upload) { await transport.UploadAsync(request, Count, phase.Token); Interlocked.Add(ref acknowledged, transferred); }
                    else await transport.DownloadAsync(request, Count, phase.Token);
                }
                catch (OperationCanceledException) when (phase.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or HttpRequestException or SocketException)
                { Interlocked.CompareExchange(ref failure, error, null); phase.Cancel(); break; }
            }
        }));
        token.ThrowIfCancellationRequested();
        if (failure is not null) throw new IOException("The test endpoint failed: " + failure.Message, failure);
        if (acknowledged == 0) throw new IOException("The test endpoint returned no complete measurable payload.");
        return (bytes, acknowledged, clock.Elapsed.TotalSeconds, budget.Remaining == 0);
    }
}

public static class InterfaceNetwork
{
    public static IPAddress SourceForPeer(Guid adapter, IPEndPoint peer)
    {
        if (peer.Address.IsIPv6LinkLocal)
            return HardwareService.Find(adapter)?.GetIPProperties().UnicastAddresses.Select(a => a.Address).FirstOrDefault(a => a.IsIPv6LinkLocal)
                ?? throw new IOException("The selected adapter has no IPv6 link-local address. Include the peer's correct scope ID.");
        var source = SourceAddress(adapter, peer.AddressFamily == AddressFamily.InterNetworkV6);
        if (source.AddressFamily != peer.AddressFamily) throw new IOException("The peer and the selected adapter must use the same IP family.");
        return source;
    }
    public static IPAddress SourceAddress(Guid adapterId, bool preferIpv6)
    {
        var addresses = HardwareService.Find(adapterId)?.GetIPProperties().UnicastAddresses.Select(a => a.Address)
            .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal)).ToArray()
            ?? throw new IOException("The selected Wi-Fi adapter is unavailable.");
        var family = preferIpv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        return addresses.FirstOrDefault(a => a.AddressFamily == family) ?? addresses.FirstOrDefault()
            ?? throw new IOException("The selected Wi-Fi adapter has no usable address.");
    }
    public static async Task<NetworkStream> ConnectAsync(IPAddress source, IPEndPoint destination, CancellationToken token)
    {
        var socket = new Socket(source.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { socket.Bind(new IPEndPoint(source, 0)); await socket.ConnectAsync(destination, token); return new(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }
    public static async Task<LatencyResult> ProbeTcpAsync(IPAddress source, IPEndPoint remote, int count, CancellationToken token)
    {
        List<double?> values = [];
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(1200);
            var watch = Stopwatch.StartNew();
            try { await using var stream = await ConnectAsync(source, remote, limit.Token); values.Add(watch.Elapsed.TotalMilliseconds); }
            catch (Exception error) when (error is SocketException or IOException || error is OperationCanceledException && !token.IsCancellationRequested) { values.Add(null); }
            if (i < count - 1) await Task.Delay(150, token);
        }
        return LatencyMath.Summarise(remote + " · selected-interface TCP", values);
    }
    public static HttpClient HttpClient(Guid adapterId, bool preferIpv6)
    {
        var source = SourceAddress(adapterId, preferIpv6);
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(8),
            AllowAutoRedirect = false, MaxConnectionsPerServer = 8, UseProxy = false,
            ConnectCallback = async (context, cancellation) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation);
                Exception? last = null;
                foreach (var remote in addresses.Where(a => a.AddressFamily == source.AddressFamily))
                {
                    try { return await ConnectAsync(source, new(remote, context.DnsEndPoint.Port), cancellation); }
                    catch (SocketException error) { last = error; }
                }
                throw new IOException("The endpoint is unreachable through the selected Wi-Fi IP family.", last);
            }
        };
        return new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}

public sealed class HttpsTransport : IThroughputTransport
{
    private readonly HttpClient _http; private readonly ThroughputOptions _options;
    public HttpsTransport(Guid adapter, ThroughputOptions options) { _http = InterfaceNetwork.HttpClient(adapter, options.PreferIpv6); _options = options; }
    public string Name => "HTTPS";
    public string Endpoint => new Uri(_options.DownloadUrl).GetLeftPart(UriPartial.Path);
    public async Task DownloadAsync(int count, Action<int> received, CancellationToken token)
    {
        string url = _options.DownloadUrl.Contains("{bytes}", StringComparison.Ordinal) ? _options.DownloadUrl.Replace("{bytes}", count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            : _options.DownloadUrl + (_options.DownloadUrl.Contains('?') ? "&" : "?") + $"bytes={count}&airscope={Guid.NewGuid():N}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        await PayloadIO.ReadAsync(stream, count, received, token);
    }
    public async Task UploadAsync(int count, Action<int> sent, CancellationToken token)
    {
        using var content = new PayloadContent(count, sent);
        using var response = await _http.PostAsync(_options.UploadUrl, content, token); response.EnsureSuccessStatusCode();
    }
    public void Dispose() => _http.Dispose();
    private sealed class PayloadContent(int size, Action<int> count) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = size; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => PayloadIO.WriteAsync(stream, size, count, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => PayloadIO.WriteAsync(stream, size, count, token);
    }
}

public static class PayloadIO
{
    private static readonly byte[] RandomBlock = CreateBlock();
    private static byte[] CreateBlock() { var block = new byte[65_536]; RandomNumberGenerator.Fill(block); return block; }
    public static async Task ReadAsync(Stream stream, int count, Action<int> received, CancellationToken token)
    {
        var bytes = new byte[131_072]; int remaining = count;
        while (remaining > 0)
        {
            int n = await stream.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, remaining)), token);
            if (n == 0) throw new EndOfStreamException("The peer returned a truncated payload.");
            received(n); remaining -= n;
        }
    }
    public static async Task WriteAsync(Stream stream, int count, Action<int> sent, CancellationToken token)
    {
        for (int remaining = count; remaining > 0;)
        { int n = Math.Min(RandomBlock.Length, remaining); await stream.WriteAsync(RandomBlock.AsMemory(0, n), token); sent(n); remaining -= n; }
    }
}
