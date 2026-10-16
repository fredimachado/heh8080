namespace Heh8080.Devices;

/// <summary>
/// Host side of the CP-500 disk drives: the disk-image provider the machine reads, a display name per drive,
/// and the write-back policy. A disk that was written to is handed back to the host in the container format it
/// was loaded from when it is ejected, replaced, or flushed; read-only disks, disks nothing wrote to, and disks
/// inserted without a write-back are never saved. A failed write-back keeps the disk mounted and modified.
/// The hard-disk slot follows that same rule and is flushed with the floppies.
/// The bay owns no file system, so hosts supply the write-back. Not thread safe: callers must not use it
/// while the emulated CPU runs.
/// </summary>
public sealed class Cp500DriveBay
{
    private readonly DriveEntry?[] _entries = new DriveEntry?[ModelIIIDiskGeometry.MaxDrives];

    /// <summary>The provider to hand to <see cref="Cp500Machine"/>.</summary>
    public MemoryModelIIIDiskImageProvider Images { get; } = new();

    /// <summary>The one hard-disk slot. Floppy drives stay on <see cref="Insert(int, byte[], string, Action{byte[]}?, bool)"/>.</summary>
    public HardDiskSlot HardDisk { get; } = new();

    /// <summary>Mounts a host disk file image, first writing back whatever the drive held.</summary>
    /// <param name="drive">Drive number, 0-3.</param>
    /// <param name="image">File contents; the container is chosen with <see cref="ModelIIIDiskCodec.DetectKind"/>.</param>
    /// <param name="name">Display name, normally the file name.</param>
    /// <param name="writeBack">Receives the image in its source container format; null when the host cannot save.</param>
    /// <param name="readOnly">Mount write protected.</param>
    /// <exception cref="InvalidDataException">The bytes are not a valid disk image; the drive is left unchanged.</exception>
    public void Insert(int drive, byte[] image, string name, Action<byte[]>? writeBack, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ValidateDrive(drive);

        var parsed = ModelIIIDiskCodec.Parse(image, ModelIIIDiskCodec.DetectKind(name, image.Length));
        Flush(drive);
        Images.Mount(drive, parsed, readOnly);
        _entries[drive] = new DriveEntry(name, writeBack);
    }

    /// <summary>Writes the disk back if it was modified, then empties the drive. Does nothing for an empty drive.</summary>
    public void Eject(int drive)
    {
        ValidateDrive(drive);
        Flush(drive);
        Images.Unmount(drive);
        _entries[drive] = null;
    }

    /// <summary>Writes back every modified floppy and the hard-disk slot, and keeps them mounted.</summary>
    public void Flush()
    {
        for (int drive = 0; drive < _entries.Length; drive++)
            Flush(drive);

        HardDisk.Flush();
    }

    /// <summary>Display name of the disk in a drive, or null when it is empty or the drive does not exist.</summary>
    public string? NameOf(int drive) => drive is >= 0 and < ModelIIIDiskGeometry.MaxDrives ? _entries[drive]?.Name : null;

    private void Flush(int drive)
    {
        if (_entries[drive] is not { WriteBack: { } writeBack } || Images.IsReadOnly(drive) || !Images.IsModified(drive))
            return;

        writeBack(Images.GetImageBytes(drive));
        Images.MarkSaved(drive);
    }

    private static void ValidateDrive(int drive)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(drive);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(drive, ModelIIIDiskGeometry.MaxDrives);
    }

    private sealed record DriveEntry(string Name, Action<byte[]>? WriteBack);
}

/// <summary>
/// The one hard-disk slot. Insert parses before it replaces. Flush writes only when a callback exists,
/// the image is writable, and it is modified. A throw leaves the previous image mounted and still modified.
/// Eject of an empty slot returns. Not thread safe: the host pauses the CPU first.
/// </summary>
public sealed class HardDiskSlot
{
    private Action<byte[]>? _writeBack;

    /// <summary>Display name, or null when the slot is empty.</summary>
    public string? Name { get; private set; }

    internal HardDiskImage? Image { get; private set; }

    /// <summary>Mounts a host image, first writing back whatever the slot held.</summary>
    /// <param name="image">File contents. An HDV header is recognized by <see cref="HardDiskImage.Parse"/>.</param>
    /// <param name="name">Display name, normally the file name.</param>
    /// <param name="writeBack">Receives the image in the container it was loaded from; null when the host cannot save.</param>
    /// <param name="readOnly">Mount write protected, in addition to a protect bit in an HDV header.</param>
    /// <param name="rawGeometry">Required for a raw body. Must be null for an HDV file.</param>
    /// <exception cref="InvalidDataException">The bytes are not a valid image. The slot is unchanged.</exception>
    public void Insert(
        byte[] image,
        string name,
        Action<byte[]>? writeBack,
        bool readOnly = false,
        HardDiskGeometry? rawGeometry = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(name);

        HardDiskImage parsed = HardDiskImage.Parse(image, rawGeometry, readOnly);
        Flush();
        Image = parsed;
        Name = name;
        _writeBack = writeBack;
    }

    /// <summary>Writes the image back if it was modified, then empties the slot. Does nothing when the slot is empty.</summary>
    public void Eject()
    {
        if (Image is null)
            return;

        Flush();
        Image = null;
        Name = null;
        _writeBack = null;
    }

    /// <summary>Writes the image back when a callback exists, the image is writable, and a sector write happened.</summary>
    public void Flush()
    {
        if (Image is not { WriteProtected: false, Modified: true } image || _writeBack is not { } writeBack)
            return;

        writeBack(image.Export());
        image.MarkSaved();
    }
}
