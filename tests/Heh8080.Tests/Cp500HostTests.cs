using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class Cp500ScreenTests
{
    [Fact]
    public void ToTextKeepsPrintableBytesTrimsRowsAndIgnores32ColumnMode()
    {
        var video = new Cp500VideoDevice();
        Write(video, column: 0, (byte)'A');
        Write(video, column: 1, 0x01);
        Write(video, column: 2, 0x7F);
        Write(video, column: 3, (byte)'~');
        Write(video, column: 4, (byte)' ');
        Write(video, column: 5, (byte)'Z');
        for (int column = 6; column < 12; column++)
            Write(video, column, (byte)' ');

        const string expected = "A  ~ Z\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n";

        Assert.Equal(expected, Cp500Screen.ToText(video));

        video.Out(Cp500VideoDevice.ControlPort, Cp500VideoDevice.ThirtyTwoColumnBit);

        Assert.True(video.Is32ColumnMode);
        Assert.Equal(expected, Cp500Screen.ToText(video));
    }

    private static void Write(Cp500VideoDevice video, int column, byte value) =>
        video.VideoRam.Write((ushort)(Cp500VideoDevice.StartAddress + column), value);
}
