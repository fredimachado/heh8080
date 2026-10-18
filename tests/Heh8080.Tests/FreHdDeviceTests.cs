using Heh8080.Core;
using Heh8080.Devices;

namespace Heh8080.Tests;

internal static class FreHdKernelRead
{
    public static byte ReadBlock0FirstDataByte(IoBus bus)
    {
        bus.Out(FreHdDevice.PortCount, 1);
        bus.Out(FreHdDevice.PortSector, 1);
        bus.Out(FreHdDevice.PortSdh, 0);
        bus.Out(FreHdDevice.PortCylLo, 0);
        bus.Out(FreHdDevice.PortCylHi, 0);
        bus.Out(FreHdDevice.PortStatus, 0x20);
        Assert.Equal(0, bus.In(FreHdDevice.PortError));
        return bus.In(FreHdDevice.PortData);
    }
}

public sealed class FreHdDeviceTests
{
    [Fact]
    public void ProbeEchoAndOneBlockReadWrite()
    {
        byte[] image = new byte[512];
        image[0] = 0xAB;
        var bus = new IoBus();
        var device = new FreHdDevice(image);
        device.Register(bus);

        bus.Out(FreHdDevice.PortSdh, 0x07);
        Assert.Equal(0x07, bus.In(FreHdDevice.PortSdh));
        Assert.Equal(0x58, bus.In(FreHdDevice.PortStatus));

        Assert.Equal(0xAB, FreHdKernelRead.ReadBlock0FirstDataByte(bus));

        bus.Out(FreHdDevice.PortCount, 1);
        bus.Out(FreHdDevice.PortSector, 1);
        bus.Out(FreHdDevice.PortSdh, 0);
        bus.Out(FreHdDevice.PortCylLo, 0);
        bus.Out(FreHdDevice.PortCylHi, 0);
        bus.Out(FreHdDevice.PortStatus, 0x30);
        for (int i = 0; i < 256; i++)
            bus.Out(FreHdDevice.PortData, (byte)(i == 0 ? 0xCD : 0));
        Assert.Equal(0x78, bus.In(FreHdDevice.PortStatus));
        Assert.Equal(0xCD, image[0]);
    }

    [Fact]
    public void UnattachedStatusIsFf()
    {
        var bus = new IoBus();
        Assert.Equal(0xFF, bus.In(FreHdDevice.PortStatus));
    }
}
