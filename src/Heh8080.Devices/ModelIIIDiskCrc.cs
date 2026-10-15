namespace Heh8080.Devices;

/// <summary>CRC-16 CCITT as recorded in floppy ID and data fields.</summary>
internal static class ModelIIIDiskCrc
{
    private const ushort Preset = 0xFFFF;
    private const ushort Polynomial = 0x1021;
    private const byte SyncByte = 0xA1;
    private const int SyncByteCount = 3;

    /// <summary>Advances a running CRC by one byte.</summary>
    public static ushort Update(ushort crc, byte value)
    {
        crc ^= (ushort)(value << 8);
        for (int bit = 0; bit < 8; bit++)
            crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ Polynomial) : (ushort)(crc << 1);

        return crc;
    }

    /// <summary>
    /// CRC of a field: the address mark and body, preceded by the three A1h sync bytes in MFM.
    /// </summary>
    public static ushort ForField(ModelIIIDiskDensity density, byte addressMark, ReadOnlySpan<byte> body)
    {
        ushort crc = Preset;
        if (density == ModelIIIDiskDensity.DoubleDensity)
        {
            for (int sync = 0; sync < SyncByteCount; sync++)
                crc = Update(crc, SyncByte);
        }

        crc = Update(crc, addressMark);
        foreach (byte value in body)
            crc = Update(crc, value);

        return crc;
    }
}
