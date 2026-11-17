using System.Security.Cryptography;
using System.Text;
using System.Windows;
using AirScope.Core;
using AirScope.Windows.Services;

namespace AirScope.Windows;

public partial class MainWindow
{
    private static string ConfigurationHash(AdapterDetails hardware) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", hardware.Properties.OrderBy(p => p.RegistryKeyword).Select(p => p.RegistryKeyword + "=" + string.Join(",", p.RegistryValue))))));
    private void SaveExperiment(ExperimentRecord experiment)
    {
        if (_smokeTest) return;
        Directory.CreateDirectory(Path.Combine(JsonStore.Root, "experiments")); JsonStore.Write("experiments/" + experiment.Id + ".json", experiment);
        AppendHistory(new(DateTimeOffset.Now, "Experiment " + experiment.State, experiment.AdapterId, experiment.Ssid, experiment.Bssid,
            Detail: experiment.Change.Name + ": " + (experiment.Comparison?.Verdict ?? "Measurements recorded")));
    }
    internal async Task<ExperimentRecord> BaselineAsync(SettingChange change, UsageProfile profile, CancellationToken token)
    {
        if (Busy) throw new InvalidOperationException("Another operation is running.");
        if (_connection is null || CurrentAp() is not AccessPoint ap || _hardware is null || _plan is null || !_plan.Changes.Contains(change))
            throw new InvalidOperationException("Load a fresh plan and select one supported change.");
        var options = _settings.TestOptions with { Upload = true };
        long remaining = _settings.Automation.DailyByteLimit - _usage.Today(DateTimeOffset.Now).UsedBytes;
        if (options.MaxBytes > remaining / 6) throw new InvalidOperationException("The daily budget must cover six complete reservations (three baseline + three trial runs). Lower the per-test cap or explicitly raise the daily budget.");
        var original = _connection; var hardware = await HardwareService.ReadAsync(original.InterfaceId);
        var experiment = new ExperimentRecord(Guid.NewGuid().ToString("D"), DateTimeOffset.Now, original.InterfaceId, original.Ssid,
            original.Bssid, hardware.DriverVersion, profile, change, [], [], null, "Measuring baseline", Options: options, ConfigurationHash: ConfigurationHash(hardware));
        _experimentBusy = true; _test = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token); SetTesting(true); SaveExperiment(experiment);
        var baseline = new List<SpeedResult>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var connection = _wifi!.Current(original.InterfaceId) ?? throw new IOException("Wi-Fi disconnected.");
                if (connection.Bssid != original.Bssid) throw new IOException("The AP changed during baseline collection. Start a fresh controlled experiment.");
                baseline.Add(await RunSpeedAsync(connection, ap.Band, _test.Token, options));
                experiment = experiment with { Baseline = baseline.ToArray() }; SaveExperiment(experiment);
                if (i < 2) await Task.Delay(1000, _test.Token);
            }
            experiment = experiment with { State = baseline.Any(r => r.DownloadSeconds < 2 || r.LoadedLatency?.Received < 5) ? "Baseline too short" : "Baseline complete" };
            SaveExperiment(experiment); return experiment;
        }
        catch { SaveExperiment(experiment with { State = "Baseline interrupted" }); throw; }
        finally { _experimentBusy = false; FinishTest(); }
    }
    internal async Task<ExperimentRecord> TrialAsync(ExperimentRecord experiment, bool scheduled, CancellationToken token)
    {
        if (Busy) throw new InvalidOperationException("Another operation is running.");
        if (experiment.State != "Baseline complete" || experiment.Options is null || DateTimeOffset.Now - experiment.CreatedAt > TimeSpan.FromMinutes(30))
            throw new InvalidOperationException("Collect a complete, recent baseline with adequately long samples first.");
        if (_connection?.Ssid != experiment.Ssid || _connection.Bssid != experiment.Bssid || _adapterId != experiment.AdapterId)
            throw new InvalidOperationException("Reconnect the original adapter and access point before trialling this baseline.");
        var hardware = await HardwareService.ReadAsync(experiment.AdapterId);
        if (hardware.DriverVersion != experiment.DriverVersion || ConfigurationHash(hardware) != experiment.ConfigurationHash)
            throw new InvalidOperationException("The driver or its configuration changed. Collect a new baseline.");
        if (experiment.Options.SecondsPerDirection > 15) throw new InvalidOperationException("Use at most 15 seconds per direction so three trial runs fit within recovery's deadline.");
        long remaining = _settings.Automation.DailyByteLimit - _usage.Today(DateTimeOffset.Now).UsedBytes;
        if (experiment.Options.MaxBytes > remaining / 3) throw new InvalidOperationException("The remaining daily payload quota cannot cover all three trial runs.");
        if (!scheduled && MessageBox.Show(this, $"Trial only ‘{experiment.Change.Name}’: {experiment.Change.Before} → {experiment.Change.After}?\n\nA verified new restore point and independent rollback task are mandatory. Three measurements will follow. Regressions, incomplete tests or inconclusive evidence restore automatically. A supported improvement still requires Keep unless you enabled automatic keeping in the protected worker policy.",
            "Measure one protected change", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) throw new OperationCanceledException("Trial cancelled.");
        _experimentBusy = true; _benchmark = true;
        string? recovery = null; var trial = new List<SpeedResult>(); bool leavePending = false;
        try
        {
            var plan = new OptimisationPlan(experiment.AdapterId, hardware.Name, [experiment.Change], [], DateTimeOffset.Now);
            var response = await AdminAsync(new("apply", plan.AdapterId, plan));
            recovery = response.Data?.GetProperty("Id").GetString() ?? throw new IOException("The helper did not identify the trial snapshot.");
            _usage.RecordDisruption(DateTimeOffset.Now);
            var point = ElevationService.ListRecoveryPoints().Single(p => p.Id == recovery);
            var deadline = point.RollbackAt ?? throw new IOException("The rollback deadline is missing.");
            _test = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            var remainingTime = deadline - DateTimeOffset.Now - TimeSpan.FromSeconds(25);
            if (remainingTime <= TimeSpan.Zero) throw new IOException("Insufficient trial time remains. Restoring the snapshot.");
            _test.CancelAfter(remainingTime); SetTesting(true);
            experiment = experiment with { RecoveryId = recovery, State = "Measuring trial" }; SaveExperiment(experiment);
            var reconnectUntil = DateTimeOffset.Now.AddSeconds(25);
            while (_wifi!.Current(experiment.AdapterId)?.Ssid != experiment.Ssid && DateTimeOffset.Now < reconnectUntil)
                await Task.Delay(500, _test.Token);
            for (int i = 0; i < 3; i++)
            {
                var current = _wifi!.Current(experiment.AdapterId) ?? throw new IOException("The trial adapter is disconnected.");
                var ap = _networks.FirstOrDefault(p => p.Bssid == current.Bssid) ?? throw new IOException("The trial AP has not been observed.");
                trial.Add(await RunSpeedAsync(current, ap.Band, _test.Token, experiment.Options));
                experiment = experiment with { Trial = trial.ToArray() }; SaveExperiment(experiment);
                if (i < 2) await Task.Delay(1000, _test.Token);
            }
            var comparison = EvidenceEngine.Compare(experiment.Baseline, trial, experiment.Profile);
            experiment = experiment with { Comparison = comparison, State = comparison.RecommendKeep ? "Improvement pending Keep" : "Restoring baseline" };
            leavePending = comparison.RecommendKeep; SaveExperiment(experiment);
            return experiment;
        }
        catch { SaveExperiment(experiment with { RecoveryId = recovery, Trial = trial.ToArray(), State = "Trial interrupted; restoring" }); throw; }
        finally
        {
            _test?.Dispose(); _test = null;
            if (recovery is not null && !leavePending)
            {
                try { await AdminAsync(new("rollback", experiment.AdapterId, RecoveryId: recovery)); SaveExperiment(experiment with { State = "Baseline restored", Trial = trial.ToArray() }); }
                catch (Exception error) { AppendHistory(new(DateTimeOffset.Now, "Recovery requires attention", experiment.AdapterId, Detail: error.Message)); Status("The independent rollback remains responsible for recovery. Open Recovery to verify its state."); }
            }
            _experimentBusy = false; _benchmark = false; FinishTest(); RefreshRecovery();
        }
    }
    private async Task RunScheduledTrialAsync(AutomationPolicy policy)
    {
        var day = _usage.Today(DateTimeOffset.Now);
        var decision = AutomationRules.Evaluate(policy, DateTimeOffset.Now, DesktopIntegration.IdleTime(), null, day.LastDisruption,
            day.UsedBytes, _settings.TestOptions.MaxBytes * 6, Busy, _connection is not null, disruptive: true);
        if (!decision.Run) { Advanced.AutomationStatus(decision.Reason); return; }
        if (!_workerMode || !ElevationService.IsAdmin) { Advanced.AutomationStatus("A scheduled tuning trial needs the signed protected worker. Unsigned builds can monitor and schedule diagnostics."); return; }
        var plan = await PreparePlanAsync(); if (plan.Changes.Count == 0) return;
        var baseline = await BaselineAsync(plan.Changes[0], policy.Profile, _shutdown.Token);
        var trial = await TrialAsync(baseline, scheduled: true, _shutdown.Token);
        if (policy.AutoKeepSupportedTrials && trial.Comparison?.RecommendKeep == true && trial.RecoveryId is string recovery)
        {
            await AdminAsync(new("keep", trial.AdapterId, RecoveryId: recovery));
            SaveExperiment(trial with { State = "Kept after evidence and health verification" });
        }
    }
}
