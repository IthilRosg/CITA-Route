# Sector Console visual language

CITA Route follows [AWG CITA's sector console](https://github.com/IthilRosg/AWG-CITA/blob/main/awg_cita/static/sector-console.css), with SIGNALIS as the family's visual reference.

| Role | Colour |
| --- | --- |
| Background | `#0c0c0d` |
| Panel / strong panel | `#151416` / `#1b191b` |
| Frame / divider | `#3d3839` / `#282426` |
| Main / secondary text | `#f0eadb` / `#aaa297` |
| Brand / primary action | `#e8443f` |
| Verified healthy state | `#97c59d` |
| Waiting / recovery | `#d7ad67` |
| Direct / upload data | `#86a9c8` |

Use flat square frames, Consolas for telemetry and Segoe UI for descriptive text.
The advanced view retains the full layout: a 220 px navigation rail, numbered
sections and generous headings in Bahnschrift SemiCondensed with Windows font
fallback. The application icon combines the panel's red square frame with the
route glyph.

The planned standard view uses a separate compact window: connection state,
profile name, one main action, small traffic/session statistics and a fixed
status bar for the current operation. Keep navigation rails, large section
headings, tables and diagnostic panels in the advanced view. Preserve the same
palette, focus treatment and readable text in both. Save bounds separately;
validate compact sizing against the available work area and DPI rather than
inheriting the full window's minimum size. See the staged UI requirements in
[the reliability plan](ROUTING-RELIABILITY-PLAN.md#этап-9-стандартный-и-расширенный-интерфейс).

Brand red must not turn successful diagnostics into errors. State always has a
text label alongside colour. Preserve keyboard focus outlines, scrollable tables,
wrapping descriptions and readable popup backgrounds. Avoid flicker or animations
over operational information.

Preview every page using `RoutePilot.exe --render-all` and the minimum-size matrix
using `RoutePilot.exe --render-all --compact`. Images are local artifacts and may
contain real connection metadata; do not publish live captures. The renderer does
not change routes or save user preferences.
