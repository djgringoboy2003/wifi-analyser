using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using AirScope.Core;

namespace AirScope.Windows.Services;

public static class RecoveryHealthService
{
    public static string PreflightScript(Guid id) => $$"""
        $checks=[Collections.Generic.List[object]]::new()
        function Add-Check([string]$name,[bool]$passed,[string]$detail) { $checks.Add([pscustomobject]@{Name=$name;Passed=$passed;Detail=$detail}) }
        $admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        Add-Check 'Administrator helper' $admin 'Protected tuning requires Windows administrator access.'
        try { $a=Get-NetAdapter | Where-Object { [guid]$_.InterfaceGuid -eq [guid]'{{id:D}}' } | Select-Object -First 1; Add-Check 'Selected adapter' ($null -ne $a -and $a.Status -eq 'Up') 'The original adapter must exist and be connected.' } catch { Add-Check 'Selected adapter' $false $_.Exception.Message }
        try { Add-Check 'Task Scheduler' ((Get-Service -Name Schedule).Status -eq 'Running') 'The independent rollback task requires the Windows Task Scheduler service.' } catch { Add-Check 'Task Scheduler' $false $_.Exception.Message }
        Add-Check 'Checkpoint command' ($null -ne (Get-Command Checkpoint-Computer -ErrorAction SilentlyContinue)) 'A newly created restore point will still be verified before any mutation.'
        try {
          $key=Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' -ErrorAction SilentlyContinue
          Add-Check 'System Restore policy' ($key.DisableSR -ne 1) 'No disable flag was found. This alone does not prove a point can be created.'
          $frequency=1440; if ($null -ne $key.SystemRestorePointCreationFrequency) { $frequency=[int64]$key.SystemRestorePointCreationFrequency }
          $last=Get-ComputerRestorePoint | Sort-Object SequenceNumber | Select-Object -Last 1
          $ready=$true; $detail='No previous point was returned; creation will be attempted and verified.'
          if ($last) { $created=[Management.ManagementDateTimeConverter]::ToDateTime([string]$last.CreationTime); $age=([DateTime]::Now-$created).TotalMinutes; $ready=$age -ge $frequency; $detail='Last point age '+[math]::Round($age)+' minutes; configured frequency '+$frequency+' minutes.' }
          Add-Check 'Restore-point availability' $ready $detail
        } catch { Add-Check 'Restore-point availability' $false $_.Exception.Message }
        [pscustomobject]@{Checks=@($checks.ToArray());Time=[DateTimeOffset]::Now.ToString('o')} | ConvertTo-Json -Depth 5 -Compress
        """;
    public static async Task<RecoveryPreflight> PreflightAsync(Guid id) => JsonSerializer.Deserialize<RecoveryPreflight>(
        await PowerShellRunner.RunAsync(PreflightScript(id)), JsonStore.Options) ?? throw new InvalidDataException("No recovery preflight result.");
    public static async Task<RecoveryPreflight> HealthAsync(Guid id, string expectedSsidHex, CancellationToken token)
    {
        List<RecoveryCheck> checks = [];
        try
        {
            using var wifi = new NativeWifi(); var connection = wifi.Current(id);
            checks.Add(new("Original SSID", connection is not null && !string.IsNullOrEmpty(expectedSsidHex)
                && connection.SsidHex.Equals(expectedSsidHex, StringComparison.OrdinalIgnoreCase), "The trial adapter must still be associated with the intended SSID."));
            var adapter = HardwareService.Find(id);
            checks.Add(new("Adapter link", adapter?.OperationalStatus == OperationalStatus.Up, "The selected physical Wi-Fi adapter must report Up."));
            var source = InterfaceNetwork.SourceAddress(id, false);
            var gateway = adapter?.GetIPProperties().GatewayAddresses.Select(g => g.Address).FirstOrDefault(g => !g.Equals(IPAddress.Any) && !g.Equals(IPAddress.IPv6Any));
            checks.Add(new("Address and gateway", gateway is not null, "A usable selected-interface IP address and default gateway are required."));
            var remote = source.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Parse("1.1.1.1") : IPAddress.Parse("2606:4700:4700::1111");
            var tcp = await InterfaceNetwork.ProbeTcpAsync(source, new(remote, 443), 3, token);
            bool reachable = tcp.Received >= 2;
            if (!reachable && gateway is not null)
            {
                var ping = await DiagnosticsService.PingAsync(gateway.ToString(), 3, null, token);
                reachable = ping.Received >= 2;
            }
            checks.Add(new("Connectivity proof", reachable, "Require at least two successful selected-interface TCP probes or gateway ICMP replies. Filtering can make this check inconclusive."));
        }
        catch (Exception error) when (error is not OperationCanceledException) { checks.Add(new("Connection health", false, error.Message)); }
        return new(checks, DateTimeOffset.Now);
    }
    public static (Guid AdapterId, string Ssid) ExpectedNetwork(string id)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new ArgumentException("Invalid recovery identifier.");
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AirScope", id + ".json");
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Invalid recovery snapshot size.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return (document.RootElement.GetProperty("AdapterId").GetGuid(), document.RootElement.TryGetProperty("ExpectedSsidHex", out var field) ? field.GetString() ?? "" : "");
    }
}
