using Heh8080.Devices;

namespace Heh8080.Rs232;

/// <summary>Opens the cable a <see cref="Rs232Link"/> names. This is the only production constructor of a socket or COM port.</summary>
public static class Rs232Endpoint
{
    /// <summary>
    /// Opens <paramref name="link"/>. <see cref="Rs232Link.Off"/> returns <see cref="Rs232Attachment.None"/>.
    /// A refused connection, a failed bind, or a COM port that will not open throws <see cref="IOException"/>.
    /// </summary>
    public static Rs232Attachment Open(Rs232Link link, long clockHz)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clockHz);
        return link switch
        {
            Rs232Link.OffLink => Rs232Attachment.None,
            Rs232Link.TcpLink tcp => new Rs232Attachment(true, TcpEndpoint.Connect(tcp.Host, tcp.Port)),
            Rs232Link.ListenLink listen => new Rs232Attachment(true, TcpEndpoint.Listen(listen.Port)),
            Rs232Link.HayesLink hayes => new Rs232Attachment(true, new HayesEndpoint(hayes.Port, clockHz)),
            Rs232Link.ComLink com => new Rs232Attachment(true, ComEndpoint.Open(com.Name)),
            _ => throw new ArgumentOutOfRangeException(nameof(link), link.GetType().Name, "Unknown RS-232 link."),
        };
    }
}
