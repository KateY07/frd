using System.IO.Compression;
using System.Numerics;

namespace Frd;

/// <summary>
/// Headless check of the host's screenshot tool: a synthetic picture gets a red pen stroke, a rectangle and a text mark; the composed image must keep the
/// background where nothing was drawn, show the marks (red pixels on the stroke), and survive the PNG writer (decoded again and compared).
/// </summary>
static class ShotRegression
{
    public static int Run()
    {
        var problems = new List<string>();
        const int width = 640, height = 360;
        var picture = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++) { picture[i * 4] = 200; picture[i * 4 + 1] = 120; picture[i * 4 + 2] = 40; picture[i * 4 + 3] = 255; }   // BGRA: orange-brown
        var board = new AnnotationBoard();
        var red = UiPaint.Hex(0xFFFF0000);
        board.Begin(AnnotationKind.Pen, new Vector2(.1f, .5f), red, 6f / width);
        board.Add(new Vector2(.5f, .5f)); board.End();
        board.Begin(AnnotationKind.Rectangle, new Vector2(.6f, .2f), red, 3f / width);
        board.Add(new Vector2(.9f, .4f)); board.End();
        board.AddText(new Vector2(.1f, .75f), "FRD", red, 30f / width);
        var composed = ShotComposer.Compose(picture, width, height, board);
        if (composed.Length != picture.Length) problems.Add("composed size differs");
        else
        {
            int Pixel(float x, float y) => ((int)(y * height) * width + (int)(x * width)) * 4;
            var onStroke = Pixel(.3f, .5f);
            if (!(composed[onStroke + 2] > 200 && composed[onStroke + 1] < 60 && composed[onStroke] < 60)) problems.Add($"stroke pixel is not red: {composed[onStroke]},{composed[onStroke + 1]},{composed[onStroke + 2]}");
            var background = Pixel(.5f, .9f);
            if (composed[background] != 200 || composed[background + 1] != 120 || composed[background + 2] != 40) problems.Add("background changed");
            var changed = 0;
            for (var i = 0; i < composed.Length; i += 4) if (composed[i] != picture[i] || composed[i + 1] != picture[i + 1] || composed[i + 2] != picture[i + 2]) changed++;
            if (changed < 2000) problems.Add($"only {changed} pixels carry marks");
            Console.WriteLine($"Marks cover {changed} pixels.");

            var path = Path.Combine(Path.GetTempPath(), "frd-shot-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                PngWriter.Write(path, composed, width, height);
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length < 100 || bytes[1] != 0x50) problems.Add("PNG signature missing");
                else
                {
                    using var idat = new MemoryStream();
                    var offset = 8;
                    while (offset + 8 <= bytes.Length)
                    {
                        var length = (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
                        if (System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4) == "IDAT") idat.Write(bytes, offset + 8, length);
                        offset += 12 + length;
                    }
                    idat.Position = 0;
                    using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
                    using var raw = new MemoryStream();
                    zlib.CopyTo(raw);
                    var all = raw.ToArray();
                    if (all.Length != height * (width * 3 + 1)) problems.Add("PNG data length");
                    else
                        for (var y = 0; y < height && problems.Count == 0; y++)
                            for (var x = 0; x < width; x++)
                            {
                                var o = y * (width * 3 + 1) + 1 + x * 3; var s = (y * width + x) * 4;
                                if (all[o] != composed[s + 2] || all[o + 1] != composed[s + 1] || all[o + 2] != composed[s]) { problems.Add($"PNG pixel {x},{y} differs"); break; }
                            }
                }
            }
            finally { try { File.Delete(path); } catch (IOException) { } }
        }
        foreach (var problem in problems) Console.Error.WriteLine("FAIL " + problem);
        Console.WriteLine(problems.Count == 0 ? "Screenshot markup: PASS" : $"Screenshot markup: {problems.Count} problem(s)");
        return problems.Count == 0 ? 0 : 1;
    }
}
