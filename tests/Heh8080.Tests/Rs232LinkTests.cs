using System.Net;
using System.Net.Sockets;
using Heh8080.Core;
using Heh8080.Devices;
using Heh8080.Rs232;

namespace Heh8080.Tests;

public sealed class Rs232LinkTests
{
    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("Off")]
    public void OffIsTheSameValue(string text)
    {
        Assert.Same(Rs232Link.Off, Rs232Link.Parse(text));
    }

    [Fact]
    public void TcpKeepsTheHostCaseAndThePort()
    {
        var link = Assert.IsType<Rs232Link.TcpLink>(Rs232Link.Parse("tcp:Printer.local:2323"));

        Assert.Equal("Printer.local", link.Host);
        Assert.Equal(2323, link.Port);
        Assert.Equal(link, Rs232Link.Tcp("Printer.local", 2323));
    }

    [Fact]
    public void ListenHayesAndComKeepTheirPayload()
    {
        var listen = Assert.IsType<Rs232Link.ListenLink>(Rs232Link.Parse("LISTEN:23"));
        var hayes = Assert.IsType<Rs232Link.HayesLink>(Rs232Link.Parse("Hayes:65535"));
        var com = Assert.IsType<Rs232Link.ComLink>(Rs232Link.Parse("com:Com3"));

        Assert.Equal(23, listen.Port);
        Assert.Equal(65535, hayes.Port);
        Assert.Equal("Com3", com.Name);
    }

    [Theory]
    [InlineData("tcp:bad:host:1")]
    [InlineData("tcp:127.0.0.1:0")]
    [InlineData("tcp:127.0.0.1:65536")]
    [InlineData("listen:0")]
    [InlineData("hayes:")]
    [InlineData("com:")]
    [InlineData("com:   ")]
    [InlineData("nope")]
    [InlineData("")]
    [InlineData("tcp:host")]
    public void IllegalTextNamesTheSyntax(string text)
    {
        FormatException ex = Assert.Throws<FormatException>(() => Rs232Link.Parse(text));
        Assert.Contains(Rs232Link.Syntax, ex.Message);
    }

    [Fact]
    public void FactoriesRejectAColonInTheHostAndAPortOutsideTheRange()
    {
        Assert.Throws<ArgumentException>(() => Rs232Link.Tcp("a:b", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rs232Link.Listen(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rs232Link.Hayes(65536));
        Assert.Throws<ArgumentException>(() => Rs232Link.Com(" "));
    }

    [Fact]
    public async Task OpenConnectsListensAndRefusesAClosedPort()
    {
        long clock = Cp500MachineProfile.Original.ClockHz;
        Assert.Equal(Rs232Attachment.None, Rs232Endpoint.Open(Rs232Link.Off, clock));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
        Rs232Attachment outbound = Rs232Endpoint.Open(Rs232Link.Tcp("127.0.0.1", port), clock);
        using TcpClient peer = await accepted;
        peer.GetStream().ReadTimeout = 2000;
        Assert.Equal(ModemInputs.Online, outbound.Endpoint!.Inputs);
        outbound.Endpoint.Write(0x5A);
        byte[] got = new byte[1];
        Assert.Equal(1, peer.GetStream().Read(got, 0, 1));
        Assert.Equal(0x5A, got[0]);
        outbound.Endpoint.Dispose();

        listener.Stop();
        Assert.Throws<IOException>(() => Rs232Endpoint.Open(Rs232Link.Tcp("127.0.0.1", port), clock));

        int listenPort = FreePort();
        Rs232Attachment inbound = Rs232Endpoint.Open(Rs232Link.Listen(listenPort), clock);
        Assert.Equal(ModemInputs.Idle, inbound.Endpoint!.Inputs);
        using var client = new TcpClient { NoDelay = true };
        client.Connect(IPAddress.Loopback, listenPort);
        Assert.True(WaitUntil(() => inbound.Endpoint.Inputs == ModemInputs.Online));
        client.GetStream().Write([0xA5], 0, 1);
        Assert.True(WaitUntil(() => inbound.Endpoint.TryRead(out byte value) && value == 0xA5));
        inbound.Endpoint.Dispose();

        Assert.Throws<IOException>(() => Rs232Endpoint.Open(Rs232Link.Com("not-a-real-port"), clock));
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static bool WaitUntil(Func<bool> ready)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (ready())
                return true;
        }

        return false;
    }
}
