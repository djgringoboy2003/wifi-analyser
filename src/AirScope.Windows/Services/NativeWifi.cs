using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using AirScope.Core;

namespace AirScope.Windows.Services;

public sealed class NativeWifi : IDisposable
{
    private nint _handle;
    private readonly object _gate = new();
    private readonly NotificationCallback _callback;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _scans = new();
    private readonly ConcurrentQueue<WifiEvent> _events = new();
    public bool NotificationsSupported { get; }
    public NativeWifi()
    {
        Check(WlanOpenHandle(2, 0, out _, out _handle));
        _callback = OnNotification;
        NotificationsSupported = WlanRegisterNotification(_handle, 8, true, _callback, 0, 0, out _) == 0;
    }
    public IReadOnlyList<WifiInterface> Interfaces()
    {
        lock (_gate)
        {
            Check(WlanEnumInterfaces(_handle, 0, out nint list));
            try
            {
                int count = Marshal.ReadInt32(list);
                if (count is < 0 or > 64) throw new InvalidDataException("Invalid WLAN interface count.");
                var result = new List<WifiInterface>();
                int size = Marshal.SizeOf<InterfaceInfo>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<InterfaceInfo>(list + 8 + i * size);
                    result.Add(new(item.Id, item.Description, item.State));
                }
                return result;
            }
            finally { WlanFreeMemory(list); }
        }
    }
    public Connection? Current(Guid id)
    {
        lock (_gate)
        {
            uint status = WlanQueryInterface(_handle, ref id, 7, 0, out uint size, out nint data, out _);
            if (status == 5023) return null; // ERROR_INVALID_STATE: disconnected.
            Check(status);
            try
            {
                if (size < Marshal.SizeOf<ConnectionAttributes>()) throw new InvalidDataException("Truncated WLAN connection data.");
                var current = Marshal.PtrToStructure<ConnectionAttributes>(data);
                var a = current.Association;
                return new(id, current.Profile, Ssid(a.Ssid), Hex(a.Ssid), Mac(a.Bssid), (int)a.SignalQuality,
                    a.RxRate / 1000d, a.TxRate / 1000d, Phy(a.PhyType), Auth(current.Security.Auth));
            }
            finally { WlanFreeMemory(data); }
        }
    }
    public void RequestScan(Guid id)
    {
        lock (_gate) Check(WlanScan(_handle, ref id, 0, 0, 0));
    }
    public async Task<bool> ScanAsync(Guid id, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_scans.TryAdd(id, completion)) return false;
        try
        {
            RequestScan(id);
            if (!NotificationsSupported) { await Task.Delay(4000, token); return false; }
            var winner = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(8), token));
            token.ThrowIfCancellationRequested();
            if (winner != completion.Task) return false;
            if (!await completion.Task) throw new InvalidOperationException("The driver reported that the Wi-Fi scan failed.");
            return true;
        }
        finally { _scans.TryRemove(id, out _); }
    }
    public RealtimeQuality Quality(Guid id)
    {
        lock (_gate)
        {
            uint status = WlanQueryInterface(_handle, ref id, 19, 0, out uint size, out nint data, out _);
            if (status != 0) return new(false, false, 0, 0, 0, "Unavailable", [], new Win32Exception((int)status).Message);
            try
            {
                if (size < RealtimeDecoder.HeaderBytes || size > RealtimeDecoder.HeaderBytes + 16 * RealtimeDecoder.LinkBytes)
                    return new(false, false, 0, 0, 0, "Unavailable", [], "Unrecognised real-time WLAN buffer length.");
                var bytes = new byte[size]; Marshal.Copy(data, bytes, 0, bytes.Length);
                try { return RealtimeDecoder.Decode(bytes); }
                catch (Exception error) when (error is InvalidDataException or OverflowException)
                { return new(false, false, 0, 0, 0, "Unavailable", [], error.Message); }
            }
            finally { WlanFreeMemory(data); }
        }
    }
    public IReadOnlyList<WifiEvent> DrainEvents()
    {
        var items = new List<WifiEvent>();
        while (items.Count < 256 && _events.TryDequeue(out var item)) items.Add(item);
        return items;
    }
    private void OnNotification(nint pointer, nint context)
    {
        try
        {
            var n = Marshal.PtrToStructure<NotificationData>(pointer);
            if (n.Source != 8) return;
            if (n.Code is 7 or 8 && _scans.TryGetValue(n.Id, out var pending)) pending.TrySetResult(n.Code == 7);
            string? kind = n.Code switch { 7 => "Scan complete", 8 => "Scan failed", 9 => "Connecting", 10 => "Connected", 11 => "Connection failed", 13 => "Adapter arrived", 14 => "Adapter removed", 20 => "Disconnecting", 21 => "Disconnected", _ => null };
            if (kind is null) return;
            uint? reason = n.Code == 8 && n.DataSize >= 4 && n.Data != 0 ? unchecked((uint)Marshal.ReadInt32(n.Data)) : null;
            _events.Enqueue(new(DateTimeOffset.Now, n.Id, kind, reason.HasValue ? $"Windows WLAN reason {reason}" : "Reported by Windows WLAN notifications.", reason));
            while (_events.Count > 256) _events.TryDequeue(out _);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { /* Never throw across an unmanaged callback. */ }
    }
    public IReadOnlyList<AccessPoint> Networks(Guid id)
    {
        lock (_gate)
        {
            Check(WlanGetNetworkBssList(_handle, ref id, 0, 3, false, 0, out nint list));
            try
            {
                uint total = unchecked((uint)Marshal.ReadInt32(list));
                int count = Marshal.ReadInt32(list, 4), stride = Marshal.SizeOf<BssEntry>();
                if (total is < 8 or > 16_777_216 || count < 0 || count > (total - 8) / stride)
                    throw new InvalidDataException("Invalid WLAN scan buffer.");
                var result = new List<AccessPoint>();
                for (int i = 0; i < count; i++)
                {
                    int offset = 8 + i * stride;
                    nint address = list + offset;
                    var bss = Marshal.PtrToStructure<BssEntry>(address);
                    if (bss.Ssid.Length > 32) continue;
                    int mhz = (int)(bss.FrequencyKhz / 1000);
                    byte[] ies = [];
                    if (bss.IeSize <= 65535 && bss.IeOffset >= stride && (ulong)offset + bss.IeOffset + bss.IeSize <= total)
                    {
                        ies = new byte[bss.IeSize];
                        Marshal.Copy(address + (int)bss.IeOffset, ies, 0, ies.Length);
                    }
                    var beacon = InformationElements.Parse(ies, mhz, (bss.Capability & 16) != 0);
                    DateTimeOffset seen = DateTimeOffset.Now;
                    if (bss.HostTimestamp > 0 && bss.HostTimestamp < long.MaxValue)
                    {
                        try { seen = new DateTimeOffset(DateTime.FromFileTimeUtc((long)bss.HostTimestamp)); }
                        catch (ArgumentOutOfRangeException) { /* Broken driver timestamp: retain collection time. */ }
                    }
                    result.Add(new()
                    {
                        Ssid = Ssid(bss.Ssid), SsidHex = Hex(bss.Ssid), Bssid = Mac(bss.Bssid), InterfaceId = id,
                        FrequencyMhz = mhz, Rssi = bss.Rssi, SignalPercent = (int)Math.Min(100, bss.LinkQuality),
                        WidthMhz = beacon.WidthMhz, CenterFrequencyMhz = beacon.CenterMhz,
                        Standard = beacon.Standard ?? Phy(bss.PhyType), Security = beacon.Security ?? "Unknown",
                        UtilisationPercent = beacon.Utilisation, StationCount = beacon.Stations,
                        InRegulatoryDomain = bss.InRegDomain != 0, SeenAt = seen
                    });
                }
                return result.GroupBy(p => p.Bssid).Select(g => g.OrderByDescending(p => p.SeenAt).First()).ToArray();
            }
            finally { WlanFreeMemory(list); }
        }
    }
    public void Connect(Guid id, string profile, string? bssid = null)
    {
        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 255) throw new ArgumentException("Choose a saved Windows Wi-Fi profile.");
        lock (_gate)
        {
            nint desired = 0;
            try
            {
                if (bssid is not null)
                {
                    byte[] mac = Convert.FromHexString(bssid.Replace(":", ""));
                    if (mac.Length != 6) throw new ArgumentException("Invalid BSSID.");
                    desired = Marshal.AllocHGlobal(20);
                    Marshal.Copy(new byte[20], 0, desired, 20);
                    Marshal.WriteByte(desired, 0, 0x80); // NDIS_OBJECT_TYPE_DEFAULT
                    Marshal.WriteByte(desired, 1, 1);
                    Marshal.WriteInt16(desired, 2, 20);
                    Marshal.WriteInt32(desired, 4, 1);
                    Marshal.WriteInt32(desired, 8, 1);
                    Marshal.Copy(mac, 0, desired + 12, 6);
                }
                var parameters = new ConnectionParameters { Mode = 0, Profile = profile, BssType = 1, DesiredBssidList = desired };
                Check(WlanConnect(_handle, ref id, ref parameters, 0));
            }
            finally { if (desired != 0) Marshal.FreeHGlobal(desired); }
        }
    }
    public IReadOnlyList<string> Profiles(Guid id)
    {
        lock (_gate)
        {
            Check(WlanGetProfileList(_handle, ref id, 0, out nint list));
            try
            {
                int count = Marshal.ReadInt32(list), size = 516;
                if (count is < 0 or > 2048) throw new InvalidDataException("Invalid profile list.");
                return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(list + 8 + i * size, 256)!.TrimEnd('\0')).ToArray();
            }
            finally { WlanFreeMemory(list); }
        }
    }
    public void Dispose()
    {
        if (_handle != 0 && NotificationsSupported) WlanRegisterNotification(_handle, 0, true, null, 0, 0, out _);
        foreach (var scan in _scans.Values) scan.TrySetCanceled();
        lock (_gate) { if (_handle != 0) WlanCloseHandle(_handle, 0); _handle = 0; }
        GC.KeepAlive(_callback);
    }
    private static string Ssid(Dot11Ssid ssid) => Encoding.UTF8.GetString(ssid.Bytes, 0, (int)Math.Min(ssid.Length, 32));
    private static string Hex(Dot11Ssid ssid) => Convert.ToHexString(ssid.Bytes.AsSpan(0, (int)Math.Min(ssid.Length, 32)));
    private static string Mac(byte[] bytes) => string.Join(":", bytes.Select(b => b.ToString("x2")));
    private static void Check(uint result) { if (result != 0) throw new Win32Exception((int)result); }
    public static string Phy(uint phy) => phy switch { 4 => "802.11a", 5 => "802.11b", 6 => "802.11g", 7 => "Wi-Fi 4", 8 => "Wi-Fi 5", 9 => "802.11ad", 10 => "Wi-Fi 6/6E", 11 => "Wi-Fi 7", _ => $"PHY {phy}" };
    private static string Auth(uint auth) => auth switch { 1 => "Open", 4 => "WPA", 6 => "WPA2-Enterprise", 7 => "WPA2-Personal", 8 or 11 or 12 => "WPA3-Enterprise", 9 => "WPA3-Personal", 10 => "Enhanced Open", _ => $"Authentication {auth}" };

    [StructLayout(LayoutKind.Sequential)] private struct Dot11Ssid { public uint Length; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Bytes; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void NotificationCallback(nint data, nint context);
    [StructLayout(LayoutKind.Sequential)] private struct NotificationData { public uint Source, Code; public Guid Id; public uint DataSize; public nint Data; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct InterfaceInfo { public Guid Id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description; public int State; }
    [StructLayout(LayoutKind.Sequential)] private struct RateSet { public uint Length; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)] public ushort[] Rates; }
    [StructLayout(LayoutKind.Sequential)] private struct BssEntry
    {
        public Dot11Ssid Ssid; public uint PhyId; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
        public uint BssType, PhyType; public int Rssi; public uint LinkQuality; public byte InRegDomain; public ushort BeaconPeriod;
        public ulong Timestamp, HostTimestamp; public ushort Capability; public uint FrequencyKhz; public RateSet RateSet; public uint IeOffset, IeSize;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Association
    {
        public Dot11Ssid Ssid; public uint BssType; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
        public uint PhyType, PhyIndex, SignalQuality, RxRate, TxRate;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Security { public int Enabled, OneX; public uint Auth, Cipher; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ConnectionAttributes
    { public uint State, Mode; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Profile; public Association Association; public Security Security; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ConnectionParameters
    { public uint Mode; [MarshalAs(UnmanagedType.LPWStr)] public string Profile; public nint Ssid, DesiredBssidList; public uint BssType, Flags; }

    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, nint reserved, out uint negotiated, out nint handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanRegisterNotification(nint handle, uint source, [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicates, NotificationCallback? callback, nint context, nint reserved, out uint previous);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(nint handle, nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(nint handle, nint reserved, out nint list);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(nint memory);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(nint handle, ref Guid id, uint opcode, nint reserved, out uint size, out nint data, out uint type);
    [DllImport("wlanapi.dll")] private static extern uint WlanScan(nint handle, ref Guid id, nint ssid, nint ieData, nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetNetworkBssList(nint handle, ref Guid id, nint ssid, uint type, [MarshalAs(UnmanagedType.Bool)] bool secure, nint reserved, out nint list);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern uint WlanConnect(nint handle, ref Guid id, ref ConnectionParameters parameters, nint reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetProfileList(nint handle, ref Guid id, nint reserved, out nint list);
}
