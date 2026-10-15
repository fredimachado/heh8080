using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// CP-500 Model III text video RAM: 64 columns by 16 rows at 3C00h-3FFFh.
/// Rendering is deliberately left to a host UI; this device owns the observable character plane.
/// </summary>
public sealed class Cp500VideoDevice : IIoDevice
{
    /// <summary>First character-cell address.</summary>
    public const ushort StartAddress = 0x3C00;

    /// <summary>Text columns per row.</summary>
    public const int Columns = 64;

    /// <summary>Text rows per screen.</summary>
    public const int Rows = 16;

    /// <summary>Total text characters.</summary>
    public const int CharacterCount = Columns * Rows;

    /// <summary>First Model III video-control port.</summary>
    public const byte ControlPort = 0xEC;

    /// <summary>Last mirrored video-control port.</summary>
    public const byte LastControlPort = 0xEF;

    /// <summary>Observable video RAM region.</summary>
    public VideoRamRegion VideoRam { get; }

    /// <summary>Last non-cassette video-control value written at ECh.</summary>
    public byte ControlLatch { get; private set; }

    /// <summary>Visible text columns selected by the video-control latch.</summary>
    public int VisibleColumns => Is32ColumnMode ? 32 : 64;

    /// <summary>Whether ECh bit 2 selects 32-column expanded text.</summary>
    public bool Is32ColumnMode => (ControlLatch & 0x04) != 0;

    /// <summary>Whether ECh bit 3 selects the alternate character set.</summary>
    public bool AlternateCharacterSet => (ControlLatch & 0x08) != 0;

    /// <summary>Whether ECh bit 7 forces inverse fallback rendering.</summary>
    public bool InverseVideo => (ControlLatch & 0x80) != 0;

    /// <summary>Raised after one cell changes. Arguments are column, row, and character code.</summary>
    public event Action<int, int, byte>? CharacterChanged;

    /// <summary>Raised after the ECh video-control register changes.</summary>
    public event Action<byte>? ControlChanged;

    /// <summary>Creates a fresh CP-500 video plane.</summary>
    public Cp500VideoDevice()
        : this(new VideoRamRegion(StartAddress, CharacterCount))
    {
    }

    /// <summary>Wraps a preconfigured video region used by a machine memory map.</summary>
    public Cp500VideoDevice(VideoRamRegion videoRam)
    {
        ArgumentNullException.ThrowIfNull(videoRam);
        if (videoRam.StartAddress != StartAddress || videoRam.Length != CharacterCount)
            throw new ArgumentException("CP-500 video RAM must cover exactly 3C00h-3FFFh.", nameof(videoRam));

        VideoRam = videoRam;
        VideoRam.Changed += OnVideoRamChanged;
    }

    /// <summary>Returns the character at a text coordinate.</summary>
    public byte ReadCharacter(int column, int row)
    {
        ValidateCoordinate(column, row);
        return VideoRam.Read((ushort)(StartAddress + row * Columns + column));
    }

    /// <summary>Writes a character at a text coordinate.</summary>
    public void WriteCharacter(int column, int row, byte character)
    {
        ValidateCoordinate(column, row);
        VideoRam.Write((ushort)(StartAddress + row * Columns + column), character);
    }

    /// <summary>Registers the write side of ECh-EFh. Reads there acknowledge the timer interrupt.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.RegisterOutput(this, ControlPort, LastControlPort);
    }

    /// <summary>The latch is write-only; hosts observe it through <see cref="ControlLatch"/>.</summary>
    public byte In(byte port) => 0xFF;

    /// <summary>Writes non-cassette video-control bits. VRAM is never rewritten.</summary>
    public void Out(byte port, byte value)
    {
        if (port is < ControlPort or > LastControlPort)
            return;

        byte nonCassette = (byte)(value & 0xFC);
        if (ControlLatch == nonCassette)
            return;

        ControlLatch = nonCassette;
        ControlChanged?.Invoke(ControlLatch);
    }

    /// <summary>Returns the VRAM-backed cell visible at a display coordinate.</summary>
    public Cp500RenderedCell GetRenderedCell(int visibleColumn, int row)
    {
        if ((uint)visibleColumn >= VisibleColumns)
            throw new ArgumentOutOfRangeException(nameof(visibleColumn));
        if ((uint)row >= Rows)
            throw new ArgumentOutOfRangeException(nameof(row));

        int memoryColumn = Is32ColumnMode ? visibleColumn * 2 : visibleColumn;
        byte raw = ReadCharacter(memoryColumn, row);
        bool bit7 = (raw & 0x80) != 0;
        bool graphics = bit7 && AlternateCharacterSet;
        bool inverse = InverseVideo || (bit7 && !AlternateCharacterSet);
        byte glyph = graphics ? raw : (byte)(raw & 0x7F);
        return new Cp500RenderedCell(raw, glyph, inverse, graphics, AlternateCharacterSet);
    }

    /// <summary>Resets the video-control latch without mutating VRAM.</summary>
    public void ResetControl()
    {
        if (ControlLatch == 0)
            return;

        ControlLatch = 0;
        ControlChanged?.Invoke(ControlLatch);
    }

    private void OnVideoRamChanged(ushort address, byte value)
    {
        int offset = address - StartAddress;
        CharacterChanged?.Invoke(offset % Columns, offset / Columns, value);
    }

    private static void ValidateCoordinate(int column, int row)
    {
        if ((uint)column >= Columns)
            throw new ArgumentOutOfRangeException(nameof(column));
        if ((uint)row >= Rows)
            throw new ArgumentOutOfRangeException(nameof(row));
    }
}

/// <summary>Fallback-renderer description of one CP-500 video cell.</summary>
public readonly record struct Cp500RenderedCell(
    byte Raw,
    byte Glyph,
    bool Inverse,
    bool Graphics,
    bool AlternateCharacterSet);
