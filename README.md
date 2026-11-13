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

MMUCS (Modular Media Unpacker Content Studio) is a native .NET desktop tool (Avalonia UI + SkiaSharp) for exploring, viewing and exporting the data of SCUMM v8 games.

- **Blocks, metadata and hex:** every block in the game's files, with its fields and bytes
- **Rooms:** backgrounds, objects, hotspots, walkboxes, and the costumes the room's scripts place
- **Costumes:** cels, palettes and shading, animations in 4 or 8 directions, and Play in place in the room
- **Scripts:** decompiled to readable SCUMM source with syntax colours and named variables
- **Export:** backgrounds, objects, costume cels and scripts in bulk

### Why I built this

I originally built MMUCS to aid my own pursuit of modding The Curse of Monkey Island (COMI), with the ultimate goal of creating extended background art and higher-resolution sprites. Because I focused and tested exclusively within the context of COMI (SCUMM v8), the scope and codebase have remained slim.

As a result, it will most likely not work as expected with other SCUMM games out of the box, though adapting it to support other titles is entirely possible. I'm sharing this prematurely with the community by request, so please keep in mind that it is by no means complete!

## Documentation

The **[wiki](https://github.com/haywirephoenix/MMUCS/wiki)** covers everything:

| | |
|---|---|
| [Getting Started](https://github.com/haywirephoenix/MMUCS/wiki/Getting-Started) | Downloading, opening a game, and where MMUCS keeps its files |
| [Interface](https://github.com/haywirephoenix/MMUCS/wiki/Interface) | Panels, menus, layouts and keyboard shortcuts |
| [Room Viewer](https://github.com/haywirephoenix/MMUCS/wiki/Room-Viewer) · [Costume Viewer](https://github.com/haywirephoenix/MMUCS/wiki/Costume-Viewer) · [Script Viewer](https://github.com/haywirephoenix/MMUCS/wiki/Script-Viewer) | The viewers |
| [Exporting](https://github.com/haywirephoenix/MMUCS/wiki/Exporting) | The export wizard and what it writes |
| [Naming Variables](https://github.com/haywirephoenix/MMUCS/wiki/Naming-Variables) | Helping name the game's script variables, with the mscripts tool |
| [Building from Source](https://github.com/haywirephoenix/MMUCS/wiki/Building-from-Source) | Building, testing and making a release |
| [Contributing](https://github.com/haywirephoenix/MMUCS/wiki/Contributing) | Where help is most useful |

## Download

<a href="https://github.com/haywirephoenix/MMUCS/releases/latest">
  <img alt="Download Latest Release" src="https://img.shields.io/github/v/release/haywirephoenix/MMUCS?include_prereleases&label=Download&style=for-the-badge&color=brightgreen"/>
</a>

Self-contained builds for Windows, Linux and macOS. The legacy Godot build is on the `godot` branch.

To build it yourself, you need the .NET 8 SDK or newer:

```bash
dotnet run --project src/MMUCS.Desktop -c Release -- "path/to/COMI.LA1"
```

## Contributing

Contributions are welcome, whether you know old formats well or design interfaces. The most open way to help is [naming the game's script variables](https://github.com/haywirephoenix/MMUCS/wiki/Naming-Variables), which needs no C#. See [Contributing](https://github.com/haywirephoenix/MMUCS/wiki/Contributing) for more.

## License

This project is licensed under the **PolyForm Noncommercial License 1.0.0** - see the [LICENSE.md](LICENSE.md) file for complete details.
