using System.Globalization;
using System.Text;
using System.Text.Json;
using AirScope.Core;

namespace AirScope.Windows.Services;

public sealed class HistoryService
{
    private readonly string _root; private readonly object _gate = new();
    public HistoryService(string? root = null) => _root = Path.GetFullPath(root ?? Path.Combine(JsonStore.Root, "history"));
    public void Append(HistoryEntry entry)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            string file = Path.Combine(_root, entry.Time.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
            using var transaction = LocalFileTransaction.Acquire(file);
            if (File.Exists(file) && new FileInfo(file).Length >= 16_000_000) return;
            File.AppendAllText(file, JsonSerializer.Serialize(entry) + "\n", new UTF8Encoding(false));
        }
    }
    public IReadOnlyList<HistoryEntry> Read(int days = 7, int limit = 5000, Func<HistoryEntry, bool>? predicate = null)
    {
        if (!Directory.Exists(_root)) return [];
        if (days is < 1 or > 365 || limit is < 1 or > 20000) throw new ArgumentOutOfRangeException(nameof(days));
        List<HistoryEntry> records = [];
        foreach (string path in Directory.EnumerateFiles(_root, "*.jsonl").OrderByDescending(Path.GetFileName))
        {
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date < DateOnly.FromDateTime(DateTime.Now.AddDays(-days))) continue;
            if (new FileInfo(path).Length > 16_000_000) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var daily = new Queue<HistoryEntry>();
            while (reader.ReadLine() is string line)
            {
                if (line.Length > 16384) continue;
                try { var entry = JsonSerializer.Deserialize<HistoryEntry>(line); if (entry is not null && (predicate is null || predicate(entry))) { daily.Enqueue(entry); if (daily.Count > limit) daily.Dequeue(); } }
                catch (JsonException) { /* An interrupted last append must not hide the rest of the day. */ }
            }
            records.AddRange(daily.Reverse()); if (records.Count >= limit) break;
        }
        return records.OrderByDescending(r => r.Time).Take(limit).ToArray();
    }
    public IReadOnlyList<HistoryEntry> SignalBuckets(int days, string? ssid = null, int maximum = 1500)
    {
        if (days is < 1 or > 365 || maximum is < 10 or > 10000) throw new ArgumentOutOfRangeException(nameof(days));
        if (!Directory.Exists(_root)) return [];
        long bucketTicks = TimeSpan.FromSeconds(SignalBucketSeconds(days, maximum)).Ticks;
        var buckets = new Dictionary<(long Bucket, string Ssid), (HistoryEntry Last, long Sum, int Count)>();
        foreach (string file in Directory.EnumerateFiles(_root, "*.jsonl"))
        {
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date < DateOnly.FromDateTime(DateTime.Now.AddDays(-days)) || new FileInfo(file).Length > 16_000_000) continue;
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var reader = new StreamReader(input);
            while (reader.ReadLine() is string line)
            {
                if (line.Length > 16384) continue;
                HistoryEntry? entry; try { entry = JsonSerializer.Deserialize<HistoryEntry>(line); } catch (JsonException) { continue; }
                if (entry is not { Kind: "Sample", Rssi: not null } || ssid is not null && entry.Ssid != ssid) continue;
                var key = (entry.Time.UtcTicks / bucketTicks, entry.Ssid ?? "");
                if (buckets.TryGetValue(key, out var current)) buckets[key] = (entry.Time > current.Last.Time ? entry : current.Last, current.Sum + entry.Rssi.Value, current.Count + 1);
                else buckets[key] = (entry, entry.Rssi.Value, 1);
            }
        }
        return buckets.Values.Select(b => b.Last with { Rssi = (int)Math.Round(b.Sum / (double)b.Count), Detail = $"Average of {b.Count} observed signal samples." })
            .OrderBy(e => e.Time).TakeLast(maximum).ToArray();
    }
    public static double SignalBucketSeconds(int days, int maximum = 1500) => Math.Max(10, days * 86400d / maximum);
    public void Prune(int retentionDays)
    {
        if (retentionDays is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        if (!Directory.Exists(_root)) return;
        lock (_gate)
            foreach (string file in Directory.EnumerateFiles(_root, "*.jsonl"))
                if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    && date < DateOnly.FromDateTime(DateTime.Now.AddDays(-retentionDays))
                    && Path.GetDirectoryName(Path.GetFullPath(file)) == _root) File.Delete(file);
    }
}

public sealed class UsageDay
{
    public string Date { get; set; } = "";
    public long UsedBytes { get; set; }
    public Dictionary<string, long> Reservations { get; set; } = [];
    public DateTimeOffset? LastTest { get; set; }
    public DateTimeOffset? LastDisruption { get; set; }
}

public sealed class UsageLedger
{
    private readonly string _path; private readonly object _gate = new();
    public UsageLedger(string? path = null) => _path = path ?? Path.Combine(JsonStore.Root, "usage-ledger.json");
    private UsageDay Load(DateTimeOffset now)
    {
        string day = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!File.Exists(_path)) return new() { Date = day };
        var entry = JsonSerializer.Deserialize<UsageDay>(File.ReadAllText(_path), JsonStore.Options) ?? throw new InvalidDataException("The quota ledger is invalid; scheduled testing is stopped.");
        if (!DateOnly.TryParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var recordedDay)
            || entry.Reservations is null || entry.UsedBytes < 0 || entry.Reservations.Any(r => r.Value < 0)) throw new InvalidDataException("The quota ledger contains invalid accounting.");
        if (recordedDay > DateOnly.FromDateTime(now.LocalDateTime)) throw new InvalidDataException("The clock precedes the quota ledger date; scheduled tests are paused.");
        return entry.Date == day ? entry : new() { Date = day, LastTest = entry.LastTest, LastDisruption = entry.LastDisruption };
    }
    private void Save(UsageDay day)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(day, JsonStore.Options)); File.Move(_path + ".tmp", _path, true);
    }
    private FileStream Acquire()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(2)) { Thread.Sleep(10); }
        }
    }
    public UsageDay Today(DateTimeOffset now) { lock (_gate) { using var transaction = Acquire(); return Load(now); } }
    public string Reserve(long maximum, long dailyLimit, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var transaction = Acquire();
            if (maximum < 1 || dailyLimit < maximum) throw new InvalidOperationException("The requested test exceeds the daily payload budget.");
            var day = Load(now);
            if (day.UsedBytes > dailyLimit - maximum) throw new InvalidOperationException("The remaining daily payload budget cannot cover this test.");
            string id = Guid.NewGuid().ToString("D"); day.UsedBytes = checked(day.UsedBytes + maximum);
            day.Reservations[id] = maximum; day.LastTest = now; Save(day); return id;
        }
    }
    public void Complete(string id, long actualBytes, DateTimeOffset started)
    {
        lock (_gate)
        {
            using var transaction = Acquire();
            var day = Load(DateTimeOffset.Now);
            if (day.Date != started.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) || !day.Reservations.Remove(id, out var reserved)) return;
            if (actualBytes < 0 || actualBytes > reserved) throw new InvalidDataException("A test exceeded its reserved payload budget.");
            day.UsedBytes -= reserved - actualBytes; Save(day);
        }
    }
    public void RecordDisruption(DateTimeOffset now) { lock (_gate) { using var transaction = Acquire(); var day = Load(now); day.LastDisruption = now; Save(day); } }
}
