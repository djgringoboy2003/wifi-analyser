namespace AirScope.Core;

public static class OptimisationPlanner
{
    // Narrow allowlist for the MT7925. Values are discovered from the installed driver.
    public static readonly HashSet<string> AllowedKeywords = new(StringComparer.OrdinalIgnoreCase)
    { "PreferredBand", "BandSelection", "BWSelection24G", "BWSelection5G", "BWSelection6G", "CurrPhyMode", "LowPowerEnable" };

    public static OptimisationPlan Build(AdapterDetails adapter, IEnumerable<AccessPoint> scan, string ssidHex, bool desktop)
    {
        var changes = new List<SettingChange>();
        var advice = new List<string>();
        var own = scan.Where(p => p.SsidHex == ssidHex && !string.IsNullOrEmpty(ssidHex)).ToArray();
        bool mediaTek = adapter.Description.Contains("MT7925", StringComparison.OrdinalIgnoreCase);
        if (!mediaTek)
        {
            advice.Add("Automatic driver tuning is available for the MediaTek MT7925. This adapter stays in analysis mode.");
            return new(adapter.Id, adapter.Name, changes, advice, DateTimeOffset.Now);
        }
        if (own.Length == 0)
        {
            advice.Add("Scan your connected SSID before generating hardware changes.");
            return new(adapter.Id, adapter.Name, changes, advice, DateTimeOffset.Now);
        }
        var six = own.Where(p => p.Band == WifiBand.Band6).OrderByDescending(p => p.Rssi).FirstOrDefault();
        var five = own.Where(p => p.Band == WifiBand.Band5).OrderByDescending(p => p.Rssi).FirstOrDefault();
        string? preferred = six is { Rssi: >= -67 } ? "6GHz" : five is { Rssi: >= -72 } ? "5GHz" : null;
        if (preferred is not null) Propose("PreferredBand", v => v.Contains($"Prefer {preferred}", StringComparison.OrdinalIgnoreCase),
            $"Your SSID advertises a usable {preferred} signal. A preference keeps fallback bands available.");
        Propose("BandSelection", v => v.Contains("All Band", StringComparison.OrdinalIgnoreCase), "Keep all bands available for fallback and roaming.");
        Propose("CurrPhyMode", v => v.Contains("802.11be", StringComparison.OrdinalIgnoreCase), "Allow the Wi-Fi 7 mode supported by the MT7925 and XER10.");
        foreach (string keyword in new[] { "BWSelection5G", "BWSelection6G" })
            Propose(keyword, v => v.Contains("Auto", StringComparison.OrdinalIgnoreCase), "Let the access point and driver negotiate their supported channel width.");
        if (desktop) Propose("LowPowerEnable", v => v.Equals("Disabled", StringComparison.OrdinalIgnoreCase),
            "Disable this driver's power-saving setting on this desktop; power use may increase.");
        advice.Add("Sky manages hub channel selection. Use My Sky for supported router settings; channel recommendations are advisory.");
        advice.Add("Link rate is the negotiated PHY rate. Internet throughput and application traffic are measured separately.");
        advice.Add("Wi-Fi 7 capability does not prove MLO is active. Windows/driver-reported links remain authoritative.");
        return new(adapter.Id, adapter.Name, changes, advice, DateTimeOffset.Now);

        void Propose(string keyword, Func<string, bool> match, string reason)
        {
            var property = adapter.Properties.FirstOrDefault(p => p.RegistryKeyword.Equals(keyword, StringComparison.OrdinalIgnoreCase));
            if (property is null || property.ValidDisplayValues.Length != property.ValidRegistryValues.Length) return;
            int index = Array.FindIndex(property.ValidDisplayValues, v => match(v));
            if (index < 0) return;
            string after = property.ValidRegistryValues[index];
            if (property.RegistryValue.SequenceEqual(new[] { after })) return;
            changes.Add(new(keyword, property.DisplayName, property.DisplayValue, property.ValidDisplayValues[index],
                property.RegistryValue, [after], reason));
        }
    }

    public static void Validate(OptimisationPlan plan)
    {
        if (plan.AdapterId == Guid.Empty || plan.Changes.Count is < 1 or > 7)
            throw new InvalidOperationException("No valid adapter changes were provided.");
        if (plan.Changes.Select(p => p.Keyword).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Changes.Count)
            throw new InvalidOperationException("Duplicate driver settings are not allowed.");
        foreach (var change in plan.Changes)
        {
            if (!AllowedKeywords.Contains(change.Keyword) || change.AfterValues.Length != 1 || change.BeforeValues.Length != 1
                || !uint.TryParse(change.AfterValues[0], out _) || !uint.TryParse(change.BeforeValues[0], out _))
                throw new InvalidOperationException("The plan contains an unsupported driver setting or value.");
        }
    }
}
