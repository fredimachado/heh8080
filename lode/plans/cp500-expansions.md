# CP-500 Expansion Hardware Plan

Status: the headless host in section 0 is implemented. RS-232, the virtual
hard disk, and the network card are still planned.

This plan drives the hardware side of
[cp500-os](https://github.com/fredimachado/cp500-os) (design in its
`docs/design.md`, section 9), but every device models real hardware so other
TRS-80 software can use it too.

Port numbers and register layouts below come from memory of the Model III
technical reference, FreHD and the WIZnet W5100 datasheet. Verify each against
MAME `trs80m3` and the datasheets before implementing.

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

Model III RS-232: TR1602 UART and BR1941 baud generator.

| Port | IN | OUT |
|------|----|-----|
| `E8h` | Modem status (CTS, DSR, CD, RI) | Master reset |
| `E9h` | Configuration switches | Baud rate (receive nibble, transmit nibble) |
| `EAh` | UART status (data ready, TX empty, overrun, framing, parity) | UART and modem control (word length, parity, stop bits, DTR, RTS, break) |
| `EBh` | Receive data | Transmit data |

Interrupts in `Cp500InterruptDevice`: `E0h` bits 4 (transmit empty),
5 (receive ready), 6 (error). Today those mask bits are accepted and never
assert; the UART becomes their source.

Timing: bytes move at the programmed baud rate in emulated cycles, so software
that depends on it (terminal programs, the OS driver) sees realistic overruns.

Host side, `ISerialEndpoint` with one implementation per mode:

- **TCP client or listener**: raw bytes to a socket, for null-modem links
  between emulators or to a host program.
- **Host COM port**: real hardware.
- **Modem bridge**: a Hayes-compatible "Wi-Fi modem", the device retro users
  actually plug in today. Command mode understands `AT`, `ATZ`, `ATE0/1`,
  `ATDT host:port` (opens TCP, answers `CONNECT`), `+++` with guard time,
  `ATH`, `ATO`, and `ATS0=1` to accept incoming connections. Result codes
  `OK`, `CONNECT`, `NO CARRIER`, `ERROR`. Carrier detect follows the TCP
  connection.

## 2. Virtual hard disk

Model the Radio Shack hard disk interface (WD1010 controller), which is what
FreHD implements on real Model III machines with an SD card. Software written
for it (LDOS, NEWDOS hard disk drivers, cp500-os) then works in the emulator
and on real hardware.

| Port | Function |
|------|----------|
| `C0h`-`C1h` | Interface control and write protect status |
| `C8h` | Data |
| `C9h` | Error (IN) / write precompensation (OUT) |
| `CAh` | Sector count |
| `CBh` | Sector number |
| `CCh`-`CDh` | Cylinder low / high |
| `CEh` | Size/drive/head |
| `CFh` | Status (IN) / command (OUT) |

- 256-byte sectors, commands restore, seek, read sector, write sector, format
  track. Transfers are programmed IO with DRQ, like the WD1791 path.
- Image: raw sectors plus a geometry (cylinders, heads, sectors per track)
  stored in a small header. Import and export of the `.hdv` format used by
  xtrs, sdltrs and FreHD, so existing images work.
- `Cp500DriveBay` grows a hard disk slot with the same insert, eject and
  write-back policy as floppies.
- Clock: FreHD also exposes a battery-backed real-time clock on extra
  registers in this range. Model it, backed by an `IClock` host interface
  (tests use a fixed clock). Confirm the exact registers from the FreHD
  documentation.

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
2. RS-232 board with the TCP and modem bridge endpoints.
3. Virtual hard disk with clock.
4. Network card.

Each remaining hardware step: device unit tests against the register contract, a machine-level
test with a tiny ROM program, then a ConfigDialog option and a desktop smoke
test.

Related: [cp500-emulation.md](cp500-emulation.md),
[../cp500/devices.md](../cp500/devices.md),
[../devices/io-ports.md](../devices/io-ports.md).
