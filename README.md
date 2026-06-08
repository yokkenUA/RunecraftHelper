# RunecraftHelper

A [GH](https://github.com/Gordin/GameHelper2) plugin that draws live [poe.ninja](https://poe.ninja)
prices, in Exalted Orbs, directly onto the rewards shown in the in-game **Runeshape Combinations**
panel.

While the panel is open, the plugin reads the visible reward rows straight from the game's UI tree
and paints each row's total value at its right edge — so you can compare combination outcomes at a
glance without leaving the panel and without a separate window stealing focus.

## Features

- **In-panel price overlay.** The value is drawn (with a drop shadow) on each visible row; the in-game
  reward name is never duplicated — you read it from the panel in your own client language. Font size
  tracks the row height. No screen OCR — rows are read directly from the UI tree.
- **Value color-coding** (`ColorMode`): *Off* (neutral), *Relative* (green/yellow/red vs. the median
  of the rows on screen) or *Absolute* (fixed Exalted thresholds). Default: Relative.
- **Adjustable X offset** for the price text, to clear long localized names (or compensate for the
  black bars in letterbox display modes).
- Resolves the panel by **UI Flags fingerprint with backtracking**, so it survives child-index
  shuffles between game restarts and patches instead of breaking on a fixed path.
- **Language-independent reward matching.** The panel only shows the reward as *localized* text, so
  matching that text to poe.ninja (English) breaks on non-English clients. Instead the plugin
  translates the localized name → the item's internal identifiers via the game's own `BaseItemTypes`
  data, then prices by those. Works on any client language (EN / RU / KR / …), and the quantity
  prefix/suffix (`"6x …"` vs `"… (6)"`) is parsed for both locale styles. Matching keys, by priority:
  - **metaId** (`BaseItemType.Id`) — for shared-icon tier families (Regal / Greater / Perfect).
  - **dds-art + level** — for shared-icon *leveled* families where one icon covers every level:
    Thaumaturgic Flux (level from `…Level<n>`) and **Uncut Skill / Support / Spirit Gems** (level
    from the metaId's trailing digits, e.g. `SkillGemUncut19` → Level 19). Untradable base/quest
    variants resolve to no price.
  - **dds-art** — for non-leveled distinct-icon families (Jeweller's `…01/02/03`).
  - localized name — English clients / unmapped items.
- **Optional debug window** (`ShowWindow`) listing each visible row's count, resolved metaId, dds-art,
  price and poe.ninja English name — to see at a glance which reward failed to resolve to a price.
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
| **Color mode** | `Relative` | Price-text tint: `Off` / `Relative` (vs. on-screen median) / `Absolute` (fixed Exalted thresholds). |
| **Price X offset** | `0` | Horizontal nudge (px) of the price text; clears long names / letterbox bars (−400…+400). |
| **Show debug list window** | `on` | Per-row table: count · metaId · dds-art · price · poe.ninja name. |

The settings panel also shows the price-cache status (last sync, items cached, Divine→Exalted rate)
and a **Refresh now** button.

## Credits

- Built as a plugin for [GameHelper2](https://github.com/Gordin/GameHelper2).
- Prices courtesy of [poe.ninja](https://poe.ninja).

## Disclaimer

This is a read-only overlay tool for personal use. Use at your own risk and in accordance with the game's terms of service.
