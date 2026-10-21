// brother-raster: Brother QL and P-touch label printers from the command line.
//
//   brother-raster status  <host>                          model, media, condition and the raw values
//   brother-raster print   <host> <image>   [media] [job]  print an image already sized for the media
//   brother-raster text    <host> <line>... [media] [job]  print lines of text, as large as they fit
//   brother-raster pattern <host>           [media] [job]  print an orientation test pattern
//   brother-raster decode  <job.bin> [--png out.png]       list a job's commands and preview it
//
// media: --ql <roll> (12mm, 29mm, 62mm, "62mm x 29mm", ...) or --pt <tape> (3.5mm ... 24mm). Without
//        either, the printer's status block says what is loaded.
// job:   --two-color (QL black/red roll)  --compress  --no-cut  --half-cut  --chain  --high-res
//        --mirror  --margin <dots>  --threshold <0-255>  --dither  --length-mm <mm> (text)
//        --out <file.bin>: write the job instead of sending it ("-" for the host then needs media).

using BrotherRaster;
using BrotherRaster.Printing;
using BrotherRaster.Pt;
using BrotherRaster.Ql;
using BrotherRaster.Skia;
using BrotherRaster.Status;
using SkiaSharp;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Usage());
    return args.Length == 0 ? 2 : 0;
}

try
{
    return args[0] switch
    {
        "status" => await Status(Positional(1, "host")),
        "print" => await Print(Positional(1, "host"), media => LoadImage(Positional(2, "image"), media)),
        "text" => await Print(Positional(1, "host"), media => TextLabel(Positionals(2), media)),
        "pattern" => await Print(Positional(1, "host"), Pattern),
        "decode" => Decode(Positional(1, "job.bin")),
        _ => throw new UsageException($"unknown command '{args[0]}'"),
    };
}
catch (UsageException e)
{
    Console.Error.WriteLine($"brother-raster: {e.Message}\n\n{Usage()}");
    return 2;
}
catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine($"brother-raster: {e.Message}");
    return 1;
}

async Task<int> Status(string host)
{
    var source = new BrotherSnmpStatusSource(host);
    var snapshot = await source.ReadSnapshotAsync();
    var status = StatusInterpreter.Interpret(snapshot);
    Console.WriteLine($"{host}: {status.Condition}. {status.Message}");
    if (snapshot is null)
    {
        return 1;
    }

    if (snapshot.StatusBlock is { } block)
    {
        Console.WriteLine($"  model    {block.Model}");
        Console.WriteLine($"  media    {block.DescribeMedia()}");
        if (block.Series == BrotherSeries.Pt)
        {
            Console.WriteLine($"  colors   tape 0x{block.TapeColor:X2}, text 0x{block.TextColor:X2} (raw; not reliable, see docs/hardware.md)");
        }
        Console.WriteLine($"  errors   0x{(byte)block.Error1:X2} 0x{(byte)block.Error2:X2} ({block.Error1}, {block.Error2})");
        Console.WriteLine($"  block    {Convert.ToHexString(block.Raw)}");
    }
    else
    {
        Console.WriteLine("  (no Brother status block over SNMP)");
    }

    Console.WriteLine($"  display  '{snapshot.Display?.Trim()}', media name '{snapshot.MediaName?.Trim()}'");
    Console.WriteLine($"  MIB      device {snapshot.DeviceStatus}, printer {snapshot.PrinterStatus}, error bits 0x{snapshot.ErrorBits:X2}, cover {snapshot.CoverStatus}, input {snapshot.InputStatus}");
    return status.IsReady ? 0 : 1;
}

async Task<int> Print(string host, Func<Media, LabelBitmap> makeLabel)
{
    var media = await ResolveMedia(host);
    var label = makeLabel(media);
    var job = media.Encode(label);
    Console.WriteLine($"{media.Describe()}: label {label.Width} x {label.Height} dots, job {job.Length} bytes");

    if (Option("--out") is { } outPath)
    {
        File.WriteAllBytes(outPath, job);
        if (Option("--png") is { } png)
        {
            File.WriteAllBytes(png, LabelImages.ToPng(label));
        }

        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    var transport = new RawTcpTransport(new RawTcpTransportOptions { Host = host });
    var status = new BrotherSnmpStatusSource(host) { ExpectedWidthMm = media.WidthMm };
    var outcome = await new LabelDispatcher(transport, status).DispatchAsync(job);
    Console.WriteLine($"{outcome.Result}: {outcome.Message}");
    return outcome.Result is LabelDispatchResult.Printed or LabelDispatchResult.Held ? 0 : 1;
}

async Task<Media> ResolveMedia(string host)
{
    if (Option("--ql") is { } roll)
    {
        var media = QlMedia.All.FirstOrDefault(m => Same(m.Name, roll))
            ?? throw new ArgumentException($"no QL roll '{roll}'; one of: {string.Join(", ", QlMedia.All.Select(m => m.Name))}");
        return new QlJob(media, QlOptions(media));
    }

    if (Option("--pt") is { } width)
    {
        var tape = PtTape.All.FirstOrDefault(t => Same(t.Name, width))
            ?? throw new ArgumentException($"no TZe tape '{width}'; one of: {string.Join(", ", PtTape.All.Select(t => t.Name))}");
        return new PtJob(tape, PtOptions(tape));
    }

    if (host == "-")
    {
        throw new UsageException("without a printer to ask, give --ql or --pt");
    }

    var block = (await new BrotherSnmpStatusSource(host).ReadSnapshotAsync())?.StatusBlock
        ?? throw new InvalidOperationException($"{host} did not report what is loaded; give --ql or --pt.");
    return block.Series switch
    {
        BrotherSeries.Ql => QlMedia.Find(block.MediaWidthMm, block.MediaLengthMm) is { } m
            ? new QlJob(m, QlOptions(m))
            : throw new InvalidOperationException($"{block.Model} has {block.DescribeMedia()} loaded, which is not in the QL roll table."),
        BrotherSeries.Pt => PtTape.Find((PtMediaType)block.MediaType, block.MediaWidthMm) is { } t
            ? new PtJob(t, PtOptions(t))
            : throw new InvalidOperationException($"{block.Model} has {block.DescribeMedia()} loaded, which is not in the TZe table."),
        _ => throw new InvalidOperationException($"{host} reports an unknown model ({block.Model})."),
    };
}

QlRasterOptions QlOptions(QlMedia media) => new()
{
    Media = media,
    Cut = !Flag("--no-cut"),
    Compress = Flag("--compress"),
    TwoColor = Flag("--two-color"),
    MarginDots = Option("--margin") is { } m ? int.Parse(m) : 35,
};

PtRasterOptions PtOptions(PtTape tape) => new()
{
    Tape = tape,
    Cut = !Flag("--no-cut"),
    HalfCut = Flag("--half-cut"),
    Chain = Flag("--chain"),
    HighResolution = Flag("--high-res"),
    Mirror = Flag("--mirror"),
    MarginDots = Option("--margin") is { } m ? int.Parse(m) : null,
};

LabelBitmap LoadImage(string path, Media media)
{
    var label = LabelImages.FromPng(File.ReadAllBytes(path), Threshold(), Flag("--dither"));
    var (width, height) = media.ExpectedSize(label);
    if (label.Width != width || label.Height != height)
    {
        throw new ArgumentException($"{path} is {label.Width} x {label.Height}; {media.Describe()} needs {width} x {height} (width x height, as the label is read).");
    }

    return label;
}

LabelBitmap TextLabel(IReadOnlyList<string> lines, Media media)
{
    if (lines.Count == 0)
    {
        throw new UsageException("text needs at least one line");
    }

    const float pad = 0.08f;
    using var canvas = media switch
    {
        PtJob pt => LabelCanvas.ForPt(pt.Tape, PtTextLength(lines, pt), pt.Options.HighResolution),
        QlJob ql => LabelCanvas.ForQl(ql.Media, QlTextLength(lines, ql)),
        _ => throw new InvalidOperationException(),
    };
    var inset = canvas.Height * pad;
    LabelText.DrawFitted(canvas.Canvas, new SKRect(inset, inset, canvas.Width - inset, canvas.Height - inset), lines);
    return canvas.ToLabel(Threshold());

    int PtTextLength(IReadOnlyList<string> text, PtJob pt)
    {
        if (Option("--length-mm") is { } mm)
        {
            return (int)Math.Round(double.Parse(mm) * (pt.Options.HighResolution ? 360 : 180) / 25.4);
        }

        // As high as the tape allows; as long as that makes the widest line, plus the same padding.
        var height = pt.Tape.PrintablePins * (1 - 2 * pad);
        var size = LabelText.Measure(text, 100);
        var length = size.Width * height / size.Height * (pt.Options.HighResolution ? 2 : 1);
        return Math.Max(PtRasterEncoder.MinimumLength(pt.Options.HighResolution), (int)Math.Ceiling(length + 2 * pt.Tape.PrintablePins * pad));
    }

    int QlTextLength(IReadOnlyList<string> text, QlJob ql)
    {
        if (Option("--length-mm") is { } mm)
        {
            return (int)Math.Round(double.Parse(mm) * 300 / 25.4);
        }

        var width = ql.Media.PrintableDots * (1 - 2 * pad);
        var size = LabelText.Measure(text, 100);
        return Math.Max(150, (int)Math.Ceiling(size.Height * width / size.Width / (1 - 2 * pad)));
    }
}

LabelBitmap Pattern(Media media)
{
    // Asymmetric on purpose: a filled block in the top-left corner, an arrow pointing right, and text
    // that reads wrong if the label comes out mirrored or upside down.
    using var canvas = media switch
    {
        PtJob pt => LabelCanvas.ForPt(pt.Tape, (int)(40 * (pt.Options.HighResolution ? 360 : 180) / 25.4), pt.Options.HighResolution),
        QlJob ql => LabelCanvas.ForQl(ql.Media, (int)(30 * 300 / 25.4)),
        _ => throw new InvalidOperationException(),
    };
    var c = canvas.Canvas;
    var w = canvas.Width;
    var h = canvas.Height;
    using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    using var fill = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill };
    c.DrawRect(0, 0, w - 1, h - 1, ink);
    var block = Math.Max(4, h / 4);
    c.DrawRect(0, 0, block, block, fill);
    for (var dx = 0; dx < block; dx++)
    {
        // A right-pointing triangle, one column at a time.
        var half = block / 2f * (1 - (float)dx / block);
        c.DrawRect(w - block * 1.5f + dx, h / 2f - half, 1, half * 2, fill);
    }
    LabelText.DrawFitted(c, new SKRect(block * 1.5f, h * 0.15f, w - block * 2, h * 0.85f), ["TOP LEFT", media.Describe()]);
    return canvas.ToLabel();
}

int Decode(string path)
{
    var job = RasterDecoder.Decode(File.ReadAllBytes(path));
    Console.WriteLine($"{job.Family} job, {job.Lines} raster lines (declared {job.DeclaredRasterLines}), {job.DeclaredWidthMm} mm{(job.DeclaredLengthMm > 0 ? $" x {job.DeclaredLengthMm} mm" : "")}{(job.Red is null ? "" : ", two-color")}");
    foreach (var command in job.Commands)
    {
        Console.WriteLine($"  {command}");
    }

    if (Option("--png") is { } png)
    {
        var label = job.Family == RasterFamily.Ql ? job.ToQlLabel() : job.ToPtLabel();
        File.WriteAllBytes(png, LabelImages.ToPng(label));
        Console.WriteLine($"wrote {png} ({label.Width} x {label.Height})");
    }

    return 0;
}

int Threshold() => Option("--threshold") is { } t ? int.Parse(t) : 128;

bool Same(string name, string given) =>
    string.Equals(name.Replace(" ", ""), given.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)
    || string.Equals(name.Replace(" ", ""), given.Replace(" ", "") + "mm", StringComparison.OrdinalIgnoreCase);

bool Flag(string name) => args.Contains(name);

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// Arguments that are not options or option values, from index `from`.
List<string> Positionals(int from)
{
    var valued = new HashSet<string> { "--ql", "--pt", "--margin", "--threshold", "--length-mm", "--out", "--png" };
    var result = new List<string>();
    for (var i = 1; i < args.Length; i++)
    {
        if (valued.Contains(args[i]))
        {
            i++;
        }
        else if (!args[i].StartsWith("--"))
        {
            result.Add(args[i]);
        }
    }

    return result.Skip(from - 1).ToList();
}

string Positional(int index, string name) => Positionals(index).FirstOrDefault() ?? throw new UsageException($"missing <{name}>");

static string Usage() => """
    usage:
      brother-raster status  <host>
      brother-raster print   <host> <image.png> [media] [job options]
      brother-raster text    <host> <line>...   [media] [job options]
      brother-raster pattern <host>             [media] [job options]
      brother-raster decode  <job.bin> [--png preview.png]

    media (default: whatever the printer reports is loaded):
      --ql <roll>    12mm 29mm 38mm 50mm 54mm 62mm, or a die-cut size such as "62mm x 29mm"
      --pt <tape>    3.5mm 6mm 9mm 12mm 18mm 24mm (TZe)

    job options:
      --two-color    QL: two-color job, required on the DK-22251 black/red roll
      --compress     QL: PackBits rows (PT jobs are always compressed)
      --no-cut       do not cut after the label
      --half-cut     PT: cut the laminate but not the backing
      --chain        PT: leave the label in the printer (saves tape before the next one)
      --high-res     PT: 180 x 360 dpi
      --mirror       PT: mirror the label
      --margin <n>   feed margin in dots
      --threshold <n>, --dither   how image grays become dots
      --length-mm <n>             text: label length instead of fitting the text
      --out <job.bin> [--png <preview.png>]   write the job instead of sending it; host may be "-"
    """;

abstract record Media(int WidthMm)
{
    public abstract byte[] Encode(LabelBitmap label);

    public abstract string Describe();

    public abstract (int Width, int Height) ExpectedSize(LabelBitmap label);
}

sealed record QlJob(QlMedia Media, QlRasterOptions Options) : Media(Media.WidthMm)
{
    public override byte[] Encode(LabelBitmap label) => QlRasterEncoder.Encode(label, Options);

    public override string Describe() => $"QL {Media.Name}{(Options.TwoColor ? " two-color" : "")}";

    public override (int Width, int Height) ExpectedSize(LabelBitmap label) =>
        (Media.PrintableDots, Media.Kind == QlMediaKind.DieCut ? Media.PrintableLength : label.Height);
}

sealed record PtJob(PtTape Tape, PtRasterOptions Options) : Media(Tape.WidthMm)
{
    public override byte[] Encode(LabelBitmap label) => PtRasterEncoder.Encode(label, Options);

    public override string Describe() => $"PT {Tape.Name}";

    public override (int Width, int Height) ExpectedSize(LabelBitmap label) => (label.Width, Tape.PrintablePins);
}

sealed class UsageException(string message) : Exception(message);
