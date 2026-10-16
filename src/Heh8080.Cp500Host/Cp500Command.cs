using System.Globalization;
using Heh8080.Devices;

namespace Heh8080.Cp500Host;

/// <summary>Refusal before a result exists: bad argv, bad files, unmapped keys, bad memory range, flush failure.</summary>
public sealed class Cp500Rejected : Exception
{
    public Cp500Rejected(string message) : base(message) { }
}

public static class Cp500Exit
{
    public const int Success = 0;
    public const int Timeout = 1;
    public const int Rejected = 2;

    public static int For(Cp500BatchResult result) => result.Matched ? Success : Timeout;
}

/// <summary>Matched is true when the batch had no until-screen, or the text appeared. Screen is empty when the batch did not dump.</summary>
public readonly record struct Cp500BatchResult(bool Matched, string Screen);

/// <summary>A disk flag before the file is read.</summary>
public readonly record struct Cp500DiskFile(int Drive, string Path);

/// <summary>Paths only. No bytes, no machine.</summary>
public sealed record Cp500Files(string RomPath, IReadOnlyList<Cp500DiskFile> Disks)
{
    public static Cp500Session Load(Cp500Files files)
    {
        ArgumentNullException.ThrowIfNull(files);
        byte[] rom = ReadRequired(files.RomPath, "ROM");
        var media = new Cp500Media();
        foreach (Cp500DiskFile disk in files.Disks)
        {
            string path = disk.Path;
            byte[] image = ReadRequired(path, "Disk");
            media.Set(disk.Drive, new Cp500Disk(image, Path.GetFileName(path), bytes => File.WriteAllBytes(path, bytes)));
        }

        return Cp500Session.Create(rom, media);
    }

    private static byte[] ReadRequired(string path, string kind)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new Cp500Rejected($"{kind} '{path}' could not be read: {ex.Message}");
        }
    }
}

/// <summary>
/// CLI language. A batch cannot carry an until-screen without a timeout, or a timeout without an until-screen.
/// </summary>
public abstract record Cp500Command
{
    public const string Usage =
        """
        heh8080-cp500 --rom <path> [--disk0 <path>] [--disk1 <path>] [--disk2 <path>] [--disk3 <path>] [--type <text>] [--until-screen <text> --timeout <Ns|Nms>] [--dump-screen]
        heh8080-cp500 --mcp --rom <path> [--disk0 <path>] [--disk1 <path>] [--disk2 <path>] [--disk3 <path>]
        """;

    private Cp500Command()
    {
    }

    public sealed record Batch : Cp500Command
    {
        public Cp500Files Files { get; }
        public string? TypeText { get; }
        public ScreenText? Until { get; }
        public EmulatedTime? Timeout { get; }
        public bool Dump { get; }

        public Batch(Cp500Files files, string? typeText, ScreenText? until, EmulatedTime? timeout, bool dump)
        {
            if ((until is null) != (timeout is null))
                throw new Cp500Rejected("--until-screen and --timeout must be passed together.");

            Files = files;
            TypeText = typeText;
            Until = until;
            Timeout = timeout;
            Dump = dump;
        }

        public Cp500BatchResult Run(Cp500Session session)
        {
            ArgumentNullException.ThrowIfNull(session);
            if (TypeText is not null)
                session.Type(TypeText);

            bool matched = this is { Until: { } until, Timeout: { } timeout }
                ? session.RunUntil(until, timeout)
                : true;
            return new Cp500BatchResult(matched, Dump ? session.ReadScreen() : "");
        }
    }

    public sealed record Serve(Cp500Files Files) : Cp500Command;

    public static Cp500Command Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args.Contains("--help"))
            throw new Cp500Rejected(Usage);

        string? rom = null;
        string? type = null;
        string? until = null;
        string? timeout = null;
        bool dump = false;
        bool mcp = false;
        var disks = new List<Cp500DiskFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (token is "--dump-screen" or "--mcp")
            {
                RequireOnce(seen, token);
                if (token == "--mcp")
                    mcp = true;
                else
                    dump = true;
                continue;
            }

            if (TryDiskFlag(token, out int drive))
            {
                RequireOnce(seen, token);
                Cp500Media.EnsureInstalled(drive);
                disks.Add(new Cp500DiskFile(drive, TakeValue(args, ref i, token)));
                continue;
            }

            switch (token)
            {
                case "--rom":
                    RequireOnce(seen, token);
                    rom = TakeValue(args, ref i, token);
                    break;
                case "--type":
                    RequireOnce(seen, token);
                    type = TakeValue(args, ref i, token);
                    break;
                case "--until-screen":
                    RequireOnce(seen, token);
                    until = TakeValue(args, ref i, token);
                    break;
                case "--timeout":
                    RequireOnce(seen, token);
                    timeout = TakeValue(args, ref i, token);
                    break;
                default:
                    throw new Cp500Rejected($"Unknown argument '{token}'.");
            }
        }

        if (rom is null)
            throw new Cp500Rejected("Missing --rom.");

        if (mcp && (type is not null || until is not null || timeout is not null || dump))
            throw new Cp500Rejected("--mcp cannot be combined with --type, --until-screen, --timeout, or --dump-screen.");

        var files = new Cp500Files(rom, disks);
        if (mcp)
            return new Serve(files);

        ScreenText? untilText = until is null ? null : ScreenText.Parse(until);
        EmulatedTime? timeoutTime = timeout is null ? null : EmulatedTime.Parse(timeout, Cp500MachineProfile.Original.ClockHz);
        return new Batch(files, type, untilText, timeoutTime, dump);
    }

    private static void RequireOnce(HashSet<string> seen, string flag)
    {
        if (!seen.Add(flag))
            throw new Cp500Rejected($"Duplicate flag {flag}.");
    }

    private static string TakeValue(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new Cp500Rejected($"Missing value for {flag}.");

        index++;
        return args[index];
    }

    private static bool TryDiskFlag(string token, out int drive)
    {
        const string Prefix = "--disk";
        drive = -1;
        if (!token.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        if (!int.TryParse(token[Prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out drive))
            return false;

        return token == Prefix + drive.ToString(CultureInfo.InvariantCulture);
    }
}
