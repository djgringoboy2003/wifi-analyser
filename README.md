# AirScope

A native Windows Wi-Fi manager, analyser and reversible optimiser, built for a **MediaTek MT7925 Wi-Fi 7 adapter and Sky Gigafast+ Hub (XER10)**. C# / .NET 10 / WPF; no browser shell, third-party NuGet dependencies, account or telemetry.

## Run

**On the development PC:** double-click `AirScope.cmd`. It launches the standalone executable when available, or builds from source using the .NET 10 SDK.

**Portable build:** extract `AirScope-win-x64.zip` and run `AirScope.exe`. The published build includes .NET; no runtime installation is needed. It is unsigned. Windows can show the normal publisher/SmartScreen warning for an unsigned download.

**From GitHub:** open **Actions → Windows build and safety checks → latest successful run → Artifacts → AirScope-windows-x64**. Download the artifact, then extract the included application ZIP. Builds run on pushes to `main` and can also be started manually.

Windows 11 x64 is the primary target. Wi-Fi 6E/7 support depends on Windows, the adapter, its driver, the hub and regional rules. Normal monitoring runs without administrator privileges. Protected hardware reads and tuning request Windows UAC on demand.

On first use, complete a scan. If Windows restricts access, use **Location settings**, enable the appropriate device/user/desktop-app permission, then scan again. The current SSID becomes the analysis target automatically. The app never bypasses Windows privacy permissions.

## What's included

| Workspace | Capabilities |
| --- | --- |
| Overview | Live Wi-Fi download/upload, signal, negotiated RX/TX rates, gateway, security, 120-second chart, session data totals and your SSID's three-band summary |
| Channel explorer | 2.4 / 5 / 6 GHz footprints, exact target-SSID filtering, advertised width/center parsing, neighbour overlap model and channel ranking |
| Nearby networks | One row per BSSID; search/sort/filter by band and SSID; RSSI, channel, width, standard, security, advertised load/station count and last-seen information |
| Speed & diagnostics | Wi-Fi-bound HTTPS download/upload tests, measured history, sequential band comparison, gateway/public ICMP latency, jitter, loss and system DNS checks |
| Optimiser | Plans derived from MT7925 driver options and observations of the connected SSID; verified restore point, snapshot, settings verification and timed rollback |
| Hardware & hub | Adapter selector, installed driver version/options, gateway admin link and supported Sky settings link |
| Recovery & settings | Keep/restore trials, manual snapshots, automated scanning, tray behaviour, floating speed widget, redacted HTML/CSV export |

Monitoring and scanning start automatically. Speed tests, network changes and tuning start when requested in the app. Minimise to keep monitoring in the tray; closing exits. The speed widget can be dragged and double-clicked to reopen the main window.

## How protected tuning works

1. **Analyse & build plan** reads current driver values and the connected target SSID. It proposes only supported changes from a narrow MT7925 allowlist. Values are discovered from the installed driver's valid-value mappings.
2. **Trial this plan** shows the exact changes and requests UAC. The helper verifies that the adapter and original values still match.
3. Original values are saved under `%ProgramData%\AirScope`, accessible for writing only to Administrators and SYSTEM.
4. A **new Windows System Restore point must be created and verified**. If System Protection is off, access is denied, or Windows' restore-point frequency limit prevents it, tuning stops before changing the adapter. AirScope does not weaken the restore-point policy.
5. A fixed rollback script and a SYSTEM Task Scheduler task are installed before any setting is written. The task runs after three minutes, or on startup if the PC restarts. The script and its snapshot live in protected storage; the task never executes a user-writable application binary.
6. The helper applies the plan, restarts the adapter and reads the settings back to verify them. A failure triggers immediate restoration; failed restoration remains visible and the task retains retries.
7. Reconnect and test, then choose **Keep selected trial** in Recovery. Otherwise the scheduled task restores the original values even if AirScope closes. Kept snapshots remain available for manual restoration.

Automatic changes are limited to `PreferredBand`, `BandSelection`, `BWSelection24G`, `BWSelection5G`, `BWSelection6G`, `CurrPhyMode` and `LowPowerEnable`. The current planner proposes band preference with fallback, all-band availability, Wi-Fi 7, automatic 5/6-GHz widths and optional desktop power-saving changes. It leaves security, TCP, DNS, firmware, transmit-power/regulatory rules and unknown adapters alone. It cannot guarantee a speed increase.

Windows may limit restore-point creation to one per 24 hours. The app blocks tuning when it cannot verify a fresh point. **Save adapter snapshot** is a separate backup-only action; it does not create a Windows restore point or change adapter settings. See [Recovery](docs/RECOVERY.md).

## Measurements and limitations

- **2.4 GHz** is the Wi-Fi band; 2.5 Gbps can refer to an internet or Ethernet speed.
- **Live Mbps** is traffic through the selected Wi-Fi interface. **PHY Mbps** is its negotiated radio rate. **Speed-test Mbps** is measured HTTPS payload throughput to Cloudflare. They answer different questions.
- The speed-test sockets bind to the selected adapter's usable IPv4 address and disable application proxy routing. The test may fail on IPv6-only, proxy-only or VPN-enforced connections. ICMP and DNS diagnostics follow Windows routing.
- One test downloads up to **128 MB** and optionally uploads **16 MB**. Band comparison repeats this for each observed band. These are bounded estimates, affected by competing traffic, protocol overhead, server capacity and sample length.
- Comparison temporarily requests each band using the existing Windows profile and a desired BSSID. It verifies association before measuring, discards results if the BSSID changes, and restores the original connection in `finally`. Windows/driver behaviour can prevent association. Keep the app open; closing during comparison first cancels and attempts reconnection. A PC crash or forced termination cannot run that in-process cleanup; no permanent band restriction is written.
- Channel width comes from HT/VHT/HE/EHT beacon operation elements when present. **Not reported** means unavailable. Advertised width is not necessarily the width used for every packet. An 80+80 operation is not labelled as one contiguous 160-MHz channel.
- Overlap scores are a **relative planning model**, not measured spectrum noise, utilisation or predicted speed. Hidden stations and non-Wi-Fi interference are not detected. The optional BSS Load information is AP-advertised, not a local airtime measurement. The UK 6-GHz planning view covers the lower band; the driver/hub remain the authority on permitted channels.
- Wi-Fi 7 capability does not establish that Multi-Link Operation is active. This version does not expose individual MLO-link telemetry.
- The hub model is configured from the intended setup, not authenticated from its SSID. Sky hub controls remain in Sky's supported interfaces. AirScope does not use undocumented router APIs or silently change router channels, firmware, SSIDs or credentials.
- Scans wait for asynchronous driver results and omit beacons older than 90 seconds. Last-seen times remain available; cached data can still appear. Scanning is paused while tests or protected operations run.

## Data and privacy

`%LocalAppData%\AirScope` holds settings, up to 250 speed-test results, operation request/results and error logs. `%ProgramData%\AirScope` holds protected recovery snapshots, the transaction lock and fixed rollback script. Windows holds its own restore points and scheduled tasks. Wi-Fi passwords are never requested, read or exported.

HTML reports and CSV exports prompt for identifiers and default to redaction. Raw exports can contain other nearby SSIDs and BSSIDs, so choose deliberately before sharing. HTML values are escaped and CSV cells protect against spreadsheet formula injection. Local diagnostic screenshots and hardware output are excluded from source control.

External requests happen for user-started Cloudflare speed tests, public ICMP probes, DNS checks and clicked links. The app has no analytics or automatic updater. No installer, driver or system setting is changed by building the source.

## Build and verify

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows, then:

```powershell
.\scripts\Build.ps1          # compile + core/integration tests + mocked recovery tests
.\scripts\Build.ps1 -Publish # also create a self-contained executable and ZIP
```

There are no third-party application packages. A self-contained publish downloads Microsoft's runtime packs from NuGet. The unprivileged build can run offline after those packs are cached.

```powershell
# Render all seven screens without accessing Wi-Fi
.\src\AirScope.Windows\bin\Release\net10.0-windows\AirScope.exe --smoke-test --output "$pwd\artifacts\ui-smoke\result.private.json"

# Optional real-hardware scan and rendering (no tuning, band switching or speed test)
.\artifacts\AirScope-win-x64\AirScope.exe --smoke-test --live --output "$pwd\artifacts\live-smoke\result.private.json"
```

Add `--speed-test` to an optional live smoke test to measure the current connection; this uses up to 144 MB. Smoke output and screenshots are private local artifacts. Recovery tests replace Windows adapter, restore-point and scheduling commands with mocks and redirect all file writes to a unique workspace test folder. They do not tune the development PC.

Source layout:

```text
src/AirScope.Core       Models, radio maths, beacon parsing, traffic/latency calculations, planner
src/AirScope.Windows    WPF UI/charts, Native Wi-Fi interop, diagnostics, protected recovery engine
tests/AirScope.Tests    Dependency-free executable test suite
scripts                Build, launcher and recovery fault-injection tests
docs                   Recovery, implementation notes and source references
```

## References and inspiration

The app is an original implementation. The supplied projects were reviewed for feature ideas; their source was not copied or bundled. In particular, NetSpeedTray's GPL code and unlicensed repository code are not incorporated. See [sources and technical decisions](docs/SOURCES.md).

MIT licensed. This is an initial release; real hardware changes and full Windows System Restore have not been exercised during development. Verify recovery on a test PC before relying on it for unattended changes.
