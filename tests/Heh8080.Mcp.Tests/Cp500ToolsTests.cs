using Heh8080.Cp500Host;
using Heh8080.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Heh8080.Mcp.Tests;

public sealed class Cp500ToolsTests
{
    private static readonly byte[] ReadyStub =
    [
        0x21, 0x0D, 0x00,
        0x11, 0x00, 0x3C,
        0x01, 0x05, 0x00,
        0xED, 0xB0,
        0x18, 0xFE,
        0x52, 0x45, 0x41, 0x44, 0x59,
    ];

    [Fact]
    public void ToolsBootTypeReadPeekPokeAndStep()
    {
        using Cp500Session session = Cp500Session.Create(ReadyStub, new Cp500Media());
        var tools = new Cp500Tools(session);

        Assert.Equal("booted", tools.Boot());
        Assert.Contains("READY", tools.ReadScreen());
        Assert.Equal("5245414459", tools.PeekMemory(15360, 5));
        Assert.Equal("wrote 1", tools.PokeMemory(16384, "21"));
        Assert.Equal("21", tools.PeekMemory(16384, 1));
        Assert.Equal("typed", tools.Type("A"));
        Assert.Contains("READY", tools.ReadScreen());
        Assert.Equal("12", tools.Step(1));
    }

    [Fact]
    public void ARefusalThrowsInsteadOfReturningAnErrorString()
    {
        using Cp500Session session = Cp500Session.Create(ReadyStub, new Cp500Media());
        var tools = new Cp500Tools(session);
        string before = tools.ReadScreen();

        Assert.Throws<Cp500Rejected>(() => tools.Type("\\"));
        Cp500Rejected address = Assert.Throws<Cp500Rejected>(() => tools.PeekMemory(-1, 1));
        Cp500Rejected hex = Assert.Throws<Cp500Rejected>(() => tools.PokeMemory(0, "zz"));
        Assert.Throws<Cp500Rejected>(() => tools.Step(0));

        Assert.Equal(before, tools.ReadScreen());
        Assert.Equal("Invalid hex data.", hex.Message);
        Assert.Equal("Address -1 is outside 0..65535.", address.Message);
    }

    [Fact]
    public void TheContainerDoesNotDisposeASessionHandedToIt()
    {
        using Cp500Session session = Cp500Session.Create(ReadyStub, new Cp500Media());
        var services = new ServiceCollection();
        services.AddSingleton(session);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(session, provider.GetRequiredService<Cp500Session>());
        provider.Dispose();

        Assert.Contains("READY", session.ReadScreen());
    }
}
