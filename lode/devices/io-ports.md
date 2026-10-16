# I/O Port Specification

heh8080 I/O port assignments for CP/M compatibility.

## Port Map

| Port | Device | Read | Write |
|------|--------|------|-------|
| 0 | Console | Status (FFh=ready, 00h=not ready) | - |
| 1 | Console | Data (character) | Data (character) |
| 2 | Printer | Status (FFh=ready) | - |
| 3 | Printer | Data (1Ah=EOF) | Data (character) |
| 4 | Auxiliary | Status | EOF flag |
| 5 | Auxiliary | Data | Data |
| 10 | FDC | Current drive | Set drive (0-15) |
| 11 | FDC | Current track | Set track (0-255) |
| 12 | FDC | Sector low byte | Set sector low |
| 13 | FDC | 00h | Command (0=read, 1=write) |
| 14 | FDC | Status | - |
| 15 | DMA | Address low byte | Set address low |
| 16 | DMA | Address high byte | Set address high |
| 17 | FDC | Sector high byte | Set sector high |
| 20 | MMU | Bank count | Initialize banks |
| 21 | MMU | Current bank | Select bank |
| 22 | MMU | Segment size (pages) | Set segment size |
| 23 | MMU | Write protect status | Set write protect |
| 25 | Clock | - | Command |
| 26 | Clock | Data | Data |
| 27 | Timer | Status (1=enabled) | Enable/disable |
| 28 | Delay | 00h | Delay (n × 10ms) |
| 30 | Speed | CPU speed low | Set speed low |
| 31 | Speed | CPU speed high | Set speed high |
| 40-47 | Network | Server socket 1-4 status/data | |
| 50-51 | Network | Client socket status/data | |
| 160 | Hardware | Lock status | Control (see below) |

## Console (Ports 0-1)

**Port 0 - Status (IN)**
- Returns FFh if character available
- Returns 00h if no input ready

**Port 1 - Data (IN/OUT)**
- IN: Read character from keyboard
- OUT: Write character to display

## Printer (Ports 2-3)

**Port 2 - Status (IN)**
- Always returns FFh (ready)

**Port 3 - Data (IN/OUT)**
- IN: Returns 1Ah (CP/M EOF)
- OUT: Write character to printer

## FDC - Floppy Disk Controller (Ports 10-17)

### Registers
- Port 10: Drive select (0-15, A-P)
- Port 11: Track number (0-255)
- Port 12: Sector number low byte
- Port 17: Sector number high byte
- Port 13: Command register
- Port 14: Status register
- Port 15-16: DMA address (low/high)

### Commands (Port 13 OUT)
- 0: Read sector (128 bytes from disk to DMA address)
- 1: Write sector (128 bytes from DMA address to disk)

### Status Codes (Port 14 IN)
| Code | Meaning |
|------|---------|
| 0 | OK |
| 1 | Invalid drive |
| 2 | Invalid track |
| 3 | Invalid sector |
| 4 | Seek error |
| 5 | Read error |
| 6 | Write error |
| 7 | Invalid command |

### Disk Geometry

Standard 8" floppy (drives A-D):
- 77 tracks (0-76)
- 26 sectors per track (1-26)
- 128 bytes per sector
- Total: 256,256 bytes per disk

File offset calculation:
```
offset = (track × sectors_per_track + (sector - 1)) × 128
```

## MMU - Memory Management Unit (Ports 20-23)

**Port 20 - Initialize (OUT)**
- Allocate N memory banks (including bank 0)
- Read returns current bank count

**Port 21 - Bank Select (OUT)**
- Select active bank (0 to N-1)
- Read returns current bank

**Port 22 - Segment Size (OUT)**
- Set segment size in 256-byte pages
- Default: 192 pages (48KB banked, 16KB common)
- Must set before allocating banks

**Port 23 - Write Protect (OUT)**
- Protect/unprotect common memory segment

## Timer (Port 27)

**OUT**
- 1: Enable 10ms interrupt timer
- 0: Disable timer

**IN**
- Returns current timer state

When enabled, generates maskable interrupt every 10ms.

## Hardware Control (Port 160)

Port is locked until magic byte AAh is written.

**Control bits (after unlock):**
| Bit | Function |
|-----|----------|
| 4 | Switch to 8080 mode |
| 5 | Switch to Z80 mode |
| 6 | Reset CPU and MMU, reboot |
| 7 | Halt emulation |

## Unassigned Ports

All unassigned ports return FFh on read and ignore writes.

## CP-500 / Model III overlay ports

The reusable `Cp500Machine` has a separate I/O map from the CP/M devices above,
taken from the standard Model III map (MAME `trs80m3`). Its split `IoBus`
assigns read and write handlers separately; every range has exactly one read
owner and one write owner, and there are no alias ranges.

| Ports | IN | OUT | Owner (IN / OUT) |
|-------|----|-----|------------------|
| C0 | Write protect. Bits 7-4 are drives 0-3. Bit 1 is set with the drive-0 bit when that image is protected. Bit 0 stays 0. Uninstalled, the port reads FFh. | Ignored. | `Cp500HardDiskController` |
| C1 | Control byte last written. | Bit 4 resets the task file. Bits 3 and 2 are stored and do not gate commands. | `Cp500HardDiskController` |
| C2 | Extra data. Clock bytes are read here. | Extra data. Set-time bytes are written here. | `Cp500HardDiskController` |
| C3 | Extra size, low byte. A stored 0 means 256. Transfers stay 256 bytes. | Extra size. | `Cp500HardDiskController` |
| C4 | FFh. No boot ROM is loaded. | Extra command, low nibble. `01h` reads the clock. `02h` sets it. | `Cp500HardDiskController` |
| C5 | Extra error. | Boot-ROM address. Nothing reads it back. | `Cp500HardDiskController` |
| C6-C7 | FFh. | Ignored. | `Cp500HardDiskController` |
| C8 | Next byte of the sector buffer during a read. | Next byte of the sector buffer during a write. | `Cp500HardDiskController` |
| C9 | Error register. ID not found is `10h`. Aborted command is `04h`. | Write precompensation. Stored and otherwise ignored. | `Cp500HardDiskController` |
| CA | Sector count. | Sector count. One command still moves one sector. | `Cp500HardDiskController` |
| CB | Sector number. Sector 0 is valid. | Sector number. | `Cp500HardDiskController` |
| CC | Cylinder low. | Cylinder low. | `Cp500HardDiskController` |
| CD | Cylinder high, bits 1-0. | Cylinder high. Only bits 1-0 are kept. | `Cp500HardDiskController` |
| CE | Size, drive, and head packed as `essddhhh`, with ECC reading 0. | `essddhhh`. The write sets seek complete. | `Cp500HardDiskController` |
| CF | Status. Busy `80h`, ready `40h`, write fault `20h`, seek complete `10h`, DRQ `08h`, corrected `04h`, command in progress `02h`, error `01h`. Busy is clear while DRQ is set. | Command. The high nibble selects it. | `Cp500HardDiskController` |
| E0-E3 | `~(mask & (timer pending or live RS-232 flags))`, active low. The read clears nothing. | IRQ mask. Bit 2 is the 30 Hz timer. Bits 4, 5, and 6 are RS-232 transmit-empty, data-received, and receiver error when the board is installed. Bits 0, 1, and 3 are accepted and never asserted. | `Cp500InterruptDevice` |
| E4-E7 | `~(nmiMask & nmiStatus)`; bit 7 mirrors the live FDC INTRQ line | NMI mask (bit 7 FDC INTRQ) | `Cp500InterruptDevice` |
| E8 | Modem status. 1 is true: d7 CTS, d6 DSR, d5 carrier, d4 ring, d1 receiver line (idle mark is 1). d3, d2, and d0 read 0. Uninstalled, the port reads FFh. | Bit 1 is the UART control-load level. It does not reset the UART. | `Cp500Rs232Device` |
| E9 | FFh. There is no configuration-switch read. | High nibble transmit baud, low nibble receive baud. Indices 0 through 15 are 50, 75, 110, 134.5, 150, 300, 600, 1200, 1800, 2000, 2400, 3600, 4800, 7200, 9600, 19200. | `Cp500Rs232Device` |
| EA | UART status, 1 is true: d7 data received, d6 transmit holding empty, d5 overrun, d4 framing, d3 parity. d2 through d0 read 1. The read clears nothing. | UART control word, and only while E8 bit 1 is 1. Word: d7 even parity, d6/d5 length (11=8, 01=7, 10=6, 00=5), d4 two stop bits, d3 parity inhibit, d2 break when 0, d1 RTS, d0 DTR. Reset value 6Ch. | `Cp500Rs232Device` |
| EB | Assembled byte. Clears data received only. Overrun, framing, and parity change when the next stop bit is judged. | Queues a byte and clears transmit-holding-empty until the start bit begins. | `Cp500Rs232Device` |
| EC-EF | Acknowledges the timer IRQ (clears only bit 2 pending), returns 00h | Video/misc control latch | `Cp500InterruptDevice` / `Cp500VideoDevice` |
| F0-F3 | WD1791 status, track, sector, data | WD1791 command, track, sector, data | `Wd1791Controller` |
| F4-F7 | Toggles the ROM A11 flip-flop, returns 00h | Drive select (bits 0-3), side (4), WAIT (6), MFM (7) | `Cp500SystemRom` / `Cp500DiskControlDevice` |
| F8-FB | Printer status (select, no fault; BUSY when the printer is busy) | Printer data | `Cp500PrinterPort` |
| FF | 00h, no side effects (no cassette attached) | Ignored | `Cp500CassettePort` |

With the RS-232 board uninstalled, E8-EB stay unregistered and read FFh.
With the hard disk uninstalled, C0-CF stay unregistered and read FFh.
CF command nibbles are 1 restore, 2 read, 3 write, 4 verify, 5 format,
6 init, 7 seek, and 9 test. Any other nibble aborts. A write with bit `04h`
set aborts. A size code other than 00 (256 bytes) aborts a sector command.
Format and init succeed and do not write the image. cp500-os reads the clock
by writing `01h` to C4, polling CF until bit 7 clears, then reading six
binary bytes from C2: seconds, minutes, hours, year since 2000, day, month.
Year 12 is 2012. The bytes are not BCD. Command `02h` followed by six bytes
written to C2 sets the clock.
cp500-os must use the four ports as distinct registers, set E8 bit 1 before
an EA control write, put transmit baud in the high E9 nibble and receive baud
in the low nibble, treat EA as active high, enable E0 bits 4-6, and not read
switches from E9. The F4-F7 bit 7 write
selects MFM (set) or FM (clear) density for the WD1791.

```csharp
machine.IoBus.Out(0xE0, 0x04); // enable the 30 Hz timer IRQ
machine.Interrupts.AdvanceCycles(machine.Interrupts.TimerPeriodCycles);
_ = machine.IoBus.In(0xE0);    // FBh: timer pending (active low)
_ = machine.IoBus.In(0xEC);    // acknowledges the timer; IN E0 now reads FFh
machine.IoBus.Out(0xF4, 0x41); // drive 0 + WAIT request
_ = machine.IoBus.In(0xF4);    // toggles the ROM A11 flip-flop
```

Contracts: F4-F7 reads never change disk selection, F4-F7 writes never change
the ROM view, FDC INTRQ is routed through `Cp500InterruptDevice`, and cassette
interrupt/audio transport remains intentionally inactive. The ROM's IM 1
handler reads E0 to find the source, then reads EC to acknowledge the timer.
