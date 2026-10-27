# MoonSweepers MCP

An MCP (Model Context Protocol) mod that lets an AI agent **play Moon Sweepers
(Steam) as crew** — sweeping lunar dust, driving the sweeper truck, following
crewmates in formation, and dumping loads at the Collector, all through MCP
tools any MCP client can call.

> **The game is NOT included.** You need your own copy of
> [Moon Sweepers on Steam](https://store.steampowered.com/) — this repo is the
> mod, the custom interop injector it needs, and the agent-side tooling.

## What you get

| Piece | What it does |
|---|---|
| `mcp-plugin/` | BepInEx IL2CPP plugin (v0.6.13). Runs a line-delimited JSON-RPC MCP server on `127.0.0.1:8913` inside the game: 26 tools — `state`, `sense`, `radar`, `screenshot`, `sweep`, `equip`, `vehicle`, `drive_to` (pure-pursuit autopilot with an anti-orbit watchdog), `crew`, `host_info`, `join_lobby`, `deposit`, `buy`, `save_game`, and more. |
| `interop-gen10/` + `interop-gen/` | **The custom injector.** A two-stage pipeline that generates the IL2CPP interop assemblies this game needs (see "Why a custom injector" below). |
| `mcp-bridge/moonmcp-bridge.py` | stdio ↔ TCP bridge so any MCP client (Claude Code, Hermes, Muse, LM Studio) can use the plugin. |
| `mcp-bridge/moon.py` | Fast command-line client for the MCP: state/radar/screenshot/drive/follow in one-liners. |
| `mcp-bridge/canonical_driver.py` | An autonomous player: coverage patterns (grid / hex / spiral), a dirt map, the full-tank dump-and-resume protocol, formation follow — with optional D1 decision models (via LM Studio) arbitrating maneuvers in a fast loop. |
| `mcp-bridge/add_lmstudio_mcp.py` | Idempotently registers this MCP in an LM Studio `mcp.json`. |

## Why a custom injector

Moon Sweepers runs Unity 6000.6 with **IL2CPP metadata v108**. BepInEx
6.0.0-be.791's bundled LibCpp2IL caps at v106, so the stock in-game interop
generation can never work on this title. The fix is a two-stage external
pipeline:

1. **Stage 1** (`interop-gen10/`, .NET 10): a current **dev build of Cpp2IL**
   parses v108, runs its `AttributeInjectorProcessingLayer`, and writes
   attribute-treated dummy assemblies.
2. **Stage 2** (`interop-gen/`, .NET 6): BepInEx be.791's **own**
   Il2CppInterop generator consumes the treated dummies and emits the final
   108 interop assemblies — the exact set the be.791 runtime expects.

Two BepInEx config pins are load-bearing (explained in step 2 below):
Il2CppInterop 1.5.3 mis-reads class flags on this Unity build, so
`ClassInjector` is unusable and the plugin pumps its main-thread work through
Harmony postfixes on the game's own `Update` loops instead.

## Requirements

- Moon Sweepers (Steam, Windows)
- [BepInEx 6.0.0-be.791](https://github.com/BepInEx/BepInEx/releases)
  (Unity.IL2CPP, win-x64)
- .NET SDK 8+ (builds the net6.0 plugin and stage 2) **and** the .NET 10 SDK
  (stage 1)
- A **dev build of Cpp2IL** new enough to parse metadata v108 (stage 1's
  references come from its output folder)
- Python 3.11+ (bridge, CLI, driver)
- Optional: LM Studio, if you want the D1 decision-model co-driving or an
  MCP client with a GUI

## Install

### 1. BepInEx

Extract BepInEx 6.0.0-be.791 into the game folder
(`...\Steam\steamapps\common\Moon Sweepers\`), run the game once, quit.

### 2. Pin BepInEx.cfg

In `BepInEx\config\BepInEx.cfg` set:

```ini
[IL2CPP]
UpdateInteropAssemblies = false

[Logging]
UnityLogListening = false
```

`UpdateInteropAssemblies = false` because you will supply the interop
assemblies yourself (step 3) — the bundled generator cannot parse this game's
metadata. `UnityLogListening = false` because on this Unity build the
Il2CppInterop class-flag bug crashes BepInEx's own Unity log source (and any
`ClassInjector` use) — the plugin needs neither.

### 3. Generate the interop assemblies

Point the projects at your machine (defaults are placeholders):

- `interop-gen10/interop-gen10.csproj`: `<Dev>` = your dev Cpp2IL output
  folder; `<BepCore>` = the game's `BepInEx\core`.
- `interop-gen/interop-gen.csproj`: `<BepCore>` = the game's `BepInEx\core`.

Then:

```powershell
# Stage 1 — treated dummy assemblies -> work\interop-treated
dotnet run --project interop-gen10 -- "<game folder>"

# Stage 2 — final assemblies; args: <game folder> <dummy dir>
dotnet run --project interop-gen -- "<game folder>" "work\interop-treated"
```

Stage 2 writes the assemblies into the game's `BepInEx\interop\` folder
(108 assemblies + `assembly-hash.txt`).

### 4. Build and install the plugin

```powershell
dotnet build mcp-plugin -c Release
```

The `.csproj` resolves every reference from the game folder (`$(Game)`) —
BepInEx core + the interop assemblies from step 3. Copy the built
`MoonSweepersMcp.dll` to `BepInEx\plugins\`. (With the game **closed** —
the DLL is file-locked while the game runs, and a copy over a locked file
fails silently. `mcp-plugin/deploy-v0613.ps1` automates kill → copy →
MD5-verify → relaunch if you set up a `MoonMods-Launch` scheduled task, or
just relaunch through Steam.)

### 5. First run

Launch the game. The BepInEx log (`BepInEx\LogOutput.log`) should show the
plugin listening on `127.0.0.1:8913`. Verify from a terminal:

```bash
python3 mcp-bridge/moon.py tools
python3 mcp-bridge/moon.py state
```

### 6. Connect an MCP client

Point your client at the stdio bridge:

```json
{
  "mcpServers": {
    "moonsweepers": {
      "command": "python3",
      "args": ["/path/to/mcp-bridge/moonmcp-bridge.py"]
    }
  }
}
```

For LM Studio specifically:

```bash
python3 mcp-bridge/add_lmstudio_mcp.py ~/.lmstudio/mcp.json python3 /path/to/mcp-bridge/moonmcp-bridge.py
```

### Optional: reach the game from another machine

The plugin only ever binds loopback. To drive it from a second machine, put
a forwarder in front of it — e.g. Tailscale Serve on the game PC exposing
`127.0.0.1:8913` to your tailnet — then use
`moonmcp-bridge.py --host <game-pc> --port <forwarded-port>`, or
`MOON_HOST`/`MOON_PORT` for the driver. Never expose the raw port publicly:
whoever can reach it can drive your game.

### Optional: the autonomous driver

```bash
cp mcp-bridge/driver_config.example.json mcp-bridge/driver_config.json
# edit guide/targets to taste
python3 mcp-bridge/canonical_driver.py
```

The driver plays by itself: coverage patterns on the game's own 25 m
sections, a persistent dirt map, and the dump protocol — at ~5,400 kg it
stops at a unit boundary, drives to the Collector, dwell-verifies the tank
is actually empty, returns to the exact interrupted spot, and resumes. If
LM Studio is serving decision models on `:1234` with a `/v1/systemone`
decision endpoint, the driver also consults them every cycle (a fast omni
model proposes maneuvers, a larger one confirms changes); without them it
runs deterministically on its guardrails alone.

## House rules (learned the hard way)

- **No teleporting in play.** A `teleport` tool exists for debugging; the
  driver never uses it. The bot drives with the game's own physics, like a
  human crewmate.
- **A human boards the truck.** The game's `Enter()` refuses mod-side
  boarding — the player gets in by hand; after that, the truck drives fine
  (even driverless).
- **The HUD is ground truth.** Cargo kg bottom-left, sector % top-right,
  wallet bottom-right. The driver screenshots and cross-checks instead of
  trusting return values.
- **The world is a sphere** (|position| ≈ 2,000 m everywhere). All steering
  and distance math is done in the surface tangent plane — flat XZ bearings
  degenerate near the station pole.
- **Walking the character is unsolved.** Input-level walking hooks produce
  ~zero movement on this build; the bot's mobility is the truck.
- **One driver at a time.** Two control loops on one truck is chaos.

## Layout

```
mcp-plugin/    the in-game MCP server (C#, BepInEx IL2CPP plugin)
interop-gen10/ stage 1 of the interop injector (.NET 10, dev Cpp2IL)
interop-gen/   stage 2 of the interop injector (.NET 6, be.791 generator)
mcp-bridge/    stdio bridge, moon.py CLI, canonical autonomous driver
```

The game itself, its assets, and the generated interop assemblies are not
part of this repository.
