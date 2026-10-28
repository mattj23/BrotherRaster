# BrotherRaster

Print to Brother QL and P-touch label printers on the network from .NET, without Brother's driver or SDK.

- **Raster encoders** for the QL-800 series (QL-800, QL-810W, QL-820NWB: DK rolls, including two-color
  black/red) and the PT-E550W / PT-P750W / PT-P710BT (TZe tape, 3.5 to 24 mm).
- **Delivery that does not leave a printer hung.** Jobs go over raw TCP 9100. The transport waits for the
  printer to close the connection, which it does only after reading the whole job.
- **Status over SNMP**, including Brother's 32-byte status block: the model, and the loaded media's type
  and width. Neither family answers a status request on port 9100.
- **A dispatcher** that reads status, retries only jobs that never connected, and never sends one job twice.
- No dependencies in the core package. `BrotherRaster.Skia` adds drawing and PNG helpers, and
  `BrotherRaster.Cli` is a command-line tool.

The encoders are written from Brother's *Raster Command Reference* documents. The QL encoder is tested
byte for byte against [brother_ql](https://github.com/pklaus/brother_ql) 0.9.4. brother_ql runs as a black
box in `tools/brother-ql-oracle`, and none of its code is in this library.

## Packages

| Package | What it has |
|---|---|
| `BrotherRaster` | `LabelBitmap`, `QlRasterEncoder`, `PtRasterEncoder`, `RasterDecoder`, `BrotherSnmpStatusSource`, `RawTcpTransport`, `LabelDispatcher` |
| `BrotherRaster.Skia` | `LabelCanvas` (a canvas sized for a roll or tape), `LabelText` (text fitted to a box, IBM Plex Sans Bold embedded), `LabelImages` (PNG, threshold and dither) |
| `BrotherRaster.Cli` | `brother-raster` dotnet tool: `status`, `print`, `text`, `pattern`, `decode` |

On Linux, add `SkiaSharp.NativeAssets.Linux.NoDependencies` (or another native assets package) next to
`BrotherRaster.Skia`.

## Printing a dated sticker on a P-touch

```csharp
using BrotherRaster.Printing;
using BrotherRaster.Pt;
using BrotherRaster.Skia;
using BrotherRaster.Status;
using SkiaSharp;

var tape = PtTape.TZe12;
using var canvas = LabelCanvas.ForPt(tape, lengthDots: 300);   // 300 dots at 180 dpi = 42 mm
LabelText.DrawFitted(canvas.Canvas, new SKRect(6, 6, canvas.Width - 6, canvas.Height - 6),
    ["CAL 2026-10-21", "DUE 2027-10-21"]);

var job = PtRasterEncoder.Encode(canvas.ToLabel(), new PtRasterOptions { Tape = tape });

var host = "192.168.1.45";
var dispatcher = new LabelDispatcher(
    new RawTcpTransport(new RawTcpTransportOptions { Host = host }),
    new BrotherSnmpStatusSource(host) { ExpectedWidthMm = tape.WidthMm });
var outcome = await dispatcher.DispatchAsync(job);
Console.WriteLine($"{outcome.Result}: {outcome.Message}");
```

A QL label works the same way with `LabelCanvas.ForQl(QlMedia.Continuous62, lengthDots)` and
`QlRasterEncoder.Encode(label, new QlRasterOptions { Media = QlMedia.Continuous62 })`.

### Which way round

- **QL:** the bitmap is the label as read with the tape coming out top first. Its width is the roll's
  `PrintableDots` (696 on 62 mm) at 300 dpi, and its height is the length.
- **P-touch:** the bitmap is the label as read along the tape. Its height is the tape's `PrintablePins`
  (70 on 12 mm) at 180 dpi, and its width is the length (at 360 dpi with `HighResolution`).

## The command-line tool

```sh
dotnet tool install -g BrotherRaster.Cli
brother-raster status 192.168.1.45
brother-raster text 192.168.1.45 "CAL 2026-10-21" "DUE 2027-10-21"
brother-raster print 192.168.1.50 label.png --ql 62mm
brother-raster pattern - --pt 24mm --out job.bin --png preview.png    # no printer needed
brother-raster decode job.bin --png preview.png
```

With no `--ql` or `--pt`, the tool asks the printer what is loaded.

## Printer setup

- **Command Mode must be Raster** (web page → Printer Settings → Device Settings). New printers often ship
  in P-touch Template mode, and in that mode raster jobs print nothing. On some units the settings pages
  need the admin password printed on the printer's label.
- **SNMP v1 with community `public`** must be on for status. It is on by default.
- **Never resend a job the printer accepted.** A QL-810W with the cover open or no media, and a PT-P750W
  with the cover open, hold the job and print it once the fault is cleared, so resending gives two labels.
  Set `LabelDispatchOptions.PrinterHoldsJobsOnFault = false` to send nothing to a faulted printer.
- **Use the right job for the roll.** On a QL-8xx, a one-color job on the DK-22251 black/red roll (or a
  two-color job on plain tape) leaves the printer in ERROR with the red light blinking. Only switching it
  off and on clears that, and the job is lost. Send black/red rolls `TwoColor = true`.

See [docs/hardware.md](https://github.com/mattj23/BrotherRaster/blob/main/docs/hardware.md) for what has been verified on which printer.

## What has been verified

| | QL-810W | PT-P750W |
|---|---|---|
| Encoder, byte for byte against brother_ql | 62 mm, 29 mm, 12 mm, 62 x 29 die-cut, two-color | n/a (brother_ql has no PT support) |
| Encoder against the reference's own examples | | yes |
| Printed on hardware | 62 mm, 62 mm black/red | 12 mm laminated |
| SNMP status and status block | yes | yes (except the tape color byte: see docs/hardware.md) |

Rolls and tapes not marked as verified come from the reference tables. Please report what you find.

## License

MIT. `BrotherRaster.Skia` embeds IBM Plex Sans Bold under the SIL Open Font License 1.1
(`OFL-IBMPlex.txt`). Brother, P-touch and the model names are trademarks of Brother Industries, Ltd.
This project is not affiliated with Brother.
