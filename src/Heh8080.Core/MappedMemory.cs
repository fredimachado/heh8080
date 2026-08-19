namespace Heh8080.Core;

/// <summary>
/// A memory range that can participate in a <see cref="MappedMemory"/> address map.
/// </summary>
public interface IMemoryRegion
{
    /// <summary>Inclusive start of the address range.</summary>
    ushort StartAddress { get; }

    /// <summary>Inclusive end of the address range.</summary>
    ushort EndAddress { get; }

    /// <summary>Returns whether this region responds to <paramref name="address"/>.</summary>
    bool Handles(ushort address);

    /// <summary>Reads one byte from the region.</summary>
    byte Read(ushort address);

    /// <summary>Writes one byte to the region.</summary>
    void Write(ushort address, byte value);
}

/// <summary>
/// Composes memory regions into a 64 KiB address space.
/// Regions are consulted in constructor order, so earlier regions overlay later ones.
/// </summary>
public sealed class MappedMemory : IMemory
{
    private readonly IMemoryRegion[] _regions;

    /// <summary>
    /// Creates a mapped address space. At least one region, normally an
    /// <see cref="UnmappedMemoryRegion"/>, must cover every unhandled address.
    /// </summary>
    public MappedMemory(params IMemoryRegion[] regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Length == 0)
            throw new ArgumentException("A mapped address space needs at least one region.", nameof(regions));

        _regions = regions.ToArray();
        if (_regions.Any(region => region is null))
            throw new ArgumentException("Memory regions cannot contain null.", nameof(regions));
    }

    /// <summary>Regions in descending mapping priority.</summary>
    public IReadOnlyList<IMemoryRegion> Regions => _regions;

    public byte Read(ushort address)
    {
        foreach (var region in _regions)
        {
            if (region.Handles(address))
                return region.Read(address);
        }

        return 0xFF;
    }

    public void Write(ushort address, byte value)
    {
        foreach (var region in _regions)
        {
            if (region.Handles(address))
            {
                region.Write(address, value);
                return;
            }
        }
    }
}

/// <summary>
/// Common base for contiguous non-wrapping memory regions.
/// </summary>
public abstract class MemoryRegion : IMemoryRegion
{
    /// <summary>Inclusive start of the address range.</summary>
    public ushort StartAddress { get; }

    /// <summary>Inclusive end of the address range.</summary>
    public ushort EndAddress { get; }

    /// <summary>Size of the region in bytes.</summary>
    public int Length { get; }

    /// <summary>Creates a region at <paramref name="startAddress"/> with <paramref name="length"/> bytes.</summary>
    protected MemoryRegion(ushort startAddress, int length)
    {
        if (length <= 0 || length > 65536 || startAddress + length > 65536)
            throw new ArgumentOutOfRangeException(nameof(length), "The region must fit within the 64 KiB address space.");

        StartAddress = startAddress;
        Length = length;
        EndAddress = (ushort)(startAddress + length - 1);
    }

    public virtual bool Handles(ushort address) => address >= StartAddress && address <= EndAddress;

    /// <summary>Gets the zero-based offset for an address already handled by this region.</summary>
    protected int GetOffset(ushort address) => address - StartAddress;

    public abstract byte Read(ushort address);
    public abstract void Write(ushort address, byte value);
}

/// <summary>
/// Read-only memory that can be temporarily disabled to reveal a lower-priority region.
/// </summary>
public sealed class RomMemoryRegion : MemoryRegion
{
    private readonly byte[] _data;

    /// <summary>Whether this ROM currently responds to reads and writes.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Creates a ROM region from a defensive copy of <paramref name="data"/>.</summary>
    public RomMemoryRegion(ushort startAddress, ReadOnlySpan<byte> data)
        : base(startAddress, data.Length)
    {
        _data = data.ToArray();
    }

    public override bool Handles(ushort address) => Enabled && base.Handles(address);

    public override byte Read(ushort address) => _data[GetOffset(address)];

    /// <summary>ROM writes are deliberately ignored.</summary>
    public override void Write(ushort address, byte value)
    {
    }

    /// <summary>Copies the ROM contents without exposing mutable backing storage.</summary>
    public byte[] ToArray() => _data.ToArray();
}

/// <summary>
/// Mutable random-access memory.
/// </summary>
public class RamMemoryRegion : MemoryRegion
{
    private readonly byte[] _data;

    /// <summary>Creates zero-initialized RAM.</summary>
    public RamMemoryRegion(ushort startAddress, int length)
        : base(startAddress, length)
    {
        _data = new byte[length];
    }

    public override byte Read(ushort address) => _data[GetOffset(address)];

    public override void Write(ushort address, byte value) => _data[GetOffset(address)] = value;

    /// <summary>Loads bytes into RAM, stopping at the end of this region.</summary>
    public void Load(int offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset > Length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        data[..Math.Min(data.Length, Length - offset)].CopyTo(_data.AsSpan(offset));
    }

    /// <summary>Returns an explicit snapshot suitable for diagnostics or tests.</summary>
    public byte[] ToArray() => _data.ToArray();

    /// <summary>Clears the entire RAM region.</summary>
    public void Clear() => Array.Clear(_data);
}

/// <summary>
/// Character video memory that reports only effective byte changes.
/// </summary>
public sealed class VideoRamRegion : RamMemoryRegion
{
    /// <summary>Raised after a character cell changes. Arguments are address and new value.</summary>
    public event Action<ushort, byte>? Changed;

    public VideoRamRegion(ushort startAddress, int length)
        : base(startAddress, length)
    {
    }

    public override void Write(ushort address, byte value)
    {
        if (Read(address) == value)
            return;

        base.Write(address, value);
        Changed?.Invoke(address, value);
    }
}

/// <summary>
/// Active-low 8-by-8 keyboard matrix.
/// </summary>
public sealed class KeyboardMatrix
{
    private const int RowCount = 8;
    private const int ColumnCount = 8;
    private readonly byte[] _rows = new byte[RowCount];

    /// <summary>Raised when the CPU reads a row-selection mask and its active-low value.</summary>
    public event Action<byte, byte>? ReadObserved;

    public KeyboardMatrix()
    {
        Array.Fill(_rows, byte.MaxValue);
    }

    /// <summary>Marks one key as pressed or released.</summary>
    public void SetKeyState(int row, int column, bool pressed)
    {
        ValidatePosition(row, column);
        byte mask = (byte)(1 << column);
        _rows[row] = pressed ? (byte)(_rows[row] & ~mask) : (byte)(_rows[row] | mask);
    }

    /// <summary>Releases every key in the matrix.</summary>
    public void Clear()
    {
        Array.Fill(_rows, byte.MaxValue);
    }

    /// <summary>
    /// Returns the OR of selected active-low rows.
    /// A zero mask selects no rows and returns the inactive value, <c>FFh</c>.
    /// </summary>
    public byte Read(byte rowMask)
    {
        if (rowMask == 0)
        {
            ReadObserved?.Invoke(rowMask, byte.MaxValue);
            return byte.MaxValue;
        }

        byte result = 0;
        for (int row = 0; row < RowCount; row++)
        {
            if ((rowMask & (1 << row)) != 0)
                result |= _rows[row];
        }

        ReadObserved?.Invoke(rowMask, result);
        return result;
    }

    /// <summary>Returns the active-low value of one row.</summary>
    public byte ReadRow(int row)
    {
        if ((uint)row >= RowCount)
            throw new ArgumentOutOfRangeException(nameof(row));

        return _rows[row];
    }

    private static void ValidatePosition(int row, int column)
    {
        if ((uint)row >= RowCount)
            throw new ArgumentOutOfRangeException(nameof(row));
        if ((uint)column >= ColumnCount)
            throw new ArgumentOutOfRangeException(nameof(column));
    }
}

/// <summary>
/// Maps an active-low keyboard matrix through address-select row bits.
/// </summary>
public sealed class KeyboardMatrixRegion : MemoryRegion
{
    private readonly KeyboardMatrix _matrix;

    /// <summary>Creates a keyboard range. Its low address byte selects matrix rows.</summary>
    public KeyboardMatrixRegion(ushort startAddress, int length, KeyboardMatrix matrix)
        : base(startAddress, length)
    {
        _matrix = matrix ?? throw new ArgumentNullException(nameof(matrix));
    }

    public override byte Read(ushort address) => _matrix.Read((byte)address);

    /// <summary>Keyboard writes are ignored.</summary>
    public override void Write(ushort address, byte value)
    {
    }
}

/// <summary>
/// Open-bus memory range. Reads return a predictable value and writes are discarded.
/// </summary>
public sealed class UnmappedMemoryRegion : MemoryRegion
{
    private readonly byte _readValue;

    /// <summary>Creates an unmapped range with an open-bus read value of <c>FFh</c> by default.</summary>
    public UnmappedMemoryRegion(ushort startAddress, int length, byte readValue = 0xFF)
        : base(startAddress, length)
    {
        _readValue = readValue;
    }

    public override byte Read(ushort address) => _readValue;

    public override void Write(ushort address, byte value)
    {
    }
}
