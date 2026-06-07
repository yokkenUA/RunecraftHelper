# RunecraftHelper

A [GH](https://github.com/Gordin/GameHelper2) plugin overlays the rewards currently shown in the in-game **Runeshape Combinations** panel, annotated
with live [poe.ninja](https://poe.ninja) prices in Exalted Orbs.

While the Runeshape Combinations panel is open, the plugin reads the visible reward rows straight
from the game's UI tree and shows them in a compact window: count, reward name, and the estimated
total value. This lets you compare combination outcomes at a glance without leaving the panel.

## Features

- Reads the visible Runeshape reward rows directly from UI (no screen OCR).
- Resolves the panel by **UI Flags fingerprint with backtracking**, so it survives child-index
  shuffles between game restarts and patches instead of breaking on a fixed path.
- Pulls currency/item prices from poe.ninja and caches them on disk with a configurable TTL.
- Shows per-reward total value in Exalted Orbs (uses the live Divine→Exalted rate).

## Requirements

- A working [GH](https://github.com/Gordin/GameHelper2) checkout (this is a plugin, not a
  standalone app).
- .NET 10 SDK (the project targets `net10.0-windows`, x64).

## Build & install

This plugin is meant to live inside a GH source tree, because it references
`GameHelper.csproj` and copies its build output into GameHelper's `Plugins` folder.

1. Clone this repo into the GameHelper2 `Plugins` directory so the layout is:

   ```
   <GameHelper2>/
     GameHelper/
       GameHelper.csproj
     Plugins/
       RunecraftHelper/      ← contents of this repo
         RunecraftHelper.csproj
         RunecraftHelperCore.cs
         ...
   ```

   The `.csproj` expects `..\..\GameHelper\GameHelper.csproj` to exist relative to itself.

2. Build:

   ```
   dotnet build Plugins/RunecraftHelper/RunecraftHelper.csproj -c Debug
   ```

   The post-build step copies `RunecraftHelper.dll` into
   `GameHelper/<OutDir>/Plugins/RunecraftHelper/`.

3. Launch GameHelper2 and enable **RunecraftHelper** in the plugin list.

## Settings

| Setting | Default | Notes |
|---|---|---|
| **League** | `Runes of Aldur` | poe.ninja PoE2 league slug; update each league launch. |
| **Refresh interval (min)** | `60` | How long cached prices stay valid before a re-fetch (5–60). |

The settings panel also has a **Debug → last panel-resolve trace** section that shows how the
plugin walked the UI tree on the most recent frame — useful if a game patch moves the panel and
the fingerprints need updating.

## Credits

- Built as a plugin for [GameHelper2](https://github.com/Gordin/GameHelper2).
- Prices courtesy of [poe.ninja](https://poe.ninja).

## Disclaimer

This is a read-only overlay tool for personal use. Use at your own risk and in accordance with the game's terms of service.
