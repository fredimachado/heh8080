# Lode Map

## Root
- [summary.md](summary.md) - Project overview and status
- [terminology.md](terminology.md) - Domain vocabulary
- [practices.md](practices.md) - Patterns and conventions

## Plans
- [plans/emulator-implementation.md](plans/emulator-implementation.md) - CP/M architecture and validation scope
- [plans/phase5-terminal.md](plans/phase5-terminal.md) - ADM-3A terminal with CRT effects
- [plans/cp500-emulation.md](plans/cp500-emulation.md) - CP-500 status by phase and open follow-ups
- [plans/cp500-compatibility-remediation.md](plans/cp500-compatibility-remediation.md) - Model III compatibility steps (done) and follow-ups
- [plans/cp500-expansions.md](plans/cp500-expansions.md) - Headless host `heh8080-cp500` is implemented. RS-232, virtual hard disk, and network card are still planned

## Core
- [core/cpu.md](core/cpu.md) - 8080 CPU implementation details
- [core/z80.md](core/z80.md) - Z80 CPU implementation details

## Devices
- [devices/io-ports.md](devices/io-ports.md) - I/O port specification
- [devices/devices.md](devices/devices.md) - Device layer implementation

## CP-500
- [cp500/summary.md](cp500/summary.md) - Reusable CP-500 machine, ROM boot flow, BREAK-held reset, and the `heh8080-cp500` host
- [cp500/memory-map.md](cp500/memory-map.md) - Mapped-ROM/RAM/VRAM/keyboard contract
- [cp500/devices.md](cp500/devices.md) - CP-500 peripherals, WD1791, disk images, drive bay and keyboard contracts
- [cp500/desktop-integration.md](cp500/desktop-integration.md) - Avalonia machine selection, display, input, disk mounting, character-generator rendering and execution host

## Terminal
- [terminal/terminal.md](terminal/terminal.md) - ADM-3A terminal emulator

## External
- `tests/cpu_tests/README.md` - Third-party CPU test integration

## UI
- [ui/desktop-app.md](ui/desktop-app.md) - Desktop application architecture
- [ui/browser.md](ui/browser.md) - Browser WASM platform

## MCP
- [mcp/mcp-server.md](mcp/mcp-server.md) - CP/M MCP server on Desktop `--mcp`, and the separate `heh8080-cp500 --mcp` tools
