<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/icon.svg" />
    <img src="assets/generated/icon-black.svg" alt="MMUCS" width="200" height="auto" />
  </picture>
</p>

<h1 align="center">MMUCS</h1>
<h3 align="center">Modular Media Unpacker Content Studio</h3>

<p align="center"> A modular environment for the analysis, extraction, and visualization of SCUMM V8 titles. </p>

<p align="center">  
  <a href="https://github.com/haywirephoenix/MMUCS/releases"> 
  <!-- <a href="https://github.com/haywirephoenix/MMUCS/issues"> <img alt="GitHub Issues" src="https://img.shields.io/github/issues/haywirephoenix/MMUCS?style=for-the-badge"> </a> -->
  <a href="https://github.com/haywirephoenix/MMUCS/blob/main/LICENSE"> <img alt="GitHub License" src="https://img.shields.io/badge/License-PolyForm_Noncommercial_1.0.0-orange?style=for-the-badge"></a>
  <a href="https://github.com/haywirephoenix/MMUCS/releases/latest">
  <img alt="Download Latest Release" src="https://img.shields.io/github/v/release/haywirephoenix/MMUCS?include_prereleases&label=Download&style=for-the-badge&color=brightgreen"/>
</a>
</a>
</p>

## Overview

MMUCS (Modular Media Unpacker Content Studio) is a native .NET desktop tool (Avalonia UI + SkiaSharp) designed to deconstruct, visualize, and analyze SCUMM-based media architectures.

Why I Built This
I originally built MMUCS to aid my own pursuit of modding The Curse of Monkey Island (COMI), with the ultimate goal of creating extended background art and higher-resolution sprites. Because I focused and tested exclusively within the context of COMI (SCUMM v8), the scope and codebase have remained slim.

As a result, it will most likely not work as expected with other SCUMM games out of the box—though adapting it to support other titles is entirely possible. I'm sharing this prematurely with the community by request, so please keep in mind that it is by no means complete!

---

### Releases

Download releases here:

<a href="https://github.com/haywirephoenix/MMUCS/releases/latest">
  <img alt="Download Latest Release" src="https://img.shields.io/github/v/release/haywirephoenix/MMUCS?include_prereleases&label=Download&style=for-the-badge&color=brightgreen"/>
</a>

The legacy Godot build is on the `godot` branch (previously `master`). 


### Building from source (Avalonia frontend)

Requires the .NET 8 SDK (or newer).

```bash
dotnet run --project src/MMUCS.Desktop -c Release -- "path/to/COMI.LA1"
```

The file argument is optional; you can also use **File ▸ Open…** or drag a file onto the window.

| Project | Purpose |
|---|---|
| `src/MMUCS.Core` | UI-agnostic SCUMM v8 parsing, decoding, caches and PNG export. No framework dependencies. |
| `src/MMUCS.Desktop` | Avalonia 12 desktop host. Skia viewports (`ICustomDrawOperation`) for rooms and AKOS cels, SkSL palette shader, virtualized hex inspector. |

Decode caches live in `%LocalAppData%/MMUCS/cache` (`~/.local/share/MMUCS` on Linux). Crash reports go to `crash.log` in the same folder.

**Viewport controls:** mouse wheel zooms around the cursor; middle- or right-drag (or Space + left-drag) pans; double-click or `F` fits; `1` shows actual pixels.

### Making a release

1. Bump `<Version>` in `Directory.Build.props` and push.
2. On GitHub: **Actions ▸ Release ▸ Run workflow**, choose the branch, and leave *pre-release* ticked or untick it.

The workflow builds self-contained packages for Windows, Linux and macOS (arm64 + x64), tags the commit with the version, and publishes a release whose notes list every commit since the previous tag (`- <hash> <subject>`).

---

## Usage

### Loading Resources
1.  Launch the application.
2.  Open a resource file (eg: `COMI.LA0`) from your game directory. MMUCS will automatically find the other `.LA` files. 
4.  The `ScummResourceParser` will begin indexing the file, and the `BlockHierarchyPanel` will populate with detected data segments.

### Visualizing
1.  Within the `BlockHierarchyPanel`, browse through the tree. You can navigate with the keyboard or mouse.
2.  When an `LFLF` or `ROOM` block is selected, the `RoomPreviewPanel` will automatically populate.
3. Inside an `LFLF` block, you will often find `AKOS` blocks. Selecting one of those will automatically populate the `AkosViewerPanel` where you can cycle through the cels.

---

## Contributing

Contributions welcome! Whether you are an expert in legacy formats or a UI/UX designer, your input is valuable. 
 
For any SCUMM veterans out there, please verify my metadata descriptions in [ScummTag](scripts/OS/ScummUtils/ScummTag.cs) - Some are my best guess. 
Also see [ScummMeta](scripts/OS/ScummUtils/ScummMeta.cs) as the new type-safe format which may need expanding. 

Please let me know if I missed any decoding formats in Scumm V8 - feel free to add more decoders.

---

## License

This project is licensed under the **PolyForm Noncommercial License 1.0.0** - see the [LICENSE.md](LICENSE.md) file for complete details.
