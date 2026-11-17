using System.Buffers.Binary;

namespace AirScope.Core;

public static class RealtimeDecoder
{
    public const int HeaderBytes = 24, LinkBytes = 272;
    public static RealtimeQuality Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes) throw new InvalidDataException("Truncated real-time WLAN header.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        if (count > 16 || HeaderBytes + count * LinkBytes > bytes.Length) throw new InvalidDataException("Invalid WLAN link count or length.");
        int quality = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (quality > 100) throw new InvalidDataException("Invalid WLAN link quality.");
        List<ConnectedLink> links = [];
        for (int i = 0; i < count; i++)
        {
            var link = bytes.Slice(HeaderBytes + i * LinkBytes, LinkBytes);
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(link[4..]);
            int mhz = checked((int)(raw >= 1_000_000 ? raw / 1000 : raw));
            uint rawWidth = BinaryPrimitives.ReadUInt32LittleEndian(link[8..]);
            int? width = rawWidth is 5 or 10 or 20 or 40 or 80 or 160 or 320 ? (int)rawWidth : null;
            int rssi = BinaryPrimitives.ReadInt32LittleEndian(link[12..]);
            if (RadioMath.Band(mhz) == WifiBand.Unknown || rssi is < -127 or > 0)
                throw new InvalidDataException($"Unrecognised WLAN link units: frequency={raw}, RSSI={rssi}.");
            links.Add(new(link[0], mhz, width, rssi, rawWidth));
        }
        uint phy = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return new(true, BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]) != 0, quality,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) / 1000d,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) / 1000d,
            phy switch { 11 => "Wi-Fi 7", 10 => "Wi-Fi 6/6E", 8 => "Wi-Fi 5", 7 => "Wi-Fi 4", _ => $"PHY {phy}" }, links);
    }
}
