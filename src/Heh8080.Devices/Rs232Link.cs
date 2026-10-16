using System.Globalization;

namespace Heh8080.Devices;

/// <summary>
/// How the RS-232 board is cabled. Illegal ports and a colon inside a host cannot be constructed.
/// <see cref="Parse"/> is the text boundary.
/// </summary>
public abstract record Rs232Link
{
    /// <summary>Canonical text for a usage error.</summary>
    public const string Syntax = "off | tcp:<host>:<port> | listen:<port> | hayes:<port> | com:<name>";

    /// <summary>Board stays out of the machine.</summary>
    public static Rs232Link Off { get; } = new OffLink();

    private Rs232Link()
    {
    }

    /// <summary>Parses one link. The scheme and <c>off</c> are case-insensitive. Host and COM name keep their case.</summary>
    public static Rs232Link Parse(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new FormatException(Message());

            if (text.Equals("off", StringComparison.OrdinalIgnoreCase))
                return Off;

            int colon = text.IndexOf(':');
            if (colon <= 0)
                throw new FormatException(Message());

            string scheme = text[..colon];
            string rest = text[(colon + 1)..];
            if (scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase))
            {
                int split = rest.IndexOf(':');
                if (split <= 0 || split != rest.LastIndexOf(':'))
                    throw new FormatException(Message());

                return Tcp(rest[..split], ParsePort(rest[(split + 1)..]));
            }

            if (scheme.Equals("listen", StringComparison.OrdinalIgnoreCase))
                return Listen(ParsePort(rest));

            if (scheme.Equals("hayes", StringComparison.OrdinalIgnoreCase))
                return Hayes(ParsePort(rest));

            if (scheme.Equals("com", StringComparison.OrdinalIgnoreCase))
                return Com(rest);

            throw new FormatException(Message());
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            throw new FormatException(Message(), ex);
        }
    }

    /// <summary>Connect to <paramref name="host"/> on <paramref name="port"/>.</summary>
    public static Rs232Link Tcp(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Contains(':'))
            throw new ArgumentException("Host must be a name or an IPv4 address with no colon.", nameof(host));

        return new TcpLink(host, CheckPort(port));
    }

    /// <summary>Bind all interfaces and wait for one client.</summary>
    public static Rs232Link Listen(int port) => new ListenLink(CheckPort(port));

    /// <summary>Answer AT commands on this port.</summary>
    public static Rs232Link Hayes(int port) => new HayesLink(CheckPort(port));

    /// <summary>Open a host serial port.</summary>
    public static Rs232Link Com(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A COM port name is required.", nameof(name));

        return new ComLink(name);
    }

    private static int ParsePort(string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int port))
            throw new FormatException(Message());

        return CheckPort(port);
    }

    private static int CheckPort(int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be 1 through 65535.");

        return port;
    }

    private static string Message() => $"RS-232 link must be {Syntax}.";

    /// <summary>No cable.</summary>
    public sealed record OffLink : Rs232Link;

    /// <summary>Outbound TCP.</summary>
    public sealed record TcpLink : Rs232Link
    {
        internal TcpLink(string host, int port)
        {
            Host = host;
            Port = port;
        }

        public string Host { get; }

        public int Port { get; }
    }

    /// <summary>Inbound raw TCP.</summary>
    public sealed record ListenLink : Rs232Link
    {
        internal ListenLink(int port) => Port = port;

        public int Port { get; }
    }

    /// <summary>Hayes modem on a listening socket.</summary>
    public sealed record HayesLink : Rs232Link
    {
        internal HayesLink(int port) => Port = port;

        public int Port { get; }
    }

    /// <summary>Host serial port.</summary>
    public sealed record ComLink : Rs232Link
    {
        internal ComLink(string name) => Name = name;

        public string Name { get; }
    }
}
