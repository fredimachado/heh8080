# CP-500 Mapped Memory

`MappedMemory` in Core dispatches the first matching `IMemoryRegion`, so region
order is hardware priority. `Cp500Machine` puts keyboard, video, and RTC ahead
of ROM and RAM, with an `UnmappedMemoryRegion` as its final deterministic
open-bus fallback. This is separate from the legacy banked `Memory` class,
which remains the CP/M-compatible implementation.

```csharp
var memory = new MappedMemory(
    keyboardRegion, // 3800h-3BFFh, active-low selected rows
    video.VideoRam, // 3C00h-3FFFh, events only on changed character cells
    rtc,            // 4040h-4042h, BCD seconds/minutes/hours
    rom,            // 0000h-3FFFh when enabled; writes are ignored
    ram,            // 4000h+ or full 64 KiB M80 underlay
    new UnmappedMemoryRegion(0, 65536));
```

```mermaid
graph LR
    CPU[CpuZ80] --> Map[MappedMemory]
    Map -->|first match| KBD[3800-3BFF Keyboard]
    Map -->|first match| VRAM[3C00-3FFF VRAM]
    Map -->|first match| RTC[4040-4042 RTC]
    Map -->|enabled| ROM[0000-3FFF ROM]
    Map -->|fallback| RAM[RAM]
    Map -->|otherwise| OpenBus[FFh unmapped]
```

## Contracts and invariants

- All regions fit one non-wrapping 64 KiB address range. `MappedMemory` returns
  `FFh` if no region responds, even when a caller omits a fallback region.
- `RomMemoryRegion` makes a defensive copy and ignores writes. Its `Enabled`
  switch exposes lower-priority RAM only for a 64 KiB M80/M80C profile.
- `VideoRamRegion.Changed` fires after a value actually changes, avoiding
  redundant host redraws.
- `KeyboardMatrix` is 8-by-8 and active low. Address low-byte bits select
  rows; released switches read as 1 and pressed switches clear their column
  bit. Selected rows are combined with bitwise OR; an idle or zero-row scan
  reads `FFh` for the CP-500's open-bus behavior.
  behavior and lets multi-row selection report every asserted key bit.
- Open bus writes are discarded and reads are predictably `FFh` by default.

## Rationale and lessons

The 16 KiB ROM socket overlaps the hardware keyboard/video addresses; priority
routing models that overlay directly. This supersedes the earlier planning
assumption of a fixed 12 KiB ROM range: the copied CP-500 ROM image is 16 KiB,
while the keyboard and VRAM still win at their physical addresses.

Related: [summary.md](summary.md), [devices.md](devices.md),
[../core/z80.md](../core/z80.md).
