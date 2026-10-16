using System.ComponentModel;
using System.Globalization;
using Heh8080.Cp500Host;
using ModelContextProtocol.Server;

namespace Heh8080.Mcp;

/// <summary>
/// Wire adapter. Hex and int addresses live here. Each method calls one session method.
/// A refusal throws, so the SDK sets isError.
/// </summary>
[McpServerToolType]
public sealed class Cp500Tools
{
    private readonly Cp500Session _session;

    public Cp500Tools(Cp500Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    [McpServerTool, Description("Reboot the CP-500. BREAK is up when this returns. Disks stay mounted.")]
    public string Boot()
    {
        _session.Boot();
        return "booted";
    }

    [McpServerTool, Description("Type CP-500 keystrokes. Backslash followed by r, or a CR, is ENTER.")]
    public string Type(string text)
    {
        _session.Type(text);
        return "typed";
    }

    [McpServerTool, Description("Read the 64 by 16 screen as text.")]
    public string ReadScreen() => _session.ReadScreen();

    [McpServerTool, Description("Read memory. Address is 0..65535. Length is 1 through 256. Returns hex.")]
    public string PeekMemory(int address, int length = 16) =>
        Convert.ToHexString(_session.Peek(Address(address), length));

    [McpServerTool, Description("Write hex bytes to memory. Address is 0..65535.")]
    public string PokeMemory(int address, string hexData)
    {
        byte[] data = Hex(hexData);
        _session.Poke(Address(address), data);
        return $"wrote {data.Length}";
    }

    [McpServerTool, Description("Execute count instructions on the CP-500. Count is 1 through 1000000.")]
    public string Step(int count = 1) =>
        _session.Step(count).ToString(CultureInfo.InvariantCulture);

    private static ushort Address(int address)
    {
        if (address is < 0 or > ushort.MaxValue)
            throw new Cp500Rejected($"Address {address} is outside 0..65535.");

        return (ushort)address;
    }

    private static byte[] Hex(string hexData)
    {
        ArgumentNullException.ThrowIfNull(hexData);
        try
        {
            return Convert.FromHexString(hexData);
        }
        catch (FormatException)
        {
            throw new Cp500Rejected("Invalid hex data.");
        }
    }
}
