# mroom

mroom shows a room of the game on one page: its exits and entrances, objects with their hover names and verbs, the actors the scripts put there, its dialogue, and the variables its scripts use. It reads an MMUCS export, like [mscripts](../mscripts/README.md), and shares mscripts' export folder, variable names and refs index.

## Requirements

- Python 3.9 or newer. Nothing else. mroom imports `../mscripts/mscripts.py`, so keep the two folders side by side.
- An export from MMUCS: **File ▸ Export**, tick **Scripts** and **Room data**:

```
scripts/...                     the decompiled scripts (what mscripts reads)
data/game.json                  every object in the game's object table: name, room, state, classes
data/rooms/015_town.json        one room: size, objects, walkboxes, costumes
```

Without `data/`, mroom still works from the scripts. It just can't show the room's size, object positions, hover names, walk-to points or costumes.

## Setup

Run it with `python tools/mroom/mroom.py`, or add this folder to your PATH and run `mroom`. On Windows, `mroom.bat` works from cmd and PowerShell, and `mroom` from Git Bash.

It reads the export that `mscripts use` saved. `mroom use DIR` saves it too (for both tools), or pass `--root DIR`.

## Commands

| Command | What it does |
|---|---|
| `use [DIR]` | Remembers the export to read, or shows it |
| `rooms` | Every room with its object, script and dialogue counts, and the rooms it leads to |
| `room ROOM` | One room on a page (see below). `--top N` rows per section (default 12), `--all` for everything |
| `object OBJ` | One object: room, hover name, starting state and classes, image box, walk-to point, verbs with their lines, where its doors lead, and the other scripts that name it |
| `exits [ROOM]` | All rooms and where they lead, or one room's exits (`→`) and entrances (`←`) with the line each comes from. `--dot` writes a Graphviz graph |
| `lines [ROOM]` | Dialogue in script order, as `path:line  actor: text`. `--actor A`, `--grep REGEX`, `--choices` for the player's dialogue choices, `--limit N` |
| `actor A` | The rooms an actor appears in, its costumes there, its line counts and a few lines |
| `costume N` | Where a costume is stored, where MMUCS places it, each script that dresses an actor in it with the elevations and placements that follow, and the other scripts in its room that place the same actors. `--limit N` |
| `find TEXT` | Rooms, objects, hover names and lines that contain the text |

`ROOM` is a number (`15`) or part of a name (`town`). `OBJ` is an id (`397`) or part of a name (`chicken`). Results are written as `path:line` under `scripts/`, so `mscripts script` and an editor open them.

### What `room` shows

```
Room 15 town   2096x480, 1 palette, 14 sounds, 63 walkboxes  (COMI.LA1)
scripts/rooms/015_town: entry, exit, 18 local scripts, 43 object scripts

Exits (5 rooms)
  → 13 plndrmap   come-out-door plndrmap-town-door-2  from objects/0406_town-jungle-plndrmap-door.scumm:12 (verb walk-to)  (+3, mroom exits 15)
Entrances (6 rooms)
  ← 16 chkn-int   come-out-door town-chicken-int-door  from rooms/016_chkn-int/objects/0440_chicken-int-town-door.scumm:8 (verb walk-to)

Objects (43)   id name                     hover name   at                verbs
   397 town-chicken-int-door    "door"       1288,168 48x144   look hand mouth walk-to exit-cursor hand-text

Actors (10)
  4 (kenny-object)   costumes 75, 84;  17 lines;  put at 552,268 (local_2001.scumm:31)

Dialogue: 123 lines   (1 (player): 96, 4 (kenny-object): 17, …)
Global scripts started here: 3, 20x2, 52x27, …
Variables: 52 used here: 29 unnamed, 14 named, 7 engine, 2 tracked
  not named: bit9 (42), bit8 (42), bit44 (30), …
```

### How it works these out

- **Exits** are the room changes in a room's scripts: `come-out-door OBJECT` goes to that object's room, and `current-room N` to room N. A global script counts as the room's exit when only that room's scripts start it, and the line shows `via global/script_NNNN.scumm:LINE`. A room change made through a variable, or through a global script that several rooms start, isn't found.
- **Verbs** are named where the scripts make their meaning plain: 5 `use-with`, 6 `look`, 7 `hand`, 8 `mouth`, 9 `walk-to`, 94 `exit-cursor`, 216 `hand-text`, 218 `mouth-text`, 219 `use-with-text`. The rest print as numbers.
- **Actors** come from `actor A costume C`, `put-actor A at X,Y in-room R` and `say-line A`. Sums such as `492 + 300` are worked out; other expressions are shown as written. An actor is named after the object its costume belongs to in the room data (`4 (kenny-object)`). Actor numbers are reused from room to room, so the name only shows in that room. The player's actor is named everywhere.
- **Elevation** (`actor A elevation E`) draws an actor E pixels above its y, so a placement shows as `320,792 elevation 300 (drawn at 320,492)`. It counts for a placement when it's set after the actor's last costume change or `default` in the same script. Scripts use it to keep an actor drawn in one place while a large y puts it in front of the others, often as `elevation E` followed by `put-actor A at x-of(A),y-of(A) + E`.
- **Scale** (`actor A scale N`, 255 is full size) is read the same way and shown as `, scale 150`. When a room starts a script that dresses the actor it passes in (`start-script 88(local2, 150, 0)`, where script 88 sets the costume and `scale local1`), the room data shows the costume at the spot the room places that actor, with the scale passed in, marked `dressed by script 88`.
- **Hover names** are the names the game shows under the cursor ("mysterious flowers"), from each object's `OBNA` block.
- **Walk-to** is where an actor walks to use the object, one point per state, read from the object image's hotspots the way ScummVM reads them.
- **Classes** are the object's starting classes from the game's object table. Class 32 is shown as `untouchable`.

### The index

The first run reads every script and saves what it finds (doors, room changes, scripts started, dialogue, actors, sounds) to `scripts/.mroom-index.json`, beside mscripts' index. It's rebuilt by itself when the export changes. The room data files are read as they're needed.

## Tests

```bash
cd tools/mroom
python -m unittest test_mroom
```

The tests write a small export to a temporary folder, so they need no game files.
