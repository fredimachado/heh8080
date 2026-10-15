namespace Heh8080.Devices;

/// <summary>Host-independent parser and serializer for common TRS-80/CP-500 disk containers.</summary>
public static class ModelIIIDiskCodec
{
    /// <summary>Parses a disk image in the declared container format.</summary>
    public static ModelIIIDiskImage Parse(ReadOnlySpan<byte> bytes, ModelIIIDiskImageKind kind) => kind switch
    {
        ModelIIIDiskImageKind.Raw => ParseRaw(bytes),
        ModelIIIDiskImageKind.Jv3 => Jv3Format.Parse(bytes),
        ModelIIIDiskImageKind.Dmk => DmkFormat.Parse(bytes),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Serializes a native image to the requested container format.</summary>
    public static byte[] Serialize(ModelIIIDiskImage image, ModelIIIDiskImageKind kind)
    {
        ArgumentNullException.ThrowIfNull(image);
        return kind switch
        {
            ModelIIIDiskImageKind.Raw => image.ToRawBytes(),
            ModelIIIDiskImageKind.Jv3 => Jv3Format.Serialize(image),
            ModelIIIDiskImageKind.Dmk => DmkFormat.Serialize(image),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    /// <summary>
    /// Chooses the container of a host file: ".dmk" is DMK, ".jv3" is JV3, and any other name is raw
    /// when its length is exactly a raw disk size and JV3 otherwise.
    /// </summary>
    public static ModelIIIDiskImageKind DetectKind(string fileName, int length)
    {
        string extension = Path.GetExtension(fileName);
        if (extension.Equals(".dmk", StringComparison.OrdinalIgnoreCase))
            return ModelIIIDiskImageKind.Dmk;

        if (extension.Equals(".jv3", StringComparison.OrdinalIgnoreCase))
            return ModelIIIDiskImageKind.Jv3;

        return length is ModelIIIDiskGeometry.DiskSizeBytes or ModelIIIDiskGeometry.DoubleSidedDiskSizeBytes
            ? ModelIIIDiskImageKind.Raw
            : ModelIIIDiskImageKind.Jv3;
    }

    private static ModelIIIDiskImage ParseRaw(ReadOnlySpan<byte> bytes)
    {
        var format = bytes.Length switch
        {
            ModelIIIDiskGeometry.DiskSizeBytes => ModelIIIDiskFormat.SingleSided,
            ModelIIIDiskGeometry.DoubleSidedDiskSizeBytes => ModelIIIDiskFormat.DoubleSided,
            _ => throw new InvalidDataException("Raw Model III images must be exactly 180 KiB or 360 KiB.")
        };

        return ModelIIIDiskImage.FromRaw(bytes, format);
    }
}
