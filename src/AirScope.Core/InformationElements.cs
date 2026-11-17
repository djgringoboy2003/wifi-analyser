using System.Buffers.Binary;

namespace AirScope.Core;

public sealed record BeaconDetails(int? WidthMhz, int? CenterMhz, double? Utilisation, int? Stations, string? Standard, string? Security);

public static class InformationElements
{
    // All beacon data is untrusted. A malformed/truncated IE terminates parsing safely.
    public static BeaconDetails Parse(ReadOnlySpan<byte> bytes, int primaryMhz, bool privacy)
    {
        int? width = null, center = null, stations = null;
        double? load = null;
        string? standard = null;
        string security = privacy ? "Protected (type unreported)" : "Open";
        bool ht = false, vht = false, he = false, eht = false;
        for (int pos = 0; pos + 2 <= bytes.Length;)
        {
            int id = bytes[pos++], length = bytes[pos++];
            if (length > bytes.Length - pos) break;
            var data = bytes.Slice(pos, length);
            pos += length;
            if (id == 11 && data.Length >= 5)
            {
                stations = BinaryPrimitives.ReadUInt16LittleEndian(data);
                load = data[2] * 100d / 255;
            }
            if (id == 48) security = ParseRsn(data);
            if (id == 61 && data.Length >= 2)
            {
                ht = true;
                int secondary = data[1] & 3;
                width = (data[1] & 4) != 0 && secondary is 1 or 3 ? 40 : 20;
                center = primaryMhz + (width == 40 ? secondary == 1 ? 10 : -10 : 0);
            }
            if (id == 192 && data.Length >= 3)
            {
                vht = true;
                if (data[0] == 1)
                {
                    width = data[2] != 0 && Math.Abs(data[1] - data[2]) == 8 ? 160 : 80;
                    center = 5000 + (width == 160 ? data[2] : data[1]) * 5;
                    // 80+80 is not one contiguous 160-MHz channel.
                    if (data[2] != 0 && width != 160) { width = null; center = null; }
                }
                else if (data[0] == 2) { width = 160; center = 5000 + data[1] * 5; }
                else if (data[0] == 3) { width = null; center = null; }
            }
            if (id == 255 && data.Length > 0)
            {
                if (data[0] is 35 or 36) he = true;
                if (data[0] is 108 or 106) eht = true;
                // HE Operation: ext-id, 3 parameter octets, BSS color, basic MCS set,
                // then optional VHT/co-hosted fields and the 6-GHz operation field.
                if (data[0] == 36 && data.Length >= 7 && RadioMath.Band(primaryMhz) == WifiBand.Band6)
                {
                    int parameters = data[1] | data[2] << 8 | data[3] << 16;
                    int offset = 7 + ((parameters & (1 << 14)) != 0 ? 3 : 0) + ((parameters & (1 << 15)) != 0 ? 1 : 0);
                    if ((parameters & (1 << 17)) != 0 && offset + 5 <= data.Length)
                    {
                        var operation = data.Slice(offset, 5);
                        int control = operation[1] & 3;
                        width = control switch { 0 => 20, 1 => 40, 2 => 80, 3 => 160, _ => null };
                        center = 5950 + (control == 3 && operation[3] != 0 ? operation[3] : operation[2]) * 5;
                        if (control == 3 && operation[3] != 0 && Math.Abs(operation[2] - operation[3]) != 8)
                        { width = null; center = null; }
                    }
                }
                // EHT Operation: ext-id, parameters, basic EHT-MCS/NSS (4), operation info.
                if (data[0] == 106 && data.Length >= 9 && (data[1] & 1) != 0)
                {
                    int control = data[6] & 7;
                    int? ehtWidth = control switch { 0 => 20, 1 => 40, 2 => 80, 3 => 160, 4 => 320, _ => null };
                    if (ehtWidth.HasValue)
                    {
                        width = ehtWidth;
                        int baseMhz = RadioMath.Band(primaryMhz) == WifiBand.Band6 ? 5950 : 5000;
                        center = baseMhz + (control >= 3 && data[8] != 0 ? data[8] : data[7]) * 5;
                    }
                }
            }
        }
        if (eht) standard = "Wi-Fi 7";
        else if (he) standard = "Wi-Fi 6/6E";
        else if (vht) standard = "Wi-Fi 5";
        else if (ht) standard = "Wi-Fi 4";
        return new(width, center, load, stations, standard, security);
    }

    private static string ParseRsn(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return "RSN (details unavailable)";
        int pairs = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6));
        int offset = 8 + pairs * 4;
        if (offset + 2 > data.Length) return "RSN (details unavailable)";
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset));
        offset += 2;
        bool sae = false, psk = false, enterprise = false, owe = false;
        for (int i = 0; i < count && offset + 4 <= data.Length; i++, offset += 4)
        {
            if (data[offset] != 0 || data[offset + 1] != 15 || data[offset + 2] != 172) continue;
            int akm = data[offset + 3];
            sae |= akm is 8 or 9 or 24 or 25;
            psk |= akm is 2 or 4 or 6;
            enterprise |= akm is 1 or 3 or 5 or 11 or 12 or 13;
            owe |= akm == 18;
        }
        return (sae, psk, enterprise, owe) switch
        {
            (true, true, _, _) => "WPA2/WPA3-Personal", (true, _, _, _) => "WPA3-Personal",
            (_, true, _, _) => "WPA2-Personal", (_, _, true, _) => "Enterprise (RSN)",
            (_, _, _, true) => "Enhanced Open (OWE)", _ => "RSN protected"
        };
    }
}
