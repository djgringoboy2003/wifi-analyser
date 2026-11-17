namespace AirScope.Core;

public static class RadioMath
{
    public static WifiBand Band(int mhz) => mhz switch
    {
        >= 2400 and <= 2500 => WifiBand.Band24,
        >= 4900 and < 5925 => WifiBand.Band5,
        >= 5925 and <= 7125 => WifiBand.Band6,
        _ => WifiBand.Unknown
    };

    public static int Channel(int mhz) => mhz switch
    {
        2484 => 14,
        >= 2412 and <= 2472 when (mhz - 2407) % 5 == 0 => (mhz - 2407) / 5,
        5935 => 2,
        >= 5955 and <= 7115 when (mhz - 5950) % 5 == 0 => (mhz - 5950) / 5,
        >= 5000 and < 5925 when mhz % 5 == 0 => (mhz - 5000) / 5,
        >= 4910 and < 5000 when mhz % 5 == 0 => (mhz - 4000) / 5,
        _ => 0
    };

    public static string Label(WifiBand band) => band switch
    {
        WifiBand.Band24 => "2.4 GHz", WifiBand.Band5 => "5 GHz", WifiBand.Band6 => "6 GHz", _ => "Unknown"
    };

    public static string Signal(int rssi) => rssi switch
    {
        >= -55 => "Excellent", >= -67 => "Good", >= -75 => "Fair", _ => "Weak"
    };

    // A relative interference proxy based on received power and spectral overlap.
    // This is neither measured airtime nor a throughput prediction.
    public static IReadOnlyList<ChannelRating> RateChannels(IEnumerable<AccessPoint> points, WifiBand band, string? ownSsidHex = null)
    {
        var networks = points.Where(p => p.Band == band).GroupBy(p => p.Bssid).Select(g => g.First()).ToArray();
        var candidates = band switch
        {
            WifiBand.Band24 => Enumerable.Range(1, 13).Select(c => (c, 2407 + c * 5)),
            WifiBand.Band5 => new[] { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165 }.Select(c => (c, 5000 + c * 5)),
            // UK lower-6-GHz planning range. The driver/router remain the authority on allowed channels.
            WifiBand.Band6 => Enumerable.Range(0, 24).Select(i => 1 + i * 4).Select(c => (c, 5950 + c * 5)),
            _ => Enumerable.Empty<(int, int)>()
        };
        return candidates.Select(candidate =>
        {
            var (channel, frequency) = candidate;
            double score = 0;
            int count = 0;
            foreach (var ap in networks)
            {
                if (!string.IsNullOrEmpty(ownSsidHex) && ap.SsidHex == ownSsidHex) continue;
                int width = ap.WidthMhz ?? (band == WifiBand.Band24 ? 22 : 20);
                int center = ap.CenterFrequencyMhz ?? ap.FrequencyMhz;
                // With an unknown bonded center, use a conservative envelope around the primary.
                double halfWidth = ap.CenterFrequencyMhz is null && width > 20 ? width : width / 2d;
                double overlap = Math.Max(0, Math.Min(frequency + 10, center + halfWidth) - Math.Max(frequency - 10, center - halfWidth)) / 20d;
                if (overlap <= 0) continue;
                count++;
                score += overlap * Math.Pow(10, (Math.Clamp(ap.Rssi, -100, -20) + 70) / 20d);
            }
            return new ChannelRating(channel, frequency, band, Math.Round(score, 2), count,
                networks.Any(p => p.Channel == channel), band == WifiBand.Band5 && channel is >= 52 and <= 144
                    ? "DFS; availability controlled by hub" : "Planning estimate; verify hub/regulatory support");
        }).OrderBy(r => r.OverlapScore).ThenBy(r => r.Channel).ToArray();
    }
}

public sealed class TrafficCalculator
{
    private (long Rx, long Tx, double Time, string Id)? _previous;
    public TrafficSample? Add(string interfaceId, long received, long sent, double monotonicSeconds, DateTimeOffset now)
    {
        var prior = _previous;
        _previous = (received, sent, monotonicSeconds, interfaceId);
        if (prior is null || prior.Value.Id != interfaceId || monotonicSeconds <= prior.Value.Time
            || received < prior.Value.Rx || sent < prior.Value.Tx) return null;
        double seconds = monotonicSeconds - prior.Value.Time;
        return new TrafficSample(now, (received - prior.Value.Rx) * 8d / seconds / 1_000_000,
            (sent - prior.Value.Tx) * 8d / seconds / 1_000_000, received, sent);
    }
}

public static class LatencyMath
{
    public static LatencyResult Summarise(string target, IReadOnlyList<double?> samples)
    {
        var valid = samples.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var deltas = samples.Zip(samples.Skip(1)).Where(x => x.First.HasValue && x.Second.HasValue)
            .Select(x => Math.Abs(x.First!.Value - x.Second!.Value)).ToArray();
        return new(target, samples.Count, valid.Length, valid.Length > 0 ? valid.Average() : null,
            deltas.Length > 0 ? deltas.Average() : null, valid.Length > 0 ? valid.Min() : null,
            valid.Length > 0 ? valid.Max() : null);
    }
}
