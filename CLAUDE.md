# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

UltrabotMod is a reinforcement learning bot for ULTRAKILL. It has two halves:

1. **C# BepInEx Plugin** (`UltrabotMod/Plugin/`) — runs inside the game, reads game state, executes actions, and exposes a TCP bridge on port 7865.
2. **Python RL Agent** (`UltrabotMod/Python/`) — connects to the bridge, trains a PPO policy (via Stable Baselines3 + Gymnasium), and runs inference.

The plugin injects into ULTRAKILL's Unity runtime using BepInEx + Harmony patches. It uses heavy reflection to access private game internals (e.g., `NewMovement.inputDir`, `InputActionState` setters for weapon firing).

## Build & Run

### C# Plugin (requires .NET Framework 4.7.1 SDK)
```bash
cd UltrabotMod/Plugin
dotnet build
```
The built DLL goes to `Plugin/bin/Debug/net471/UltrabotMod.dll`. Copy it to `<ULTRAKILL>/BepInEx/plugins/UltrabotMod/`. The `install.bat` script builds and copies in one step.

The `.csproj` references game DLLs from `C:\Steam\steamapps\common\ULTRAKILL\ULTRAKILL_Data\Managed\` and BepInEx from `C:\Steam\steamapps\common\ULTRAKILL\BepInEx\core\`. If the game is installed elsewhere, edit `GameDir` in the csproj.

### Python Agent
```bash
cd UltrabotMod/Python
pip install -r requirements.txt
python train.py                     # train (game must be running with mod loaded on a level)
python train.py --resume            # resume from checkpoint
python play.py                      # run trained agent
```

## Architecture

### TCP Protocol (port 7865)
Binary protocol with message types: 0=Step, 1=Reset, 2=Close, 3=SetSpeed, 4=GetInfo. All values are little-endian. The Python `UltrakillEnv` and C# `TcpBridge` must stay in sync on wire format and constants.

### Observation Space (241 floats)
Defined in `GameStateReader` (C#) and mirrored in `ultrakill_env.py` (Python). Layout:
- **Player state** (44): position, velocity, look direction, HP, weapon charges, style rank, enemy-facing (dot product, distance, line-of-sight)
- **Raycasts** (24): 12 horizontal + 4 vertical + 8 diagonal normalized distance rays
- **NavMesh hint** (5): direction/distance to target (enemy > checkpoint > exit, queried every 10 frames)
- **Aim hint** (4): yaw/pitch delta to nearest enemy, has_target, in_frustum — tells RL where to look
- **Enemies** (10 × 10 = 100): relative position, health, type, weight class, sorted by distance
- **Projectiles** (8 × 8 = 64): relative position, velocity, damage, sorted by distance

### NavMeshAgent Navigation
ActionExecutor creates an invisible NavMeshAgent (like enemies use) that pathfinds to targets. Movement is blended: 60% NavMeshAgent direction + 40% RL input. RL can strafe/dodge around the navmesh path. When no navmesh path exists (airborne, no navmesh), RL has full control. GameStateReader sets the destination (enemy > checkpoint > exit).

### Action Space (20 floats)
Defined in `ActionExecutor`. First 4 are continuous (move fwd/right, look yaw/pitch). Remaining 16 are binary (>0.5 threshold): jump, dash, slide, fire1, fire2, punch, 6 weapon slots, whiplash, slam, swap variation, change fist. All actions go through InputActionState (game's input system) — no more reflection bypass.

### Reward Function
In `TcpBridge.CalculateReward()`. Components: style points, kills, parries, headshots, rank changes, multikills, survival bonus, damage penalty, death penalty, horizontal exploration bonus, height penalty, and a small per-step cost.

### Harmony Patches (StyleTracker.cs)
Patches `StyleHUD.AddPoints`, `StyleHUD.RemovePoints`, `EnemyIdentifier.Death`, and `NewMovement.GetHurt` to accumulate per-step event data consumed by the reward function.

### Fire System
Weapon firing works by reflecting into `InputManager.InputSource` (a `PlayerInput` object) and setting `Fire1`/`Fire2` `InputActionState` fields via their private setters. This is the only reliable way — weapons check these states in their `Update()`.

## In-Game Hotkeys
- **F7**: Toggle debug HUD
- **F8**: Toggle bot active/inactive
- **F9**: Emergency stop (releases all inputs, resets timescale)

## Key Constraints
- The C# plugin targets .NET Framework 4.7.1 (Unity's Mono runtime). Do not use modern C# features unsupported by this target.
- `AllowUnsafeBlocks` is enabled in the csproj.
- Game type references (e.g., `NewMovement`, `GunControl`, `StyleHUD`, `EnemyIdentifier`) come from `Assembly-CSharp.dll` — there are no source files or docs for these. Use the `dll-inspector/` tool to explore game types via reflection.
- When changing observation layout or action layout, **both** the C# constants and Python constants must be updated together.

## Other Directories
- `dll-inspector/` — .NET 10 console app for inspecting ULTRAKILL's Assembly-CSharp.dll via reflection (useful for discovering game internals).
- `old/` and `d--Games-I-HOPE-LASTULTRABOT/` — previous iterations and conversation logs, not part of the active codebase.
