# A mods repository for MMUCS

A GitHub Pages site that lists mods for MMUCS's Mods view to install. MMUCS reads `mods.json`; `index.html` shows the same list to people browsing.

```
mods.json                     the list MMUCS reads
index.html                    the page people see
zips/<id>-<version>.zip       each mod's folder, zipped
.nojekyll                     serve the files as they are
```

## Start your own

1. Make a new repository on GitHub and copy this folder's files into it. Call it `<you>.github.io` for the site to be at `https://<you>.github.io/`; any other name serves it at `https://<you>.github.io/<name>/`.
2. In the repository's **Settings ▸ Pages**, serve from the main branch's root folder.
3. In MMUCS: open the game window, press **Mods**, and in **Settings** set the repository to `https://<you>.github.io/<name>/mods.json`. **Test** reads it.

## Add or update a mod

Mods are folders under `_mods` beside the game, each with a `mod.json` (see "Mods" in MMUCS's `tools/mprobe/README.md`). Give the `mod.json` what the list shows:

```json
{ "enabled": true, "name": "Wide screen", "version": "1.1.0", "category": "Images",
  "description": "Haggis' caber reaches past the edge of the 640 screen." }
```

Then, from MMUCS's source:

```bash
mprobe mods --strip --out shared             # a copy of each mod without the game's own art or music
mprobe mods --pack shared --to <your repo>   # zips them into zips/ and lists them in mods.json
```

Commit and push. MMUCS shows an update when a mod's `version` is newer than the installed one.

`--strip` turns object images and costume cels into overlays drawn over the player's own game, and leaves music mods' audio out, so the zips hold only your work. It skips hi-res mods, whose pictures are the game's art upscaled: share how you made those instead. Each `mods.json` entry has the zip's SHA-256, and MMUCS checks every download against it.

## mods.json

```json
{ "name": "My MMUCS mods",
  "mods": [
    { "id": "WideScreenMod", "name": "Wide screen", "category": "Images", "version": "1.1.0",
      "description": "…", "zip": "zips/WideScreenMod-1.1.0.zip", "sha256": "…", "size": 114845 } ] }
```

- `id` is the mod's folder under `_mods`.
- `zip` is relative to `mods.json`, or a full address (a GitHub release's asset, say).
- `category` is `Music`, `Images`, `Hi-res` or `Other`. The Mods view's Music and Images switches gate those kinds.

A zip may hold the mod's folder (`WideScreenMod/mod.json`, …) or just its files. Installing replaces the folder that's there. A mod that's off stays off.
