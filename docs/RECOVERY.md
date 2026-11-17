# Recovery operations

Normal monitoring never needs UAC. Clicking a protected action starts another copy of the app with the fixed `--admin-task <request-guid>` mode. Requests are bounded JSON records. The helper accepts only inspect, snapshot, apply, keep or rollback and rejects unsupported settings. It rechecks the live driver rather than trusting a previously displayed plan.

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
