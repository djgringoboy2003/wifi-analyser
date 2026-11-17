using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using AirScope.Core;
using AirScope.Windows.Services;

int passed = 0, failed = 0;
void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
}
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; received {actual}"); }
void Throws(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Unsafe input was accepted"); }
AccessPoint Ap(int mhz = 6295, int rssi = -52, string ssid = "my-network", string mac = "00:11:22:33:44:55", int? width = 160, int? center = 6365) => new()
{ Ssid = ssid, SsidHex = Convert.ToHexString(Encoding.UTF8.GetBytes(ssid)), Bssid = mac, FrequencyMhz = mhz, Rssi = rssi, WidthMhz = width, CenterFrequencyMhz = center };
AdvancedProperty Property(string key, string value, string[] display, string[] values) => new(key, display[Array.IndexOf(values, value)], key, [value], display, values);
AdapterDetails Hardware(params AdvancedProperty[] properties) => new(Guid.Parse("12345678-1234-1234-1234-123456789abc"), "WiFi test", "MediaTek Wi-Fi 7 MT7925 Wireless LAN Card", "test", "test", "MediaTek", properties);
var preferred = Property("PreferredBand", "2", ["1. No Preference", "2. Prefer 2.4GHz band", "3. Prefer 5GHz band", "4. Prefer 6GHz band"], ["0", "1", "2", "3"]);
var power = Property("LowPowerEnable", "1", ["Disabled", "Auto"], ["0", "1"]);
var own = Ap();

Test("2.4-GHz channels, including special channel 14", () => { Equal(1, RadioMath.Channel(2412)); Equal(13, RadioMath.Channel(2472)); Equal(14, RadioMath.Channel(2484)); });
Test("5-GHz channel 157 does not become a 6-GHz channel", () => { Equal(157, RadioMath.Channel(5785)); Equal(WifiBand.Band5, RadioMath.Band(5785)); });
Test("6-GHz channel 69 and special channel 2", () => { Equal(69, RadioMath.Channel(6295)); Equal(2, RadioMath.Channel(5935)); Equal(233, RadioMath.Channel(7115)); });
Test("Invalid frequency is not assigned a channel", () => { Equal(0, RadioMath.Channel(6296)); Equal(WifiBand.Unknown, RadioMath.Band(0)); });
Test("UK lower-6-GHz planning stops at channel 93", () => { var ratings = RadioMath.RateChannels([], WifiBand.Band6); Equal(24, ratings.Count); Equal(93, ratings.Max(r => r.Channel)); });
Test("Own SSID excluded from overlap scoring", () => { Check(RadioMath.RateChannels([own], own.Band, own.SsidHex).All(c => c.OverlapScore == 0)); });
Test("Bonded channel overlaps secondary primaries", () => { var scores = RadioMath.RateChannels([Ap(mhz: 5180, center: 5210, width: 80)], WifiBand.Band5); Check(scores.Single(c => c.Channel == 48).OverlapScore > 0); Equal(0d, scores.Single(c => c.Channel == 100).OverlapScore); });
Test("Stronger interferers have higher overlap scores", () => { var strong = RadioMath.RateChannels([Ap(2412, -40, width: 20, center: 2412)], WifiBand.Band24); var weak = RadioMath.RateChannels([Ap(2412, -85, width: 20, center: 2412)], WifiBand.Band24); Check(strong.Single(r => r.Channel == 1).OverlapScore > weak.Single(r => r.Channel == 1).OverlapScore); });
Test("Repeated BSSID does not double count", () => { var a = RadioMath.RateChannels([own], own.Band); var b = RadioMath.RateChannels([own, own], own.Band); Equal(a.Sum(x => x.OverlapScore), b.Sum(x => x.OverlapScore)); });
Test("Empty beacon does not invent width or utilisation", () => { var b = InformationElements.Parse([], 6295, true); Equal<int?>(null, b.WidthMhz); Equal<double?>(null, b.Utilisation); });
Test("AP load and station count are parsed from BSS Load", () => { var b = InformationElements.Parse([11, 5, 42, 0, 128, 0, 0], 2412, false); Equal<int?>(42, b.Stations); Check(Math.Abs(b.Utilisation!.Value - 50.196) < 0.01); });
Test("HT secondary-above center and 40-MHz width", () => { var b = InformationElements.Parse([61, 2, 1, 5], 2412, false); Equal<int?>(40, b.WidthMhz); Equal<int?>(2422, b.CenterMhz); });
Test("VHT 160-MHz signaling uses second center", () => { var b = InformationElements.Parse([192, 3, 1, 42, 50], 5180, true); Equal<int?>(160, b.WidthMhz); Equal<int?>(5250, b.CenterMhz); });
Test("80+80 operation is not represented as contiguous 160", () => { var b = InformationElements.Parse([192, 3, 1, 42, 106], 5180, true); Equal<int?>(null, b.WidthMhz); Equal<int?>(null, b.CenterMhz); });
Test("HE 6-GHz Operation reads optional 160-MHz center", () => { var b = InformationElements.Parse([255, 12, 36, 0, 0, 2, 0, 0, 0, 69, 3, 71, 79, 0], 6295, true); Equal<int?>(160, b.WidthMhz); Equal<int?>(6345, b.CenterMhz); });
Test("EHT 320-MHz operation parsed only when present", () => { var b = InformationElements.Parse([255, 9, 106, 1, 0, 0, 0, 0, 4, 31, 63], 6295, true); Equal<int?>(320, b.WidthMhz); Equal("Wi-Fi 7", b.Standard); });
Test("WPA3 SAE recognized from RSN AKM", () => { var b = InformationElements.Parse([48, 18, 1, 0, 0, 15, 172, 4, 1, 0, 0, 15, 172, 4, 1, 0, 0, 15, 172, 8], 6295, true); Equal("WPA3-Personal", b.Security); });
Test("Truncated RSN is safe and unambiguous", () => { var b = InformationElements.Parse([48, 1, 0], 2412, true); Equal("RSN (details unavailable)", b.Security); });
Test("5,000 malformed beacon buffers never escape bounds", () => { var random = new Random(91); for (int i = 0; i < 5000; i++) { var bytes = new byte[random.Next(1024)]; random.NextBytes(bytes); _ = InformationElements.Parse(bytes, 6295, true); } });
Test("MT7925 plan uses installed driver mapping for 6-GHz preference", () => { var plan = OptimisationPlanner.Build(Hardware(preferred), [own], own.SsidHex, true); Equal(1, plan.Changes.Count); Equal("3", plan.Changes[0].AfterValues[0]); OptimisationPlanner.Validate(plan); });
Test("Weak 6 GHz with good 5 GHz keeps 5-GHz preference", () => { var plan = OptimisationPlanner.Build(Hardware(preferred), [own with { Rssi = -80 }, Ap(5785, -60, mac: "01:02:03:04:05:06")], own.SsidHex, true); Equal(0, plan.Changes.Count); });
Test("Other people's SSIDs do not drive tuning", () => { var plan = OptimisationPlanner.Build(Hardware(preferred, power), [own with { SsidHex = "UNRELATED" }], own.SsidHex, true); Equal(0, plan.Changes.Count); });
Test("No automatic changes for unknown hardware", () => { var plan = OptimisationPlanner.Build(Hardware(preferred) with { Description = "Other Wi-Fi adapter" }, [own], own.SsidHex, true); Equal(0, plan.Changes.Count); });
Test("Laptop plan excludes driver power-saving change", () => { var plan = OptimisationPlanner.Build(Hardware(power), [own], own.SsidHex, false); Equal(0, plan.Changes.Count); });
Test("Desktop power-saving change is supported and reversible", () => { var plan = OptimisationPlanner.Build(Hardware(power), [own], own.SsidHex, true); Equal("1", plan.Changes[0].BeforeValues[0]); Equal("0", plan.Changes[0].AfterValues[0]); });
Test("Broken driver mapping cannot produce a plan", () => { var bad = preferred with { ValidRegistryValues = ["0"] }; var plan = OptimisationPlanner.Build(Hardware(bad), [own], own.SsidHex, true); Equal(0, plan.Changes.Count); });
var validPlan = OptimisationPlanner.Build(Hardware(preferred), [own], own.SsidHex, true);
Test("Arbitrary registry keywords rejected", () => Throws(() => OptimisationPlanner.Validate(validPlan with { Changes = [validPlan.Changes[0] with { Keyword = "UnsafeSetting" }] })));
Test("Non-numeric registry payload rejected", () => Throws(() => OptimisationPlanner.Validate(validPlan with { Changes = [validPlan.Changes[0] with { AfterValues = ["'; Invoke-Expression 'bad"] }] })));
Test("Duplicate changes rejected", () => Throws(() => OptimisationPlanner.Validate(validPlan with { Changes = [validPlan.Changes[0], validPlan.Changes[0]] })));
Test("Empty plans rejected at execution boundary", () => Throws(() => OptimisationPlanner.Validate(validPlan with { Changes = [] })));
Test("Traffic uses monotonic time and Mbps (not MB/s)", () => { var calc = new TrafficCalculator(); Equal<TrafficSample?>(null, calc.Add("a", 0, 0, 1, DateTimeOffset.Now)); var sample = calc.Add("a", 1_000_000, 500_000, 2, DateTimeOffset.Now)!; Equal(8d, sample.DownloadMbps); Equal(4d, sample.UploadMbps); });
Test("Adapter replacement and counter resets do not spike", () => { var calc = new TrafficCalculator(); calc.Add("a", 1_000_000, 1_000_000, 1, DateTimeOffset.Now); Equal<TrafficSample?>(null, calc.Add("b", 100, 100, 2, DateTimeOffset.Now)); Equal<TrafficSample?>(null, calc.Add("b", 10, 10, 3, DateTimeOffset.Now)); });
Test("Clock regression is ignored", () => { var calc = new TrafficCalculator(); calc.Add("a", 100, 100, 2, DateTimeOffset.Now); Equal<TrafficSample?>(null, calc.Add("a", 200, 200, 1, DateTimeOffset.Now)); });
Test("Packet loss and jitter exclude failed probes", () => { var l = LatencyMath.Summarise("x", [10, 20, null, 30, 35]); Equal(20d, l.LossPercent); Equal<double?>(7.5, l.JitterMs); Equal(4, l.Received); });
Test("All lost probes report unavailable latency", () => { var l = LatencyMath.Summarise("x", [null, null]); Equal(100d, l.LossPercent); Equal<double?>(null, l.AverageMs); });
Test("Native x64 WLAN structures match SDK layout", () => { Equal(360, Marshal.SizeOf(typeof(NativeWifi).GetNestedType("BssEntry", BindingFlags.NonPublic)!)); Equal(604, Marshal.SizeOf(typeof(NativeWifi).GetNestedType("ConnectionAttributes", BindingFlags.NonPublic)!)); Equal(532, Marshal.SizeOf(typeof(NativeWifi).GetNestedType("InterfaceInfo", BindingFlags.NonPublic)!)); });
Test("Wi-Fi 6 and Wi-Fi 7 use official PHY enum values", () => { Equal("Wi-Fi 6/6E", NativeWifi.Phy(10)); Equal("Wi-Fi 7", NativeWifi.Phy(11)); Equal("802.11ad", NativeWifi.Phy(9)); });
Test("HTML report escapes hostile SSID", () => { var malicious = own with { Ssid = "<script>alert(1)</script>" }; var html = ReportService.Html([malicious], null, null, [], [], true); Check(!html.Contains("<script>")); Check(html.Contains("&lt;script&gt;")); });
Test("Redacted report never contains identifiers", () => { var html = ReportService.Html([own], new(own.InterfaceId, "private-profile", own.Ssid, own.SsidHex, own.Bssid, 80, 2882, 2882, "Wi-Fi 7", "WPA3"), null, [], [], false); Check(!html.Contains(own.Ssid)); Check(!html.Contains(own.Bssid)); Check(!html.Contains("private-profile")); });
Test("CSV shields spreadsheet formula injection", () => { var csv = ReportService.Csv([own with { Ssid = "=CMD()" }], true); Check(csv.Contains("\"'=CMD()\"")); });
Test("CSV quotes commas and embedded double quotes", () => { var csv = ReportService.Csv([own with { Ssid = "a,\"b\"" }], true); Check(csv.Contains("\"a,\"\"b\"\"\"")); });
Test("CSV defaults can redact names and MACs", () => { var csv = ReportService.Csv([own], false); Check(!csv.Contains(own.Ssid)); Check(!csv.Contains(own.Bssid)); });
if (OperatingSystem.IsWindows())
{
    Test("PowerShell bridge round-trips Unicode and newlines", () => { var output = PowerShellRunner.RunAsync("[pscustomobject]@{Value='Wi-Fi → 6 GHz';Count=2} | ConvertTo-Json -Compress").GetAwaiter().GetResult(); Check(output.Contains("Wi-Fi → 6 GHz")); Check(output.Contains("\"Count\":2")); });
    int hardwareArg = Array.IndexOf(args, "--hardware-guid");
    if (hardwareArg >= 0 && hardwareArg + 1 < args.Length)
        Test("Live adapter advanced properties deserialize", () => { var hardware = HardwareService.ReadAsync(Guid.Parse(args[hardwareArg + 1])).GetAwaiter().GetResult(); Check(hardware.Properties.Count > 0); Console.WriteLine($"  {hardware.Properties.Count} advanced properties read"); });
}
AdvancedTests.Run(Test, args);
if (args.Contains("--quality") && OperatingSystem.IsWindows())
{
    using var wifi = new NativeWifi();
    var selected = wifi.Interfaces().FirstOrDefault(i => i.State == 1);
    if (selected is not null) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(wifi.Quality(selected.Id)));
}
Console.WriteLine($"\n{passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;
