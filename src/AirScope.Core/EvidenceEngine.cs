namespace AirScope.Core;

public static class EvidenceEngine
{
    public static IReadOnlyList<DiagnosticFinding> Diagnose(DiagnosticEvidence e)
    {
        List<DiagnosticFinding> findings = [];
        if (e.Connection is null)
            findings.Add(new("Connection", "Unavailable", "High", "Windows returned no connected Wi-Fi link.", "Check Wi-Fi state and location consent; no throughput conclusion is possible."));
        if (e.AccessPoint is { } ap)
            findings.Add(ap.Rssi < -72
                ? new("Signal", "Weak", "Medium", $"{ap.Rssi} dBm on {ap.BandLabel}.", "Compare a closer location and the other available bands.")
                : new("Signal", "Usable", "Medium", $"{ap.Rssi} dBm; signal alone does not establish available capacity.", "Check loaded latency and local-network throughput before tuning."));
        if (e.Quality is { Supported: true } quality)
            findings.Add(new("Connected links", quality.IsMlo ? "MLO reported active" : "Single-link reported", "High",
                $"Windows reports {quality.Links.Count} link(s); connected widths: {string.Join(", ", quality.Links.Select(l => l.WidthLabel))}.",
                "Connected-link width is separate from the access point's advertised width."));
        if (e.Gateway is { } gateway)
            findings.Add(new("Local path", gateway.LossPercent > 0 || gateway.AverageMs > 15 ? "Needs investigation" : "Responsive",
                "Medium", $"Gateway: {gateway.AverageMs?.ToString("F1") ?? "unavailable"} ms, {gateway.LossPercent:F0}% ICMP loss.",
                "ICMP can be filtered or deprioritised. Corroborate with repeated probes and a LAN test."));
        if (e.InternetSpeed is { } speed)
        {
            double? increase = LoadedIncrease(speed);
            if (increase.HasValue) findings.Add(new("Latency under load", increase > 40 ? "Elevated" : "Limited increase", "Medium",
                $"Mean RTT increased by {increase:F1} ms during the transfer.", "Compare repeated idle/loaded tests; congestion can be at the Wi-Fi, hub or ISP."));
            if (speed.DataLimited || speed.DownloadSeconds < 5)
                findings.Add(new("Test quality", "Short or data-limited", "High", $"Download sample: {speed.DownloadSeconds:F1}s; {speed.DownloadBytes / 1_000_000d:F1} MB.",
                    "Increase the visible test budget/duration before treating this result as a performance ceiling."));
            if (e.LanSpeed is { } lan && lan.Time - speed.Time < TimeSpan.FromHours(1)
                && speed.Time - lan.Time < TimeSpan.FromHours(1) && lan.Ssid == speed.Ssid && lan.Bssid == speed.Bssid)
                findings.Add(new("LAN versus internet", lan.DownloadMbps > speed.DownloadMbps * 1.5 ? "Internet path may limit throughput" : "No clear separation", "Medium",
                    $"LAN {lan.DownloadMbps:F0} versus internet {speed.DownloadMbps:F0} Mbps on the same AP.",
                    "The wired peer, test server, host load and route can also limit a test. This is evidence, not a definitive ISP diagnosis."));
            if (e.PlanMbps > 0)
                findings.Add(new("Service target", "Context", "Low", $"Test achieved {speed.DownloadMbps / e.PlanMbps * 100:F0}% of your entered service speed.",
                    "A package speed is not a guarantee of Wi-Fi throughput; compare a suitable wired baseline."));
        }
        return findings;
    }
    public static double? LoadedIncrease(SpeedResult result) => result.LoadedLatency?.AverageMs is double loaded
        && result.IdleLatency?.AverageMs is double idle ? loaded - idle : null;

    public static ComparisonResult Compare(IReadOnlyList<SpeedResult> before, IReadOnlyList<SpeedResult> after, UsageProfile profile)
    {
        static ComparisonResult Insufficient(string reason) => new("Inconclusive", false, 0, 0, 0, 0, 0, reason);
        if (before.Count < 3 || after.Count < 3) return Insufficient("At least three complete baseline and trial samples are required.");
        var all = before.Concat(after).ToArray();
        if (all.Select(s => s.Ssid).Distinct().Count() != 1 || all.Select(s => s.Bssid).Distinct().Count() != 1
            || all.Select(s => s.Endpoint).Distinct().Count() != 1 || all.Select(s => s.Streams).Distinct().Count() != 1
            || all.Select(s => s.Transport).Distinct().Count() != 1 || all.Any(s => s.DownloadSeconds < 2 || s.DownloadMbps <= 0))
            return Insufficient("SSID, AP, endpoint, transport and stream count must match; short/invalid tests cannot be compared.");
        bool latencyObjective = profile is UsageProfile.Gaming or UsageProfile.VideoCalls;
        if (latencyObjective && all.Any(s => s.LoadedLatency?.AverageMs is null || s.LoadedLatency.Received < 5))
            return Insufficient("This profile needs at least five successful loaded-latency probes per sample.");
        if (all.Any(s => s.LoadedLatency is not { Received: >= 5 } || s.UploadError is not null))
            return Insufficient("Every sample needs successful loaded-latency probes and no upload error before all guardrails can be evaluated.");
        double Metric(SpeedResult s) => latencyObjective ? s.LoadedLatency!.AverageMs!.Value : s.DownloadMbps;
        var b = before.Select(Metric).ToArray(); var a = after.Select(Metric).ToArray();
        double mb = Median(b), ma = Median(a);
        double Gain(double bv, double av) => latencyObjective ? 100 * (bv - av) / Math.Max(bv, 0.1) : 100 * (av - bv) / Math.Max(bv, 0.1);
        var random = new Random(42019); var bootstrap = new double[2000];
        for (int i = 0; i < bootstrap.Length; i++)
            bootstrap[i] = Gain(Median(Enumerable.Range(0, b.Length).Select(_ => b[random.Next(b.Length)])),
                Median(Enumerable.Range(0, a.Length).Select(_ => a[random.Next(a.Length)])));
        Array.Sort(bootstrap); double low = bootstrap[50], high = bootstrap[1949];
        bool throughputGuard = Median(after.Select(s => s.DownloadMbps)) >= Median(before.Select(s => s.DownloadMbps)) * (latencyObjective ? 0.8 : 0.95);
        bool lossGuard = after.All(s => s.LoadedLatency?.LossPercent is null or <= 2);
        var beforeUp = before.Where(s => s.UploadMbps.HasValue).Select(s => s.UploadMbps!.Value).ToArray();
        bool uploadGuard = beforeUp.Length == 0 || after.All(s => s.UploadMbps.HasValue)
            && Median(after.Select(s => s.UploadMbps!.Value)) >= Median(beforeUp) * (latencyObjective ? 0.8 : 0.9);
        double? beforeLoaded = before.Where(s => s.LoadedLatency?.AverageMs is not null).Select(s => s.LoadedLatency!.AverageMs).DefaultIfEmpty().Average();
        double? afterLoaded = after.Where(s => s.LoadedLatency?.AverageMs is not null).Select(s => s.LoadedLatency!.AverageMs).DefaultIfEmpty().Average();
        bool latencyGuard = beforeLoaded is null || afterLoaded is not null && afterLoaded <= beforeLoaded * 1.2 + 5;
        bool keep = low >= 5 && throughputGuard && uploadGuard && lossGuard && latencyGuard;
        string reason = !throughputGuard || !uploadGuard || !lossGuard || !latencyGuard ? "A download, upload, loss or latency guardrail regressed. Restore the baseline." :
            keep ? "Repeated samples favour the trial beyond the minimum 5% improvement threshold. Review the connection and explicitly Keep before rollback." :
            "The resampled range includes a small or negative improvement. Restore or collect more comparable measurements.";
        return new(keep ? "Improvement supported" : low < -5 && high < 0 ? "Regression" : "Inconclusive", keep, mb, ma,
            Gain(mb, ma), low, high, reason + " The 95% bootstrap range describes these samples; it does not establish causation or future performance.");
    }
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}

public static class AutomationRules
{
    public static AutomationDecision Evaluate(AutomationPolicy policy, DateTimeOffset now, TimeSpan idle,
        DateTimeOffset? lastTest, DateTimeOffset? lastDisruption, long usedToday, long reserved, bool busy, bool connected, bool disruptive = false)
    {
        policy.Validate(); long remaining = Math.Max(0, policy.DailyByteLimit - Math.Max(0, usedToday));
        string? reason = !policy.Enabled ? "Automation is disabled." : busy ? "Another operation is running." : !connected ? "Wi-Fi is not connected." :
            !InWindow(now.Hour, policy.WindowStartHour, policy.WindowEndHour) ? "Outside the maintenance window." :
            idle.TotalMinutes < policy.MinimumIdleMinutes ? "Waiting for the required idle time." :
            lastTest.HasValue && now - lastTest < TimeSpan.FromMinutes(policy.IntervalMinutes) ? "The test interval has not elapsed." :
            reserved > remaining ? "The remaining daily budget cannot cover this test." :
            disruptive && !policy.AllowDisruptiveTrials ? "Disruptive trials are not enabled." :
            disruptive && lastDisruption.HasValue && now - lastDisruption < TimeSpan.FromMinutes(policy.CooldownMinutes) ? "The disruption cooldown is active." : null;
        return new(reason is null, reason ?? "Idle, schedule and budget checks passed.", remaining);
    }
    public static bool InWindow(int hour, int start, int end) => start == end || (start < end ? hour >= start && hour < end : hour >= start || hour < end);
}

public sealed class TransferBudget
{
    private long _remaining;
    public TransferBudget(long bytes) { if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes)); _remaining = bytes; }
    public long Remaining => Interlocked.Read(ref _remaining);
    public int Reserve(int desired)
    {
        if (desired < 0) throw new ArgumentOutOfRangeException(nameof(desired));
        while (true)
        {
            long current = Remaining; int granted = (int)Math.Min(current, desired);
            if (Interlocked.CompareExchange(ref _remaining, current - granted, current) == current) return granted;
        }
    }
}
