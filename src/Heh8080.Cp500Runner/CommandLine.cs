using System.Globalization;
using Heh8080.Devices;

namespace Heh8080.Cp500Runner;

/// <summary>A command line that cannot run; the message says why.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>Parses the runner's arguments into typed options and an ordered list of steps.</summary>
internal static class CommandLine
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static readonly string Usage =
        """
        Usage: heh8080-cp500 --rom <path> [options] [steps]

        Boots a CP-500 from a system ROM and runs the steps in order, as fast as the host allows.
        Time is emulated Z80 cycles only, so every run is reproducible.

        Info:
          -h, --help                Show this help and exit.
          -V, --version             Show the version and exit.

        Machine:
          --rom <path>              System ROM image (required).
          --disk0 .. --disk3 <path> Disk image for a drive: raw, JV3 or DMK.
          --hard-disk <path>        FreHD image, or a raw sector body when --chs is set.
          --chs <c>,<h>,<s>         Cylinders, heads, and sectors for a raw --hard-disk body.
          --rs232 <link>            off | tcp:<host>:<port> | listen:<port> | hayes:<port> | com:<name>.
          --w5100                   Attach a W5100 network board at D0h-D3h.
          --write-back              At exit, save modified disks to their files in their own format.
          --timeout <duration>      Emulated time each --until-screen may take (default 30s).

        Steps, run in command-line order:
          --until-screen <text>     Run until the text appears within a screen row.
          --type <text>             Type through the keyboard. \r or a carriage return is ENTER, \\ a
                                    backslash, and {ENTER} {CLEAR} {BREAK} {UP} {DOWN} {LEFT} {RIGHT}
                                    {SPACE} name keys without a character.
          --run <duration>          Run for that much emulated time.

        At exit:
          --dump-screen             Print the screen as text.
          --dump-memory <hex>:<len> Print a hex dump of <len> bytes from address <hex>; repeatable.

        Durations: 500ms, 30s, 2m (decimals allowed). Exit codes: 0 all steps succeeded,
        1 an --until-screen timed out, 2 usage error, missing file or invalid image.
        """.ReplaceLineEndings("\n");

    private const int MaxDrives = ModelIIIDiskGeometry.MaxDrives;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromDays(1);

    public static ParsedCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
            throw new UsageException("--rom is required.");

        string? rom = null;
        var disks = new DiskOption?[MaxDrives];
        bool w5100 = false;
        bool writeBack = false;
        bool dumpScreen = false;
        Rs232Link? rs232 = null;
        string? hardDisk = null;
        HardDiskGeometry? chs = null;
        TimeSpan? timeout = null;
        var dumps = new List<MemoryRange>();
        var steps = new List<RunnerStep>();

        for (int i = 0; i < args.Count; i++)
        {
            string option = args[i];
            switch (option)
            {
                case "-h" or "--help":
                    return new ShowHelp();
                case "-V" or "--version":
                    return new ShowVersion();
                case "--rom":
                    rom = Once(rom, option, Value(args, ref i));
                    break;
                case "--w5100":
                    w5100 = true;
                    break;
                case "--write-back":
                    writeBack = true;
                    break;
                case "--rs232":
                    rs232 = Once(rs232, option, ParseLink(Value(args, ref i)));
                    break;
                case "--hard-disk":
                    hardDisk = Once(hardDisk, option, Value(args, ref i));
                    break;
                case "--chs":
                    chs = Once(chs, option, ParseGeometry(Value(args, ref i)));
                    break;
                case "--dump-screen":
                    dumpScreen = true;
                    break;
                case "--timeout":
                    timeout = Once(timeout, option, ParseDuration(option, Value(args, ref i)));
                    break;
                case "--dump-memory":
                    dumps.Add(ParseMemoryRange(Value(args, ref i)));
                    break;
                case "--until-screen":
                    steps.Add(ParseUntilScreen(Value(args, ref i)));
                    break;
                case "--type":
                    steps.Add(ParseType(Value(args, ref i)));
                    break;
                case "--run":
                    steps.Add(new RunStep(ParseDuration(option, Value(args, ref i))));
                    break;
                default:
                    int drive = DriveOf(option);
                    disks[drive] = Once(disks[drive], option, new DiskOption(drive, Value(args, ref i)));
                    break;
            }
        }

        if (rom is null)
            throw new UsageException("--rom is required.");

        if (chs is not null && hardDisk is null)
            throw new UsageException("--chs requires --hard-disk.");

        return new RunMachine(
            rom,
            disks.OfType<DiskOption>().ToArray(),
            w5100,
            writeBack,
            timeout ?? DefaultTimeout,
            dumpScreen,
            dumps,
            steps,
            rs232 ?? Rs232Link.Off,
            hardDisk is null ? null : new HardDiskOption(hardDisk, chs));
    }

    private static HardDiskGeometry ParseGeometry(string text)
    {
        string[] parts = text.Split(',');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int cylinders)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int heads)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int sectors))
        {
            throw new UsageException("--chs needs <cylinders>,<heads>,<sectors>.");
        }

        try
        {
            return new HardDiskGeometry(cylinders, heads, sectors);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new UsageException("--chs needs cylinders 1-1023, heads 1-8, and sectors 1-256.");
        }
    }

    /// <summary>Parses 500ms, 30s, 2m or 1.5s into a positive duration of at most a day.</summary>
    public static TimeSpan ParseDuration(string option, string text)
    {
        (string number, double unitSeconds) = text switch
        {
            _ when text.EndsWith("ms", StringComparison.Ordinal) => (text[..^2], 0.001),
            _ when text.EndsWith('s') => (text[..^1], 1.0),
            _ when text.EndsWith('m') => (text[..^1], 60.0),
            _ => (string.Empty, 0.0)
        };

        if (unitSeconds == 0 || number.Length == 0 || !char.IsAsciiDigit(number[0]) ||
            !double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value))
        {
            throw new UsageException($"{option} needs a duration such as 500ms, 30s or 2m, not '{text}'.");
        }

        TimeSpan duration = TimeSpan.FromSeconds(value * unitSeconds);
        if (duration <= TimeSpan.Zero || duration > MaxDuration)
            throw new UsageException($"{option} must be more than zero and at most a day, not '{text}'.");

        return duration;
    }

    private static Rs232Link ParseLink(string text)
    {
        try
        {
            return Rs232Link.Parse(text);
        }
        catch (FormatException ex)
        {
            throw new UsageException(ex.Message);
        }
    }

    private static UntilScreenStep ParseUntilScreen(string text) =>
        text.Length == 0 ? throw new UsageException("--until-screen needs non-empty text.") : new UntilScreenStep(text);

    private static TypeStep ParseType(string text)
    {
        try
        {
            return new TypeStep(text, Cp500KeySequence.Parse(text));
        }
        catch (FormatException error)
        {
            throw new UsageException($"--type \"{text}\": {error.Message}");
        }
    }

    /// <summary>Parses &lt;hex address&gt;:&lt;decimal length&gt;, for example 4300:256. A 0x prefix or h suffix is allowed.</summary>
    private static MemoryRange ParseMemoryRange(string text)
    {
        string[] parts = text.Split(':');
        string address = parts[0];
        if (address.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            address = address[2..];
        else if (address.EndsWith('h') || address.EndsWith('H'))
            address = address[..^1];

        if (parts.Length != 2 || address.Length is 0 or > 4 ||
            !ushort.TryParse(address, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort start) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int length))
        {
            throw new UsageException($"--dump-memory needs <hex address>:<length>, such as 4300:256, not '{text}'.");
        }

        if (length < 1 || length > 0x10000 - start)
            throw new UsageException($"--dump-memory '{text}': the length must be 1 to {0x10000 - start} bytes from {start:X4}h.");

        return new MemoryRange(start, length);
    }

    private static int DriveOf(string option)
    {
        if (option.Length == "--disk0".Length && option.StartsWith("--disk", StringComparison.Ordinal) &&
            option[^1] is >= '0' and < (char)('0' + MaxDrives))
        {
            return option[^1] - '0';
        }

        throw new UsageException(option.StartsWith('-') ? $"Unknown option '{option}'." : $"Unexpected argument '{option}'.");
    }

    private static string Value(IReadOnlyList<string> args, ref int index)
    {
        string option = args[index];
        if (index + 1 >= args.Count)
            throw new UsageException($"{option} needs a value.");

        return args[++index];
    }

    private static T Once<T>(T? current, string option, T value) where T : class =>
        current is null ? value : throw new UsageException($"{option} was given more than once.");

    private static T Once<T>(T? current, string option, T value) where T : struct =>
        current is null ? value : throw new UsageException($"{option} was given more than once.");
}
