---
name: windowsdetox-plugin-icon
description: Design and validate the `icon` path data in a WindowsDetox plugin.json (sidebar, widget header and widget-picker rail icon). Use when asked to draw, change or review a plugin "icon", "ikona", "ikonka", "logo wtyczki", SVG path, "glyph", "UiGlyph", or when an icon looks blurry, thin, off-center or too similar to another plugin. Includes preview_icon.py to rasterize and validate locally.
---

# Plugin icon (`plugin.json` -> `"icon"`)

The manifest `icon` is one string of SVG path data. The host draws it as a filled, single-colour shape. Same data format as `UiGlyph.PathData`.

## What the host accepts (`UiTreeValidator.IsValidPathData`, also checked by `PluginManifestParser`)

- 1 to 4096 characters, not whitespace-only. Every character must be a digit, space, `,` `.` `-` `+` `e` `E`, or one of the commands `M m L l H h V v C c S s Q q T t A a Z z`.
- Nothing else: no `F0/F1` fill-rule prefix, no `<svg>`/`<path>` markup, no attributes, no `;`, no letters outside that list. An invalid icon fails manifest parsing, so the plugin does not install. The validator checks characters only, not syntax; a syntax error is dropped at render time (the sidebar falls back to a puzzle-piece icon), so always run the preview script.
- Existing icons (`plugins/*/plugin.json`) use only `M A L H V Z`, 150-170 characters. Aim for under ~400.

## How it is drawn (read before designing)

- `src/Blocker.UI/Views/PluginPathIcon.cs`: `Geometry.Parse(pathData)`, `Stretch.Uniform` into a 16 x 16 px square at 0.85 opacity in the navigation rail, `Fill` = the item's foreground brush (follows selected/hover/disabled states and theme). The widget header uses 14 px and the widget picker rail 24 px, also at 0.85 opacity (same data, same fill); the lighter rendering keeps a filled shape from outweighing the thin built-in line icons.
- Consequences:
  1. Fill only. Strokes do not exist; a "line" must be a closed outline polygon (a ring is two arcs, a bar is `H`/`V` rectangle).
  2. Default fill rule is even-odd (`Geometry.Parse` without `F1`). Overlapping subpaths punch holes; a subpath inside another is a hole. Never overlap shapes you want solid: keep them disjoint, or draw holes deliberately.
  3. `Stretch.Uniform` scales the geometry's bounding box to the square, so the icon always fills its box, regardless of coordinates. Centered = bounding box centered. Do not rely on padding in coordinates.
  4. Single colour: no gradients, no tones, no opacity. Meaning must come from silhouette and holes.

## Coordinate convention

Existing icons draw in a 0..100 box with a 4..96 safe area (bounding boxes 4.0-96.0, center 50,50). Keep to it: it makes sizes comparable across plugins and previews. Use 1-2 decimals at most.

## Design rules for 18 px

Scale: 1 px at 18 px = 5.6 units.
- Stroke weight: outlines >= 10 units thick (about 1.8-2 px at 18 px); 8 units (1.4 px) is the existing minimum and already looks light; never go below 8. Gaps between shapes >= 8 units, or they close up at 14 px.
- Counters (holes): >= 12 units wide.
- No fine detail: at most 3 simple shapes or about 8-12 path segments; no text, no hairlines, no dots smaller than 10 units, no shading or tiny serifs.
- Corners: sharp corners are fine; rounded caps need `A` arcs of radius >= 5.
- Optical balance: equal visual mass left/right and top/bottom around (50,50); circles may overshoot square shapes by 2-3 units, thin diagonals look lighter than bars so thicken them. Check with the 18 px zoom, not the 96 px one.
- Every plugin logo sits INSIDE A CIRCLE (required house style): a ring (about 8-10 units wide, open or closed, outer radius about 46 around (50,50)) with one symbol inside that does not touch it (gap >= 8). Existing family: an open ring (gap at top-right) with a symbol. A new plugin keeps the ring but needs a different symbol and preferably a different ring gap, so the sidebar stays scannable. One idea per symbol (checkmark, bars, tree...).
- It must work in light and dark themes (foreground fill), and must not rely on being coloured.

## Workflow

1. Sketch in the 4..96 box with `M L H V A Z` (arcs: `A rx ry rot large-arc sweep x y`; the ring in the bundled icons is two arcs + `Z`).
2. Preview and validate:
   ```
   python3 .claude/skills/windowsdetox-plugin-icon/preview_icon.py plugins/<Folder>/plugin.json --out /tmp/icon-preview
   python3 .claude/skills/windowsdetox-plugin-icon/preview_icon.py "M10 10 H90 V90 H10 Z"
   ```
   The script (stdlib only) validates length and characters exactly like the C# validator, checks command syntax, prints bounding box and centre, writes `icon.svg`, `icon-18.png`, `icon-24.png`, `icon-96.png` and `icon-sheet.png` (light panel, dark panel, 18 px enlarged 8x; mimics even-odd and Stretch.Uniform), and exits 1 on invalid input. Open the PNGs (Read tool) and judge the 18 px version.
3. Uniqueness: the script compares against `--compare` (default `plugins/*/plugin.json`) and prints the raster overlap with each: IDENTICAL or > 75% overlap means redraw. Existing pair overlaps about 67%; stay well below. `BundledPluginTests` also requires distinct icon strings and valid path data per bundled plugin; a new bundled plugin needs its own assertion there.
4. Paste the string into `plugin.json` as one line, keep `"icon"` out of capability reasoning (no capability needed), and re-run the preview from the final file.

## Checklist

- [ ] Valid per `IsValidPathData` (script says `valid`), 4096 max, preferably < 400 chars.
- [ ] Bounding box about 4..96, centre about (50,50), reads at 18 px, 14 px and 24 px.
- [ ] Strokes >= 10 units (>= 8 absolute minimum), gaps >= 8, no overlapping solids (even-odd).
- [ ] Symbol inside a ring (circle), not touching it.
- [ ] One colour, no detail, one idea; distinct from every other plugin icon.
