using System.Text;
using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class Cp500HardDiskRomTests
{
    [Fact]
    public void TinyRomReadsSectorZeroIntoMemory()
    {
        byte[] image = OneSector("FREHD OK");
        var bay = new Cp500DriveBay();
        bay.HardDisk.Insert(image, "disk.hdv", writeBack: null);
        var board = HardDiskAttachment.Install(
            bay.HardDisk,
            new OffsetClock(() => new DateTime(2012, 1, 1, 0, 0, 0, DateTimeKind.Local)));
        var automation = new Cp500Automation(
            Cp500TestRoms.ReadingHardDiskSector(),
            bay,
            board.Apply(Cp500MachineProfile.Original),
            hardDisk: board);

        automation.Run(TimeSpan.FromMilliseconds(100));

        Assert.Equal("FREHD OK"u8.ToArray(), automation.ReadMemory(0x4000, 8));
    }

    private static byte[] OneSector(string text)
    {
        var file = new byte[512];
        file[0] = 0x56;
        file[1] = 0xCB;
        file[2] = 0x10;
        file[26] = 1;
        file[28] = 1;
        file[29] = 1;
        Encoding.ASCII.GetBytes(text).CopyTo(file, 256);
        return file;
    }
}
