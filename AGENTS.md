# AGENTS.md

Notes for coding agents working on heh8080. The README is for users and is written in Portuguese (pt-BR). This file and everything under `lode/` stay in English.

## What this is

A cross-platform Intel 8080 / Zilog Z80 emulator in .NET 10 and Avalonia 11. It runs two machines:

- **CP/M**: boots LOLOS (a CP/M 2.2 compatible OS, bundled as `src/Heh8080.Desktop/Assets/disks/lolos.dsk`).
- **Prológica CP-500**: a Brazilian TRS-80 Model III compatible, desktop only, booted from user-supplied ROM dumps.

## Read the lode first

`lode/` is the project knowledge base. Start with [lode/lode-map.md](lode/lode-map.md), then read the page for the area you touch:

- CPU: `lode/core/`
- CP/M devices and ports: `lode/devices/`
- CP-500 machine, memory map, devices, desktop integration: `lode/cp500/`
- Headless runner contract: [lode/cp500/headless-runner.md](lode/cp500/headless-runner.md)
- MCP servers: [lode/mcp/mcp-server.md](lode/mcp/mcp-server.md)
- Conventions: [lode/practices.md](lode/practices.md)

When you change behavior a lode page describes, update that page in the same change.

## Project layout

```
src/
  Heh8080.Core/        # Cpu8080, CpuZ80, memory, I/O bus
  Heh8080.Devices/     # FDC, console, MMU, printer, auxiliary, timer
  Heh8080.Terminal/    # FJM-3A (ADM-3A) terminal emulator
  Heh8080.App/         # Shared UI library: ViewModels, Views, Controls
  Heh8080.Mcp/         # MCP servers (CP/M and CP-500)
  Heh8080.Cp500Runner/ # heh8080-cp500 headless CP-500 runner
  Heh8080.Desktop/     # Desktop entry point (NativeAOT)
  Heh8080.Browser/     # Browser entry point (Avalonia WASM)
tests/
  Heh8080.Tests/       # Unit and integration tests
  Heh8080.Mcp.Tests/   # MCP server tests
  cpu_tests/           # External CPU test suites (see its README)
```

## Build and test

```bash
dotnet build heh8080.sln
dotnet test heh8080.sln
```

- CPU exerciser tests (TST8080, ZEXDOC, ZEXALL, ...) need `HEH8080_CPU_TESTS` pointing at the test files, and skip without it. ZEXALL/ZEXDOC take hours; don't run them casually.
- Publish builds:

```bash
dotnet publish src/Heh8080.Desktop -c Release
dotnet publish src/Heh8080.Browser -c Release
dotnet publish src/Heh8080.Cp500Runner -c Release
```

## Rules that bite

- **NativeAOT everywhere.** No reflection-based patterns, no APIs marked `RequiresUnreferencedCode`, compiled Avalonia bindings. Run `dotnet publish -c Release` on the desktop or runner project to catch trim warnings.
- **Libraries multi-target** `net10.0;net10.0-browser` (Core, Devices, Terminal, App). Don't use desktop-only APIs there.
- **Determinism.** CP-500 emulated time counts Z80 cycles only. Don't introduce wall-clock dependencies into the machine core; the headless runner relies on identical results across runs.
- **Never commit ROMs.** `cp500/*.rom` is gitignored. Tests that need a ROM must skip, or use a stub ROM, when it is absent.
- **MIT-compatible dependencies only.**

## CP-500 ROMs

The desktop app and the runner need `cp500.rom` (16,384 bytes) and `cp500-chargen.rom` (2,048 bytes). Lookup order: `cp500/` next to the executable, `cp500/` in the current directory, then `%LOCALAPPDATA%\heh8080\`. If the user has dumps, they are usually in `cp500/` at the repo root.

## Headless runner (`heh8080-cp500`)

Use this to check CP-500 changes end to end without a window. It boots a ROM and up to four disks, runs the steps in order as fast as the host allows, and exits with a code you can check.

```bash
dotnet run --project src/Heh8080.Cp500Runner -- --rom cp500/cp500.rom \
  --until-screen "BASIC (S ou N)" --type 'S\r\r' --until-screen READY \
  --type 'PRINT 2+3\r' --until-screen " 5" --dump-screen
```

- Steps: `--until-screen <text>` waits for text on the screen, `--type <text>` types it (`\r` is Enter; `{BREAK}`, `{CLEAR}` and the arrows are named in braces), `--run <duration>` runs for a while (`500ms`, `30s`, `2m`).
- `--disk0` to `--disk3` mount `.dsk`, `.jv3` or `.dmk` images; `--write-back` saves modified disks at the end. `--timeout` sets how long each wait may take (30 s emulated by default).
- `--dump-screen` prints the screen as text (Portuguese letters, block graphics and katakana as Unicode). `--dump-memory 4300:256` prints a hex dump.
- Exit codes: 0 every step succeeded, 1 a wait timed out (screen printed to stderr), 2 usage error or bad file.
- `--mcp` runs it as an MCP server instead (tools `Boot`, `Type`, `ReadScreen`, `PeekMemory`, `PokeMemory`, `Step`).

Full contract: [lode/cp500/headless-runner.md](lode/cp500/headless-runner.md).

## Desktop app flags

- `--cpm` starts in CP/M even if CP-500 ROMs are present.
- `--disk <path>` mounts an image in CP-500 drive 0 before boot.
- `--mcp` runs the CP/M MCP server over stdio.

## Releases

Pushing a `v*` tag triggers `.github/workflows/release.yml`, which builds NativeAOT zips (win-x64, linux-x64, osx-arm64) for the desktop app and `heh8080-cp500` and creates a GitHub Release. The tag without the `v` becomes the assembly version (`heh8080-cp500 --version`). Only tag when the user asks.
