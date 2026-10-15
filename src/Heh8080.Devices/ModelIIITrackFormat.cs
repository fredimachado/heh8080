namespace Heh8080.Devices;

/// <summary>
/// Raw IBM-style FM/MFM track layout shared by READ TRACK, WRITE TRACK and the DMK codec.
/// </summary>
internal static class ModelIIITrackFormat
{
    /// <summary>Index-to-index length of an MFM track at 300 RPM.</summary>
    public const int MfmTrackBytes = 6250;

    /// <summary>Index-to-index length of an FM track at 300 RPM.</summary>
    public const int FmTrackBytes = 3125;

    public const byte IdAddressMark = 0xFE;
    public const byte DataAddressMark = 0xFB;
    public const byte DeletedDataAddressMark = 0xF8;

    private const byte SyncByte = 0xA1;
    private const byte MfmGapByte = 0x4E;
    private const byte FmGapByte = 0xFF;
    private const byte ZeroByte = 0x00;

    private const int MfmLeadingGap = 40;
    private const int MfmSyncRun = 12;
    private const int MfmGap2 = 22;
    private const int MfmGap3 = 24;
    private const int FmLeadingGap = 16;
    private const int FmSyncRun = 6;
    private const int FmGap2 = 11;
    private const int FmGap3 = 27;

    private const byte WriteTrackSync = 0xF5;
    private const byte WriteTrackCrc = 0xF7;
    private const int WriteTrackIdFieldBytes = 4;

    /// <summary>Raw byte budget of one revolution in the given density.</summary>
    public static int TrackBytes(ModelIIIDiskDensity density) =>
        density == ModelIIIDiskDensity.DoubleDensity ? MfmTrackBytes : FmTrackBytes;

    /// <summary>
    /// Encodes sectors into <paramref name="track"/>, recording the offset of each ID address mark.
    /// With <paramref name="doubleFmBytes"/>, every byte of a single-density sector is written twice as DMK requires.
    /// Returns false when the sectors do not fit; the buffer then holds a truncated track.
    /// </summary>
    public static bool Encode(
        IReadOnlyList<ModelIIIDiskSector> sectors,
        Span<byte> track,
        ModelIIIDiskDensity fillDensity,
        bool doubleFmBytes,
        List<(int Offset, ModelIIIDiskDensity Density)>? idamOffsets = null)
    {
        bool mfm = fillDensity == ModelIIIDiskDensity.DoubleDensity || sectors.Any(IsDoubleDensity);
        var writer = new TrackWriter(track);
        track.Fill(mfm ? MfmGapByte : FmGapByte);
        writer.Repeat(mfm ? MfmGapByte : FmGapByte, mfm ? MfmLeadingGap : FmLeadingGap);

        foreach (var sector in sectors)
        {
            writer.Doubled = doubleFmBytes && sector.Density == ModelIIIDiskDensity.SingleDensity;
            EncodeSector(ref writer, sector, idamOffsets);
        }

        return !writer.Overflowed;
    }

    /// <summary>
    /// Interprets the byte stream a FORMAT program sends with WRITE TRACK:
    /// F5h writes a sync byte and presets the CRC, F7h writes the CRC, FEh after sync is an ID mark
    /// and FBh/F8h after sync is a data mark followed by the payload.
    /// </summary>
    public static List<ModelIIIDiskSector> DecodeWriteTrack(
        ReadOnlySpan<byte> stream,
        ModelIIIDiskDensity density,
        int track,
        int side)
    {
        var sectors = new List<ModelIIIDiskSector>();
        var ids = new HashSet<int>();
        (int Track, int Id, int SizeCode)? pending = null;

        for (int index = 0; index < stream.Length; index++)
        {
            byte value = stream[index];
            bool marked = density == ModelIIIDiskDensity.SingleDensity || index > 0 && stream[index - 1] == WriteTrackSync;
            if (!marked)
                continue;

            if (value == IdAddressMark && index + WriteTrackIdFieldBytes < stream.Length)
            {
                pending = (stream[index + 1], stream[index + 3], stream[index + 4]);
                index += WriteTrackIdFieldBytes;
            }
            else if (value is DataAddressMark or DeletedDataAddressMark && pending is { SizeCode: <= 3 } id)
            {
                int length = 128 << id.SizeCode;
                int start = index + 1;
                if (start + length > stream.Length)
                    break;

                bool crcWritten = start + length < stream.Length && stream[start + length] == WriteTrackCrc;
                if (ids.Add(id.Id))
                {
                    sectors.Add(new ModelIIIDiskSector(
                        track,
                        side,
                        id.Id,
                        stream.Slice(start, length).ToArray(),
                        density,
                        value == DeletedDataAddressMark,
                        crcWritten,
                        id.Track));
                }

                pending = null;
                index = start + length - 1;
            }
        }

        return sectors;
    }

    private static void EncodeSector(
        ref TrackWriter writer,
        ModelIIIDiskSector sector,
        List<(int Offset, ModelIIIDiskDensity Density)>? idamOffsets)
    {
        bool mfm = IsDoubleDensity(sector);
        byte[] id = [(byte)sector.RecordedTrack, (byte)sector.Side, (byte)sector.Id, sector.SizeCode];
        byte dataMark = sector.DeletedData ? DeletedDataAddressMark : DataAddressMark;
        ushort idCrc = ModelIIIDiskCrc.ForField(sector.Density, IdAddressMark, id);
        ushort dataCrc = ModelIIIDiskCrc.ForField(sector.Density, dataMark, sector.Data);
        if (!sector.CrcValid)
            dataCrc = (ushort)~dataCrc;

        writer.Repeat(ZeroByte, mfm ? MfmSyncRun : FmSyncRun);
        if (mfm)
            writer.Repeat(SyncByte, 3);

        idamOffsets?.Add((writer.Position, sector.Density));
        writer.Write(IdAddressMark);
        writer.Write(id);
        writer.WriteCrc(idCrc);

        writer.Repeat(mfm ? MfmGapByte : FmGapByte, mfm ? MfmGap2 : FmGap2);
        writer.Repeat(ZeroByte, mfm ? MfmSyncRun : FmSyncRun);
        if (mfm)
            writer.Repeat(SyncByte, 3);

        writer.Write(dataMark);
        writer.Write(sector.Data);
        writer.WriteCrc(dataCrc);
        writer.Repeat(mfm ? MfmGapByte : FmGapByte, mfm ? MfmGap3 : FmGap3);
    }

    private static bool IsDoubleDensity(ModelIIIDiskSector sector) => sector.Density == ModelIIIDiskDensity.DoubleDensity;

    private ref struct TrackWriter(Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;

        public int Position { get; private set; }

        public bool Overflowed { get; private set; }

        public bool Doubled { get; set; }

        public void Write(byte value)
        {
            int copies = Doubled ? 2 : 1;
            for (int copy = 0; copy < copies; copy++)
            {
                if (Position >= _buffer.Length)
                {
                    Overflowed = true;
                    return;
                }

                _buffer[Position++] = value;
            }
        }

        public void Write(ReadOnlySpan<byte> values)
        {
            foreach (byte value in values)
                Write(value);
        }

        public void Repeat(byte value, int count)
        {
            for (int index = 0; index < count; index++)
                Write(value);
        }

        public void WriteCrc(ushort crc)
        {
            Write((byte)(crc >> 8));
            Write((byte)crc);
        }
    }
}
