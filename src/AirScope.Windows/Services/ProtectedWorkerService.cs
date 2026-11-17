using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AirScope.Core;

namespace AirScope.Windows.Services;

public sealed record WorkerConfiguration(string Id, string UserSid, Guid AdapterId, string ExpectedSsidHex,
    AutomationPolicy Policy, ThroughputOptions Options, string PublisherThumbprint);

public static class ProtectedWorkerService
{
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AirScope");
    private static string WorkerRoot => Path.Combine(Root, "worker");
    private static string Sid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("User identity unavailable.");
    private static string TaskName => "AirScope-Protected-" + Sid;
    public static async Task<string> ConfigureAsync(WorkerConfiguration? configuration, bool remove)
    {
        if (!ElevationService.IsAdmin) throw new InvalidOperationException("Administrator approval is required to configure the worker.");
        if (ElevationService.ListRecoveryPoints().Any(p => p.State is "Applying" or "Pending" or "RollbackFailed"))
            throw new InvalidOperationException("Resolve active or failed trials before changing the protected worker.");
        if (remove)
        {
            await PowerShellRunner.RunAsync("$name=" + PowerShellRunner.Quote(TaskName) + "; Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue");
            return "Protected worker task removed. Recovery snapshots and independent rollback tasks were preserved.";
        }
        var config = configuration ?? throw new InvalidDataException("No worker policy was provided.");
        config.Policy.Validate(); config.Options.Validate();
        if (config.AdapterId == Guid.Empty || !Guid.TryParseExact(config.Id, "D", out _) || config.ExpectedSsidHex.Length is < 2 or > 64
            || !config.ExpectedSsidHex.All(Uri.IsHexDigit) || config.PublisherThumbprint.Length != 40 || !config.PublisherThumbprint.All(Uri.IsHexDigit)
            || config.Options.LanEndpoint is not null) throw new InvalidDataException("Invalid worker adapter, SSID, publisher pin or internet test policy.");
        string executable = Environment.ProcessPath ?? throw new IOException("Executable path unavailable.");
        await VerifyPublisherAsync(executable, config.PublisherThumbprint);
#pragma warning disable IL3000
        if (!string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location)) throw new InvalidOperationException("Configure the worker from the published self-contained single-file AirScope executable.");
#pragma warning restore IL3000
        ProtectDirectory(Root); ProtectDirectory(WorkerRoot);
        string copy = Path.Combine(WorkerRoot, "AirScope.exe"), temporary = Path.Combine(WorkerRoot, Guid.NewGuid().ToString("N") + ".tmp");
        if (File.Exists(copy) && (File.GetAttributes(copy) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid protected worker executable path.");
        File.Copy(executable, temporary); await VerifyPublisherAsync(temporary, config.PublisherThumbprint);
        await PowerShellRunner.RunAsync("Stop-ScheduledTask -TaskName " + PowerShellRunner.Quote(TaskName) + " -ErrorAction SilentlyContinue");
        File.Move(temporary, copy, true);
        var protectedConfig = config with { UserSid = Sid };
        File.WriteAllText(Path.Combine(WorkerRoot, config.Id + ".json"), JsonSerializer.Serialize(protectedConfig, JsonStore.Options));
        string script = $$"""
            $name={{PowerShellRunner.Quote(TaskName)}}
            $action=New-ScheduledTaskAction -Execute {{PowerShellRunner.Quote(copy)}} -Argument {{PowerShellRunner.Quote("--protected-worker " + config.Id)}}
            $principal=New-ScheduledTaskPrincipal -UserId {{PowerShellRunner.Quote(Sid)}} -LogonType Interactive -RunLevel Highest
            $trigger=New-ScheduledTaskTrigger -AtLogOn -User {{PowerShellRunner.Quote(Sid)}}
            $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
            $null=Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force
            Start-ScheduledTask -TaskName $name
            """;
        await PowerShellRunner.RunAsync(script);
        return "Signed protected worker configured for this logged-in user. Its policy and executable are protected from ordinary-user writes; policy changes require administrator approval.";
    }
    public static async Task<WorkerConfiguration> LoadAsync(string id)
    {
        if (!ElevationService.IsAdmin || !Guid.TryParseExact(id, "D", out _)) throw new InvalidOperationException("Invalid protected-worker invocation.");
        VerifyDirectory(WorkerRoot); string file = Path.Combine(WorkerRoot, id + ".json");
        if (new FileInfo(file).Length > 65536 || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Invalid worker policy file.");
        var config = JsonSerializer.Deserialize<WorkerConfiguration>(File.ReadAllText(file), JsonStore.Options) ?? throw new InvalidDataException("Invalid protected-worker policy.");
        if (config.Id != id || config.UserSid != Sid) throw new InvalidOperationException("The worker policy belongs to a different session user.");
        config.Policy.Validate(); config.Options.Validate(); await VerifyPublisherAsync(Environment.ProcessPath!, config.PublisherThumbprint); return config;
    }
    private static async Task VerifyPublisherAsync(string file, string thumbprint)
    {
        string script = "$s=Get-AuthenticodeSignature -LiteralPath " + PowerShellRunner.Quote(file)
            + "; if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Thumbprint -ne " + PowerShellRunner.Quote(thumbprint) + ") { throw 'A trusted, valid Authenticode signature matching the configured publisher is required. Unsigned builds can monitor and schedule diagnostics but cannot install a privileged worker.' }";
        await PowerShellRunner.RunAsync(script);
    }
    private static void VerifyDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Protected worker storage is unavailable or linked.");
        string owner = directory.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value ?? throw new UnauthorizedAccessException("Protected storage owner is unavailable.");
        if (owner is not ("S-1-5-32-544" or "S-1-5-18")) throw new UnauthorizedAccessException("Protected worker storage has an unexpected owner.");
    }
    private static void ProtectDirectory(string path)
    {
        if (Directory.Exists(path)) VerifyDirectory(path); else Directory.CreateDirectory(path);
        var administrators = new SecurityIdentifier("S-1-5-32-544"); var security = new DirectorySecurity();
        security.SetOwner(administrators); security.SetAccessRuleProtection(true, false);
        foreach (string sid in new[] { "S-1-5-32-544", "S-1-5-18" }) security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
