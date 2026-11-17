# Sources and implementation decisions

References checked while implementing this initial release on 17 November 2026. Links are upstream documentation, not runtime dependencies.

## Windows APIs

- [Native Wi-Fi API](https://learn.microsoft.com/en-us/windows/win32/api/_nwifi/): WLAN interface enumeration, connection/profile operations and scan access.
- [WlanGetNetworkBssList](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlangetnetworkbsslist), [WLAN_BSS_ENTRY](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/ns-wlanapi-wlan_bss_entry) and [WLAN_RATE_SET](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/ns-wlanapi-wlan_rate_set): native buffer layout and advertised information elements. Parser lengths, offsets, entry count and total buffer size are bounded; beacon contents are untrusted.
- [Wi-Fi access and location changes](https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes): modern Windows gates scan/BSSID/connection information behind location consent.
- [WlanConnect](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlanconnect) and [WLAN_CONNECTION_PARAMETERS](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/ns-wlanapi-wlan_connection_parameters): saved-profile connections and desired BSSID requests. A successful request is not proof of association; the app polls and verifies actual BSSID.
- [DOT11_PHY_TYPE](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/windot11/ne-windot11-_dot11_phy_type): HE is 10, EHT is 11; this matters when distinguishing Wi-Fi 6 from Wi-Fi 7.
- [Checkpoint-Computer](https://learn.microsoft.com/en-us/powershell/module/Microsoft.PowerShell.Management/checkpoint-computer?view=powershell-5.1): system restore-point behaviour and frequency restrictions. The app requires a newly verified point before tuning.

Native interop uses the stable desktop WLAN API directly, not localised `netsh` output. PowerShell uses the installed NetAdapter module for protected adapter properties; production scripts interpolate only quoted JSON or GUIDs, never unescaped SSID names or arbitrary shell commands.

## Sky

- [XER10 product page](https://www.sky.com/help/page/sky-gigafast-plus-hub).
- [Sky Wi-Fi settings](https://www.sky.com/help/articles/change-wifi-max-hub-settings).
- [Gigafast+ overview](https://www.sky.com/broadband/full-fibre-broadband/gigafast-plus).

Hub controls stay in Sky's supported interfaces. Router capabilities are supplemented by live observations of the configured SSID; no undocumented administrative API is assumed.

## User-provided project references

| Project | Reviewed for | Reuse |
| --- | --- | --- |
| [hov172/WinWiFiAnalyzer](https://github.com/hov172/WinWiFiAnalyzer) | Diagnostic workflow, channel intelligence and reporting | Feature inspiration; no copied source |
| [yunnysunny/wifi-rainbow](https://github.com/yunnysunny/wifi-rainbow) | Wi-Fi visualisation and channel presentation | Feature inspiration; no copied source |
| [Kurulko/WiFi-Analyzer](https://github.com/Kurulko/WiFi-Analyzer) | Windows analysis workflow | Feature inspiration; no copied source |
| [calanchenlins/WiFiManager](https://github.com/calanchenlins/WiFiManager) | Connection/profile and BSSID-oriented management | Feature inspiration; no copied source |
| [erez-c137/NetSpeedTray](https://github.com/erez-c137/NetSpeedTray) | Compact traffic display and tray workflow | Feature inspiration; no copied or bundled GPL source/assets |

## Validation boundary

Automated core tests cover channel conversion, bonded overlap, malformed/truncated beacon parsing, model limits, planner value mappings, unsafe execution requests, monotonic counters, loss/jitter, native layouts, export escaping/redaction and the PowerShell bridge. Recovery tests use fault injection with mocks, including blocked prerequisites, stale state, failed writes, manual restoration, expiry, scheduled rollback and failed recovery.

UI smoke tests render every page and capture WPF binding failures. Optional hardware smoke tests call the real scanner/driver readers; optional current-link speed tests exercise real HTTPS transfers. No automated development test writes real adapter properties, creates real restore points, installs real scheduled tasks or switches Wi-Fi bands. Those paths require validation on a disposable/test Windows installation before release-grade reliability can be claimed.
