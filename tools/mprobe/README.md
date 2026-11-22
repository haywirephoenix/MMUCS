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
| `masked ROOM OUT` | Writes the room as the room viewer composes it (`room14_masked.png`): the background, then each actor MMUCS places, at its position, elevation and scale, in SCUMM's draw order, cut by its z-plane. The planes include the objects shown on entry. Lists each actor's costume, position, scale, z-plane and the object it belongs to. `--debug` paints the cut pixels magenta instead (`room14_debug.png`). `--start` draws only what's there as the room opens, as the room viewer starts it: costumes placed on entry, not the player's, one per actor (`room14_start.png`). Each actor's line says when it's placed: `entry`, `entry-if`, `cutscene` or `later`; `--only 122,112` draws just those costumes. `masked all OUT` draws every room (`room013_plndrmap.png` …) with all its costumes and writes the listings to `OUT/costumes.txt`, each actor's line ending with `[box B scale S]`: the walkbox at its feet and the scale it gives, marked `≠` when the actor is drawn at another scale (most of those ignore the boxes on purpose) |

| `audio OUT [music\|voice] [--check]` | Decodes the Digital iMUSE bundles (`.bun` beside the game or in its `resource` folder) into `OUT/music/NNNN.wav` (by the number the track's file name starts with) and `OUT/voice/CODE.wav` (by the line's code), each with a `.meta.json`: format, the track's title, regions, jumps (loops, with hook and crossfade), markers, lip-sync data and the stop point, all in sample frames. Sounds on both discs are written once; musdisk's PRELOAD is skipped. `--check` decodes everything without writing. Prints per bundle the decoder checks: the most bytes a block's bit reader wanted past its data (0 or 1 is right), sounds whose map points outside their data, and per-disc copies that differ (all should be 0). `export OUT --audio` writes the same as part of an export |
| `cutscenes OUT [NAME…]` | Decodes the SMUSH cutscenes (`.san` beside the game or in its `resource` folder) into `OUT/video/NAME/`: one 8-bit indexed PNG per distinct picture under `frame/` (a held shot or an unchanged frame gets no file), and `video.json` with the size, frame rate, sample rate and, under `frames`, the frame whose picture each frame shows. NAME picks videos (`opening`, `finale` …). Prints per video: images written, problems, and two decoder checks: block-coded frames that left data unread (should be 0) and motion copies that reached outside the screen (should be 0). `export OUT --cutscenes` writes the same as part of an export |
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
