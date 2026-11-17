# AirScope live handover

Updated: 17 November 2026. Read this first; keep it current after every milestone and before a pause or handover.

## User scope and decisions

The user authorised **all ten roadmap improvements**, implementation/testing/packaging/publication, and a Markdown handover ready at any time. Continue concrete work, not just planning. Native Windows C#/.NET 10/WPF; MediaTek MT7925 Wi-Fi 7 and Sky XER10. Workspace `D:\wifi-analyse`; repo https://github.com/djgringoboy2003/wifi-analyser. No AGENTS.md found. Do not delegate/spawn agents without explicit permission for delegation.

Latest user answer: **“No — ship unsigned with those safeguards.”** No signing certificate is available. Ship unsigned ordinary monitoring, scheduled diagnostics and manual protected trials. **Privileged unattended tuning and automatic update installation stay gated on trusted Authenticode signing.** Do not ask about certificates again, purchase one or install a fake trust root.

## Current outcome — ready to hand over

**The agreed unsigned release is implemented, verified and published.** All ten roadmap areas are delivered in **AirScope 0.2.1**. Implementation commit: `e37f09ac22535938356d9ec3fdb9cd9aee27742e`. [CI run 44082791724](https://github.com/djgringoboy2003/wifi-analyser/actions/runs/44082791724) passed both the Windows and release jobs. All four [0.2.1 release assets](https://github.com/djgringoboy2003/wifi-analyser/releases/tag/v0.2.1) are public. A final documentation-only commit updates this handover after release verification. Local metadata was synced and the worktree verified clean before that documentation update.

The patch conservatively accounts for partially failed upload writes and rejects HTTP payload replays; only acknowledged upload requests earn throughput credit. Public 0.2.0 assets and commit `d333a5147a7cea0b025c4d8bfd931989a73f3e33` are preserved; their run `44081973329` also passed. No implementation or release work remains for the agreed unsigned shipping scope. Future recovery/hardware certification boundaries below remain explicit.

## Final acceptance evidence

| Check | Result |
|---|---|
| Release build | Zero warnings/errors locally and in CI |
| C# | **80 passed**, including authenticated bounded real loopback, concurrency/quota and three upload-failure/cancellation/replay regression cases |
| Recovery fault injection | **18 passed**; mocked prerequisites, mutation, expiry, disconnection, manual/scheduled restore and failed recovery |
| Native setup | Bundled payload, isolated installation, marked upgrade, uninstall preservation and unmarked-directory refusal passed locally and in CI |
| UI | **8 main pages + 6 tool tabs**, no WPF binding errors; repeated on 0.2.1 in CI |
| Background | Zero windows; local 0.2.0 smoke ~70 MB, with zero-window startup checked again in 0.2.1 CI |
| Hardware | 0.2.0 live smoke read 8 APs and 20 driver properties; Wi-Fi 7/6 GHz, one link, MLO inactive, unknown driver width code 3 |
| Real HTTPS | 0.2.0 completed a **32 MB total payload cap**; the 1.28-second download was data-limited and does not establish line capacity |
| Release integrity | Downloaded 0.2.1 manifest/checksums match GitHub asset digests; ZIP hash matches its manifest |

`scripts/Test-Package.ps1` uses `-NoRegistration -NoShortcuts -NoLaunch` inside unique workspace directories. User/Windows settings were untouched. **No real adapter tune, restore point, rollback/protected-worker task, reboot or whole-system restore was applied during development.** Real protected recovery certification remains future validation on a suitable test Windows machine. Read-only preflight may report insufficient UAC/System Restore access. Do not stall this authorised unsigned release waiting for a certificate or describe mocked rollback as hardware proof.

## Downloads and integrity

- [Native setup](https://github.com/djgringoboy2003/wifi-analyser/releases/download/v0.2.1/AirScope-Setup.exe)
- [Portable ZIP](https://github.com/djgringoboy2003/wifi-analyser/releases/download/v0.2.1/AirScope-win-x64.zip)
- [Update manifest](https://github.com/djgringoboy2003/wifi-analyser/releases/download/v0.2.1/AirScope-update.json)
- [Checksums](https://github.com/djgringoboy2003/wifi-analyser/releases/download/v0.2.1/SHA256SUMS.txt)

Public release SHA-256 values:

```text
ba5f41f819677ad70d80742ecf7b74852389f7616ca227809c0bec247c394e2f  AirScope-win-x64.zip
bb0d020ef901f37d02f80ac254bc6c90f7037725af223b9e6055e4d3a2cfb3e8  AirScope-Setup.exe
```

Local outputs: `artifacts\AirScope-win-x64\AirScope.exe`, `artifacts\AirScope-win-x64.zip`, `artifacts\AirScope-Setup.exe`, manifest and checksum list. These are separate local builds; use their adjacent checksums, not the public CI hashes. Local 0.2.1 was checked as NotSigned, and its ZIP matches its local manifest. `AirScope.cmd` launches the local standalone app.

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
| 10 | Distribution / access | Publisher-pinned manifest/checksum/signature staging, unsigned apply blocked; setup/install/upgrade/uninstall/refusal checks pass; input names, focus, DPI, larger text. Remote CI and release publication passed. |

## Architecture / key files

- `src/AirScope.Core`: AdvancedModels, EvidenceEngine, RealtimeDecoder, existing Models/RadioMath/IE/planner. Comparisons need repeated stable AP/endpoint/transport/stream settings; bootstrap ranges are descriptive, not causal proof.
- MainWindow split into original, `.Advanced.cs` and `.Experiments.cs` partials. `Controls/AdvancedWorkspace.cs` + `.Tools.cs` build the six new tabs; HistoryAndCoverage has chart/map controls.
- `SpeedTestService`: bounded transport-independent runner, HTTPS transport, interface network helper/TCP probes, reusable random payload. Upload rate uses acknowledged blocks; partial attempted payload still counts toward quota.
- `LanPeerService`: explicit private/loopback listener, 32-hex token, 15-minute lifetime, bounded bytes/client slots; no automatic firewall changes.
- `HistoryService`: JSONL and retention; `UsageLedger` atomically reserves quota before tests and conservatively charges abandoned reservations. Cross-instance file locking; local-midnight cancellation handles daily boundaries.
- `RecoveryHealthService`, `ElevationService`, Recovery.ps1: preflight/health and tightly scoped protected operations. `ProtectedWorkerService` verifies a valid trusted publisher before copying a bundled EXE into administrator-owned ProgramData and registering a per-user high-integrity task.
- `BackgroundMonitor` / App CLI: `--background`, `--protected-worker <guid>`, `--background-smoke`. No full window until opened or transiently needed for a protected experiment.
- `UpdateService`: repo-scoped release URL, manifest SHA-256, staged ZIP with only expected EXE extracted, Windows publisher pin, verified-only replacement retaining previous EXE. Pins use the conventional 40-hex Windows certificate thumbprint.
- Scripts: Build.ps1, Build-Setup.ps1 (publishes native `AirScope.Setup`), Install-AirScope.ps1, Sign-Artifact.ps1, Test-Package.ps1 and Test-Recovery.ps1. All network/private test artifacts are ignored. Solution now has four projects. Native WinForms setup replaced the unreliable IExpress path. Installation markers/link checks protect arbitrary nonempty folders; known-file-only uninstall preserves unknown/user/recovery data without recursive deletion.

## Build, environment and resume commands

Use workspace `.tools` for DOTNET_CLI_HOME and `.packages` for NUGET_PACKAGES. Set DOTNET_CLI_TELEMETRY_OPTOUT=1 and DOTNET_GENERATE_ASPNET_CERTIFICATE=false. Cached offline build: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Build.ps1 -Publish -Offline -Setup`. Omit Setup to isolate publish from installer problems. Normal CI can leave NuGet audit enabled.

`dotnet run --project tests\AirScope.Tests -c Release --no-build -- --network` needs loopback socket access; use outside the sandbox when denied. Build.ps1 supports `-NetworkTests`; Test-Package.ps1 checks the final installer. `--quality` prints only non-identifying quality metadata. Read-only live UI: `AirScope.exe --smoke-test --live --output D:\wifi-analyse\artifacts\final\result.private.json`. `--background-smoke --output ...` checks zero-window operation. Add `--speed-test --test-mb 32` only for an explicitly intended real bounded transfer.

`.git` is read-only inside sandbox. Use justified escalation for Git metadata. Commands: `git -c safe.directory=D:/wifi-analyse -c core.excludesFile=D:/wifi-analyse/.gitignore ...`. Source publishing through connected GitHub tools is authorised. v0.1 run `44059510003`, v0.2.0 run `44081973329` and v0.2.1 run `44082791724` passed. Verify normalised local source before a metadata-only mixed reset to a published commit; never overwrite the worktree. Tools can be discovered via ALL_TOOLS; avoid credential exposure.

Main/tag pushes build/test/package, stage a draft release, upload ZIP/setup/manifest/checksums and then publish. **Existing public release assets are never overwritten.** Bump Product.Version and Directory.Build.props together for code fixes. Use a documentation-only `[skip ci]` commit for final evidence changes. Optional signing uses an existing AIRSCOPE_SIGNING_THUMBPRINT; none is configured here. The connector exposes release metadata but returned 404 for download files, so the small public manifest/checksums were verified using Invoke-WebRequest.

## Evidence, limits and safeguards

- Installed SDK 10.0.26100.0 confirms realtime header 24 bytes and link stride 272. On this PC, frequency was 6295 (MHz despite conflicting docs), width raw 3, RSSI around −56. Do not map undocumented codes by guessing; nullable width keeps valid MLO/RSSI data visible. `RealtimeDecoder` handles MHz/kHz frequencies conservatively.
- All scheduled tuning now additionally requires `_workerMode` (a signed, validated protected-worker invocation); manually elevating an unsigned GUI cannot bypass the signing gate. Manual user-started trials still use UAC, expected-SSID health checks and rollback. Keep requests validate the actual snapshot adapter identity.
- No usable CurrentUser code-signing certificate. User explicitly chose unsigned shipping with signature-dependent gates. `signtool.exe` exists under Windows Kits 10.0.26100.0 x64. No private keys exported or trust stores changed.
- No real tuning, system restore point or scheduled worker was applied. Do not silently weaken point frequency, change security/TCP/DNS/firmware, force reboot or alter Sky through undocumented APIs.
- Payload quotas exclude protocol overhead; never call them billing limits. External tests only under explicit policy or user action. LAN peers require explicit setup and suitable wired capacity.
- Real MLO-active/multiple-link, full-capacity multi-gigabit, external wired-peer and multi-room survey validation were not performed. Comparisons need complete guardrails and sufficient data/duration. Six default 256 MB reservations exceed the default 1,000 MB daily allowance; intentionally choose the cap/budget before experiments. Scheduled diagnostics remain off until the user enables and saves a policy.
- Never publish the user's SSID/MAC/interface GUID, local private screenshots or logs. Runtime discovery only. Restore snapshots live in protected ProgramData; history/settings in LocalAppData. Unsigned updater must not install itself automatically.
