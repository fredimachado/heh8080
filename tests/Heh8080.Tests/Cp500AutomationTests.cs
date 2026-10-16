using System.Text;
using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class Cp500AutomationTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    [Fact]
    public void RunUntilScreenContainsFindsTextTheProgramWrites()
    {
        var automation = new Cp500Automation(Cp500TestRoms.Printing("HELLO, CP-500", row: 2, column: 4));

        Assert.True(automation.RunUntilScreenContains("CP-500", OneSecond));

        Assert.Equal("\n\n    HELLO, CP-500" + new string('\n', 13), automation.ReadScreenText());
        Assert.True(automation.Elapsed < TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public void RunUntilScreenContainsGivesUpAfterTheTimeoutOfEmulatedTime()
    {
        var automation = new Cp500Automation(Cp500TestRoms.Spinning());

        Assert.False(automation.RunUntilScreenContains("ok", TimeSpan.FromSeconds(2)));

        Assert.InRange(automation.ElapsedCycles, 2 * 2_030_000, 2 * 2_030_000 + 20);
    }

    [Fact]
    public void RunAdvancesExactlyTheEmulatedDuration()
    {
        var automation = new Cp500Automation(Cp500TestRoms.Spinning());

        automation.Run(TimeSpan.FromMilliseconds(500));
        automation.Run(TimeSpan.FromMilliseconds(500));

        Assert.InRange(automation.ElapsedCycles, 2_030_000, 2_030_000 + 20);
    }

    [Fact]
    public void TypeClosesTheLetterSwitchWithShiftForAnUpperCaseLetter()
    {
        var automation = new Cp500Automation(Cp500TestRoms.KeyboardLatch());

        automation.Type("a");
        Assert.Equal(new byte[] { 0x02, 0x00 }, automation.ReadMemory(0x3C00, 2));

        automation.Type("G");
        Assert.Equal(new byte[] { 0x82, 0x01 }, automation.ReadMemory(0x3C00, 2));
    }

    [Fact]
    public void TypeHoldsEachKeyThenReleasesItForTheGap()
    {
        var automation = new Cp500Automation(Cp500TestRoms.Spinning());

        automation.Type("@@");

        Assert.Equal(0x00, automation.Machine.Memory.Read(0x3801));
        long expected = 2 * (long)Math.Round((Cp500Automation.KeyHoldDuration + Cp500Automation.KeyGapDuration).TotalSeconds * 2_030_000);
        Assert.InRange(automation.ElapsedCycles, expected, expected + 20);
    }

    [Fact]
    public void ReadMemoryReturnsWhatTheBootSectorReadPutThere()
    {
        var drives = new Cp500DriveBay();
        byte[] disk = new byte[ModelIIIDiskGeometry.DiskSizeBytes];
        Encoding.ASCII.GetBytes("BOOTDATA").CopyTo(disk, 0);
        disk[255] = 0x5A;
        drives.Insert(0, disk, "boot.dsk", writeBack: null);
        var automation = new Cp500Automation(Cp500TestRoms.ReadingBootSector(), drives);

        automation.Run(TimeSpan.FromMilliseconds(100));

        Assert.Equal("BOOTDATA"u8.ToArray(), automation.ReadMemory(0x4300, 8));
        Assert.Equal(new byte[] { 0x5A, 0x00 }, automation.ReadMemory(0x43FF, 2));
    }

    [Fact]
    public void ReadMemoryStopsAtTheTopOfTheAddressSpace()
    {
        var automation = new Cp500Automation(Cp500TestRoms.Spinning());

        Assert.Equal(new byte[] { 0x18, 0xFE }, automation.ReadMemory(0x0000, 2));
        Assert.Single(automation.ReadMemory(0xFFFF, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => automation.ReadMemory(0xFFFF, 2));
    }

    [Fact]
    public void AnEmptyDriveZeroStartsHoldingBreakLikeTheDesktop()
    {
        var empty = new Cp500Automation(Cp500TestRoms.Spinning());
        var drives = new Cp500DriveBay();
        drives.Insert(0, new byte[ModelIIIDiskGeometry.DiskSizeBytes], "boot.dsk", writeBack: null);
        var booting = new Cp500Automation(Cp500TestRoms.Spinning(), drives);
        var noDrives = new Cp500Automation(Cp500TestRoms.Spinning(), profile: new Cp500MachineProfile(installedDrives: 0));

        Assert.Equal(0x04, empty.Machine.Memory.Read(0x3840));
        Assert.Equal(0x00, booting.Machine.Memory.Read(0x3840));
        Assert.Equal(0x00, noDrives.Machine.Memory.Read(0x3840));
    }

    [RealRomFact]
    public void RealRomTypesALineIntoBasicAndSeesTheResult()
    {
        var automation = new Cp500Automation(File.ReadAllBytes(Cp500RomFile.Path!));

        Assert.True(automation.RunUntilScreenContains("BASIC (S ou N)", TimeSpan.FromSeconds(10)));
        automation.Type(@"S\r\r");
        Assert.True(automation.RunUntilScreenContains("READY", TimeSpan.FromSeconds(10)));
        automation.Type(@"PRINT 2+3\r");
        Assert.True(automation.RunUntilScreenContains(" 5", TimeSpan.FromSeconds(5)));

        Assert.Contains("\n>PRINT 2+3\n 5\nREADY\n>", automation.ReadScreenText());
    }
}
