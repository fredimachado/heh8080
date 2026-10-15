# CP500 (TRS-80 Model III) Emulation Plan

Goal: add a **Prológica CP-500** machine alongside the existing CP/M machine.
The CP-500 is a Brazilian TRS-80 Model III clone, so the target is Model III
hardware compatibility with CP-500-specific keyboard/ROM details layered on top.

Status: **desktop CP-500 is complete for ROM bring-up, keyboard, video, timed
WD1791 with WAIT, JV3/DMK/raw disk images and desktop disk mounting with
write-back**. Open work: browser disk persistence, a bootable DOS acceptance
run (TRS-DOS 1.3 / DOS-500 images are not in the repo) and cassette (out of
scope). The compatibility sequence is in
[cp500-compatibility-remediation.md](cp500-compatibility-remediation.md).

## Why this was feasible

- `CpuZ80` is complete (ZEXALL-validated); the CP500 CPU (Z80 @ 2.03 MHz) needed
  no new opcodes, only a WAIT input, a level interrupt line and NMI.
- `IoBus` is a plain 256-port bus with no CP/M assumptions in Core.
- All CP/M-isms live in `Heh8080.Devices` port handlers and in `MainViewModel`
  wiring, so a parallel machine coexists without touching LOLOS support.

See [../core/z80.md](../core/z80.md), [../devices/devices.md](../devices/devices.md).

## Hardware modelled (Model III / CP-500)

| Component | Model III spec | Status |
|-----------|----------------|--------|
| CPU | Z80 @ 2.03 MHz | `CpuZ80`, paced by `CycleThrottle` |
| ROM | 16 KB socket at `0000-3FFF` | User-supplied dump, never bundled; keyboard/video overlay it |
| RAM | 16/32/48 KB at `4000+` | `Cp500MachineProfile`; desktop uses 48 KiB |
| Video RAM | `3C00-3FFF`, 64x16 chars | `Cp500VideoDevice`, `ECh` mode latch |
| Keyboard | Matrix at `3800-3BFF` | 8 active-high rows; `Cp500KeyMap`, `Cp500HostKeyboard` |
| FDC | WD1791 at `F0-F3`; control at `F4-F7` | Timed, with WAIT; 0-4 installed drives |
| Interrupts | IM 1 timer at 30 Hz; IRQ `E0-E3`, NMI `E4-E7`, ack `IN EC-EF` | `Cp500InterruptDevice` |
| Printer | Ports `F8-FB` | `Cp500PrinterPort` to `IPrinterDevice` |
| Cassette | 500/1500 baud, port `FF` | Stub: reads 00h; transport out of scope |
| RS-232, RTC | `E8-EB`, `4040-4042` | Not modelled; the stock ROM runs without them |

Prologica revisions (CP-500/M80 with 64 KiB RAM and a ROM overlay, M80C without
cassette, Turbo at 4 MHz) are not modelled. Turbo would be a clock parameter;
M80 would need a switchable ROM region (`IRemappableMemoryRegion` exists).

## Verified ROM dump (in repo `cp500/`, git-ignored, never commit)

- `cp500.rom` - 16 KB system ROM (EPROM 27128), mapped at `0000-3FFF`, CRC32
  `c2fc1b92`.
  - Reset: `F3 AF C3 15 30` (DI, XOR A, JP 3015h); RST vectors jump to RAM hooks `4000h+`.
  - Contains "PROLOGICA 1981 BASIC", "Mem. usada", "Diskette?", "Cass?", the
    BASIC keyword and error tables, and a built-in monitor ("MONITOR VERSAO 1.1
    1982") hidden behind the A11 flip-flop, useful for bring-up debugging.
  - Boot flow with and without disks: [../cp500/summary.md](../cp500/summary.md).
- `cp500-chargen.rom` - 2 KB character generator (256 glyphs, 8x8), required
  next to `cp500.rom`. The desktop rasterizes CP-500 text from it
  (`Cp500CharacterGenerator`, `Cp500Raster`).

## Phases

### Phase 1 - Machine abstraction - done
- `EmulatedMachine { Cpm, Cp500 }` in `MainViewModel`; CP/M wiring unchanged.

### Phase 2 - CP500 core machine - done
- `MappedMemory`, `Cp500SystemRom`, RAM, video and keyboard regions.
- `Cp500Machine` composes the Z80, ROM, RAM, IM 1 and the CP-500 devices.
- `CycleThrottle` returns an asynchronous host delay; it never blocks UI/WASM.

### Phase 3 - Video display - done
- `Cp500VideoDevice` exposes the 64x16 plane; `Cp500Raster` draws it with the
  real character generator into a 512x192 frame, and `RetroTerminalControl`
  shows it with the existing CRT shader and housing. 32-column mode and the
  alternate character set are supported.

### Phase 4 - Keyboard - done
- Held-key matrix input with SHIFT handling; focus loss releases all keys.
- Optional: ABNT-specific layout.

### Phase 5 - Interrupts and misc ports - done
- Cycle-driven 30 Hz timer, IRQ level and NMI edge, printer, cassette stub.
- The RTC and RS-232 stubs were removed as unused by the ROM.

### Phase 6 - Floppy (WD1791) - done
- Timed controller with CPU WAIT, native JV3/DMK/raw images with CRC and
  deleted-data metadata, installed-drive behavior (TR00 without media).
- Open: boot a real TRS-DOS 1.3 / DOS-500 image to `DOS READY`.

### Phase 7 - Disk host and polish - desktop done
- Desktop: mount/unmount drives 0-3 in `ConfigDialog`, write-back in the source
  format, `Cp500DriveBay` policy tested at view-model level
  ([../cp500/desktop-integration.md](../cp500/desktop-integration.md)).
- Open: browser persistence (IndexedDB), performance pass, WASM validation.
- Not planned: cassette transport, audio, motor control.

## Testing strategy

- Unit tests per device in `tests/Heh8080.Tests` (`Cp500*`, `Wd1791*`, `ModelIII*`).
- Real-ROM tests (`[RealRomFact]`, skipped without `cp500/cp500.rom`) drive the
  actual firmware through `RealRomSession`: empty-drive `Diskette?`, boot sector
  execution, BASIC without drives, BASIC with empty drives via
  `ResetHoldingBreak`, a key-echo sweep, and the boot banner rasterized with
  the real character ROM (`[RealCharacterGeneratorFact]`, needs both ROMs).
- A view-model test with a tiny ROM covers mount, pause/resume and write-back.
- Keep the CP/M tests green.
- Open: real-software acceptance (TRS-DOS 1.3, one game).

## Current product choices

1. Desktop targets the original 1982 48 KiB CP-500 at 2.03 MHz with two drives.
2. CP/M remains the default and can be selected again at runtime.
3. ROMs remain user supplied; `cp500.rom` and `cp500-chargen.rom` are
   loaded from `cp500/` and both are required.
4. Cassette is out of scope; disk and ROM bring-up has priority.

## Legal note

ROM images and DOS disks are copyrighted; the app loads user-supplied files
only and never embeds them in resources.

## Related lodes

- [emulator-implementation.md](emulator-implementation.md) - original 8-phase CP/M plan (complete)
- [../core/z80.md](../core/z80.md), [../devices/io-ports.md](../devices/io-ports.md)
- [../ui/desktop-app.md](../ui/desktop-app.md)
