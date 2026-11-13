# mprobe

mprobe runs MMUCS.Core from the command line, without the app. It exports the game the way **File ▸ Export** does, and draws the debug pictures used to check the room viewer: a room's z-planes, and a room with its actors placed and cut by their z-planes.

## Requirements

- The .NET 8 SDK, as for MMUCS itself.
- The game's resource files. Point mprobe at the first one (`COMI.LA0`); the others beside it are loaded too.

```bash
dotnet run --project tools/mprobe -c Release -- --game D:/Games/COMI/COMI.LA0 COMMAND ...
```

Or set the `MMUCS_GAME` variable once and leave `--game` out:

```bash
export MMUCS_GAME=D:/Games/COMI/COMI.LA0      # PowerShell: $env:MMUCS_GAME = "D:/Games/COMI/COMI.LA0"
dotnet run --project tools/mprobe -c Release -- masked 14 out
```

## Commands

| Command | What it does |
|---|---|
| `export OUT` | Exports into `OUT` like File ▸ Export. With no option it writes the scripts and the room data, which is what [mscripts](../mscripts/README.md) and [mroom](../mroom/README.md) read. `--scripts`, `--data` and `--images` (backgrounds, object frames, costume cels) pick what to write; `--offsets` keeps byte offsets in the scripts' margin |
| `zplanes ROOM OUT` | Writes the room's background once per z-plane with that plane tinted (`room15_plane1.png` …): plane 1 red, 2 green, 3 blue, 4 yellow, 5 magenta, 6 cyan. Prints how much of the room each plane covers |
| `masked ROOM OUT` | Writes the room as the room viewer composes it (`room14_masked.png`): the background, then each actor MMUCS places, at its position, elevation and scale, in SCUMM's draw order, cut by its z-plane. The planes include the objects shown on entry. Lists each actor's costume, position, scale, z-plane and the object it belongs to. `--debug` paints the cut pixels magenta instead (`room14_debug.png`) |

| `box ROOM X Y` | Lists the walkboxes at a point (those holding it, or within 80 pixels) with their mask and flags, and the box the engine puts an actor there in, as ScummVM's `adjustXYToBeInBox` does: searched from the last box down to box 1, invisible boxes skipped, and a point outside every box moved to the closest edge. `--player` for the player's actor (player-only boxes). Use it to see why a costume gets the z-plane it does |

`ROOM` is a room number (`14`) or name (`town`).

## Refreshing the export for mscripts and mroom

```bash
dotnet run --project tools/mprobe -c Release -- export D:/exports/comi
python tools/mscripts/mscripts.py use D:/exports/comi
```

The export uses the variable names built into MMUCS.Core, so run it again after changing `src/MMUCS.Core/Names/variables.json` to see new names in the exported scripts. mscripts and mroom rebuild their indexes when the export changes.

## Notes

mprobe isn't part of `MMUCS.sln`, so building the app doesn't build it. It references `src/MMUCS.Core` directly and always runs the current code.
