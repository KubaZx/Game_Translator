using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;

internal static class SheetWriter
{
    private const int PanelWidth = 960;
    private const int PanelHeight = 540;
    private const int StripWidth = 480;
    private const int StripHeight = 270;
    private const int HeaderHeight = 92;

    public static string Write(MotionAnalyzer analyzer, Problem problem, int rank, string directory)
    {
        var width = analyzer.Recording.Width;
        var height = analyzer.Recording.Height;
        var frame = Math.Clamp(problem.Frame, 0, analyzer.Clock.Count - 1);
        var roi = Roi(problem, width, height);
        var name = $"{rank:D2}-{Slug(problem.Type)}-f{frame:D5}.jpg";
        var path = Path.Combine(directory, name);

        using var sheet = new Bitmap(PanelWidth * 2, HeaderHeight + PanelHeight + 8 + StripHeight + 22, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(sheet);
        graphics.Clear(Color.FromArgb(18, 18, 22));
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using (var original = LoadFrame(analyzer, frame))
        {
            using var composite = Composite(analyzer, frame, original);
            graphics.DrawImage(original, new Rectangle(0, HeaderHeight, PanelWidth, PanelHeight), ToRect(roi), GraphicsUnit.Pixel);
            graphics.DrawImage(composite, new Rectangle(PanelWidth, HeaderHeight, PanelWidth, PanelHeight), ToRect(roi), GraphicsUnit.Pixel);
            DrawOutlines(graphics, analyzer, problem, frame, roi);
        }

        var offsets = new[] { -1000.0, -500.0, 500.0, 1000.0 };
        var t0 = analyzer.Clock.Start[frame];
        using var labelFont = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var labelBack = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        for (var k = 0; k < offsets.Length; k++)
        {
            var f = analyzer.Clock.FrameAt(t0 + offsets[k]);
            using var original = LoadFrame(analyzer, f);
            using var composite = Composite(analyzer, f, original);
            var target = new Rectangle(k * StripWidth, HeaderHeight + PanelHeight + 8, StripWidth, StripHeight);
            graphics.DrawImage(composite, target, ToRect(roi), GraphicsUnit.Pixel);
            var label = $"{offsets[k] / 1000:+0.0;-0.0} s  (klatka {f}{(analyzer.Moving[f] ? ", ruch" : string.Empty)})";
            graphics.FillRectangle(labelBack, target.X, target.Y, 230, 18);
            graphics.DrawString(label, labelFont, Brushes.White, target.X + 4, target.Y + 2);
        }

        using var titleFont = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textFont = new Font("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        var step = analyzer.StepAt(frame);
        var title = string.Create(CultureInfo.InvariantCulture,
            $"#{rank} {problem.Type}   t = {problem.StartMs / 1000:0.0}–{problem.EndMs / 1000:0.0} s   klatka {frame}   krok: {step?.Label ?? "—"}   " +
            $"ruch kamery: {(analyzer.Moving[frame] ? "tak" : "nie")} (mocne {analyzer.StrongFraction[frame] * 100:0}%)   kąt tekstu OCR: {analyzer.Angle[frame]:0.0}°");
        graphics.DrawString(title, titleFont, Brushes.Gold, 8, 6);
        graphics.DrawString($"tekst: „{problem.Text}” — {problem.Detail}", textFont, Brushes.White, 8, 32);
        graphics.DrawString("lewo: oryginał (zielone = prawda OCR, fioletowe = bloki nakładki, żółte = łatka) | prawo: oryginał + nakładka | dół: nakładka −1 s, −0,5 s, +0,5 s, +1 s",
            textFont, Brushes.Silver, 8, 56);
        graphics.DrawString($"wycinek {roi} z {width}×{height}", textFont, Brushes.Gray, 8, HeaderHeight + PanelHeight + 8 + StripHeight + 3);

        var encoder = ImageCodecInfo.GetImageEncoders().First(static e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L) } };
        sheet.Save(path, encoder, parameters);
        return path;
    }

    private static Bitmap LoadFrame(MotionAnalyzer analyzer, int frame)
    {
        using var stream = new FileStream(analyzer.Recording.FramePath(frame), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    private static Bitmap Composite(MotionAnalyzer analyzer, int frame, Bitmap original)
    {
        var result = new Bitmap(original);
        var shown = analyzer.Data.Shown[frame];
        if (shown.Layer <= 0 || !analyzer.Data.Layers.TryGetValue(shown.Layer, out var layer)) return result;
        var file = analyzer.Data.PathOf("layers", layer.File);
        if (!File.Exists(file)) return result;
        using var graphics = Graphics.FromImage(result);
        using var layerImage = new Bitmap(file);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.DrawImage(layerImage, new Rectangle(layer.Box.X, layer.Box.Y, layer.Box.W, layer.Box.H));
        return result;
    }

    private static void DrawOutlines(Graphics graphics, MotionAnalyzer analyzer, Problem problem, int frame, Box roi)
    {
        var scale = (double)PanelWidth / roi.W;
        Rectangle Map(Box box) => new(
            (int)Math.Round((box.X - roi.X) * scale), HeaderHeight + (int)Math.Round((box.Y - roi.Y) * scale),
            Math.Max(1, (int)Math.Round(box.W * scale)), Math.Max(1, (int)Math.Round(box.H * scale)));
        graphics.SetClip(new Rectangle(0, HeaderHeight, PanelWidth, PanelHeight));
        using var truthPen = new Pen(Color.FromArgb(220, 60, 230, 90), 2);
        using var overlayPen = new Pen(Color.FromArgb(230, 230, 70, 230), 2) { DashStyle = DashStyle.Dash };
        using var patchPen = new Pen(Color.FromArgb(230, 250, 220, 40), 1);
        foreach (var (track, obs) in analyzer.ObsAt(frame))
            if (track.Live) graphics.DrawRectangle(truthPen, Map(obs.Box));
        var state = analyzer.Timeline.At(analyzer.Clock.Mid(frame));
        foreach (var block in state.Displayed)
        {
            graphics.DrawRectangle(overlayPen, Map(block.Box));
            if (block.PatchBox is { } patch) graphics.DrawRectangle(patchPen, Map(patch));
        }
        if (problem.PatchBox is { } problemPatch) graphics.DrawRectangle(patchPen, Map(problemPatch));
        graphics.ResetClip();
    }

    private static Box Roi(Problem problem, int width, int height)
    {
        var union = default(Box);
        foreach (var box in problem.TruthBoxes.Concat(problem.OverlayBoxes)) union = union.Union(box);
        if (problem.PatchBox is { } patch) union = union.Union(patch);
        if (union.IsEmpty) union = new Box(width / 2 - 640, height / 2 - 360, 1280, 720);
        union = union.Inflate(160);
        var w = Math.Max(1280, union.W);
        var h = Math.Max(720, union.H);
        if (w * 9 > h * 16) h = (int)Math.Ceiling(w * 9 / 16.0);
        else w = (int)Math.Ceiling(h * 16 / 9.0);
        w = Math.Min(w, width);
        h = Math.Min(h, height);
        var x = (int)Math.Round(union.Cx - w / 2.0);
        var y = (int)Math.Round(union.Cy - h / 2.0);
        x = Math.Clamp(x, 0, width - w);
        y = Math.Clamp(y, 0, height - h);
        return new Box(x, y, w, h);
    }

    private static Rectangle ToRect(Box box) => new(box.X, box.Y, box.W, box.H);

    private static string Slug(string type)
    {
        var map = new Dictionary<char, char> { ['Ą'] = 'A', ['Ć'] = 'C', ['Ę'] = 'E', ['Ł'] = 'L', ['Ń'] = 'N', ['Ó'] = 'O', ['Ś'] = 'S', ['Ź'] = 'Z', ['Ż'] = 'Z' };
        var chars = type.ToUpperInvariant().Select(c => map.TryGetValue(c, out var m) ? m : c)
            .Select(static c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
