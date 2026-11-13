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

The same export can be made from the command line with [probe](../probe/README.md): `dotnet run --project tools/probe -c Release -- --game COMI.LA0 export FOLDER`.

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
| `script WHERE` | Prints a script with line numbers: `global 6`, `9 local_2006`, `9 entry`, or part of a path (`--plain` drops the numbers). Prints part of it with `--lines A-B`, `--at N` (`-C K` lines either side, default 10) or `--block N`, the brace block around line N (`--levels K` for K blocks out) |
| `search REGEX` | Finds lines. `--in ROOM` (number or part of the name, repeatable), `--global`, `--rooms`, `-C N` for context, `-i`, `--count` per script, `-l` for scripts only, `--limit N` |
| `calls N` | Lists who starts, calls or chains to script `N`, with `--in ROOM` |
| `progress` | How many variables are named, noted (Unconfirmed, Unclear) or left, what changed since git `--ref` (default `HEAD`), and where the rest are used, room by room. `--left [ROOM]` lists them, `--all` lists every room, `--bits` |
| `vars` | Ranks variables by how many scripts use them, with their status. `--unnamed`, `--tracked`, `--bits`, `--sort scripts\|refs\|num`, `--top N` |
| `var V` | One variable's evidence (see below). `--top N` rows per section, `--examples N` |
| `refs V` | Every line that uses a variable, from the index. `--writes` for the lines that write it, `-C N` for context, `--in ROOM`, `--global`, `--by-room` or `--count` for counts, `--limit N` |
| `name V NAME --note TEXT` | Checks the name and writes it to `variables.json`. `--local` writes the app data copy instead, `--map FILE` another file. `""` as the name clears it |
| `note V TEXT` | Tracks a variable with a note, without naming it |
| `review [REF]` | Checks the entries changed in `variables.json` since git `REF` (default `HEAD`): their citations, names and evidence. `--all` checks every entry, `-v` lists good citations too |

`V` is a number (`266`), `varN`, `bitN` or a name (`item-icon`). Results are written as `path:line`, relative to `scripts/`, so they open in an editor.

### The index

The first command that needs it reads the whole export and saves a map of where every variable is used to `scripts/.mscripts-index.json`. `refs`, `vars`, `progress`, `var` and `review` use it, so they only read the scripts a variable is in. It's rebuilt by itself when a script is added, removed or changed, or `variables.index.json` changes. Delete it to force a rebuild.

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
mscripts progress --left              # or see what's left, room by room
mscripts var 630                      # read the evidence
mscripts refs 630 --writes -C 8       # each write with the code around it
mscripts script 9 local_2006 --block 120   # the block around one line, if that isn't enough
mscripts name 630 some-name --note "how you know"
```

`name` checks that the name is lower-case kebab-case with each part starting with a letter, isn't `varN`/`localN`/`bitN` or a decompiler keyword, isn't an engine variable's number, and isn't already used. It needs a note, or keeps the one the entry already has. New names resolve in mscripts straight away. To see them in the exported files and the Script viewer, rebuild MMUCS and export again.

To try names in MMUCS without changing the repo, use `--local`: MMUCS reads `%LocalAppData%/MMUCS/variables.json` (`~/.local/share/MMUCS` on Linux) on top of the built-in file when it starts.

## Reviewing names

Before committing new names, check them:

```bash
mscripts review              # entries changed since the last commit
mscripts review origin/main  # or since another ref
```

For each changed entry, `review` shows the old and new name, the note, and the variable's evidence in a few lines (writes, the scripts that write it, comparisons, commands), so the note's claims can be read against it. It also checks every citation in the note:

| Result | Meaning |
|---|---|
| ok | The line exists and mentions the variable, or another variable the note cites it for |
| check | The line exists but doesn't mention the variable. It may show context (where a script is started), so read it |
| WRONG | The script or the line doesn't exist |

Citations are written `global/script_0039.scumm:61`, `:109-110` for a range or `:5/58/124` for several lines. A bare `:112` means the script cited just before. A script named without a line also counts, except inside a parenthesis that has closed, so `script_0040.scumm (… script_0001.scumm:193): … at :118` means script 40. Write the script in full when it could be read either way.

`review` also flags names that break the rules, and entries whose notes cite nothing. It exits with 1 when anything is wrong.

## Tests

```bash
cd tools/mscripts
python -m unittest test_mscripts
```

The tests write a small export to a temporary folder, so they need no game files.
