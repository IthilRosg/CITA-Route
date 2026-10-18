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

Use flat square frames, a 220 px navigation rail, numbered sections, generous
headings, Consolas for telemetry and Segoe UI for descriptive text. The large
headings use Bahnschrift SemiCondensed with Windows font fallback. The application
icon combines the panel's red square frame with the route glyph.

Brand red must not turn successful diagnostics into errors. State always has a
text label alongside colour. Preserve keyboard focus outlines, scrollable tables,
wrapping descriptions and readable popup backgrounds. Avoid flicker or animations
over operational information.

Preview every page using `RoutePilot.exe --render-all` and the minimum-size matrix
using `RoutePilot.exe --render-all --compact`. Images are local artifacts and may
contain real connection metadata; do not publish live captures. The renderer does
not change routes or save user preferences.
