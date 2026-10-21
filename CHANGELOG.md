# Changelog

## 0.1.0 (unreleased)

First release.

- QL-800 series raster encoder: continuous and die-cut DK rolls, one- and two-color jobs, optional PackBits.
- PT-E550W/P750W/P710BT raster encoder: TZe tape 3.5 to 24 mm, cut, half cut, chain printing,
  high resolution, mirror.
- One decoder for both families' jobs.
- SNMPv1 status: the standard printer MIB plus Brother's 32-byte status block (model, media type and
  width; the P-touch color bytes are exposed raw).
- Raw TCP transport that waits for the printer to take the whole job; file and serializing transports;
  a dispatcher that never sends a job twice.
- `BrotherRaster.Skia`: canvases sized for a roll or tape, fitted text, PNG, threshold and dither.
- `BrotherRaster.Cli`: `brother-raster status | print | text | pattern | decode`.
