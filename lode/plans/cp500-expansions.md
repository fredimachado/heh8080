# CP-500 Expansion Hardware Plan

Status: the headless host in section 0, the RS-232 board in section 1, and
the virtual hard disk in section 2 are implemented. The network card is still
planned.

This plan drives the hardware side of
[cp500-os](https://github.com/fredimachado/cp500-os) (design in its
`docs/design.md`, section 9), but every device models real hardware so other
TRS-80 software can use it too.

Section 2 was checked against FreHD firmware, xtrs, sdltrs, and the WD1001
manual. MAME `trs80m3.cpp` documents `C0h`-`CFh` and does not decode them, so
it is not the source for this board. Section 3 is still from memory of the
W5100 datasheet. Verify it before implementing.

## Principles

- Each expansion is optional, off by default, and selected in
  `Cp500MachineProfile` (a set of expansions next to RAM size and drive
  count). The stock machine keeps behaving exactly as today: unused ports
  read as open bus `FFh`.
- Devices live in `Heh8080.Devices` with no host IO. Host access (sockets,
  files, clock) goes through small interfaces the Desktop host implements, so
  tests use fakes and stay deterministic.
- The Browser platform cannot open raw sockets; serial-over-TCP and the
  network card are desktop-only until a WebSocket relay exists.

## 0. Headless runner (prerequisite for OS development)

Implemented. The executable is `heh8080-cp500` (`src/Heh8080.Cp500`). It is a
separate program, not a flag on `Heh8080.Desktop`. Desktop `--mcp` is still
the CP/M server.

```text
heh8080-cp500 --rom stub.rom --disk0 build/boot.dsk --type "ls /bin\r" --until-screen "ok" --timeout 30s --dump-screen
```

`--rom` is required. `--disk0`, `--disk1`, `--disk2`, and `--disk3` are
optional image paths. RAM stays 48 KiB. The clock stays 2.03 MHz. There is no
profile flag.

Installed drives start at 2, the count on `Cp500MachineProfile.Original`. A
drive index outside 0 through 3 is refused. No disks, or only `--disk0` and
`--disk1`, keeps 2 installed drives. `--disk2` installs 3. `--disk3` installs
4. The controller can select every drive in that count.

`--timeout 30s` is 30 emulated seconds at 2.03 MHz, which is 60,900,000
cycles. The unit suffix is required, `s` or `ms`. The budget starts when
typing finishes. It is not wall-clock time. The host never sleeps on
`LastSuggestedDelay`.

`--until-screen` is a case-sensitive substring of the screen text
(`StringComparison.Ordinal`). The check reads all 64 by 16 video cells, and
32-column mode does not change that. An empty pattern is refused.
`--until-screen` and `--timeout` are accepted only together.

`--type` sends TRS-80 keystrokes. SHIFT is held only for a stroke whose mode
is `Cp500ShiftMode.Shifted`. A CR, or the two characters `\` and `r`, is
ENTER. `\`, `]`, `^`, `_`, and any other character the key map refuses are
rejected before the first key goes down.

`Cp500Screen.ToText` is the screen codec. Bytes `0x20` through `0x7E` stay.
Every other byte becomes a space. The host trims the end of each row, joins
the rows with `\n`, and writes no extra trailing newline.

`--dump-screen` writes that text to stdout when the run stops, on exit 0 and
on exit 1. The bytes are raw UTF-8, so a Windows console does not rewrite the
line endings. Stderr is refusals only. A timeout does not print a message.

The process uses these exit codes.

- 0 when the until-screen text appeared, or when the batch had no until-screen and finished.
- 1 when the until-screen text did not appear within the emulated budget.
- 2 when the run was refused. A refusal is a bad flag, a missing file, a ROM that is empty or longer than 16 KiB, a bad disk image, an unmapped character, or a flush failure.

Disks are written back when the process disposes the session, after the dump
is written. A flush failure exits 2 even when the screen already matched.

`--mcp` starts the stdio server instead of a batch. It takes `--rom` and the
same disk flags. Combining it with `--type`, `--until-screen`, `--timeout`,
or `--dump-screen` exits 2.

The MCP tools in that process are `Boot`, `Type`, `ReadScreen`,
`PeekMemory`, `PokeMemory`, and `Step`. The wire names are those PascalCase
method names. There is no `WaitForText`. A refusal throws.

cp500-os needs the executable name `heh8080-cp500`, the flags above, an
emulated timeout with `s` or `ms`, exit codes 0, 1, and 2, the screen codec
above, and the rule that drives 2 and 3 raise the installed count. It does
not need `cp500.rom`.

## 1. RS-232 board

Implemented. Verified against MAME `trs80m3_m.cpp` and `ay31015.cpp`.
`Cp500Rs232Device` owns `E8h`, `E9h`, `EAh`, and `EBh` as four registers, not
one mirrored block. `Rs232Installed` defaults to false, so a stock machine
still reads `FFh` there. `Cp500Machine.Step` clocks the board with the
instruction's cycle count. Sockets and COM ports are opened only by
`Heh8080.Rs232`.

| Port | IN | OUT |
|------|----|-----|
| `E8h` | d7 CTS, d6 DSR, d5 carrier, d4 ring, d1 receiver line. Idle mark is 1. d3, d2, and d0 read 0. 1 means the signal is true. | Stores bit 1 as the control-load level. This write does not reset the UART. |
| `E9h` | `FFh`. There is no configuration-switch read. | High nibble selects transmit baud. Low nibble selects receive baud. |
| `EAh` | d7 data received, d6 transmit holding empty, d5 overrun, d4 framing, d3 parity. d2 through d0 read 1. The read clears nothing. | Loads the UART control word only while E8 bit 1 is 1. Otherwise the write changes nothing. |
| `EBh` | Returns the assembled byte and clears data-received only. | Queues a byte and clears transmit-holding-empty until the start bit begins. |

Baud indices 0 through 15 are 50, 75, 110, 134.5, 150, 300, 600, 1200, 1800,
2000, 2400, 3600, 4800, 7200, 9600, and 19200. The bit clock is 16 samples
per bit. Reset leaves both nibbles at 0, so an unprogrammed board runs at
50 baud, and loads control word `6Ch`: 8 bits, parity inhibited, one stop
bit, break off, RTS clear, DTR clear.

The control word is d7 even parity, d6 and d5 word length (`11` is 8, `01`
is 7, `10` is 6, `00` is 5), d4 two stop bits, d3 parity inhibit, d2 break
when 0, d1 RTS, d0 DTR. RTS and DTR stay in that word. They do not change `E8h`.

Overrun, framing, and parity change when the next stop bit is judged.
Overrun means data-received was still set. The old byte stays. Framing means
the stop bit was low.

`E0h` bit 4 is transmit-empty, bit 5 is data-received, and bit 6 is a
receiver error (framing, parity, or overrun). `Cp500InterruptDevice` samples
those levels into `~(mask & (timer pending | RS-232 flags))`. It does not
store a second copy. An `E0h` read leaves a pending 30 Hz timer pending.
`ECh`-`EFh` still clears only the timer bit.

Both `heh8080-cp500` programs take `--rs232 off`, `--rs232 tcp:<host>:<port>`,
`--rs232 listen:<port>`, `--rs232 hayes:<port>`, or `--rs232 com:<name>`.
Omitting the flag is `off`. The FJM-3A configuration dialog has the same
choices. Hayes, in `Heh8080.Rs232`, answers `AT`, `ATZ`, `ATE0`, `ATE1`,
`ATDT host:port`, `ATH`, `ATO`, `ATS0=0` through `255`, and `ATA`. Result
codes are `OK`, `CONNECT`, `NO CARRIER`, and `ERROR`. Carrier follows the
TCP connection. The `+++` guard is one emulated second, counted in
`AdvanceCycles`, not wall time. `ATDT` starts the connect and queues
`CONNECT` or `NO CARRIER` when the socket finishes. It does not block `Step`.

cp500-os has to program this map, not the earlier draft:

- Treat `E8h`, `E9h`, `EAh`, and `EBh` as distinct ports.
- Set `E8h` bit 1 before a control write to `EAh`. That bit is not a master reset.
- Program transmit baud in the high nibble of `E9h` and receive baud in the low nibble.
- Treat `EAh` status as active high, including the forced 1s in bits 2 through 0. The read clears nothing.
- Enable `E0h` bits 4, 5, and 6 for transmit-empty, data-received, and receiver error.
- Do not read configuration switches from `E9h`. A read is `FFh`.

## 2. Virtual hard disk

Implemented. Verified against FreHD firmware, xtrs, sdltrs, and the WD1001
OEM manual (Feb 1983). MAME `trs80m3.cpp` documents `C0h`-`CFh` and does not
decode them, so it is not the source for this board. The board is the FreHD
Model III hard disk. It presents a WD1010 task file at `C8h`-`CFh` and its
own extra registers at `C0h`-`C7h`.

With the expansion off, `C0h`-`CFh` stay unregistered and read `FFh`.

While a command is busy, every read returns the status byte and every write
is ignored. That is FreHD `handle_busy`, and it matches WD1001 section 5.5.7.

| Port | IN | OUT |
|------|----|-----|
| `C0h` | Write protect. Bits 7-4 are drives 0-3. Bit 1 is set when any present drive is protected. Bit 0 is defined as interrupt and stays 0. | Ignored. |
| `C1h` | Control byte last written. | Bit 4 software reset, bit 3 device enable, bit 2 wait enable. |
| `C2h` | Extra data. The clock bytes are read here. | Extra data. The set-time bytes are written here. |
| `C3h` | Extra size, low byte. A stored 0 means 256. | Extra size. |
| `C4h` | One byte from the boot ROM stream. | Extra command. The low nibble runs. `01h` reads the clock. `02h` sets it. |
| `C5h` | Extra error. | Boot-ROM address. |
| `C6h`-`C7h` | `FFh`. UART stubs, not implemented. | Ignored. |
| `C8h` | Next byte of the sector buffer. | Next byte of the sector buffer. Accepted only during a write command. |
| `C9h` | Error register. | Write precompensation. Stored and otherwise ignored. |
| `CAh` | Sector count. | Sector count. |
| `CBh` | Sector number. | Sector number. |
| `CCh` | Cylinder low. | Cylinder low. |
| `CDh` | Cylinder high, bits 1-0. The upper bits read 0. | Cylinder high. Only bits 1-0 are kept. |
| `CEh` | Size, drive, and head, packed back into the SDH shape. | `essddhhh`. Bit 7 ECC, ignored. Bits 6-5 size. Bits 4-3 drive. Bits 2-0 head. The write sets ready and seek complete. |
| `CFh` | Status. | Command. Only the high nibble selects the command. |

Size codes in `CEh` bits 6-5 are `00` = 256, `01` = 512, `10` = 1024, `11` = 128. cp500-os and TRS-DOS use `00`. This phase transfers 256-byte sectors. Any other size code aborts the command.

One hard-disk slot is drive 0. A command that selects drive 1, 2, or 3 behaves as a missing disk. The slot does not change `InstalledDrives` or the WD1791.

Command high nibbles:

| Nibble | Command | This phase |
|--------|---------|------------|
| `1` | Restore | Clears the cylinder. The step-rate field is ignored. |
| `2` | Read sector | Loads one 256-byte sector into the buffer. |
| `3` | Write sector | Accepts one 256-byte sector. The multi-sector bit (`04h`) aborts. |
| `4` | Verify | Succeeds when the sector exists. |
| `5` | Format track | No-op success, matching FreHD. It does not write the track. |
| `6` | Init | No-op success, matching FreHD. |
| `7` | Seek | Sets the cylinder from the task file. |
| `9` | Test | Clears the error register and leaves ready set. FreHD only. |

Status bits are busy `80h`, ready `40h`, write fault `20h`, seek complete `10h`, data request `08h`, corrected `04h`, command in progress `02h`, error `01h`. Error bits used here are uncorrectable `40h`, ID not found `10h`, aborted command `04h`.

DRQ means the whole sector buffer is ready. It clears on the access that takes or stores the last byte. Software polls busy and then moves 256 bytes. `AdvanceCycles` can hold busy for a programmed number of CPU cycles. The machine default is immediate, the same way `Wd1791Timing` leaves the command delay at 0. A test can set a non-zero delay.

A missing image leaves status at ready (`40h`) after reset. A sector command then sets ID not found (`10h`). A read-only image sets the drive-0 bit in `C0h` and refuses a write with aborted command (`04h`). The image is not modified. Read-only is the host flag or header byte 7 bit 7.

The in-memory image is a geometry plus 256-byte sectors. On disk, `.hdv` is a 256-byte header and then the raw sectors. Magic is `56h CBh`. Byte 2 is the version (`10h` or `11h`). Byte 7 bit 7 is write protect. Byte 29 is sectors per cylinder, and 0 means 256. There is no separate sector-size field.

Import accepts the two layouts that real images use:

- Heads byte 26 is 0. Classic xtrs. Sectors per track are 32. Heads are byte 29 divided by 32. Cylinders are byte 28, and 0 means 256. Byte 27 is ignored.
- Heads byte 26 is 1 through 8. FreHD. Cylinders are `(byte 27 & 3) << 8 | byte 28`. Sectors per track are byte 29 divided by the head count.

Export writes the FreHD layout, which is what the firmware on a real machine reads. A truncated body, a bad magic, a version other than `10h` or `11h`, or a geometry that does not divide is rejected and the slot is left untouched. A raw image is the sector body only. The caller passes the geometry. Write-back uses the format the image was loaded from.

The sector at cylinder 0, head 0, sector 0 is file offset 256. That sector is cp500-os block 0, the partition table. A tool that writes the table at file offset 0 is not an `.hdv` image.

The clock is not a WD1010 register. The guest writes `01h` to `C4h`, polls `CFh` until bit 7 clears, then reads six binary bytes from `C2h`. The order is seconds, minutes, hours, year since 2000, day, month. Command `02h` followed by six bytes written to `C2h` sets the clock. `IClock` supplies the time. The desktop and the runner use the host wall clock. Tests pass a fixed clock. The value is not BCD and it is not seconds since 1980.

cp500-os keeps 256-byte blocks and the partition table in block 0. Its clock reader has to issue this sequence and convert the six bytes into seconds since 1980-01-01. Year 12 means 2012.

`Cp500DriveBay` grows one hard-disk slot with the same insert, eject, and write-back rule as a floppy. Parse first. A bad image leaves the slot alone. A modified writable image is written on eject, replace, and shutdown. A read-only image is never written. A failed save leaves the image mounted.

## 3. Network card

Model a WIZnet W5100 in indirect bus mode, the TCP/IP offload chip used by
real 8-bit network cards (Uthernet II for the Apple II, RC2014 and MSX
cards). The Z80 sees sockets; TCP, UDP and retransmission happen in the chip,
which here means the host.

| Offset from base | Register |
|------------------|----------|
| 0 | Mode (reset, auto-increment of the address on data access) |
| 1 | Address high |
| 2 | Address low |
| 3 | Data |

- Default base `50h`, configurable; check it against every port the CP-500
  map and the other expansions use.
- Model the common registers (MAC, IP, gateway, subnet), 4 sockets with TCP
  and UDP modes and the 8 KiB transmit and receive buffers, mapped onto .NET
  sockets through an `INetworkStack` interface. Raw IP and MAC modes are not
  needed.
- The chip has no DNS; the OS resolves names over UDP itself.
- Interrupt output: the real cards wire it to the bus interrupt line. Decide
  between polling only and routing it to `E0h` bit 3 (I/O bus) when
  implementing.

## Order

1. Headless runner and MCP tools. Done. See section 0.
2. RS-232 board with TCP, Hayes, and COM. Done. See section 1.
3. Virtual hard disk with clock. Done. See section 2.
4. Network card.

Each remaining hardware step: device unit tests against the register contract, a machine-level
test with a tiny ROM program, then a ConfigDialog option and a desktop smoke
test.

Related: [cp500-emulation.md](cp500-emulation.md),
[../cp500/devices.md](../cp500/devices.md),
[../devices/io-ports.md](../devices/io-ports.md).
