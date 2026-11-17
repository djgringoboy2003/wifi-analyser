using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AirScope.Core;
using AirScope.Windows.Services;

internal static class AdvancedTests
{
    public static void Run(Action<string, Action> test, string[] args)
    {
        void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
        void Reject(Action action) { try { action(); } catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException) { return; } throw new Exception("Invalid input accepted"); }
        SpeedResult Sample(double speed, double rtt = 15, string ap = "ap") => new(DateTimeOffset.Now, "test", ap, WifiBand.Band6, speed, speed / 4, 1000, 1000, "test", DownloadSeconds: 10, UploadSeconds: 10, Streams: 4,
            IdleLatency: new("TCP", 10, 10, 10, 1, 9, 11), LoadedLatency: new("TCP", 10, 10, rtt, 1, rtt - 1, rtt + 1));
        test("Parallel payload reservations never exceed the cap", () =>
        {
            var budget = new TransferBudget(3_000_007); long granted = 0;
            Parallel.For(0, 1000, _ => Interlocked.Add(ref granted, budget.Reserve(13001)));
            Check(granted == 3_000_007 && budget.Remaining == 0);
        });
        test("Negative quota reservations are rejected", () => Reject(() => new TransferBudget(3).Reserve(-1)));
        test("Endpoint validation blocks plaintext and embedded credentials", () =>
        { Reject(() => (new ThroughputOptions { DownloadUrl = "http://example.com" }).Validate()); Reject(() => (new ThroughputOptions { UploadUrl = "https://user:pass@example.com" }).Validate()); });
        test("Endpoint and timing budgets are bounded", () => { Reject(() => (new ThroughputOptions { SecondsPerDirection = 100 }).Validate()); Reject(() => (new ThroughputOptions { Streams = 100 }).Validate()); Reject(() => (new ThroughputOptions { MaxBytes = long.MaxValue }).Validate()); });
        test("Multi-stream download accounts every payload byte", () =>
        {
            long usage = 0; using var transport = new FakeTransport();
            var result = SpeedTestService.RunPhaseAsync(transport, false, 1_234_567, new() { Streams = 8 }, 100_003,
                new Progress<string>(), n => Interlocked.Add(ref usage, n), CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Bytes == 1_234_567 && result.Acknowledged == result.Bytes && usage == result.Bytes && result.Capped);
        });
        test("Multi-stream upload counts server-acknowledged chunks", () =>
        {
            using var transport = new FakeTransport();
            var result = SpeedTestService.RunPhaseAsync(transport, true, 1_000_019, new() { Streams = 4 }, 100_003, new Progress<string>(), _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Bytes == 1_000_019 && result.Acknowledged == result.Bytes);
        });
        test("Cancelled tests cannot create a completed result", () =>
        {
            using var transport = new FakeTransport(); using var stop = new CancellationTokenSource(); stop.Cancel();
            try { SpeedTestService.RunPhaseAsync(transport, false, 1000, new(), 1000, new Progress<string>(), _ => { }, stop.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return; } throw new Exception("Cancellation was ignored");
        });
        test("Truncated payload cannot be presented as a complete transfer", () =>
        {
            using var stream = new MemoryStream(new byte[31]);
            try { PayloadIO.ReadAsync(stream, 32, _ => { }, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (EndOfStreamException) { return; } throw new Exception("Truncation accepted");
        });
        test("MLO buffer reports the connected widths and flag", () =>
        {
            var bytes = new byte[24 + 272 * 2];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 11); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 80);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2_882_400); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 2);
            for (int i = 0; i < 2; i++)
            {
                int offset = 24 + i * 272; bytes[offset] = (byte)i;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), i == 0 ? 6295u : 5210000u);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8), 160);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 12), -55);
            }
            var result = RealtimeDecoder.Decode(bytes); Check(result.IsMlo && result.Links.Count == 2 && result.Links[1].CenterFrequencyMhz == 5210 && result.RxMbps == 2882.4);
        });
        test("MLO truncated arrays and invalid units are rejected", () =>
        {
            var bytes = new byte[24]; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 16);
            Reject(() => RealtimeDecoder.Decode(bytes)); Reject(() => RealtimeDecoder.Decode(new byte[2]));
        });
        test("Unknown driver bandwidth code does not hide valid MLO state", () =>
        {
            var bytes = new byte[24 + 272]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 11); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 80);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 6295);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), 3); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36), -56);
            var result = RealtimeDecoder.Decode(bytes); Check(result.Supported && !result.IsMlo && result.Links.Single().WidthMhz is null && result.Links.Single().ReportedWidthCode == 3);
        });
        test("Experiment needs repeated samples", () => Check(!EvidenceEngine.Compare([Sample(100)], [Sample(200)], UsageProfile.Throughput).RecommendKeep));
        test("Stable throughput improvement passes guardrails", () => Check(EvidenceEngine.Compare([Sample(100), Sample(101), Sample(99)], [Sample(130), Sample(129), Sample(131)], UsageProfile.Throughput).RecommendKeep));
        test("Throughput gain cannot conceal loaded-latency regression", () => Check(!EvidenceEngine.Compare([Sample(100), Sample(101), Sample(99)], [Sample(130, 100), Sample(129, 110), Sample(131, 120)], UsageProfile.Balanced).RecommendKeep));
        test("Gaming profile evaluates loaded latency", () => Check(EvidenceEngine.Compare([Sample(100, 40), Sample(101, 41), Sample(99, 39)], [Sample(100, 20), Sample(101, 21), Sample(99, 19)], UsageProfile.Gaming).RecommendKeep));
        test("A large upload regression cannot pass the gaming profile", () => Check(!EvidenceEngine.Compare([Sample(100, 40), Sample(101, 41), Sample(99, 39)], [Sample(100, 20) with { UploadMbps = 1 }, Sample(101, 21) with { UploadMbps = 1 }, Sample(99, 19) with { UploadMbps = 1 }], UsageProfile.Gaming).RecommendKeep));
        test("Missing guardrail evidence cannot recommend keeping a trial", () => Check(!EvidenceEngine.Compare([Sample(100), Sample(101), Sample(99)], [Sample(130) with { LoadedLatency = null }, Sample(129), Sample(131)], UsageProfile.Throughput).RecommendKeep));
        test("Different APs make experiment inconclusive", () => Check(!EvidenceEngine.Compare([Sample(100), Sample(101), Sample(99)], [Sample(130, ap: "other"), Sample(129), Sample(131)], UsageProfile.Throughput).RecommendKeep));
        test("Diagnostics flag short samples without claiming an ISP fault", () =>
        { var result = EvidenceEngine.Diagnose(new(null, null, null, null, null, Sample(600) with { DownloadSeconds = 1, DataLimited = true }, null)); Check(result.Any(f => f.Area == "Test quality")); });
        test("Maintenance windows handle midnight", () => { Check(AutomationRules.InWindow(23, 22, 5)); Check(AutomationRules.InWindow(2, 22, 5)); Check(!AutomationRules.InWindow(12, 22, 5)); });
        var policy = new AutomationPolicy { Enabled = true, WindowStartHour = 0, WindowEndHour = 0 };
        test("Automation requires idle and budget", () =>
        { Check(!AutomationRules.Evaluate(policy, DateTimeOffset.Now, TimeSpan.Zero, null, null, 0, 1, false, true).Run); Check(!AutomationRules.Evaluate(policy, DateTimeOffset.Now, TimeSpan.FromHours(1), null, null, policy.DailyByteLimit, 1, false, true).Run); });
        test("Disruptive automation is opt-in and cooled down", () =>
        { Check(!AutomationRules.Evaluate(policy, DateTimeOffset.Now, TimeSpan.FromHours(1), null, null, 0, 1, false, true, true).Run); var enabled = new AutomationPolicy { Enabled = true, AllowDisruptiveTrials = true, WindowStartHour = 0, WindowEndHour = 0 }; Check(!AutomationRules.Evaluate(enabled, DateTimeOffset.Now, TimeSpan.FromHours(1), null, DateTimeOffset.Now.AddMinutes(-1), 0, 1, false, true, true).Run); });
        string data = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(data);
        test("History tolerates interrupted final record", () =>
        { var history = new HistoryService(data); history.Append(new(DateTimeOffset.Now, "Sample", Guid.Empty)); File.AppendAllText(Path.Combine(data, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl"), "{broken"); Check(history.Read().Count == 1); });
        test("Event filtering happens before the row limit", () =>
        {
            var history = new HistoryService(Path.Combine(data, "filter")); history.Append(new(DateTimeOffset.Now.AddHours(-1), "Outage", Guid.Empty));
            for (int i = 0; i < 50; i++) history.Append(new(DateTimeOffset.Now.AddSeconds(i), "Sample", Guid.Empty, Rssi: -60));
            Check(history.Read(limit: 1, predicate: e => e.Kind != "Sample").Single().Kind == "Outage");
        });
        test("Signal buckets retain observations across the selected days", () =>
        {
            var history = new HistoryService(Path.Combine(data, "buckets"));
            history.Append(new(DateTimeOffset.Now.AddDays(-5), "Sample", Guid.Empty, Ssid: "test", Rssi: -70));
            history.Append(new(DateTimeOffset.Now, "Sample", Guid.Empty, Ssid: "test", Rssi: -50));
            Check(history.SignalBuckets(7, "test").Count == 2);
        });
        test("Quota survives a crash without refunding a reserved test", () =>
        { string file = Path.Combine(data, "quota.json"); var first = new UsageLedger(file); first.Reserve(100, 150, DateTimeOffset.Now); var second = new UsageLedger(file); Check(second.Today(DateTimeOffset.Now).UsedBytes == 100); Reject(() => second.Reserve(100, 150, DateTimeOffset.Now)); });
        test("Completed quota reservation refunds only unspent payload", () =>
        { var ledger = new UsageLedger(Path.Combine(data, "quota2.json")); var now = DateTimeOffset.Now; string id = ledger.Reserve(100, 200, now); ledger.Complete(id, 42, now); Check(ledger.Today(now).UsedBytes == 42); ledger.Complete(id, 0, now); Check(ledger.Today(now).UsedBytes == 42); });
        test("Independent ledger instances cannot race beyond the daily quota", () =>
        {
            string file = Path.Combine(data, "shared-quota.json"); int granted = 0;
            Parallel.For(0, 12, _ => { try { new UsageLedger(file).Reserve(100, 500, DateTimeOffset.Now); Interlocked.Increment(ref granted); } catch (InvalidOperationException) { } });
            Check(granted == 5 && new UsageLedger(file).Today(DateTimeOffset.Now).UsedBytes == 500);
        });
        test("Concurrent history appends preserve every completed record", () =>
        {
            string path = Path.Combine(data, "concurrent-history");
            Parallel.For(0, 40, i => new HistoryService(path).Append(new(DateTimeOffset.Now, "Event " + i, Guid.Empty)));
            Check(new HistoryService(path).Read().Count == 40);
        });
        test("Independent JSON updates merge instead of losing results", () =>
        {
            string file = Path.Combine(data, "shared-results.json");
            Parallel.For(0, 30, i => JsonStore.UpdatePath<List<int>>(file, current => (current ?? []).Append(i).ToList()));
            var result = System.Text.Json.JsonSerializer.Deserialize<List<int>>(File.ReadAllText(file))!;
            Check(result.Count == 30 && result.Distinct().Count() == 30);
        });
        test("Malformed quota dates cannot reset the budget", () =>
        {
            string path = Path.Combine(data, "broken-quota.json"); File.WriteAllText(path, "{\"Date\":\"invalid\",\"UsedBytes\":0}");
            Reject(() => new UsageLedger(path).Reserve(1, 100, DateTimeOffset.Now));
        });
        test("Update source and checksum are constrained", () =>
        {
            UpdateService.Validate(new("0.3.0", "https://github.com/djgringoboy2003/wifi-analyser/releases/download/v0.3.0/AirScope-win-x64.zip", new string('a', 64), "AirScope-win-x64.zip"));
            Reject(() => UpdateService.Validate(new("0.3.0", "https://example.com/malware.zip", new string('a', 64), "malware.zip")));
            Reject(() => UpdateService.Validate(new("0.3.0", "https://github.com/djgringoboy2003/wifi-analyser/releases/download/../../../../other/repo/a.zip", new string('a', 64), "a.zip")));
        });
        test("LAN peer rejects public listeners and malformed tokens", () =>
        { Reject(() => new LanPeerService(IPAddress.Parse("8.8.8.8"), 49826, new string('a', 32))); Reject(() => LanPeerService.ParsePrivateEndpoint("8.8.8.8:49826")); Check(LanPeerService.IsPrivate(IPAddress.Parse("192.168.5.2"))); });
        if (args.Contains("--network")) test("LAN peer authenticates and transfers bounded loopback payload", () => LoopbackAsync().GetAwaiter().GetResult());
    }
    private sealed class FakeTransport : IThroughputTransport
    {
        public string Name => "Test"; public string Endpoint => "Test";
        public async Task DownloadAsync(int count, Action<int> received, CancellationToken token) { token.ThrowIfCancellationRequested(); await Task.Yield(); received(count); }
        public async Task UploadAsync(int count, Action<int> sent, CancellationToken token) { token.ThrowIfCancellationRequested(); await Task.Yield(); sent(count); }
        public void Dispose() { }
    }
    private static async Task LoopbackAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        string key = new string('a', 32); await using var peer = new LanPeerService(IPAddress.Loopback, port, key, 1_000_000); peer.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var stream = await InterfaceNetwork.ConnectAsync(IPAddress.Loopback, new(IPAddress.Loopback, port), timeout.Token);
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"AIRSCOPE1 {key} DOWN 131071\n"), timeout.Token);
        if (await LanPeerService.ReadLineAsync(stream, timeout.Token) != "OK") throw new Exception("Handshake failed");
        int total = 0; await PayloadIO.ReadAsync(stream, 131071, n => total += n, timeout.Token);
        if (total != 131071) throw new Exception("Wrong payload length");
        await using var wrong = await InterfaceNetwork.ConnectAsync(IPAddress.Loopback, new(IPAddress.Loopback, port), timeout.Token);
        await wrong.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"AIRSCOPE1 {new string('b', 32)} DOWN 100\n"), timeout.Token);
        var read = new byte[1]; if (await wrong.ReadAsync(read, timeout.Token) != 0) throw new Exception("Bad token accepted");
    }
}
