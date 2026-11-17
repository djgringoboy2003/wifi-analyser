using System.Net;
using System.Text;
using AirScope.Core;

namespace AirScope.Windows.Services;

public static class ReportService
{
    public static string Html(IReadOnlyList<AccessPoint> networks, Connection? connection, AdapterDetails? hardware,
        IReadOnlyList<SpeedResult> speeds, IReadOnlyList<LatencyResult> latencies, bool includeIdentifiers)
    {
        var ssids = new Dictionary<string, string>(StringComparer.Ordinal);
        var macs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Ssid(string name) { if (includeIdentifiers) return name; if (!ssids.ContainsKey(name)) ssids[name] = $"Network {ssids.Count + 1}"; return ssids[name]; }
        string Mac(string mac) { if (includeIdentifiers) return mac; if (!macs.ContainsKey(mac)) macs[mac] = $"AP {macs.Count + 1}"; return macs[mac]; }
        string E(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? "—");
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>AirScope Wi-Fi report</title><style>body{font:15px/1.6 system-ui,sans-serif;color:#172b43;background:#f4f7fa;margin:40px auto;max-width:1200px;padding:0 24px}h1{font-size:36px}h2{margin-top:36px}table{width:100%;border-collapse:collapse;background:white}th,td{text-align:left;padding:12px;border-bottom:1px solid #d9e2ec}th{background:#132638;color:#c6f7eb}small{color:#536b82}.note{padding:18px;background:#e3f3ed;border-radius:10px}@media print{body{margin:0;font-size:11px}table{page-break-inside:auto}tr{page-break-inside:avoid}}</style><body><h1>AirScope <small>Wi-Fi intelligence</small></h1>");
        html.Append($"<p>{E(DateTimeOffset.Now.ToString("f"))} · v{Product.Version} · Identifiers {(includeIdentifiers ? "included" : "redacted")}</p>");
        html.Append("<p class=\"note\">Live traffic, negotiated link rates and internet speed tests are different measurements. Scan observations do not predict achievable throughput. No Wi-Fi passwords are read or included.</p>");
        html.Append("<h2>Connected link</h2>");
        if (connection is null) html.Append("<p>No connection information available. Windows location consent may be required.</p>");
        else html.Append($"<p>{E(Ssid(connection.Ssid))} · {E(Mac(connection.Bssid))} · {E(connection.Standard)} · {E(connection.Security)}<br>Receive PHY: {connection.RxLinkMbps:F1} Mbps · Transmit PHY: {connection.TxLinkMbps:F1} Mbps · Signal: {connection.SignalPercent}%</p>");
        html.Append($"<h2>Hardware &amp; hub</h2><p>{E(hardware?.Description ?? "Hardware details unavailable")}<br>Driver: {E(hardware?.DriverVersion)}<br>{E(Product.SkyHub)} (user-configured model)</p>");
        html.Append("<h2>Observed access points</h2><table><thead><tr><th>Network</th><th>AP</th><th>Band / channel</th><th>Signal</th><th>Width</th><th>Security</th><th>Last seen</th></tr></thead><tbody>");
        foreach (var ap in networks.OrderBy(p => p.Band).ThenByDescending(p => p.Rssi))
            html.Append($"<tr><td>{E(Ssid(ap.Name))}</td><td>{E(Mac(ap.Bssid))}</td><td>{E(ap.BandLabel)} / {ap.Channel}</td><td>{ap.Rssi} dBm</td><td>{E(ap.WidthLabel)}</td><td>{E(ap.Security)}</td><td>{E(ap.SeenAt.ToLocalTime().ToString("T"))}</td></tr>");
        html.Append("</tbody></table><h2>Internet throughput history</h2><table><thead><tr><th>Time</th><th>Network</th><th>Band</th><th>Download Mbps</th><th>Upload Mbps</th><th>Endpoint</th></tr></thead><tbody>");
        foreach (var s in speeds.Take(30)) html.Append($"<tr><td>{E(s.Time.ToLocalTime().ToString("g"))}</td><td>{E(Ssid(s.Ssid))}</td><td>{E(s.BandLabel)}</td><td>{s.DownloadMbps:F1}</td><td>{E(s.UploadMbps?.ToString("F1"))}</td><td>{E(s.Endpoint)}</td></tr>");
        html.Append("</tbody></table><p>HTTPS payload measurements are estimates affected by server, protocol overhead and competing traffic. The speed-test sockets are bound to the selected Wi-Fi adapter's IPv4 address.</p><h2>Latency probes</h2><table><thead><tr><th>Target</th><th>Received / sent</th><th>Mean ms</th><th>Jitter ms</th><th>Loss</th></tr></thead><tbody>");
        foreach (var l in latencies) html.Append($"<tr><td>{E(l.Target)}</td><td>{l.Received}/{l.Sent}</td><td>{E(l.AverageMs?.ToString("F1"))}</td><td>{E(l.JitterMs?.ToString("F1"))}</td><td>{l.LossPercent:F0}%</td></tr>");
        html.Append("</tbody></table><p>ICMP follows Windows routing and may be filtered. Jitter is the mean absolute difference between consecutive successful round trips.</p><h2>Interpretation &amp; limits</h2><p>Channel overlap scores are a relative model of signal strength and advertised footprints. They do not measure RF noise, non-Wi-Fi interference or hidden stations. AP load is shown only when advertised. MLO is not inferred from Wi-Fi 7 capability. Sky manages router firmware and channels.</p><p><a href=\"https://www.sky.com/help/articles/change-wifi-max-hub-settings\">Sky Wi-Fi settings</a> · <a href=\"https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes\">Windows Wi-Fi permissions</a></p></body></html>");
        return html.ToString();
    }
    public static string Csv(IReadOnlyList<AccessPoint> points, bool identifiers)
    {
        static string Cell(string? value)
        {
            string v = value ?? "";
            if (v.TrimStart().StartsWith('=') || v.TrimStart().StartsWith('+') || v.TrimStart().StartsWith('-') || v.TrimStart().StartsWith('@') || v.StartsWith('\t') || v.StartsWith('\r')) v = "'" + v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }
        var labels = points.Select(p => p.SsidHex).Distinct().Select((id, index) => (id, label: $"Network {index + 1}")).ToDictionary(x => x.id, x => x.label);
        var result = new StringBuilder("SSID,BSSID,Band,Channel,Frequency_MHz,RSSI_dBm,Width_MHz,Standard,Security,AP_Load_Percent,Stations,Seen_UTC\r\n");
        int i = 0;
        foreach (var p in points)
        {
            i++;
            result.AppendLine(string.Join(",", new[] { Cell(identifiers ? p.Ssid : labels[p.SsidHex]), Cell(identifiers ? p.Bssid : $"AP {i}"),
                Cell(p.BandLabel), p.Channel.ToString(), p.FrequencyMhz.ToString(), p.Rssi.ToString(), p.WidthMhz?.ToString() ?? "", Cell(p.Standard), Cell(p.Security),
                p.UtilisationPercent?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "", p.StationCount?.ToString() ?? "", Cell(p.SeenAt.ToUniversalTime().ToString("o")) }));
        }
        return result.ToString();
    }
}
