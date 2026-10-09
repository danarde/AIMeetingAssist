#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property OutputType=Exe
#:property PublishAot=false
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Renders the app icon into PNGs and a multi-size .ico: two voices, and the spark of a cue
// between them. The main window's sidebar draws the Medium version in XAML; keep them alike.
//
//   dotnet run tools/render-icon.cs -- out
//   copy out\app.ico src\MeetingAssist.App\app.ico
var output = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(output);

var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var pngs = new List<(int Size, byte[] Png)>();
var thread = new Thread(() =>
{
    foreach (var size in sizes)
    {
        var png = Render(size);
        pngs.Add((size, png));
        File.WriteAllBytes(Path.Combine(output, $"icon-{size}.png"), png);
    }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

using (var ico = new BinaryWriter(File.Create(Path.Combine(output, "app.ico"))))
{
    ico.Write((short)0); ico.Write((short)1); ico.Write((short)pngs.Count);
    var offset = 6 + 16 * pngs.Count;
    foreach (var (size, png) in pngs)
    {
        ico.Write((byte)(size >= 256 ? 0 : size)); ico.Write((byte)(size >= 256 ? 0 : size));
        ico.Write((byte)0); ico.Write((byte)0);
        ico.Write((short)1); ico.Write((short)32);
        ico.Write(png.Length); ico.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in pngs) ico.Write(png);
}
Console.WriteLine($"Wrote {pngs.Count} sizes to {Path.GetFullPath(output)}");

static byte[] Render(int size)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        // Seven shapes do not fit in 16 pixels: smaller sizes drop bars, so the spark keeps
        // clear space around it. Each version is drawn on its own grid.
        var (design, grid) = size switch
        {
            <= 24 => (Icon.Small, 16.0),
            <= 48 => (Icon.Medium, 32.0),
            _ => (Icon.Design, 64.0)
        };
        dc.PushTransform(new ScaleTransform(size / grid, size / grid));
        foreach (var drawing in design.Children) dc.DrawDrawing(drawing);
    }
    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

static class Icon
{
    static Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    static Pen P(string hex, double width) => new(B(hex), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    public static DrawingGroup Design => new()
    {
        Children =
        {
            new GeometryDrawing(B("#1E293B"), null, new RectangleGeometry(new Rect(2, 2, 60, 60), 14, 14)),
            new GeometryDrawing(null, P("#60A5FA", 4.5), Geometry.Parse("M10 28V36 M17 22V42 M24 26V38")),
            new GeometryDrawing(null, P("#FFFFFF", 4.5), Geometry.Parse("M40 26V38 M47 20V44 M54 28V36")),
            new GeometryDrawing(B("#FBBF24"), null, Geometry.Parse("M32 21 L35 29 L43 32 L35 35 L32 43 L29 35 L21 32 L29 29 Z"))
        }
    };

    public static DrawingGroup Medium => new()
    {
        Children =
        {
            new GeometryDrawing(B("#1E293B"), null, new RectangleGeometry(new Rect(1, 1, 30, 30), 7, 7)),
            new GeometryDrawing(B("#60A5FA"), null, new RectangleGeometry(new Rect(4, 12, 2.5, 8), 1.25, 1.25)),
            new GeometryDrawing(B("#60A5FA"), null, new RectangleGeometry(new Rect(8, 8, 2.5, 16), 1.25, 1.25)),
            new GeometryDrawing(B("#FFFFFF"), null, new RectangleGeometry(new Rect(21.5, 8, 2.5, 16), 1.25, 1.25)),
            new GeometryDrawing(B("#FFFFFF"), null, new RectangleGeometry(new Rect(25.5, 12, 2.5, 8), 1.25, 1.25)),
            new GeometryDrawing(B("#FBBF24"), null, Geometry.Parse("M16 11.5 L17.5 14.5 L20.5 16 L17.5 17.5 L16 20.5 L14.5 17.5 L11.5 16 L14.5 14.5 Z"))
        }
    };

    public static DrawingGroup Small => new()
    {
        Children =
        {
            new GeometryDrawing(B("#1E293B"), null, new RectangleGeometry(new Rect(0.5, 0.5, 15, 15), 3.5, 3.5)),
            new GeometryDrawing(B("#60A5FA"), null, new RectangleGeometry(new Rect(2, 4, 2, 8), 1, 1)),
            new GeometryDrawing(B("#FFFFFF"), null, new RectangleGeometry(new Rect(12, 4, 2, 8), 1, 1)),
            new GeometryDrawing(B("#FBBF24"), null, Geometry.Parse("M8 4.5 L9.1 6.9 L11.5 8 L9.1 9.1 L8 11.5 L6.9 9.1 L4.5 8 L6.9 6.9 Z"))
        }
    };
}
