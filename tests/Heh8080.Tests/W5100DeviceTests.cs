using System.Net;
using System.Net.Sockets;
using Heh8080.Core;
using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class W5100DeviceTests
{
    [Fact]
    public async Task PayloadArrivesAfterTheConnectDrainWindow()
    {
        byte[] payload = "HTTP/1.0 200 OK\r\n\r\nOK"u8.ToArray();
        using var listen = new TcpListener(IPAddress.Loopback, 0);
        listen.Start();
        int port = ((IPEndPoint)listen.LocalEndpoint).Port;
        Task server = AcceptAndWriteAsync(listen, payload);

        var bus = new IoBus();
        var device = new W5100Device();
        device.Register(bus);
        W5100Port.OpenTcp(bus);
        await Task.Run(() => W5100Port.Connect(bus, port));
        await server;

        Assert.Equal(0x17, W5100Port.Status(bus));
        Assert.Equal(0x17, W5100Port.Status(bus));
        Assert.Equal(payload, W5100Port.Receive(bus));
    }

    [Fact]
    public async Task SendWritesPayloadToThePeer()
    {
        using var listen = new TcpListener(IPAddress.Loopback, 0);
        listen.Start();
        int port = ((IPEndPoint)listen.LocalEndpoint).Port;
        Task<TcpClient> accepted = listen.AcceptTcpClientAsync();

        var bus = new IoBus();
        var device = new W5100Device();
        device.Register(bus);
        W5100Port.OpenTcp(bus);
        await Task.Run(() => W5100Port.Connect(bus, port));
        using TcpClient peer = await accepted;
        using NetworkStream stream = peer.GetStream();

        byte[] payload = "PING"u8.ToArray();
        W5100Port.Send(bus, payload);

        var got = new byte[payload.Length];
        int n = 0;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (n < got.Length && DateTime.UtcNow < deadline)
        {
            if (peer.Available > 0)
                n += await stream.ReadAsync(got.AsMemory(n, got.Length - n));
            else
                await Task.Delay(10);
        }

        Assert.Equal(payload.Length, n);
        Assert.Equal(payload, got);
    }

    [Fact]
    public async Task QuietPeerStaysEstablished()
    {
        using var listen = new TcpListener(IPAddress.Loopback, 0);
        listen.Start();
        int port = ((IPEndPoint)listen.LocalEndpoint).Port;
        Task<TcpClient> accepted = listen.AcceptTcpClientAsync();

        var bus = new IoBus();
        var device = new W5100Device();
        device.Register(bus);
        W5100Port.OpenTcp(bus);
        await Task.Run(() => W5100Port.Connect(bus, port));
        using TcpClient peer = await accepted;

        Assert.Equal(0x17, W5100Port.Status(bus));
        Assert.Equal(0x17, W5100Port.Status(bus));
        Assert.Equal(0, W5100Port.RxSize(bus));
    }

    private static async Task AcceptAndWriteAsync(TcpListener listen, byte[] payload)
    {
        using TcpClient client = await listen.AcceptTcpClientAsync();
        await Task.Delay(200);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(payload);
    }
}

internal static class W5100Port
{
    public static void OpenTcp(IoBus bus)
    {
        Write(bus, 0x0400, 0x01);
        Write(bus, 0x0401, 0x01);
    }

    public static void Connect(IoBus bus, int port)
    {
        Write(bus, 0x040C, 127);
        Write(bus, 0x040D, 0);
        Write(bus, 0x040E, 0);
        Write(bus, 0x040F, 1);
        Select(bus, 0x0410);
        bus.Out(W5100Device.PortDr, (byte)(port >> 8));
        bus.Out(W5100Device.PortDr, (byte)port);
        Write(bus, 0x0401, 0x04);
    }

    public static byte Status(IoBus bus)
    {
        Select(bus, 0x0403);
        return bus.In(W5100Device.PortDr);
    }

    public static int RxSize(IoBus bus)
    {
        Select(bus, 0x0426);
        int high = bus.In(W5100Device.PortDr);
        int low = bus.In(W5100Device.PortDr);
        return (high << 8) | low;
    }

    public static byte[] Receive(IoBus bus)
    {
        int size = RxSize(bus);
        Select(bus, 0x0426);
        bus.Out(W5100Device.PortDr, (byte)(size >> 8));
        bus.Out(W5100Device.PortDr, (byte)size);
        Write(bus, 0x0401, 0x40);
        var data = new byte[size];
        Select(bus, 0x6000);
        for (int i = 0; i < size; i++)
            data[i] = bus.In(W5100Device.PortDr);
        return data;
    }

    /// <summary>Kernel-shaped send: copy into TX at 4000h, advance TX_WR, issue SEND.</summary>
    public static void Send(IoBus bus, ReadOnlySpan<byte> payload)
    {
        Select(bus, 0x4000);
        foreach (byte value in payload)
            bus.Out(W5100Device.PortDr, value);

        Select(bus, 0x0424);
        bus.Out(W5100Device.PortDr, (byte)(payload.Length >> 8));
        bus.Out(W5100Device.PortDr, (byte)payload.Length);
        Write(bus, 0x0401, 0x20);
    }

    private static void Write(IoBus bus, ushort address, byte value)
    {
        Select(bus, address);
        bus.Out(W5100Device.PortDr, value);
    }

    private static void Select(IoBus bus, ushort address)
    {
        bus.Out(W5100Device.PortAr0, (byte)(address >> 8));
        bus.Out(W5100Device.PortAr1, (byte)address);
    }
}
