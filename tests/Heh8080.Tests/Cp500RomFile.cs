using System.Runtime.CompilerServices;

namespace Heh8080.Tests;

/// <summary>
/// Locates the copyrighted, gitignored <c>cp500/cp500.rom</c> dump. The search starts at the test
/// binaries and at this source file, because build output may live outside the repository.
/// </summary>
public static class Cp500RomFile
{
    private const string RepoRootMarker = "Directory.Packages.props";

    public const string MissingReason = "cp500/cp500.rom is not present (copyrighted, not committed).";

    public static string? Path { get; } = Find(AppContext.BaseDirectory) ?? Find(SourceDirectory());

    private static string SourceDirectory([CallerFilePath] string sourceFile = "") =>
        System.IO.Path.GetDirectoryName(sourceFile) ?? string.Empty;

    private static string? Find(string startDirectory)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory != null; directory = directory.Parent)
        {
            if (!File.Exists(System.IO.Path.Combine(directory.FullName, RepoRootMarker)))
                continue;

            string rom = System.IO.Path.Combine(directory.FullName, "cp500", "cp500.rom");
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

/// <summary>A theory that is skipped, not failed, when the CP-500 ROM dump is not available.</summary>
public sealed class RealRomTheoryAttribute : TheoryAttribute
{
    public RealRomTheoryAttribute()
    {
        if (Cp500RomFile.Path == null)
            Skip = Cp500RomFile.MissingReason;
    }
}
