using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AirScope.Core;

namespace AirScope.Windows.Services;

public sealed class LanPeerService : IAsyncDisposable
{
    private readonly TcpListener _listener; private readonly string _token; private readonly TransferBudget _budget;
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromMinutes(15));
    private readonly SemaphoreSlim _clients = new(8); private readonly List<Task> _tasks = [];
    private Task? _loop;
    public string Endpoint => _listener.LocalEndpoint.ToString()!;
    public LanPeerService(IPAddress bind, int port, string token, long maxBytes = 4_000_000_000)
    {
        if (!IsPrivate(bind) || port is < 1024 or > 65535 || token.Length != 32 || !token.All(Uri.IsHexDigit) || maxBytes is < 1_000_000 or > 20_000_000_000)
            throw new ArgumentException("Use a private/loopback address, unprivileged port, 32-character hex token and bounded payload budget.");
        _listener = new(bind, port); _token = token; _budget = new(maxBytes);
    }
    public void Start() { _listener.Start(8); _loop = AcceptAsync(); }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (!await _clients.WaitAsync(0, _stop.Token)) { client.Dispose(); continue; }
                var task = ServeAsync(client); lock (_tasks) { _tasks.RemoveAll(t => t.IsCompleted); _tasks.Add(task); }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            limit.CancelAfter(TimeSpan.FromSeconds(35));
            try
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IsPrivate(remote.Address)) return;
                await using var stream = client.GetStream();
                string line = await ReadLineAsync(stream, limit.Token); var fields = line.Split(' ');
                if (fields.Length != 4 || fields[0] != "AIRSCOPE1" || fields[1].Length != 32
                    || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(fields[1]), Encoding.ASCII.GetBytes(_token))
                    || fields[2] is not ("DOWN" or "UP") || !int.TryParse(fields[3], out int count) || count is < 1 or > 8_000_000) return;
                if (_budget.Reserve(count) != count) { await stream.WriteAsync("LIMIT\n"u8.ToArray(), limit.Token); return; }
                await stream.WriteAsync("OK\n"u8.ToArray(), limit.Token);
                if (fields[2] == "DOWN") await PayloadIO.WriteAsync(stream, count, _ => { }, limit.Token);
                else { await PayloadIO.ReadAsync(stream, count, _ => { }, limit.Token); await stream.WriteAsync("DONE\n"u8.ToArray(), limit.Token); }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException) { }
            finally { _clients.Release(); }
        }
    }
    public static async Task<string> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[256]; var one = new byte[1]; int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(one, token); if (n == 0) throw new EndOfStreamException();
            if (one[0] == 10) return Encoding.ASCII.GetString(buffer, 0, offset);
            if (one[0] is < 32 or > 126) throw new InvalidDataException("Invalid LAN protocol header.");
            buffer[offset++] = one[0];
        }
        throw new InvalidDataException("LAN protocol header exceeds the supported length.");
    }
    public static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) return IsPrivate(address.MapToIPv4());
        byte[] b = address.GetAddressBytes();
        return b.Length == 4 ? b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31
            : (b[0] & 0xfe) == 0xfc || address.IsIPv6LinkLocal;
    }
    public static IPEndPoint ParsePrivateEndpoint(string value)
    {
        if (!IPEndPoint.TryParse(value, out var endpoint) || !IsPrivate(endpoint.Address) || endpoint.Port is < 1024 or > 65535)
            throw new ArgumentException("Enter a private peer IP:port (bracket IPv6 addresses), for example 192.168.1.10:49826.");
        return endpoint;
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); if (_loop is not null) await _loop;
        Task[] active; lock (_tasks) active = _tasks.ToArray(); await Task.WhenAll(active); _stop.Dispose(); _clients.Dispose();
    }
}

public sealed class LanTransport : IThroughputTransport
{
    private readonly IPAddress _source; private readonly IPEndPoint _endpoint; private readonly string _token;
    public LanTransport(Guid adapter, string endpoint, string token, bool preferIpv6)
    { _endpoint = LanPeerService.ParsePrivateEndpoint(endpoint); _source = InterfaceNetwork.SourceForPeer(adapter, _endpoint); _token = token; }
    public string Name => "LAN TCP";
    public string Endpoint => _endpoint.ToString();
    public async Task DownloadAsync(int count, Action<int> received, CancellationToken token)
    { await using var stream = await OpenAsync("DOWN", count, token); await PayloadIO.ReadAsync(stream, count, received, token); }
    public async Task UploadAsync(int count, Action<int> sent, CancellationToken token)
    {
        await using var stream = await OpenAsync("UP", count, token); await PayloadIO.WriteAsync(stream, count, sent, token);
        if (await LanPeerService.ReadLineAsync(stream, token) != "DONE") throw new IOException("The LAN peer did not acknowledge the upload.");
    }
    private async Task<NetworkStream> OpenAsync(string direction, int count, CancellationToken token)
    {
        var stream = await InterfaceNetwork.ConnectAsync(_source, _endpoint, token);
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"AIRSCOPE1 {_token} {direction} {count}\n"), token);
            if (await LanPeerService.ReadLineAsync(stream, token) != "OK") throw new IOException("The LAN peer refused the request (token or budget).");
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
    public void Dispose() { }
}
