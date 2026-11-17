# AirScope live handover

Updated: 17 November 2026. Read this first; keep it current after every milestone and before a pause or handover.

## User scope and decisions

The user authorised **all ten roadmap improvements**, implementation/testing/packaging/publication, and a Markdown handover ready at any time. Continue concrete work, not just planning. Native Windows C#/.NET 10/WPF; MediaTek MT7925 Wi-Fi 7 and Sky XER10. Workspace `D:\wifi-analyse`; repo https://github.com/djgringoboy2003/wifi-analyser. No AGENTS.md found. Do not delegate/spawn agents without explicit permission for delegation.

Latest user answer: **“No — ship unsigned with those safeguards.”** No signing certificate is available. Ship unsigned ordinary monitoring, scheduled diagnostics and manual protected trials. **Privileged unattended tuning and automatic update installation stay gated on trusted Authenticode signing.** Do not ask about certificates again, purchase one or install a fake trust root.

## Immediate state and next actions

Version 0.2.0 implementation is currently **uncommitted**, ready for publication over v0.1 `254173eda6963721917c120ae7edfc516c6ba689`. Final source builds with zero warnings/errors and passes **77 C# checks including authenticated real loopback**, plus **18 recovery fault-injection scenarios**. Final packaged live UI smoke rendered **8 + 6 pages**, no binding errors, 8 APs and 20 installed-driver properties. Real-time telemetry reports Wi-Fi 7/6 GHz, one link, MLO inactive, width unknown (driver code 3). A real HTTPS test completed within a **32 MB total payload cap**; its 1.28-second download was explicitly data-limited and must not be treated as a line-capacity benchmark. Final windowless smoke: zero windows, ~70 MB working set. No real adapter settings or restore points were changed.

Current focus: publication and remote CI/release verification. Native self-contained WinForms setup in `src/AirScope.Setup` replaced unreliable IExpress. Final `scripts/Test-Package.ps1` **passed** setup-payload, install, marked upgrade, uninstall preservation and refusal to install/remove arbitrary unmarked folders. Tests used `-NoRegistration -NoShortcuts -NoLaunch`; user/Windows settings were untouched. JSON and daily history writes use cross-process locks; signal graphs leave monitoring gaps; trials allow 25 seconds for reconnect. README, RELEASE_NOTES, RECOVERY and SOURCES are updated. CI builds setup, checks all pages/background/package and publishes a new version through a draft release after checks pass, preserving existing public assets. The updated workflow has not run remotely yet.

Next:

1. Publish the verified source and this handover to GitHub; sync local Git metadata without overwriting the worktree.
2. Verify the updated CI/release workflow, address failures, confirm all four release assets and SHA-256 manifest/checksums. Build.ps1 supports `-Offline -NetworkTests -Publish -Setup` and optional signing. Never overwrite a public release's assets.
3. Record the final commit/run/release links here. Keep this file's acceptance/remaining gates explicit. Final edge-case checks have passed; only repeat checks for new changes/failures.
4. Real protected recovery certification remains future validation: read-only preflight may report insufficient UAC/System Restore access. No real tune, restore point, task, worker, reboot or whole-system restore was performed. User authorised unsigned shipping with those gates; do not stall publication waiting for a certificate or claim hardware rollback was proven.

## Roadmap implementation

| # | Feature | Current implementation / remaining validation |
|---|---|---|
| 1 | Multi-gigabit measurement | Parallel streams, adaptive blocks, duration/payload caps, warm-up, configurable endpoints, selected-interface IPv4/IPv6, cancellation/accounting. Final 32 MB HTTPS transfer passes; short/data-limited result is not a capacity claim. |
| 2 | Wi-Fi 7/MLO | Real-time API, bounded decoder, MLO flag/link list/RSSI, advertised versus connected width. Final live API succeeds; undocumented bandwidth 3 stays null without hiding valid signal/MLO state. |
| 3 | Diagnosis / LAN | Evidence/confidence engine, comparison with LAN, private token-protected bounded peer/client, endpoints UI. Real authenticated loopback passes; external wired peer not available. |
| 4 | Experiments | Three baseline + three trial runs, one change, configuration fingerprint, bootstrap range, upload/download/loss/latency guardrails, automatic restore on failure/inconclusive, explicit Keep or signed-worker opt-in. Actual privileged run pending. |
| 5 | Recovery | Read-only normal/protected preflight; helper enforces expected SSID, address/gateway/connectivity proof; mandatory new verified point + independent timed rollback retained. 18 mocked fault scenarios pass; real UAC/reboot recovery not exercised. |
| 6 | History | Daily JSONL, retention, events, selected-period signal buckets/gaps, filter-before-limit table, ten-second UI aggregates and scan notifications. Concurrent appends/merges are tested. |
| 7 | Profiles / loaded latency | Selected-interface TCP idle/loaded probes, Balanced/Gaming/VideoCalls/Throughput objectives; missing guardrail evidence cannot recommend Keep. |
| 8 | Automation | Sign-in option, idle/window/interval/budget/cooldown rules, notifications, normal windowless monitor, signed-only protected worker with protected binary/policy and elevated task. User accepted signing gate; no worker installed. |
| 9 | Coverage | Floor-plan import, measured point capture by room/band, connected/advertised widths, recent exact-AP speed association, local save/load, SVG export without SSID/MAC, keyboard selection. Visually inspected. |
| 10 | Distribution / access | Publisher-pinned manifest/checksum/signature staging, unsigned apply blocked; setup/install/upgrade/uninstall/refusal checks pass; input names, focus, DPI, larger text. Remote CI/release publication pending. |

## Architecture / key files

- `src/AirScope.Core`: AdvancedModels, EvidenceEngine, RealtimeDecoder, existing Models/RadioMath/IE/planner. Comparisons need repeated stable AP/endpoint/transport/stream settings; bootstrap ranges are descriptive, not causal proof.
- MainWindow split into original, `.Advanced.cs` and `.Experiments.cs` partials. `Controls/AdvancedWorkspace.cs` + `.Tools.cs` build the six new tabs; HistoryAndCoverage has chart/map controls.
- `SpeedTestService`: bounded transport-independent runner, HTTPS transport, interface network helper/TCP probes, reusable random payload. Upload rate uses acknowledged blocks; partial attempted payload still counts toward quota.
- `LanPeerService`: explicit private/loopback listener, 32-hex token, 15-minute lifetime, bounded bytes/client slots; no automatic firewall changes.
- `HistoryService`: JSONL and retention; `UsageLedger` atomically reserves quota before tests and conservatively charges abandoned reservations. Cross-instance file locking; local-midnight cancellation handles daily boundaries.
- `RecoveryHealthService`, `ElevationService`, Recovery.ps1: preflight/health and tightly scoped protected operations. `ProtectedWorkerService` verifies a valid trusted publisher before copying a bundled EXE into administrator-owned ProgramData and registering a per-user high-integrity task.
- `BackgroundMonitor` / App CLI: `--background`, `--protected-worker <guid>`, `--background-smoke`. No full window until opened or transiently needed for a protected experiment.
- `UpdateService`: repo-scoped release URL, manifest SHA-256, staged ZIP with only expected EXE extracted, Windows publisher pin, verified-only replacement retaining previous EXE. Pins use the conventional 40-hex Windows certificate thumbprint.
- Scripts: Build.ps1, Build-Setup.ps1 (publishes native `AirScope.Setup`), Install-AirScope.ps1, Sign-Artifact.ps1, Test-Recovery.ps1. All network/private test artifacts are ignored. Solution now has four projects.

## Build, environment and resume commands

Use workspace `.tools` for DOTNET_CLI_HOME and `.packages` for NUGET_PACKAGES. Set DOTNET_CLI_TELEMETRY_OPTOUT=1 and DOTNET_GENERATE_ASPNET_CERTIFICATE=false. Cached offline build: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Build.ps1 -Publish -Offline -Setup`. Omit Setup to isolate publish from installer problems. Normal CI can leave NuGet audit enabled.

`dotnet run --project tests\AirScope.Tests -c Release --no-build -- --network` needs loopback socket access; use outside the sandbox when denied. `--quality` prints only non-identifying quality metadata. Read-only live UI: `AirScope.exe --smoke-test --live --output D:\wifi-analyse\artifacts\final\result.private.json`. `--background-smoke --output ...` checks zero-window operation. `--speed-test` on a live smoke runs real payload transfers; add a small explicit test cap before another development test.

`.git` is read-only inside sandbox. Use justified escalation for Git metadata. Commands: `git -c safe.directory=D:/wifi-analyse -c core.excludesFile=D:/wifi-analyse/.gitignore ...`. Source publishing through connected GitHub tools is authorised. v0.1 CI run `44059510003` passed; v0.2 not published yet. Tools can be discovered via ALL_TOOLS; avoid credential exposure.

## Evidence, limits and safeguards

- Installed SDK 10.0.26100.0 confirms realtime header 24 bytes and link stride 272. On this PC, frequency was 6295 (MHz despite conflicting docs), width raw 3, RSSI around −56. Do not map undocumented codes by guessing; nullable width keeps valid MLO/RSSI data visible. `RealtimeDecoder` handles MHz/kHz frequencies conservatively.
- All scheduled tuning now additionally requires `_workerMode` (a signed, validated protected-worker invocation); manually elevating an unsigned GUI cannot bypass the signing gate. Manual user-started trials still use UAC, expected-SSID health checks and rollback. Keep requests validate the actual snapshot adapter identity.
- No usable CurrentUser code-signing certificate. User explicitly chose unsigned shipping with signature-dependent gates. `signtool.exe` exists under Windows Kits 10.0.26100.0 x64. No private keys exported or trust stores changed.
- No real tuning, system restore point or scheduled worker was applied. Do not silently weaken point frequency, change security/TCP/DNS/firmware, force reboot or alter Sky through undocumented APIs.
- Payload quotas exclude protocol overhead; never call them billing limits. External tests only under explicit policy or user action. LAN peers require explicit setup and suitable wired capacity.
- Never publish the user's SSID/MAC/interface GUID, local private screenshots or logs. Runtime discovery only. Restore snapshots live in protected ProgramData; history/settings in LocalAppData. Unsigned updater must not install itself automatically.
