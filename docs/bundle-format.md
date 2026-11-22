# MMUCS bundle format and asset IDs (draft)

Status: draft for discussion. Nothing here is implemented yet.

## Goals

1. On first run, MMUCS extracts the user's COMI files once, faithfully, into a **base bundle**.
2. MMUCS plays the game from bundles, never from the original `.la*` / `.bun` / `.san` files.
3. Anyone can **unpack** assets into editable formats (PNG, WAV, video frames), change them, and **repack** them
   as a **mod pack** that contains only their own work, so it can be shared.
4. Cutscenes and audio are first-class assets, playable in MMUCS, with the same override rules as art.

Everything hangs on one contract: **every asset has a stable ID**. Extraction, the engine, unpack, repack, mods
and the loose override folder all agree on these IDs. Once mods exist, the IDs cannot change, so they are the
thing to get right first.

## Layers

At load time an asset ID is resolved top to bottom; the first layer that has it wins.

| Layer | What it is | Distributable |
|---|---|---|
| `loose/` folder | files dropped in by hand, for authors iterating (`loose/<id>.<ext>`) | n/a |
| mod packs | `*.mmpack`, in priority order | yes: holds only the author's work |
| base bundle | `base.mmbundle`, built from the user's own copy | **no**, never shared |

A mod only ever *replaces or adds* IDs. It never patches bytes inside another asset.

## Asset ID grammar

```
id        = namespace "/" segment *( "/" segment )
namespace = "game" / "room" / "object" / "costume" / "script" / "font" / "text"
          / "voice" / "music" / "sfx" / "video" / "ui"
segment   = number / name
number    = 4DIGIT                     ; decimal, always zero-padded to 4: 0009, 0145, 1098
name      = 1*( a-z / 0-9 / "-" / "_" )  ; lowercase ASCII only
```

Rules:

- **IDs are also paths.** `room/0009/background` lives at `room/0009/background.png` in a bundle and at
  `loose/room/0009/background.png` on disk. The extension is never part of the ID, so a mod can swap a `.png`
  for an `.svg` or a `.webp` under the same ID.
- **Numbers come from the game's own resource numbers**, not from file offsets or list positions: room numbers
  from `DROO`, costume numbers from `DCOS`, global object IDs from `CDHD`, sound numbers from `DSOU` / the bundle
  file names. Same game, same IDs, on every machine.
- **No display names in IDs.** Names differ between languages and releases, and some objects share names. Names
  are stored in each entry's `meta.name`. (Today's exporter uses `rooms/009_cannon/...` and
  `costumes/akos_00BE5A10/...`; both need to move to numeric IDs, see [Migration](#migration-from-the-current-exporter).)
- **One padding rule everywhere**, so a parser never has to guess: every numeric segment is 4 digits. Side art then
  lands on `room/0009/sides`, which matches the `0009.png` naming in `scummvm-ai-upscale`, so packs convert in one
  step.
- **Text-keyed audio uses the game's own codes**, lowercased: voice `BCCB301.IMX` becomes `voice/bccb301`, and
  the subtitle carrying the `/BCCB301/` code becomes `text/bccb301`. Subtitle and voice line share the key.

## Namespaces

### `game/` (index-level data)

| ID | Source | Content |
|---|---|---|
| `game/objects` | `DOBJ` | the global object table: owner, state, room and classes per object |
| `game/rooms` | `DROO`, `RNAM` | room directory and names |
| `game/maxs` | `MAXS` | engine limits |
| `game/arrays` | `AARY` | array definitions |
| `game/variables` | MMUCS | variable-name table (`VariableNames`) |
| `game/music-table` | **MMUCS-authored** | iMUSE state/sequence → track table (see [Music](#music)) |

### `room/NNNN/`

| ID | Source | Content |
|---|---|---|
| `room/NNNN/background` | `RMIM` | 8-bit **indexed** PNG, decoded with the default palette's indices intact |
| `room/NNNN/zplane/NNNN` | `ZPLN` | 1-bit mask per plane |
| `room/NNNN/palette/NNNN` | `PALS`/`APAL` | 256-entry palette |
| `room/NNNN/cycles` | `CYCL` | colour-cycling ranges |
| `room/NNNN/walkboxes` | `BOXD`, `BOXM` | polygons and the connectivity matrix |
| `room/NNNN/scale` | `SCAL` | scale slots |
| `room/NNNN/data` | `RMHD` and more | size, transparent index, object placement, z-plane count, actors |
| `room/NNNN/script/entry` | `ENCD` | bytecode |
| `room/NNNN/script/exit` | `EXCD` | bytecode |
| `room/NNNN/script/local/NNNN` | `LSCR` | bytecode |
| `room/NNNN/sides` | **authored only** | widescreen pillar art (never present in the base bundle) |
| `room/NNNN/background/variant/NNNN` | `OBIM` | an object image wide enough to act as an alternative background, aliased so painters can find it |

### `object/NNNN/` (global object ID)

Object IDs are global, so objects get their own namespace even though each one belongs to a room. The owning room
is in `meta.room`.

| ID | Source | Content |
|---|---|---|
| `object/NNNN/state/NNNN` | `OBIM`/`IMAG` | image for one state, indexed PNG |
| `object/NNNN/state/NNNN/zplane/NNNN` | `ZPLN` in the state | mask |
| `object/NNNN/code` | `OBCD` | verb scripts, bytecode |
| `object/NNNN/data` | `CDHD`, `OBNA` | name, hotspot box, walk-to point, parent/state relations, `MaskOr` |

### `costume/NNNN/` (DCOS costume number)

The engine composes limbs from cels at runtime, so the **cel** is the unit that gets replaced. Animation data
stays as data.

| ID | Source | Content |
|---|---|---|
| `costume/NNNN/cel/NNNN` | `AKCD` via `AKCI` | indexed PNG plus its offset |
| `costume/NNNN/palette` | `AKPL`/`RGBS` | the costume's palette mapping |
| `costume/NNNN/anim` | `AKHD`, `AKSQ`, `AKCH`, `AKOF` | chores, limb sequences, commands |

An authored override may replace a cel with a sheet for a whole chore (`costume/NNNN/chore/NNNN/sheet`), so a
painter can work on consistent frames. The manifest gives each frame's rectangle in the sheet; a sheet override
wins over the individual cels it covers.

### `script/NNNN`

Global scripts (`SCRP`), as bytecode. The decompiled `.scumm` source is a view generated on unpack, not a
separate asset. Later, `script/NNNN` may be overridden by a script mod in a future source format.

### `font/NNNN/`

COMI's fonts are the NUT files in `resource/` (`font0.nut` … `font4.nut`; each is an `ANIM` container). Any `CHAR`
blocks in the `.la*` files go here too.

| ID | Content |
|---|---|
| `font/NNNN` | metrics: glyph sizes, offsets, line height |
| `font/NNNN/glyph/NNNN` | one glyph image |

### `text/`

| ID | Source | Content |
|---|---|---|
| `text/<code>` | script strings, `resource/language.tab` | one line in every language present |

A line's ID is its `/CODE/` message prefix, lowercased. Lines without a code get
`text/script/NNNN/NNNN` (script number and byte offset).

### `voice/<code>`

From `voxdisk1.bun` and `voxdisk2.bun` (`LB83` bundles of `COMP`-compressed `iMUS` files; 3,815 and 4,675
entries). In this copy, 414 names appear on both discs with identical sizes: **dedupe them by name**, and mark it
an error if the contents ever differ.

### `music/NNNN`

From `musdisk1.bun` and `musdisk2.bun` (63 and 68 entries, one shared, plus `PRELOAD`). The number is the
file-name prefix: `1098-C~1.IMX` becomes `music/1098`. The DOS short name after the number is truncated, so the
real track title is kept in `meta.name` only.

Unpacked, each sound is `<id>.wav` (16-bit PCM, 22,050 Hz; stereo music, mono speech) plus `<id>.meta.json` with the [audio meta](#audio-meta-voice-music-sfx) in sample frames; the music meta also gives the track's own title from its first marker (`1098-Credits1`). `MMUCS.Core.Audio` reads the bundles (`Bundle`, `BundleCodec`, `ImusSound`). Speech regions often run one byte past their data (an odd length of 16-bit samples), which is in the original files.

### `sfx/NNNN`

Sound effects, from `SOUN` resources referenced by `DSOU`. (Still to be verified: whether every COMI sound effect
is a `SOUN` in the `.la*` files, and which use `iMUS` inside.)

### `video/<name>/`

From `resource/*.san` (15 files: `opening`, `finale`, `curserng`, …). Each is an `ANIM` with an `AHDR` and then
`FRME` chunks; frames hold `FOBJ` (image) and `IACT` (audio) chunks.

| ID | Content |
|---|---|
| `video/<name>` | the original SMUSH stream, played by MMUCS's own decoder (`MMUCS.Core.Smush`) |
| `video/<name>/frame/NNNN` | unpacked: the picture shown from frame NNNN on, as an indexed PNG (a mod may replace it) |
| `video/<name>/subtitles` | timed lines, pointing at `text/` IDs |
| `video/<name>/sides` | authored only: per-frame or per-shot pillar art |
| `video/<name>/replacement` | authored only: a modern video file that replaces the picture |

### `ui/` (aliases)

The verb coin, inventory, cursors and dialogue UI are really objects, costumes and fonts. `ui/` IDs are
**aliases** declared in the manifest (`ui/verb-coin → costume/0010`, for example), so modders find them by
meaning. An alias can't hold data itself; overriding `ui/verb-coin` overrides what it points at.

## Storage formats

The rule: **the base bundle stores whatever is lossless and cheapest to load; unpack turns it into editable
formats; mods supply modern formats.**

| Asset kind | Base bundle | Unpacked for editing | Accepted in mods |
|---|---|---|---|
| backgrounds, object states, cels, glyphs | indexed PNG (keeps palette indices) | indexed PNG + RGBA PNG | PNG (indexed or RGBA), WebP, SVG |
| z-planes | 1-bit PNG | PNG | PNG at any scale |
| palettes, cycles, walkboxes, scale, room/object data | JSON | JSON | JSON |
| bytecode | raw bytes | `.scumm` decompiled view + raw | raw (later: source) |
| voice, music, sfx | original `iMUS`, decompressed from `COMP` | WAV + region/marker JSON | FLAC, Ogg Vorbis/Opus, WAV |
| video | original SMUSH (`.san`) | PNG frames + WAV + subtitle JSON | WebM/MP4 replacement, side art |

Why indexed PNG for images: COMI's palette tricks (cycling, fades, actor shadows through remap tables, lighting
changes) work on indices. Keeping indices in the base lets the faithful renderer reproduce them exactly. A
truecolor mod image declares `meta.paletteEffects` (below) to say how those effects should be done on it.

Why originals for audio and video: re-encoding the 700 MB of `.bun` files and the `.san` files would be either
lossy or much larger. MMUCS needs decoders for unpacking anyway, so the runtime uses the same ones.

## Container

A bundle (`.mmbundle`) or mod pack (`.mmpack`) is a **ZIP with every entry stored uncompressed**:

- `manifest.json` is the first entry.
- Other entries are at `<id>.<ext>`.
- PNG/FLAC/Ogg are compressed already; storing keeps every entry memory-mappable and seekable, which audio and video
  streaming need.

Any ZIP tool can open one, and a mod pack is just "zip this folder, store only". If profiling ever shows a need,
a binary index can be cached next to it (`base.mmbundle.idx`), rebuilt whenever the bundle's hash changes.

## Manifest

```jsonc
{
  "format": "mmucs-bundle",
  "formatVersion": 1,
  "layer": {
    "kind": "base",                 // "base" | "mod"
    "name": "COMI base",
    "createdBy": "MMUCS 2.3.0",
    "createdAt": "2026-11-22T12:00:00Z"
  },
  "game": {
    "id": "comi",
    "language": "en",
    "release": "cd-1997",           // detected from the files
    "sources": {                    // hashes of the user's files: proves which copy a base came from
      "comi.la0": "sha256:…", "comi.la1": "sha256:…", "comi.la2": "sha256:…"
    }
  },
  "aliases": {
    "ui/verb-coin": "costume/0010"
  },
  "assets": {
    "room/0009/background": {
      "file": "room/0009/background.png",
      "kind": "image",
      "hash": "sha256:…",
      "meta": { "room": 9, "width": 640, "height": 480, "indexed": true,
                "palette": "room/0009/palette/0000", "transparentIndex": 5,
                "name": "cannon" }
    }
  }
}
```

A mod manifest has the same shape, with `"kind": "mod"` and these extra fields:

```jsonc
"layer": {
  "kind": "mod", "name": "Hand-painted pillars", "author": "…", "version": "1.0.0",
  "license": "CC-BY-4.0",
  "requires": { "game": "comi", "release": ["cd-1997"] },
  "priority": 100                   // higher wins among mods; the user can reorder
}
```

### Common entry fields

| Field | Meaning |
|---|---|
| `file` | path inside the container |
| `kind` | `image`, `mask`, `palette`, `data`, `bytecode`, `font`, `text`, `audio`, `video`, `sides` |
| `hash` | content hash, for integrity and dedupe |
| `meta` | kind-specific, below |

### `image` meta

| Field | Meaning |
|---|---|
| `width`, `height` | in **the file's** pixels |
| `scale` | file pixels per game pixel (1 in the base; 4 for a 4× repaint). SVG uses `viewBox` in game pixels |
| `origin` | `{x, y}` anchor in game pixels: cel offset, object position |
| `indexed`, `palette`, `transparentIndex` | for indexed images |
| `paletteEffects` | for truecolor overrides: `"ignore"`, `"shader"` (do cycling/fades on RGB) or `"indexMap"` + `indexMap` (an ID for a separate index image at any scale) |
| `name` | display name, if the game has one |

The engine always works in game coordinates (640×480 room space for COMI). `scale` and `origin` are all the
renderer needs to place any override, at any resolution, without the engine knowing anything about it.

### `sides` meta

```jsonc
{ "left": 104, "right": 104, "blend": 24, "scale": 1,
  "targetAspect": "16:9", "roomHeight": 480,
  "perState": false }               // true: one image per room state, under room/NNNN/sides/NNNN
```

Widths are in game pixels. `perState` handles rooms whose background changes (from alternative backgrounds or
lighting), which a single-image-per-room format can't do.

### `audio` meta (voice, music, sfx)

```jsonc
{ "codec": "imus",                  // base; mods: "flac", "vorbis", "opus", "wav"
  "sampleRate": 22050, "channels": 2, "bits": 16, "frames": 1234567,
  "regions": [ { "id": 0, "start": 0, "length": 441000 } ],
  "jumps":   [ { "from": 441000, "toRegion": 1, "hook": 0, "fadeMs": 0 } ],
  "syncs":   [ { "at": 12345, "data": "…" } ],
  "markers": [ { "at": 0, "text": "…" } ],
  "stop": 1234567 }
```

All positions are in **sample frames**, not bytes, so a replacement in another codec keeps working: a mod that
re-renders a music track has to keep the same region and jump points (unpack writes them to JSON next to the WAV,
and repack checks them).

### `video` meta

```jsonc
{ "codec": "smush", "width": 640, "height": 480, "fps": 15, "frames": 42,
  "audio": { "codec": "iact", "sampleRate": 22050, "channels": 2 },
  "overlay": false,                 // true for SMUSH clips drawn over a room
  "subtitles": "video/opening/subtitles" }
```

A `video/<name>/replacement` adds `"syncTo": "frames"` (match the original frame count, so subtitles, audio and
script waits line up) or `"syncTo": "audio"`.

## Unpack and repack

- **Unpack** (`mmucs unpack <ids or globs> <folder>`) writes editable files plus a sidecar
  `<id>.meta.json` for each one, and template files (for example `room/0009/sides.png`: 848 px wide with the room in
  the middle as a guide).
- **Repack** (`mmucs pack <folder> <out.mmpack>`) reads the sidecars, validates every entry against the base
  (`origin` and `scale` give the same game-space size as the original; z-planes match their image; audio keeps its
  region and jump points), **removes original pixels** from authored side art and templates (the same idea as
  `strip_middle.py` in `scummvm-ai-upscale`), and writes the manifest.
- The **loose folder** takes the same files and sidecars with no repack step, so authors see changes immediately.

## Migration from the current exporter

| Today | Becomes |
|---|---|
| `rooms/009_cannon/background.png` | `room/0009/background` (`meta.name: "cannon"`) |
| `rooms/…/objects/003_cannon-grate_f0.png` (room-local index, name) | `object/NNNN/state/0000` (global ID from `CDHD`) |
| `rooms/…/costumes/akos_00BE5A10/cel_000.png` (file offset) | `costume/NNNN/cel/0000` (number from `DCOS`, already resolved in `RoomActors`) |
| `background_pal1.png` (one PNG per palette) | one indexed background + `room/NNNN/palette/NNNN` |
| `data/rooms/009_cannon.json` | `room/0009/data` |

`RoomModel.TryCreate` already reads the `CDHD` object ID, and `RoomActors` already maps `DCOS` costume numbers, so
the IDs above can be derived from existing code.

## Reference tools

**Every decoder in MMUCS is MMUCS's own**, written to fit MMUCS.Core's structure (`ScummBlock`, the
`*Decoders` classes, `IndexedSurface`), the same way the AKOS and room decoders were. Other projects are where we
learn the formats, and the reference to check our output against. We don't embed or wrap them.

- **Learning sources.** [nutcracker](https://github.com/BLooperZ/nutcracker) (SMUSH frames including codec 47,
  NUT fonts with codec 44, v5–v8 resources) and ScummVM's SCUMM engine (iMUSE, the `.bun` bundles and `COMP`
  codecs, SMUSH audio). Read them to understand a format, then write our own implementation. Don't transliterate
  their code: aim for our own design, and better results where we can find them, as with AKOS palettes and the
  room-background fixes.
- **Oracles.** Tests can decode the same files with a reference tool and with MMUCS and diff the results (pixels,
  PCM samples). Where MMUCS is known to be *more* correct than the reference, the test records that file as an
  expected difference instead of failing.

Gaps the references don't fill for COMI (as of their current sources): nutcracker's SMUSH audio script handles
`PSAD` tracks (the Full Throttle style), not COMI's `IACT` audio, and it has no `.bun` bundle reader. For those,
ScummVM is the only reference.

## Open questions

1. **Music table.** Which track plays for which iMUSE state or sequence isn't in COMI's data files; in ScummVM
   it's a hardcoded engine table. `game/music-table` has to be written by MMUCS from observing the game and
   reading the scripts' iMUSE calls, not ported from ScummVM's source (licence).
2. **Sound effects.** Confirm that COMI's sound effects are all `SOUN` resources in the `.la*` files, and in which
   format.
3. **`language.tab`.** Decode its format and confirm how `/CODE/` strings map to it and to voice files.
4. **SMUSH subtitles and overlays.** Confirm where cutscene text is stored, and which clips draw over a room
   rather than full screen.
5. **Releases.** Fingerprint other COMI releases (other languages, the demo, digital store versions) so `game.release`
   is reliable and mods can declare which ones they support.
6. **Script overrides.** Leave room for a later script-source format under `script/` without changing the
   bytecode IDs.
