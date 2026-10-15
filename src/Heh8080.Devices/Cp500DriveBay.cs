namespace Heh8080.Devices;

/// <summary>
/// Host side of the CP-500 disk drives: the disk-image provider the machine reads, a display name per drive,
/// and the write-back policy. A disk that was written to is handed back to the host in the container format it
/// was loaded from when it is ejected, replaced, or flushed; read-only disks, disks nothing wrote to, and disks
/// inserted without a write-back are never saved. A failed write-back keeps the disk mounted and modified.
/// The bay owns no file system, so hosts supply the write-back. Not thread safe: callers must not use it
/// while the emulated CPU runs.
/// </summary>
public sealed class Cp500DriveBay
{
    private readonly DriveEntry?[] _entries = new DriveEntry?[ModelIIIDiskGeometry.MaxDrives];

    /// <summary>The provider to hand to <see cref="Cp500Machine"/>.</summary>
    public MemoryModelIIIDiskImageProvider Images { get; } = new();

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

    /// <summary>Writes back every modified disk and keeps them mounted.</summary>
    public void Flush()
    {
        for (int drive = 0; drive < _entries.Length; drive++)
            Flush(drive);
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
