using System.Text;
using Heh8080.Devices;

namespace Heh8080.Tests;

/// <summary>Tiny hand-assembled Z80 programs that stand in for the copyrighted CP-500 system ROM.</summary>
internal static class Cp500TestRoms
{
    private const int DataOffset = 0x100;

    /// <summary>Copies <paramref name="text"/> into video RAM at a row and column, then spins.</summary>
    public static byte[] Printing(string text, int row = 0, int column = 0)
    {
        ushort target = (ushort)(Cp500VideoDevice.StartAddress + row * Cp500VideoDevice.Columns + column);
        byte[] program =
        [
            0xF3,                                      // DI
            0x21, DataOffset & 0xFF, DataOffset >> 8,  // LD HL,data
            0x11, (byte)target, (byte)(target >> 8),   // LD DE,target
            0x01, (byte)text.Length, 0x00,             // LD BC,length
            0xED, 0xB0,                                // LDIR
            0x18, 0xFE                                 // JR $
        ];
        return WithData(program, Encoding.Latin1.GetBytes(text));
    }

    /// <summary>Spins forever without touching the screen.</summary>
    public static byte[] Spinning() => [0x18, 0xFE]; // JR $

    /// <summary>
    /// Forever ORs keyboard row 0 (@, A-G) into 3C00h and row 7 (SHIFT in bit 0) into 3C01h, so the screen
    /// latches every switch that was ever closed.
    /// </summary>
    public static byte[] KeyboardLatch() =>
    [
        0xF3,                // DI
        0x3A, 0x01, 0x38,    // loop: LD A,(3801h)
        0x21, 0x00, 0x3C,    // LD HL,3C00h
        0xB6,                // OR (HL)
        0x77,                // LD (HL),A
        0x3A, 0x80, 0x38,    // LD A,(3880h)
        0x23,                // INC HL
        0xB6,                // OR (HL)
        0x77,                // LD (HL),A
        0x18, 0xF0           // JR loop
    ];

    /// <summary>Reads drive 0, track 0, sector 1 (double density) into 4300h with the WD1791, then spins.</summary>
    public static byte[] ReadingBootSector() =>
        SectorTransfer(command: 0x80, transfer: [0xDB, 0xF3, 0x77], buffer: 0x4300); // IN A,(F3h); LD (HL),A

    /// <summary>Writes the 256 bytes of <paramref name="sector"/> to drive 0, track 0, sector 1, then spins.</summary>
    public static byte[] WritingBootSector(byte[] sector) =>
        WithData(SectorTransfer(command: 0xA0, transfer: [0x7E, 0xD3, 0xF3], buffer: DataOffset), sector); // LD A,(HL); OUT (F3h),A

    /// <summary>
    /// Selects drive 0 in double density, issues a sector command for sector 1 and moves one byte per DRQ with the
    /// three-byte <paramref name="transfer"/> at HL, until the controller is no longer busy.
    /// </summary>
    private static byte[] SectorTransfer(byte command, byte[] transfer, ushort buffer) =>
    [
        0xF3,                                  // DI
        0x3E, 0x81, 0xD3, 0xF4,                // LD A,81h; OUT (F4h),A
        0x3E, 0x01, 0xD3, 0xF2,                // LD A,1;   OUT (F2h),A
        0x3E, command, 0xD3, 0xF0,             // LD A,cmd; OUT (F0h),A
        0x21, (byte)buffer, (byte)(buffer >> 8), // LD HL,buffer
        0xDB, 0xF0,                            // loop: IN A,(F0h)
        0xCB, 0x4F,                            // BIT 1,A (DRQ)
        0x28, 0x06,                            // JR Z,check
        .. transfer,
        0x23,                                  // INC HL
        0x18, 0xF4,                            // JR loop
        0xCB, 0x47,                            // check: BIT 0,A (busy)
        0x20, 0xF0,                            // JR NZ,loop
        0x18, 0xFE                             // JR $
    ];

    private static byte[] WithData(byte[] program, byte[] data)
    {
        byte[] rom = new byte[DataOffset + data.Length];
        program.CopyTo(rom, 0);
        data.CopyTo(rom, DataOffset);
        return rom;
    }
}
