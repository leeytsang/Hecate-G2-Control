namespace PeripheralBattery.Core.Protocols;

/// <summary>
/// Edifier/BES BLE control protocol used by the HECATE G2 wireless family.
/// Frames are AA/BB EC &lt;command&gt; &lt;length:u16-be&gt; &lt;payload&gt; &lt;sum8&gt;.
/// </summary>
public static class EdifierBleProtocol
{
    public static readonly Guid AdvertisingServiceUuid = Guid.Parse("0000f700-0000-1000-8000-00805f9b34fb");
    public static readonly Guid ServiceUuid = Guid.Parse("4809f701-1a48-11e9-ab14-d663bd873d93");
    public const string AdvertisingName = "EDIFIER BLE";
    public static readonly Guid NotifyCharacteristicUuid = Guid.Parse("48090001-1a48-11e9-ab14-d663bd873d93");
    public static readonly Guid WriteCharacteristicUuid = Guid.Parse("48090002-1a48-11e9-ab14-d663bd873d93");

    public const byte HostHeader = 0xAA;
    public const byte DeviceHeader = 0xBB;
    public const byte DeviceHeaderLegacy = 0xCC;
    public const byte AppCode = 0xEC;

    public const byte BatteryQueryCommand = 0xD0;
    public const byte DeviceStateQueryCommand = 0xF2;
    public const byte EqReadCommand = 0x43;
    public const byte EqWriteCommand = 0x44;
    public const byte EqPresetQueryCommand = 0xD5;

    public const byte G2EqFormat = 0x06;
    public const byte G2EqFilterType = 0x02;
    public const byte G2EqTrailer = 0x07;
    public const int NeutralGainCode = 6;
    public const int MaxGainCode = 12;

    public static readonly ushort[] G2EqFrequencies = [100, 500, 2000, 8000];

    // HECATE G2 returns plaintext battery values.
    public const bool BatteryPayloadEncrypted = false;

    public static byte[] BuildBatteryQuery() => BuildFrame(BatteryQueryCommand);
    public static byte[] BuildDeviceStateQuery() => BuildFrame(DeviceStateQueryCommand);
    public static byte[] BuildEqReadQuery() => BuildFrame(EqReadCommand);
    public static byte[] BuildEqPresetQuery() => BuildFrame(EqPresetQueryCommand);

    public static byte[] BuildEqWriteFrame(int bandIndex, int gainCode)
    {
        if (bandIndex < 0 || bandIndex >= G2EqFrequencies.Length)
            throw new ArgumentOutOfRangeException(nameof(bandIndex));
        if (gainCode is < 0 or > MaxGainCode)
            throw new ArgumentOutOfRangeException(nameof(gainCode));

        var frequency = G2EqFrequencies[bandIndex];
        return BuildFrame(EqWriteCommand,
        [
            (byte)bandIndex,
            G2EqFilterType,
            (byte)(frequency >> 8),
            (byte)(frequency & 0xFF),
            (byte)gainCode,
            G2EqTrailer,
        ]);
    }

    public static int GainDbToCode(double gainDb)
    {
        var clamped = Math.Clamp(gainDb, -3, 3);
        var code = (int)Math.Round((clamped + 3) * 2, MidpointRounding.AwayFromZero);
        return Math.Clamp(code, 0, MaxGainCode);
    }

    public static double GainCodeToDb(int gainCode) =>
        Math.Clamp(gainCode, 0, MaxGainCode) * 0.5 - 3;

    public static byte[] BuildFrame(byte command, ReadOnlySpan<byte> payload = default)
    {
        var frame = new byte[6 + payload.Length];
        frame[0] = HostHeader;
        frame[1] = AppCode;
        frame[2] = command;
        frame[3] = (byte)(payload.Length >> 8);
        frame[4] = (byte)(payload.Length & 0xFF);
        payload.CopyTo(frame.AsSpan(5));
        frame[^1] = Checksum(frame.AsSpan(0, frame.Length - 1));
        return frame;
    }

    /// <summary>
    /// Finds a valid battery reply in a notification buffer and returns its level.
    /// Notifications may contain extra bytes or several frames, so scan for a valid frame.
    /// </summary>
    public static bool TryParseBatteryNotification(ReadOnlySpan<byte> data, out int batteryPercent)
    {
        batteryPercent = 0;
        for (var offset = 0; offset + 6 <= data.Length; offset++)
        {
            if (!TryReadFrame(data, offset, out var command, out var payloadOffset, out var payloadLength, out var frameLength)) continue;
            if (command != BatteryQueryCommand || payloadLength < 1 || data[payloadOffset] > 100) continue;

            batteryPercent = data[payloadOffset];
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses HECATE G2's 4-band, 6-byte-record hardware EQ response.
    /// Format 06 records: index, filter type (02), frequency:u16-be, gain code, 07.
    /// </summary>
    public static bool TryParseEqResponse(ReadOnlySpan<byte> data, out int[] gainCodes)
    {
        gainCodes = [];
        for (var offset = 0; offset + 6 <= data.Length; offset++)
        {
            if (!TryReadFrame(data, offset, out var command, out var payloadOffset, out var payloadLength, out var frameLength)) continue;
            if (command != EqReadCommand || payloadLength < 2) continue;

            var payload = data.Slice(payloadOffset, payloadLength);
            if (payload[0] != G2EqFormat) continue;

            var count = payload[1];
            if (count < G2EqFrequencies.Length || payload.Length < 2 + count * 6) continue;

            var parsed = new int[G2EqFrequencies.Length];
            Array.Fill(parsed, NeutralGainCode);
            var seen = new bool[G2EqFrequencies.Length];
            var valid = true;
            for (var i = 0; i < count; i++)
            {
                var record = payload.Slice(2 + i * 6, 6);
                var band = record[0];
                if (band >= G2EqFrequencies.Length || seen[band]) { valid = false; break; }
                var frequency = (ushort)((record[2] << 8) | record[3]);
                if (frequency != G2EqFrequencies[band] || record[4] > MaxGainCode) { valid = false; break; }
                parsed[band] = record[4];
                seen[band] = true;
            }

            if (!valid || seen.Any(v => !v)) continue;
            gainCodes = parsed;
            return true;
        }

        return false;
    }

    public static bool TryParsePresetResponse(ReadOnlySpan<byte> data, out byte preset)
    {
        preset = 0;
        for (var offset = 0; offset + 6 <= data.Length; offset++)
        {
            if (!TryReadFrame(data, offset, out var command, out var payloadOffset, out var payloadLength, out _)) continue;
            if (command != EqPresetQueryCommand || payloadLength < 1) continue;
            preset = data[payloadOffset];
            return true;
        }
        return false;
    }

    public static bool TryDecodeLevel(byte raw, out int batteryPercent)
    {
        batteryPercent = 0;
        var value = BatteryPayloadEncrypted ? (byte)(raw ^ 0xA5) : raw;

        // Some firmware mirrors the charging state in bit 7 of the level byte.
        if (value > 100 && (value & 0x80) != 0)
            value &= 0x7F;

        if (value > 100) return false;
        batteryPercent = value;
        return true;
    }

    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var value in bytes) sum = (sum + value) & 0xFF;
        return (byte)sum;
    }

    private static bool TryReadFrame(
        ReadOnlySpan<byte> data,
        int offset,
        out byte command,
        out int payloadOffset,
        out int payloadLength,
        out int frameLength)
    {
        command = 0;
        payloadOffset = 0;
        payloadLength = 0;
        frameLength = 0;
        if (offset + 6 > data.Length) return false;
        if (data[offset] is not (DeviceHeader or DeviceHeaderLegacy) || data[offset + 1] != AppCode) return false;

        command = data[offset + 2];
        payloadLength = (data[offset + 3] << 8) | data[offset + 4];
        frameLength = 6 + payloadLength;
        if (payloadLength < 0 || offset + frameLength > data.Length) return false;

        var frame = data.Slice(offset, frameLength);
        if (frame[^1] != Checksum(frame[..^1])) return false;

        payloadOffset = offset + 5;
        return true;
    }
}
