using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Heh8080.Devices;
using Heh8080.Rs232;

namespace Heh8080.Tests;

public sealed class HayesEndpointTests
{
    private const long Clock = 1000;

    [Fact]
    public void AtAte0AndAtzSpeakResultCodes()
    {
        using var modem = new HayesEndpoint(0, Clock);

        Assert.Equal(new ModemInputs(true, true, false, false, true), modem.Inputs);
        Assert.Equal("AT\rOK\r\n", Exchange(modem, "AT\r"));
        Assert.Equal("ATE0\rOK\r\n", Exchange(modem, "ATE0\r"));
        Assert.Equal("OK\r\n", Exchange(modem, "AT\r"));
        Assert.Equal("OK\r\n", Exchange(modem, "ATE0V1\r"));
        Assert.Equal("OK\r\n", Exchange(modem, "ATZ\r"));
        Assert.Equal("AT\rOK\r\n", Exchange(modem, "AT\r"));
        Assert.Equal("ATE0S0=1\rOK\r\n", Exchange(modem, "ATE0S0=1\r"));
        Assert.Equal("OK\r\n", Exchange(modem, "ATS0=0\r"));
        Assert.Equal("ERROR\r\n", Exchange(modem, "ATDT99999\r"));
    }

    [Fact(Timeout = 5000)]
    public async Task AtdtConnectsWithoutBlockingTheCallerThenEscapeAndHangup()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<TcpClient> accepted = AcceptAfter(listener, release.Task);

        using var modem = new HayesEndpoint(0, Clock);
        Write(modem, $"ATDT127.0.0.1:{port}\r");
        Assert.False(accepted.IsCompleted);

        release.SetResult();
        using TcpClient peer = await accepted;
        string connected = WaitFor(modem, "CONNECT");
        Assert.Contains("CONNECT\r\n", connected);
        Assert.DoesNotContain("NO CARRIER", connected);
        Assert.True(modem.Inputs.Carrier);
        Assert.Equal(0, peer.Available);

        modem.AdvanceCycles(Clock);
        modem.Write((byte)'+');
        modem.Write((byte)'+');
        modem.Write((byte)'+');
        modem.AdvanceCycles(Clock - 1);
        Assert.DoesNotContain("OK", Read(modem));
        modem.AdvanceCycles(1);

        string escaped = Read(modem);
        Assert.Contains("OK\r\n", escaped);
        Assert.True(modem.Inputs.Carrier);
        Assert.Equal(0, peer.Available);
        Assert.Contains("OK\r\n", Exchange(modem, "ATH\r"));
        Assert.False(modem.Inputs.Carrier);
    }

    [Fact]
    public void RefusedDialReturnsNoCarrier()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var modem = new HayesEndpoint(0, Clock);
        Write(modem, $"ATDT127.0.0.1:{port}\r");
        string text = WaitFor(modem, "NO CARRIER");
        Assert.Contains("NO CARRIER\r\n", text);
        Assert.False(modem.Inputs.Carrier);
    }

    [Fact(Timeout = 5000)]
    public async Task DtrDropHangsUpAnActiveCall()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<TcpClient> accepted = AcceptAfter(listener, release.Task);

        using var modem = new HayesEndpoint(0, Clock);
        modem.SetModemOutputs(new ModemOutputs(true, true));
        Write(modem, $"ATDT127.0.0.1:{port}\r");
        release.SetResult();
        using TcpClient peer = await accepted;
        WaitFor(modem, "CONNECT");
        Assert.True(modem.Inputs.Carrier);

        modem.SetModemOutputs(new ModemOutputs(false, true));
        Assert.False(modem.Inputs.Carrier);
        Assert.Contains("OK\r\n", Exchange(modem, "AT\r"));
    }

    [Fact(Timeout = 5000)]
    public async Task RtsLowHoldsReceivedBytesUntilRtsRises()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var modem = new HayesEndpoint(0, Clock);
        modem.SetModemOutputs(new ModemOutputs(true, true));
        Write(modem, $"ATDT127.0.0.1:{port}\r");
        using TcpClient peer = await listener.AcceptTcpClientAsync();
        WaitFor(modem, "CONNECT");
        Read(modem);

        modem.SetModemOutputs(new ModemOutputs(true, false));
        await peer.GetStream().WriteAsync("X"u8.ToArray());
        modem.AdvanceCycles(0);
        Assert.False(modem.TryRead(out _));

        modem.SetModemOutputs(new ModemOutputs(true, true));
        Assert.True(modem.TryRead(out byte value));
        Assert.Equal((byte)'X', value);
    }

    [Fact]
    public void Ats0AnswersAnInboundCall()
    {
        using var modem = new HayesEndpoint(0, Clock);
        Assert.Contains("OK\r\n", Exchange(modem, "ATS0=1\r"));

        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, modem.LocalPort);
        string text = WaitFor(modem, "CONNECT");
        Assert.Contains("CONNECT\r\n", text);
        Assert.True(modem.Inputs.Carrier);
        Assert.True(client.Connected);
    }

    private static async Task<TcpClient> AcceptAfter(TcpListener listener, Task release)
    {
        await release;
        return await listener.AcceptTcpClientAsync();
    }

    private static void Write(HayesEndpoint modem, string text)
    {
        foreach (char c in text)
            modem.Write((byte)c);
    }

    private static string Exchange(HayesEndpoint modem, string text)
    {
        Write(modem, text);
        return Read(modem);
    }

    private static string Read(HayesEndpoint modem)
    {
        var text = new StringBuilder();
        while (modem.TryRead(out byte value))
            text.Append((char)value);
        return text.ToString();
    }

    private static string WaitFor(HayesEndpoint modem, string token)
    {
        var text = new StringBuilder();
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            modem.AdvanceCycles(0);
            text.Append(Read(modem));
            if (text.ToString().Contains(token, StringComparison.Ordinal))
                return text.ToString();
        }

        return text.ToString();
    }
}
