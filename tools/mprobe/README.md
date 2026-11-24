# mprobe

mprobe runs MMUCS.Core and MMUCS.Runtime from the command line, without the app. It exports the game the way **File ▸ Export** does, draws the debug pictures used to check the room viewer (a room's z-planes, and a room with its actors placed and cut by their z-planes), and runs the game's scripts headless to show what the interpreter does.

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
| `masked ROOM OUT` | Writes the room as MMUCS.Player draws it, which is how the room viewer and the game draw it too (`room14_masked.png`): the background, the objects in their states on entry, then each actor MMUCS places, at its position, elevation and scale, in SCUMM's draw order, cut by its z-plane. The planes include the objects shown on entry. Lists each actor's costume, position, scale, z-plane and the object it belongs to. `--debug` paints the cut pixels magenta instead (`room14_debug.png`). `--start` draws only what's there as the room opens, as the room viewer starts it: costumes placed on entry, not the player's, one per actor (`room14_start.png`). Each actor's line says when it's placed: `entry`, `entry-if`, `cutscene` or `later`; `--only 122,112` draws just those costumes. `masked all OUT` draws every room (`room013_plndrmap.png` …) with all its costumes and writes the listings to `OUT/costumes.txt`, each actor's line ending with `[box B scale S]`: the walkbox at its feet and the scale it gives, marked `≠` when the actor is drawn at another scale (most of those ignore the boxes on purpose) |

| `audio OUT [music\|voice] [--check]` | Decodes the Digital iMUSE bundles (`.bun` beside the game or in its `resource` folder) into `OUT/music/NNNN.wav` (by the number the track's file name starts with) and `OUT/voice/CODE.wav` (by the line's code), each with a `.meta.json`: format, the track's title, regions, jumps (loops, with hook and crossfade), markers, lip-sync data and the stop point, all in sample frames. Sounds on both discs are written once; musdisk's PRELOAD is skipped. `--check` decodes everything without writing. Prints per bundle the decoder checks: the most bytes a block's bit reader wanted past its data (0 or 1 is right), sounds whose map points outside their data, and per-disc copies that differ (all should be 0). `export OUT --audio` writes the same as part of an export |
| `play ID OUT.wav [--seconds N] [--rate HZ] [--hook HOOK@SECONDS …]` | Plays one bundle sound (`music/1098`, `voice/bccb301`) offline through MMUCS's mixer and iMUSE player (`MMUCS.Core.Media`) into a 16-bit stereo WAV, listing each event as it happens: regions, markers, jumps taken or skipped, and the stop. Loops repeat and jumps crossfade as in the game. `--hook` sets the track's hook at a time: a jump with hook N is taken only when the hook is N, and 128 makes a hook-0 loop fall through once. At `--rate 22050` (the game's rate) the output is the original samples unchanged, apart from crossfades; the default is 44100 Hz, at most 600 s |
| `listen ID [--seconds N] [--rate HZ] [--hook HOOK@SECONDS …] [--device NAME]` | As `play`, but out loud through OpenAL Soft (`MMUCS.Media.OpenAL`, the default sound device unless `--device` names one). Events print when they're mixed, about one latency (93 ms) before they're heard; hooks are set by the played clock, and those at 0 before playback starts, since starting fills the device queue. Prints the latency, underruns (should be 0) and how far the played clock drifted from the wall clock. To check the output without speakers, point OpenAL Soft at its wave writer: an `alsoft.ini` with `drivers=wave` under `[general]` and `file=out.wav` under `[wave]`, then run with `ALSOFT_CONF=alsoft.ini` |
| `cutscene-audio [OUT]` | Decodes every cutscene's sound (its IACT chunks) and checks it: chunks, packets, escaped samples, bytes left over at the end (should be 0), and the sound's length against the picture's. With OUT, writes each as `OUT/NAME.wav` (16-bit stereo, 22,050 Hz) |
| `cutscenes OUT [NAME…]` | Decodes the SMUSH cutscenes (`.san` beside the game or in its `resource` folder) into `OUT/video/NAME/`: one 8-bit indexed PNG per distinct picture under `frame/` (a held shot or an unchanged frame gets no file), and `video.json` with the size, frame rate, sample rate and, under `frames`, the frame whose picture each frame shows. The sound goes to `audio.wav` beside it (16-bit stereo, 22,050 Hz, starting with frame 0). NAME picks videos (`opening`, `finale` …). Prints per video: images written, problems, and two decoder checks: block-coded frames that left data unread (should be 0) and motion copies that reached outside the screen (should be 0). `export OUT --cutscenes` writes the same as part of an export |
| `box ROOM X Y` | Lists the walkboxes at a point (those holding it, or within 80 pixels) with their mask and flags, and the box the engine puts an actor there in, as ScummVM's `adjustXYToBeInBox` does: searched from the last box down to box 1, invisible boxes skipped, and a point outside every box moved to the closest edge. `--player` for the player's actor (player-only boxes). Use it to see why a costume gets the z-plane it does. Then the ways out of that box by the room's box matrix (BOXM): for each other box, `direct`, `via N` (the next box on the way) or `none` |

| `boot [--seconds N] [--click WHAT@S …] [--right-click WHAT@S …] [--key KEY@S …] [--calls] [--every-frame] [--vars V,V…] [--boot N] [--png FILE]` | Runs the game's scripts headless with MMUCS.Runtime's interpreter: the engine's start-up variables, the boot script (global 1), then N seconds of frames (default 10; COMI gets a frame every 5 jiffies, 12 a second). Prints each script starting (`▸`) and ending (`◂`, with why: ended, stopped, room left, fault), indented by nesting, with the time and frame; rooms entered with their size; the lines said and printed (`SAY actor N TAG: text`, with the speech read along so a line lasts as long as it's spoken; `SYSTEM` for system messages), the sound effects (`SOUND N starts`; read along with the game's time so the scripts see them end, not heard) and the music (`MUSIC`: each state and sequence the scripts set, the track and how it takes over, from the tables in comi.exe; followed but not heard, so a sequence lasts until the scripts end it); faults and warnings. Then the scripts still running (sleeping or running, and where), some variables (`--vars` adds more by number, name or `bitN`) and the instructions the interpreter doesn't carry out yet, with how often they were met. During frames each line is shown the first time and repeats are counted, unless `--every-frame`. `--calls` also lists those instructions as they're met, with their arguments and text. `--boot N` is the boot parameter (0; `--param` also works), ScummVM's `-b`: COMI's boot script jumps straight to a room, without the flags, inventory or music playing there would have set, so compare odd results with ScummVM booted the same way. `--click WHAT@S` presses the left button S seconds after boot (default 1) on WHAT: an object of the current room by number or name (at its middle), `actorN` (the middle of where actor N was last drawn) or `X,Y` on the screen, and lets go 6 jiffies later; `--right-click` likewise; `--hold WHAT@S` holds the left button for a second (the verb coin); `--move WHAT@S` moves the pointer there without a click; `--key KEY@S` presses a key (a character, `esc`, `enter`, `space`, `f1`…`f9` or a code). The current room's objects, their places and which are untouchable are listed at the end, then its actors (costume, place, facing, animation, box, scale, layer, where they were drawn and their limbs' AKSQ positions). `--png FILE` saves the room as the game window draws it at the end: the objects the engine draws, the actors as its last frame drew them, and its texts (listed too, with their places). Exits 1 if a script faulted |

`ROOM` is a room number (`14`) or name (`town`).

## Checking the interpreter

```bash
mprobe boot                      # COMI boots into room 87, the difficulty choice, and waits for a click
mprobe boot --calls --seconds 0  # everything boot asks for that isn't carried out yet
mprobe boot --seconds 20 --click easyhard-hard-button-object@2
mprobe boot --seconds 60 --click easyhard-hard-button-object@5 --png cannon.png  # the first scene, its actors as the scripts put them
                                 # pick Mega-Monkey: about 3.5 s later the game leaves for room 2 (thx)
```

## Refreshing the export for mscripts and mroom

```bash
dotnet run --project tools/mprobe -c Release -- export D:/exports/comi
python tools/mscripts/mscripts.py use D:/exports/comi
```

The export uses the variable names built into MMUCS.Core, so run it again after changing `src/MMUCS.Core/Names/variables.json` to see new names in the exported scripts. mscripts and mroom rebuild their indexes when the export changes.

## Notes

mprobe isn't part of `MMUCS.sln`, so building the app doesn't build it. It references `src/MMUCS.Core` directly and always runs the current code.
