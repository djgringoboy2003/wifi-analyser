# AirScope 0.2.0

This release expands the Windows Wi-Fi manager into a measurement and recovery workspace for the MediaTek MT7925 and Sky XER10 setup.

- Parallel, duration/payload-bounded interface-bound IPv4/IPv6 tests, adaptive requests, warm-up, idle/loaded TCP latency and configurable endpoints.
- Real-time Windows MLO/link telemetry with bounded parsing and honest unknown-field handling; AP advertised and connected widths are separate.
- Evidence-based diagnosis, authenticated private LAN peer/client, historical events/signal and retention.
- Three-before/three-after single-setting experiments, descriptive bootstrap ranges and download/upload/latency/loss guardrails.
- Recovery preflight, expected-SSID health verification and stronger Keep validation; independent rollback/verified restore-point requirements retained.
- Floor-plan/room measurements, local surveys and SVG exports.
- Windowless monitoring, sign-in option, idle/window/interval policies, daily quota reservations, cooldowns and alerts.
- Native per-user setup/uninstall, optional signing tooling, release manifests/checksums and verified update staging.
- Keyboard focus/labelled controls, DPI support and larger-text controls.

## Shipping safeguards

The user chose **unsigned distribution**. No certificate was installed or purchased. Ordinary monitoring and scheduled diagnostics work. Privileged unattended tuning and automatic update application remain gated on a valid trusted publisher signature. Starting an unsigned GUI as administrator does not bypass that gate.

Manual protected trials require Windows UAC, a newly verified restore point, an independent rollback task and connection-health checks. Recovery fault injection is automated; a full real hardware/UAC/reboot/system-restore certification has not been completed. These limits are shown in the app documentation and live handover.

Driver bandwidth codes not expressed in recognised units remain unreported; valid MLO/signal information stays available. Speed results are bounded estimates, and short/data-limited samples do not establish the connection's ceiling. LAN peer capacity, VPN/routing, endpoint and host load still matter. Sky settings stay in its supported interfaces.

See HANDOVER.md for final test counts, build links and any remaining validation gates.
