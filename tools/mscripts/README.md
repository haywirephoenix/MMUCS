# mscripts

mscripts reads the scripts MMUCS decompiles and helps work out names for the game's variables. It searches the code, prints scripts, lists who calls what, and gathers the evidence for one variable in a single report. Names go into [variables.json](../../src/MMUCS.Core/Names/variables.json), which MMUCS builds in.

## Requirements

- Python 3.9 or newer. Nothing else.
- A script export from MMUCS: **File ▸ Export**, tick **Scripts**. It writes a `scripts/` folder:

```
scripts/global/script_0006.scumm
scripts/rooms/009_cannon/entry.scumm, exit.scumm, local_2006.scumm
scripts/rooms/009_cannon/objects/0277_cannon-wally-object.scumm
scripts/variables.index.json
```

`variables.index.json` lists the names the export used, so mscripts can turn a name back into its number. If an export doesn't have one, export again from a current MMUCS.

## Setup

Run it with `python tools/mscripts/mscripts.py`, or add this folder to your PATH and run `mscripts`. On Windows, `mscripts.bat` works from cmd and PowerShell, and `mscripts` from Git Bash. If the folder has a `.venv`, the wrappers use it.

Then tell it where the export is. It remembers the folder:

```bash
mscripts use D:/exports/comi
```

It also finds the export from `--root DIR`, the `MSCRIPTS_ROOT` variable, or the current folder and its parents.

## Commands

| Command | What it does |
|---|---|
| `use [DIR]` | Remembers the export to read, or shows it |
| `rooms` | Lists rooms and their script counts |
| `script WHERE` | Prints a script with line numbers: `global 6`, `9 local_2006`, `9 entry`, or part of a path (`--plain` drops the numbers) |
| `search REGEX` | Finds lines. `--in ROOM` (number or part of the name, repeatable), `--global`, `--rooms`, `-C N` for context, `-i`, `--count` per script, `-l` for scripts only, `--limit N` |
| `calls N` | Lists who starts, calls or chains to script `N`, with `--in ROOM` |
| `vars` | Ranks variables by how many scripts use them, with their status. `--unnamed`, `--tracked`, `--bits`, `--sort scripts\|refs\|num`, `--top N` |
| `var V` | One variable's evidence (see below). `--top N` rows per section, `--examples N` |
| `name V NAME --note TEXT` | Checks the name and writes it to `variables.json`. `--local` writes the app data copy instead, `--map FILE` another file. `""` as the name clears it |
| `note V TEXT` | Tracks a variable with a note, without naming it |

`V` is a number (`266`), `varN`, `bitN` or a name (`cursor-object`). Results are written as `path:line`, relative to `scripts/`, so they open in an editor.

### Status

`vars` and `var` show where a variable stands:

| Status | Meaning |
|---|---|
| `engine` | The engine uses it, and its name is fixed (from ScummVM) |
| `named` | Named in `variables.json` |
| `tracked` | Listed in `variables.json` with a note but no name yet |
| (blank) | Not listed |

### What `var` reports

```
var261  cutscene-depth  (named)
40 refs in 20 scripts: 7 global, 13 in 10 rooms

Writes: 2
      1x  + 1 (increment)          global/script_0006.scumm:8
      1x  - 1 (decrement)          global/script_0007.scumm:4
  written only in: global/script_0006.scumm, global/script_0007.scumm

Tested as true/false: 36x   e.g. global/script_0039.scumm:61 (+35)
```

- **Writes:** constants (grouped into a range when there are many), increments and decrements, and expressions, each with the first place it happens. "Written only in" appears when a few scripts do all the writing.
- **Compared with:** `== 3`, `< 10`, `in [10, 11]`, `!= local4`.
- **Tested as true/false:** `if (v)`, `while (!v)`.
- **Passed to commands** and **Inside calls:** `cursor image`, `load-object`, `class-of(...)`. These often give the meaning away.
- **Copied into** and **Used to work out:** variables assigned from it.
- **Rooms:** where it's used. One room means a room's own state; many rooms and global scripts mean game-wide state.

## Naming a variable

```bash
mscripts vars --unnamed               # pick one near the top
mscripts var 630                      # read the evidence
mscripts search "var630" -C 3         # read it in context
mscripts name 630 some-name --note "how you know"
```

`name` checks that the name is lower-case kebab-case with each part starting with a letter, isn't `varN`/`localN`/`bitN` or a decompiler keyword, isn't an engine variable's number, and isn't already used. It needs a note, or keeps the one the entry already has. New names resolve in mscripts straight away. To see them in the exported files and the Script viewer, rebuild MMUCS and export again.

To try names in MMUCS without changing the repo, use `--local`: MMUCS reads `%LocalAppData%/MMUCS/variables.json` (`~/.local/share/MMUCS` on Linux) on top of the built-in file when it starts.

## Tests

```bash
cd tools/mscripts
python -m unittest test_mscripts
```

The tests write a small export to a temporary folder, so they need no game files.
