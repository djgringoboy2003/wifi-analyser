# Recovery operations

Normal monitoring never needs UAC. Clicking a protected action starts another copy of the app with the fixed `--admin-task <request-guid>` mode. Requests are bounded JSON records. The helper accepts only inspect, snapshot, apply, keep or rollback and rejects unsupported settings. It rechecks the live driver rather than trusting a previously displayed plan.

The Automation tab offers a read-only preflight for the adapter, administrator token, Task Scheduler, checkpoint command and current restore-point policy/age. The normal preflight can report limited access; its protected equivalent requests UAC. Passing preflight is advisory: the apply engine still demands a **newly created, independently verified** restore point before any driver write. A recent Windows point can prevent a new one under the normal frequency policy. AirScope does not weaken that policy to make a trial run.

Apply and Keep also require the intended SSID on the original adapter, a usable address/default gateway and successful connectivity probes. The snapshot records the expected network and Keep validates its actual adapter identity. Successful property readback alone cannot keep a disconnected trial. Connectivity filtering can make this check inconclusive and cause restoration.

Snapshots and the rollback script are in `%ProgramData%\AirScope`. The engine requires an Administrators/SYSTEM-owned directory, rejects a linked recovery directory, removes inherited write permissions and grants ordinary Users read access. Snapshot IDs are GUIDs. Scheduled rollback accepts a GUID, reads only that protected snapshot, validates its adapter description and identifier, validates the restore values against the current driver, restores and verifies them, then records the result.

A file lock serialises helper and scheduled operations. An active trial prevents another trial. If a task starts while another recovery operation owns the lock, Task Scheduler retries it. Timed and startup triggers, StartWhenAvailable and bounded retries reduce missed-deadline risks. Task Scheduler, Windows services and the driver must still be functioning; a hardware failure or stopped scheduler can prevent automatic recovery. Failed recovery is visible as `RollbackFailed` and can be retried manually.

## State meanings

| State | Meaning |
| --- | --- |
| Captured | Backup-only snapshot; no adapter changes |
| Blocked | A prerequisite failed before tuning; captured settings remain available |
| Applying | Restore point verified and rollback being installed/applied |
| Pending | New driver values verified; keep before the deadline or automatic rollback occurs |
| Committed | User kept the trial; scheduled rollback removed; manual restoration remains available |
| RolledBack | Original driver values restored and read back successfully |
| RollbackFailed | Recovery encountered a driver/adapter error; task retries are retained |

## If a trial disconnects Wi-Fi

Wait for the three-minute deadline. Windows restores the captured values independently of the UI. The adapter restarts and Windows uses its saved Wi-Fi profile. If the app is open, refresh Recovery to see the result. If the PC restarted, the startup trigger attempts restoration. The app does not claim that a link is usable merely because settings were written successfully.

To restore earlier, select the trial or snapshot in Recovery and click **Restore selected snapshot**. This requests UAC and restarts the adapter. Do not change or replace drivers during an active trial: old values may no longer be supported after a driver update.

If the UI is unavailable, an administrator can invoke the fixed protected recovery script for the relevant snapshot ID:

```powershell
& "$env:ProgramData\AirScope\Rollback.ps1" -Id 'replace-with-the-snapshot-guid'
```

The scheduled script restores only unfinished trials; it will leave a committed snapshot alone. For a committed snapshot use the app's manual Restore operation, which intentionally permits restoring it.

Use **System Protection → System Restore** for system-level restoration from the separately verified Windows restore point. This may require a restart and affects more than the Wi-Fi adapter. AirScope never automatically starts a whole-system restore, changes restore-point frequency policies, deletes restore points or reboots the PC.

## Removing the portable app

Resolve pending trials first. Then exit from the tray or close the main window and delete the portable app folder. A pending task remains functional if the app folder is deleted because its script is in protected storage. After snapshots are no longer needed, an administrator can remove the matching `AirScope-Revert-<GUID>` tasks and `%ProgramData%\AirScope`. Local settings/history can be removed from `%LocalAppData%\AirScope`. These cleanup operations are manual to avoid deleting the only recovery copy.

## Repeated experiments

An experiment records the driver/configuration fingerprint, one proposed setting, three baseline results and three trial results. Reusing a baseline requires the original adapter, SSID, AP and unchanged driver configuration. Trials use at most 15 seconds per direction to leave time for recovery. Cancellation, missing evidence, a regression or an inconclusive comparison triggers restoration; the independent deadline remains active if the UI fails. A supported improvement remains Pending until the user explicitly chooses Keep.

The baseline and trial runs use the same endpoint, transport and stream configuration. Descriptive bootstrap ranges and profile guardrails help reject noisy results; they do not establish that the setting caused a speed change. All six tests require sufficient payload budget. With the default 256 MB per-test allowance and 1,000 MB daily allowance, reduce the per-test cap or intentionally raise the daily allowance before starting a complete experiment.

## Unsigned release and protected automation

The owner chose unsigned distribution. Normal monitoring, scheduled measurements and user-started UAC trials are available. **Unattended privileged tuning stays disabled** unless a trusted, signed single-file app is validated and copied with its policy into protected administrator-owned storage. Simply elevating the unsigned GUI does not enable scheduled tuning. Signed update application is gated separately on the configured trusted publisher pin. No certificate, trust root or worker is installed by default.

A future signed worker is an explicit UAC action in Automation, with idle/window/quota/cooldown rules and optional evidence-supported automatic Keep. Remove that worker through Automation before uninstalling. The per-user uninstaller refuses unfinished recovery trials, removes only recognised application files and preserves settings/history, unknown files and recovery storage.

## Validation boundary

The recovery engine passes 18 fault-injection scenarios, including failed prerequisites/writes, expiry, disconnection, manual/scheduled restoration and recovery failure. These use mocks. No real adapter tuning, restore point, protected worker, reboot or whole-system restoration was performed during this release's development checks. A real UAC/apply/disconnect/reboot recovery exercise on a suitable Windows test machine remains required before claiming hardware recovery certification. Unsigned privileged unattended tuning remains blocked in this release.
