using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// CP-500 Model III text video RAM: 64 columns by 16 rows at 3C00h-3FFFh.
/// Rendering is deliberately left to a host UI; this device owns the observable character plane.
/// </summary>
public sealed class Cp500VideoDevice
{
    /// <summary>First character-cell address.</summary>
    public const ushort StartAddress = 0x3C00;

    /// <summary>Text columns per row.</summary>
    public const int Columns = 64;

    /// <summary>Text rows per screen.</summary>
    public const int Rows = 16;

    /// <summary>Total text characters.</summary>
    public const int CharacterCount = Columns * Rows;

    /// <summary>Observable video RAM region.</summary>
    public VideoRamRegion VideoRam { get; }

    /// <summary>Raised after one cell changes. Arguments are column, row, and character code.</summary>
    public event Action<int, int, byte>? CharacterChanged;

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
