using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using AirScope.Core;

namespace AirScope.Windows.Services;

public sealed record AdminRequest(string Operation, Guid AdapterId, OptimisationPlan? Plan = null, string? RecoveryId = null, string? ExpectedSsidHex = null, WorkerConfiguration? Worker = null);
public sealed record AdminResponse(bool Success, string Message, JsonElement? Data = null);

public static class ElevationService
{
    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static string OperationDirectory => Path.Combine(JsonStore.Root, "operations");
    public static async Task<AdminResponse> ExecuteAsync(AdminRequest request)
    {
        Directory.CreateDirectory(OperationDirectory);
        string id = Guid.NewGuid().ToString("D"), input = Path.Combine(OperationDirectory, id + ".request.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(request, JsonStore.Options));
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the application executable.");
        if (!Path.GetFileNameWithoutExtension(executable).Equals("AirScope", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Launch AirScope.exe directly to use protected operations.");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"--admin-task {id}"
        };
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Administrator helper could not start.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            await process.WaitForExitAsync(timeout.Token);
            string output = Path.Combine(OperationDirectory, id + ".result.json");
            if (!File.Exists(output)) throw new IOException("The administrator helper returned no result. Refresh Recovery before retrying.");
            var response = JsonSerializer.Deserialize<AdminResponse>(await File.ReadAllTextAsync(output), JsonStore.Options)
                ?? throw new InvalidDataException("Invalid administrator response.");
            File.Delete(output);
            return response;
        }
        finally { File.Delete(input); }
    }
    public static async Task RunHelperAsync(string id)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed)) return;
        string prefix = Path.Combine(OperationDirectory, parsed.ToString("D"));
        AdminResponse response;
        try
        {
            if (!IsAdmin) throw new InvalidOperationException("Administrator access was not granted.");
            var requestFile = new FileInfo(prefix + ".request.json");
            if (requestFile.Length > 65536) throw new InvalidDataException("The operation request is too large.");
            var request = JsonSerializer.Deserialize<AdminRequest>(await File.ReadAllTextAsync(requestFile.FullName), JsonStore.Options)
                ?? throw new InvalidDataException("The operation request is invalid.");
            string result;
            if (request.Operation == "inspect") result = await PowerShellRunner.RunAsync(HardwareService.Script(request.AdapterId));
            else if (request.Operation == "preflight") result = await PowerShellRunner.RunAsync(RecoveryHealthService.PreflightScript(request.AdapterId));
            else if (request.Operation is "worker-install" or "worker-remove")
                result = JsonSerializer.Serialize(new { Message = await ProtectedWorkerService.ConfigureAsync(request.Worker, request.Operation == "worker-remove") });
            else
            {
                if (request.Operation is not ("apply" or "snapshot" or "rollback" or "keep")) throw new InvalidOperationException("Unsupported operation.");
                if (request.Operation == "apply")
                {
                    OptimisationPlanner.Validate(request.Plan ?? throw new InvalidOperationException("No optimisation plan supplied."));
                    if (request.Plan.AdapterId != request.AdapterId || DateTimeOffset.Now - request.Plan.CreatedAt > TimeSpan.FromMinutes(10) || request.Plan.CreatedAt > DateTimeOffset.Now.AddMinutes(1)
                        || request.ExpectedSsidHex is not { Length: > 0 and <= 64 } || !request.ExpectedSsidHex.All(Uri.IsHexDigit))
                        throw new InvalidOperationException("Adapter, plan age or expected SSID does not match this request.");
                    using var healthLimit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var health = await RecoveryHealthService.HealthAsync(request.AdapterId, request.ExpectedSsidHex, healthLimit.Token);
                    if (!health.Ready) throw new InvalidOperationException("Pre-trial health check failed: " + string.Join("; ", health.Checks.Where(c => !c.Passed).Select(c => c.Name + ": " + c.Detail)));
                }
                if (request.Operation is "rollback" or "keep" && !Guid.TryParseExact(request.RecoveryId, "D", out _))
                    throw new InvalidDataException("Invalid recovery identifier.");
                if (request.Operation == "keep")
                {
                    var expected = RecoveryHealthService.ExpectedNetwork(request.RecoveryId!);
                    if (expected.AdapterId != request.AdapterId) throw new InvalidOperationException("The trial adapter does not match the Keep request.");
                    using var healthLimit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var health = await RecoveryHealthService.HealthAsync(request.AdapterId, expected.Ssid, healthLimit.Token);
                    if (!health.Ready) throw new InvalidOperationException("The trial cannot be kept because connection health was not verified. The independent rollback remains active.");
                }
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AirScope.Windows.Services.Recovery.ps1")
                    ?? throw new IOException("The recovery engine is missing.");
                string script = await new StreamReader(stream).ReadToEndAsync();
                string payload = JsonSerializer.Serialize(new { AdapterId = request.AdapterId, Changes = request.Plan?.Changes, Id = request.RecoveryId, request.ExpectedSsidHex }, JsonStore.Options);
                result = await PowerShellRunner.RunAsync("& {" + script + "} -Operation " + PowerShellRunner.Quote(request.Operation)
                    + " -Payload " + PowerShellRunner.Quote(payload), TimeSpan.FromMinutes(6));
            }
            using var document = JsonDocument.Parse(result);
            string message = document.RootElement.TryGetProperty("Message", out var m) ? m.GetString() ?? "Operation complete." : "Hardware read complete.";
            response = new(true, message, document.RootElement.Clone());
        }
        catch (Exception e) { response = new(false, e.Message); }
        await File.WriteAllTextAsync(prefix + ".result.json", JsonSerializer.Serialize(response, JsonStore.Options));
    }

    public static IReadOnlyList<RecoveryPoint> ListRecoveryPoints()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AirScope");
        if (!Directory.Exists(root)) return [];
        List<RecoveryPoint> points = [];
        foreach (string file in Directory.EnumerateFiles(root, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var e = document.RootElement;
                if (!Guid.TryParse(e.GetProperty("Id").GetString(), out _)) continue;
                DateTimeOffset? deadline = e.TryGetProperty("RollbackAt", out var time) && time.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(time.GetString()!) : null;
                points.Add(new(e.GetProperty("Id").GetString()!, DateTimeOffset.Parse(e.GetProperty("CreatedAt").GetString()!),
                    e.GetProperty("Adapter").GetString()!, e.GetProperty("State").GetString()!, e.GetProperty("Description").GetString()!, deadline,
                    e.TryGetProperty("AdapterId", out var adapter) && Guid.TryParse(adapter.GetString(), out var adapterGuid) ? adapterGuid : null));
            }
            catch (Exception e) when (e is JsonException or IOException or KeyNotFoundException or FormatException) { /* Ignore partial/foreign snapshots. */ }
        }
        return points.OrderByDescending(p => p.CreatedAt).ToArray();
    }
}
