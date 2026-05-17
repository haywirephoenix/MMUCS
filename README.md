<p align="center">
  <img src="https://raw.githubusercontent.com/haywirephoenix/MMUCS/refs/heads/master/icon.svg" alt="MMUCS" width="200" height="auto" style="filter: drop-shadow(0px 2px 8px rgba(0, 0, 0, 0.3));" />
</p>

<h1 align="center">MMUCS</h1>
<h3 align="center">Modular Media Unpacker Content Studio</h3>

<p align="center"> A modular environment for the analysis, extraction, and visualization of legacy media formats and SCUMM engine assets. </p>

<p align="center">  
  <a href="https://github.com/haywirephoenix/MMUCS/releases"> 
  <!-- <a href="https://github.com/haywirephoenix/MMUCS/issues"> <img alt="GitHub Issues" src="https://img.shields.io/github/issues/haywirephoenix/MMUCS?style=for-the-badge"> </a> -->
  <a href="https://github.com/haywirephoenix/MMUCS/blob/main/LICENSE"> <img alt="GitHub License" src="https://img.shields.io/badge/License-PolyForm_Noncommercial_1.0.0-orange?style=for-the-badge"></a>
  <img alt="Download Latest Release" src="https://img.shields.io/github/v/release/haywirephoenix/MMUCS?include_prereleases&label=Download&style=for-the-badge&color=brightgreen">
</a>
</p>

## Table of Contents

- [Overview](#-overview)
- [Key Features](#-key-features)
- [Getting Started](#-getting-started)
- [Usage](#-usage)
- [Contributing](#-contributing)
- [License](#-license)

---

## Overview


MMUCS (Modular Media Unpacker Content Studio) is a Godot-powered tool designed to deconstruct, visualize, and analyze SCUMM-based media architectures.

Why I Built This
I originally built MMUCS to aid my own pursuit of modding The Curse of Monkey Island (COMI), with the ultimate goal of creating extended background art and higher-resolution sprites. Because I focused and tested exclusively within the context of COMI (SCUMM v8), the scope and codebase have remained slim.

As a result, it will most likely not work as expected with other SCUMM games out of the box—though adapting it to support other titles is entirely possible. I'm sharing this prematurely with the community by request, so please keep in mind that it is by no means complete!

---

## Key Features

### Inspect
*   **Block Hierarchy Visualization:** Navigate the internal structural tree of media files using the `BlockHierarchyPanel`, allowing you to see how data segments are nested and organized.
*   **Metadata Analysis:** Instantly access embedded headers, tags, and descriptive data via the `MetadataPanel`, providing context to the raw binary streams.
*   **Raw Data Transparency:** Use the integrated `HexPanel` for low-level binary inspection when precision is required at the byte level.

### Visualize
*   **AKOS Viewer:** A dedicated `AkosViewerPanel` for inspecting and playing back legacy AKOS animation data, complete with specialized caching (`AkosCelCache`) for smooth performance.
*   **Room & Background Preview:** Visualize spatial assets and environments through the `RoomPreviewPanel`, utilizing a `ScummBackgroundCache` to ensure rapid asset switching and high responsiveness.
*   **Indexed Rendering Pipeline:** Specialized `IndexedRenderer` and `IndexedSurface` utilities facilitate the accurate representation of palette-indexed legacy graphics on modern displays.

### Flow
*   **Floating UI System:** A flexible interface utilizing `FloatingPanel` and `WindowManager` that allows users to customize their layout, docking or undocking tools as needed for their specific workflow.
*   **Dynamic Theming:** Switch between aesthetics with the `Options Window`, supported by custom styleboxes and shaders.
*   **Zoomable Viewports:** High-precision inspection of visual assets is made possible through `ZoomableViewport` utilities, ensuring every pixel can be scrutinized.



## Getting Started

Download the latest release here :

<img alt="Download Latest Release" src="https://img.shields.io/github/v/release/haywirephoenix/MMUCS?include_prereleases&label=Download&style=for-the-badge&color=brightgreen"/></p>


---

## Usage

### Loading Resources
1.  Launch the application from the Godot editor or exported binary.
2.  The file dialog will automatically appear.
3.  Navigate to the root of your extracted COMI directory and chose any .LA0 file.
4.  The `ScummResourceParser` will begin indexing the file, and the `BlockHierarchyPanel` will populate with detected data segments.

### Visualizing
1.  Within the `BlockHierarchyPanel`, browse through the tree. You can navigate with the keyboard or mouse.
2.  When an `LFLF` or `ROOM` block is selected, the `RoomPreviewPanel` will automatically populate.
3. Inside an `LFLF` block, you will often find `AKOS` blocks. Selecting one of those will automatically populate the `AkosViewerPanel` where you can cycle through the cels.


### Customizing the UI
*   **Rearrange Windows:** Click and drag any `FloatingPanel` to rearrange your workspace. Grab its edges to resize.
*   **Adjust Appearance:** Navigate to the `OptionsPanel` to modify theme settings via the `ThemeManager`.
*   **Inspect Raw Data:** Select any block to see its byte-level representation in the `HexPanel` or its metadata in the `MetadataPanel`.

---

## Contributing

Contributions welcome! Whether you are an expert in legacy formats or a UI/UX designer, your input is valuable. 
 
For any SCUMM veterans out there, please verify my metadata descriptions in [ScummTag](scripts/OS/ScummUtils/ScummTag.cs) - Some are my best guess. 
Also see [ScummMeta](scripts/OS/ScummUtils/ScummMeta.cs) as the new type-safe format which may need expanding. 

Please let me know if I missed any decoding formats in Scumm V8 - feel free to add more decoders.

---

## Todo/Ideas

- Add export functionality - decoding is done.
- Add OBIM decoding and display in Room panel
- Add hotspots etc to Room panel
- Update UI config - not all settings persist yet, new config changes pedning
- Improve UI flow and reliability = window size and position can be stored, it's disabled for this release
- Unify base window into own scene, and make each window type a variant
- Maybe make Akos animations playable - instructions are decoded
- Add more wallpapers
- Script decoder
- SCUMM Runner

---

## License

This project is licensed under the **PolyForm Noncommercial License 1.0.0** - see the [LICENSE.md](LICENSE.md) file for complete details.

### What this means:

- ❌ **Commercial use:** You cannot use this project or its code for commercial purposes or financial gain.
- ✅ **Modification:** You can modify the code to suit your specific unpacking and SCUMM data-exploration needs.
- ✅ **Distribution:** You can distribute this software freely for non-commercial purposes.
- ✅ **Hobby & Personal use:** You can use this project privately for hobby projects, amateur pursuits, research, or gaming preservation.
- ⚠️ **Liability:** The software is provided "as is", without warranty of any kind.
- ⚠️ **Patent Defense:** If you make a patent infringement claim against this software, your license ends immediately.
---


<p align="center">
  <a href="#">⬆️ Back to Top</a>
</p>
