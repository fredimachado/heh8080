using System.Runtime.InteropServices;

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
/// A region whose <see cref="IMemoryRegion.Handles"/> answers can change at runtime,
/// for example a ROM that can be switched out. It must raise <see cref="MappingChanged"/>
/// after every such change so <see cref="MappedMemory"/> can refresh its page table.
/// A region only ever handles addresses inside its start and end addresses.
/// </summary>
public interface IRemappableMemoryRegion : IMemoryRegion
{
    /// <summary>Raised after <see cref="IMemoryRegion.Handles"/> may answer differently.</summary>
    event Action? MappingChanged;
}

/// <summary>
/// Composes memory regions into a 64 KiB address space.
/// Regions are consulted in constructor order, so earlier regions overlay later ones.
/// Addresses nobody handles read as FFh and ignore writes.
/// </summary>
/// <remarks>
/// A 256-entry table of 256-byte pages caches the owning region of every page that a single
/// region owns entirely. Pages shared by several regions are resolved per address.
/// </remarks>
public sealed class MappedMemory : IMemory
{
    private const int PageShift = 8;
    private const int PageSize = 1 << PageShift;
    private const int PageCount = 65536 >> PageShift;

    private static readonly IMemoryRegion OpenBus = new OpenBusRegion();

    private readonly IMemoryRegion[] _regions;
    private readonly IMemoryRegion?[] _pages = new IMemoryRegion?[PageCount];

    /// <summary>Creates a mapped address space from regions in descending mapping priority.</summary>
    public MappedMemory(params IMemoryRegion[] regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Length == 0)
            throw new ArgumentException("A mapped address space needs at least one region.", nameof(regions));

        _regions = regions.ToArray();
        if (_regions.Any(region => region is null))
            throw new ArgumentException("Memory regions cannot contain null.", nameof(regions));

        foreach (var region in _regions)
        {
            if (region is IRemappableMemoryRegion remappable)
                remappable.MappingChanged += () => ResolvePages(region.StartAddress, region.EndAddress);
        }

        ResolvePages(0, 0xFFFF);
    }

    public byte Read(ushort address)
    {
        var owner = _pages[address >> PageShift];
        return owner is null ? ScanRead(address) : owner.Read(address);
    }

    public void Write(ushort address, byte value)
    {
        var owner = _pages[address >> PageShift];
        if (owner is null)
            ScanWrite(address, value);
        else
            owner.Write(address, value);
    }

    private byte ScanRead(ushort address) => FindHandler(address).Read(address);

    private void ScanWrite(ushort address, byte value) => FindHandler(address).Write(address, value);

    private IMemoryRegion FindHandler(ushort address)
    {
        foreach (var region in _regions)
        {
            if (region.Handles(address))
                return region;
        }

        return OpenBus;
    }

    private void ResolvePages(ushort first, ushort last)
    {
        for (int page = first >> PageShift; page <= last >> PageShift; page++)
            _pages[page] = ResolvePage(page);
    }

    private IMemoryRegion? ResolvePage(int page)
    {
        int start = page << PageShift;
        var owner = FindHandler((ushort)start);
        for (int address = start + 1; address < start + PageSize; address++)
        {
            if (!ReferenceEquals(FindHandler((ushort)address), owner))
                return null;
        }

        return owner;
    }

    private sealed class OpenBusRegion : IMemoryRegion
    {
        public ushort StartAddress => 0;

        public ushort EndAddress => 0xFFFF;

        public bool Handles(ushort address) => true;

        public byte Read(ushort address) => 0xFF;

        public void Write(ushort address, byte value)
        {
        }
    }
}

/// <summary>
/// Common base for contiguous non-wrapping memory regions.
/// </summary>
public abstract class MemoryRegion : IRemappableMemoryRegion
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

    public event Action? MappingChanged;

    public virtual bool Handles(ushort address) => address >= StartAddress && address <= EndAddress;

    /// <summary>Call after every change that makes <see cref="Handles"/> answer differently.</summary>
    protected void RaiseMappingChanged() => MappingChanged?.Invoke();

    /// <summary>Gets the zero-based offset for an address already handled by this region.</summary>
    protected int GetOffset(ushort address) => address - StartAddress;

    public abstract byte Read(ushort address);
    public abstract void Write(ushort address, byte value);
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
/// Active-high 8-by-8 keyboard matrix: a pressed key reads as a 1 bit.
/// </summary>
public sealed class KeyboardMatrix
{
    private const int RowCount = 8;
    private const int ColumnCount = 8;
    private readonly byte[] _rows = new byte[RowCount];

    /// <summary>Marks one key as pressed or released.</summary>
    public void SetKeyState(int row, int column, bool pressed)
    {
        ValidatePosition(row, column);
        byte mask = (byte)(1 << column);
        _rows[row] = pressed ? (byte)(_rows[row] | mask) : (byte)(_rows[row] & ~mask);
    }

    /// <summary>Releases every key in the matrix.</summary>
    public void Clear()
    {
        Array.Clear(_rows);
    }

    /// <summary>
    /// Returns the OR of selected rows. A zero mask selects no rows and reads <c>00h</c>.
    /// </summary>
    public byte Read(byte rowMask)
    {
        byte result = 0;
        for (int row = 0; row < RowCount; row++)
        {
            if ((rowMask & (1 << row)) != 0)
                result |= _rows[row];
        }

        return result;
    }

    /// <summary>Whether any key in any row is pressed.</summary>
    public bool AnyPressed => MemoryMarshal.Read<ulong>(_rows) != 0;

    /// <summary>Returns the active-high value of one row.</summary>
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
/// Maps an active-high keyboard matrix through address-select row bits.
/// </summary>
public sealed class KeyboardMatrixRegion : MemoryRegion
{
    private readonly KeyboardMatrix[] _matrices;

    /// <summary>Creates a keyboard range. Its low address byte selects matrix rows; several matrices are ORed.</summary>
    public KeyboardMatrixRegion(ushort startAddress, int length, params KeyboardMatrix[] matrices)
        : base(startAddress, length)
    {
        ArgumentNullException.ThrowIfNull(matrices);
        _matrices = matrices;
    }

    public override byte Read(ushort address)
    {
        byte result = 0;
        foreach (KeyboardMatrix matrix in _matrices)
            result |= matrix.Read((byte)address);

        return result;
    }

    /// <summary>Keyboard writes are ignored.</summary>
    public override void Write(ushort address, byte value)
    {
    }
}
