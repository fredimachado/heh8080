using System.Runtime.CompilerServices;

namespace Heh8080.Tests;

/// <summary>
/// Locates the copyrighted, gitignored <c>cp500/cp500.rom</c> system ROM and <c>cp500/cp500-chargen.rom</c>
/// character generator dumps. The search starts at the test binaries and at this source file, because build output
/// may live outside the repository.
/// </summary>
public static class Cp500RomFile
{
    private const string RepoRootMarker = "Directory.Packages.props";

    public const string MissingReason = "cp500/cp500.rom is not present (copyrighted, not committed).";

    public const string CharacterGeneratorMissingReason =
        "cp500/cp500.rom or cp500/cp500-chargen.rom is not present (copyrighted, not committed).";

    public static string? Path { get; } = Find("cp500.rom");

    public static string? CharacterGeneratorPath { get; } = Find("cp500-chargen.rom");

    private static string? Find(string fileName) =>
        Find(AppContext.BaseDirectory, fileName) ?? Find(SourceDirectory(), fileName);

    private static string SourceDirectory([CallerFilePath] string sourceFile = "") =>
        System.IO.Path.GetDirectoryName(sourceFile) ?? string.Empty;

    private static string? Find(string startDirectory, string fileName)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory != null; directory = directory.Parent)
        {
            if (!File.Exists(System.IO.Path.Combine(directory.FullName, RepoRootMarker)))
                continue;

            string rom = System.IO.Path.Combine(directory.FullName, "cp500", fileName);
            return File.Exists(rom) ? rom : null;
        }

        return null;
    }
}

/// <summary>A fact that is skipped, not failed, when the CP-500 ROM dump is not available.</summary>
public sealed class RealRomFactAttribute : FactAttribute
{
    public RealRomFactAttribute()
    {
        if (Cp500RomFile.Path == null)
            Skip = Cp500RomFile.MissingReason;
    }
}

/// <summary>A fact that is skipped, not failed, unless both the CP-500 system ROM and character generator dumps are available.</summary>
public sealed class RealCharacterGeneratorFactAttribute : FactAttribute
{
    public RealCharacterGeneratorFactAttribute()
    {
        if (Cp500RomFile.Path == null || Cp500RomFile.CharacterGeneratorPath == null)
            Skip = Cp500RomFile.CharacterGeneratorMissingReason;
    }
}

/// <summary>A theory that is skipped, not failed, when the CP-500 ROM dump is not available.</summary>
public sealed class RealRomTheoryAttribute : TheoryAttribute
{
    public RealRomTheoryAttribute()
    {
        if (Cp500RomFile.Path == null)
            Skip = Cp500RomFile.MissingReason;
    }
}
