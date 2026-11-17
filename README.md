# AirScope 0.2

Native Windows Wi-Fi intelligence for a **MediaTek MT7925 Wi-Fi 7 adapter and Sky Gigafast+ Hub (XER10)**: live monitoring, channel analysis, repeatable measurements, controlled optimisation, recovery and coverage surveys.

C# / .NET 10 / WPF, with a lightweight tray mode and native per-user setup. No browser shell, cloud account, telemetry or third-party application packages.

## Get started

- **Install:** download `AirScope-Setup.exe` from [Releases](https://github.com/djgringoboy2003/wifi-analyser/releases), run it and review the destination folder. Setup includes .NET, adds an optional Start menu shortcut and installs for the current user. No admin rights are needed for installation.
- **Portable:** extract `AirScope-win-x64.zip` and run `AirScope.exe`. No runtime installation is needed. `Install-AirScope.ps1` is also included for scripted per-user installation.
- **Development PC:** double-click `AirScope.cmd`; it opens the standalone build if present or builds with the .NET 10 SDK.
- **Build artifacts:** [Actions](https://github.com/djgringoboy2003/wifi-analyser/actions) also provides the portable ZIP, native setup, update manifest and checksums after a successful build.

**This release is unsigned.** Windows may display its normal unknown-publisher warning. Ordinary monitoring, scheduled diagnostics and user-started protected trials work without a signing certificate. Privileged unattended tuning and automatic update installation remain blocked until a trusted publisher signature is configured. No certificate or trust root is installed for you.

Windows 11 x64 is the primary target. Normal monitoring runs without administrator privileges. Location consent may be needed for nearby SSIDs/BSSIDs: use **Location settings**, grant the appropriate device/user/desktop-app permission and scan again. The connected SSID becomes the default target. The app respects Windows privacy controls.

## Workspaces

| View | What it provides |
|---|---|
| Overview | Wi-Fi traffic, signal, RX/TX PHY rates, gateway/security, 120-second traffic chart and your SSID's band observations |
| Channel explorer | 2.4/5/6 GHz AP footprints, advertised widths, SSID filtering and a relative channel-overlap model |
| Nearby networks | One row per BSSID, band/channel/RSSI/security, advertised load/stations, last-seen details and saved-profile connections |
| Speed & diagnostics | Parallel bounded HTTPS tests, IPv4/IPv6 preference, duration/stream/payload controls, band comparison, gateway/public probes and DNS |
| Optimiser | A driver-aware MT7925 plan, original-value snapshot, verified new restore point, independent rollback and explicit Keep |
| Hardware & hub | Driver/version/options, real-time Windows link/MLO information, hub admin and supported Sky settings |
| Recovery & settings | Trial state/countdown, manual restoration, snapshots, scanning, tray/widget and redacted exports |
| History & tools | Historical events/signal, evidence-based diagnosis, LAN tests/peer, repeated experiments, floor-plan surveys, automation/preflight and verified update staging |

## Measurements you can trust

**Live Mbps** is aggregate traffic through the selected Wi-Fi adapter. **PHY Mbps** is the negotiated radio rate. **Measured Mbps** is synthetic payload throughput to a test endpoint. A 2.88 Gbps link rate does not mean the internet connection delivers that speed. The Wi-Fi band is **2.4 GHz**; 2.5 Gbps is a possible service/Ethernet rate.

The new test engine uses 1–8 parallel streams, a short warm-up, adaptive request sizes, consistent wall-clock timing and 2–30 seconds per direction. The default is four streams, ten seconds per direction and a **256 MB total payload cap**. The cap can end a fast test early. Increase the visible cap and daily allowance deliberately when measuring multi-gigabit links; short tests are flagged rather than treated as a speed ceiling.

Download reads and upload writes are counted against the payload budget. Upload throughput uses acknowledged complete requests; partial attempted uploads still consume quota. Warm-up counts toward the cap but not the throughput result. Protocol overhead and traffic buffered beyond application payload are additional, so these are **test payload limits, not ISP billing limits**.

Test sockets bind to the selected adapter's IP address. Internet endpoints require HTTPS; proxies are disabled for these measurements. Prefer IPv6 when available, or use a suitable IPv4 address. Enforced VPN routing, proxy-only networks, host load, endpoint capacity and competing traffic can affect or prevent a test.

Idle and loaded latency use TCP connection probes through the selected Wi-Fi source address. They are labelled separately from the ICMP gateway/public diagnostic probes, which follow Windows routing. A filtered probe is not proof of a Wi-Fi fault.

**Compare available bands** requests each observed band using a saved Windows profile and desired BSSID, verifies actual association, discards results if the AP changes and restores the original connection in `finally`. Closing during comparison first cancels and attempts restoration. Forced process termination/power loss cannot execute that in-process cleanup; no permanent band restriction is written. All bands share the daily quota.

## Find the likely bottleneck

In **History & tools → Diagnosis & LAN**, build a diagnosis from the current link, reported telemetry, recent matching measurements and gateway/internet evidence. Each finding includes confidence, evidence and a next action. Comparisons use recent measurements of the same SSID/AP; unrelated historical tests do not describe the current connection.

For a local-network baseline:

1. Run AirScope on a suitable **wired** peer. Open **Diagnosis & LAN → Run a peer**, enter that computer's private LAN IP and start it.
2. On the Wi-Fi PC, enter the peer's IP:port and its 32-hex access token, then choose **Test LAN connection**.
3. Compare LAN and internet throughput with the same AP and similar conditions. The peer's Ethernet/CPU capacity must be sufficient; a slow peer is not proof that Wi-Fi is slow.

The peer binds only a private or loopback address, accepts synthetic authenticated transfers, limits clients and payload, and stops after 15 minutes. No firewall rule is silently added. If Windows asks, permit only the intended private network. IPv6 link-local peers need the correct scope ID. Tokens are not Wi-Fi passwords.

Custom internet endpoints must accept the documented raw upload POST and download `bytes` query parameter (or a `{bytes}` placeholder). They are not arbitrary browser speed-test pages.

## Prove an optimisation before keeping it

The ordinary planner proposes only supported MT7925 driver options discovered from the installed driver: preferred band with fallback, all-band availability, Wi-Fi 7, automatic 5/6-GHz widths and optional desktop power-saving changes. Security, DNS, TCP, regulatory/transmit-power rules, firmware and unknown adapters are left alone.

In **History & tools → Experiments**:

1. Load a fresh plan, choose **one** setting and select Balanced, Gaming, VideoCalls or Throughput.
2. Record **three baseline runs**. The daily quota must cover six full reservations, and the samples must be long enough to assess guardrails.
3. Trial the selected change. The helper validates the original driver/configuration/SSID, snapshots the values, verifies a new restore point and installs independent rollback before writing settings.
4. Record **three trial runs** under the same measurement conditions. Use at most 15 seconds per direction so the trial fits its recovery deadline.
5. Review the median change, descriptive 95% bootstrap range and download/upload/loaded-latency/loss guardrails. Different APs, missing evidence and incomplete tests remain inconclusive. Failed, regressed or inconclusive trials restore automatically. A supported improvement requires **Keep** before the deadline.

Gaming and VideoCalls evaluate loaded latency with throughput/loss guardrails; throughput objectives evaluate download performance while checking uploads and latency. Bootstrap ranges describe these observations and do not establish causation or guarantee future performance. Performance may not improve.

## Protected recovery

Protected actions request Windows UAC as needed. The engine accepts a small allowlist of numeric installed-driver settings, verifies the adapter/current values and records originals in administrator/SYSTEM-owned `%ProgramData%\AirScope` storage. It never runs arbitrary shell commands from an SSID or user-supplied script.

Every tuning trial requires:

- A **new Windows restore point**, identified and verified after creation. A disabled System Protection setting, access denial or Windows restore-point frequency limit stops tuning before mutation. The app does not weaken that policy.
- An independent scheduled rollback task, installed before setting writes, with a three-minute deadline/startup trigger and retries.
- Driver readback after applying/restoring values, and expected-SSID/address/connectivity health checks before Keep.

An app crash does not remove the scheduled rollback. A reboot triggers its startup recovery path; Windows services, storage and the driver still have to function. Whole-system restore/reboot is never started automatically. **Save adapter snapshot** captures values without creating a restore point or applying tuning. See [recovery details](docs/RECOVERY.md).

Read-only normal/protected preflight checks are available in **Automation**. They provide readiness evidence; actual creation and verification are repeated before every trial.

**Validation boundary:** recovery failure paths have automated fault-injection coverage. Real UAC, restore-point creation, adapter mutation, reboot and complete system restoration have not all been exercised on the development PC. Privileged unattended features remain gated in this unsigned release. Do not treat simulated tests as proof of physical recovery.

## History, surveys and automation

- The GUI records ten-second traffic rollups, signal observations and connection/channel/driver/trial events. The lightweight monitor records connected-link observations without repeatedly scanning nearby networks. Retention is configurable from 1–365 days.
- The history chart averages time buckets across the selected period. The table shows the latest 5,000 matching records, with event filtering before the limit. Original samples remain in dated local JSONL files until retention removes them.
- Import a floor plan, click or use arrow keys/Enter to select a location, enter a room label and capture your recently scanned SSID. Band filters compare measured points. Unmeasured space is not painted as measured coverage. Exact-AP speed results are attached only when recent.
- Surveys save locally and export SVG without SSIDs/MACs. The exported floor plan and room labels are still personal information to review before sharing.
- Scheduled diagnostics are **off by default**. Enable an interval, idle threshold, maintenance window, daily payload budget and alerts. Equal start/end hours means all day. Quota is reserved before a test; failed/cancelled tests refund only unspent payload. Interrupted reservations remain charged until midnight.
- Sign-in starts `--background`, a windowless tray monitor. Opening it creates the full UI; optional close-to-tray behaviour is separate. The current background smoke used about 66 MB with zero windows; usage varies by machine.
- Unattended tuning additionally requires a valid signed bundled executable, publisher pin and explicit protected-worker configuration. Its copied executable/policy are protected in ProgramData; its high-integrity task runs for that logged-in user. Unsigned builds cannot install this worker, even when the ordinary GUI is started as administrator.

## Wi-Fi telemetry limits

Real-time Windows telemetry reports MLO state, link count, frequency, RSSI and bandwidth where the OS/driver supplies recognised values. Wi-Fi 7 capability alone does not establish MLO activity. Unsupported fields stay unknown. This MT7925 driver returned an undocumented bandwidth code; the app preserves valid MLO/signal data and leaves that width unreported instead of guessing a MHz value.

AP advertised width is parsed separately from HT/VHT/HE/EHT beacon information. It is not a per-packet or guaranteed connected width. Noncontiguous 80+80 is not labelled as contiguous 160 MHz. AP load/station count appear only when advertised.

Channel overlap is a relative signal/footprint model, not RF spectrum, noise, airtime or predicted speed. Hidden stations and non-Wi-Fi transmitters are not measured. The 6-GHz planning display uses the UK lower band; the driver/hub enforce allowed channels. Scan notifications have a bounded fallback and last-seen timestamps remain visible.

Sky controls stay in [Sky's supported settings](https://www.sky.com/help/articles/change-wifi-max-hub-settings). The configured XER10 model is not authenticated from an SSID. No undocumented hub API, silent channel change or firmware automation is assumed.

## Updates, installation and privacy

Update checks are user-started and read only this repository's releases. Downloads are checked against the release SHA-256. **Automatic application additionally requires a valid trusted Authenticode signature matching a saved Windows publisher thumbprint**; an unsigned or mismatched build is not automatically installed. The previous executable is retained during a verified replacement.

Per-user setup uses a marked app directory, optional Start menu shortcut and Windows uninstall entry. Run the installed `Uninstall-AirScope.cmd` or Windows Apps settings to remove it. Close the installed app and resolve active trials first. Unknown files, personal settings/history and protected recovery copies are preserved. Remove a configured protected worker through Automation before uninstalling.

Local data: `%LocalAppData%\AirScope` holds settings, up to 250 speed results, dated history, quota ledger, experiments, surveys and update staging. `%ProgramData%\AirScope` holds protected recovery/worker state. Windows manages restore points/tasks separately. Wi-Fi passwords are never requested, read or exported.

HTML and CSV exports default to redacting SSIDs/MACs; HTML escapes values and CSV protects spreadsheet cells. Private test screenshots, logs and local data are excluded from source control. Internet tests, TCP/ICMP/DNS diagnostics, update checks and clicked links make the described network requests. There is no analytics or advertising.

## Build and verify

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows:

```powershell
.\scripts\Build.ps1                              # compile + regular/simulated recovery checks
.\scripts\Build.ps1 -NetworkTests                 # also authenticated loopback peer test
.\scripts\Build.ps1 -Publish -Setup               # portable app + native setup + manifests/hashes
.\scripts\Build.ps1 -Publish -Setup -Offline      # cached runtime packs, restricted/offline environment
```

Optional future signing uses a trusted certificate already in the Windows store via `AIRSCOPE_SIGNING_THUMBPRINT`; `AIRSCOPE_TIMESTAMP_URL` can configure timestamping. Build does not create certificates, alter trust stores or buy a signing service. Current shipping policy is unsigned.

```powershell
# All 8 main screens + 6 nested tools; no hardware access
.\artifacts\AirScope-win-x64\AirScope.exe --smoke-test --output "$pwd\artifacts\ui\result.private.json"

# Read-only real scanner/hardware/telemetry check
.\artifacts\AirScope-win-x64\AirScope.exe --smoke-test --live --output "$pwd\artifacts\live\result.private.json"

# Optional bounded real test; 32 MB total payload
.\artifacts\AirScope-win-x64\AirScope.exe --smoke-test --live --speed-test --test-mb 32 --output "$pwd\artifacts\speed\result.private.json"

# Windowless monitor smoke (no scheduled internet tests)
.\artifacts\AirScope-win-x64\AirScope.exe --background-smoke --output "$pwd\artifacts\background\result.private.json"
```

The setup executable supports `--verify-payload <output.json>` for packaging checks without installing anything. Recovery tests mock Windows mutations and redirect their files into the workspace. The test installer uses a dedicated artifact directory with registration, shortcuts and launch disabled.

Source projects: Core (models/algorithms), Windows (WPF/services), Setup (bundled native installer), Tests (dependency-free executable checks). See [HANDOVER.md](HANDOVER.md) for live state and exact latest verification, [release notes](docs/RELEASE_NOTES.md), [recovery](docs/RECOVERY.md), and [sources](docs/SOURCES.md).

Original MIT-licensed implementation. Supplied reference projects informed features; their code/assets were not copied or bundled, including NetSpeedTray GPL code.
