using System.Windows.Media.Imaging;

internal sealed record PatchSample(int Frame, int PatchId, string Key, double SeamCur, double SeamBuild, double RingDiff, bool Moving, Box PatchBox, string Text);

internal sealed class PatchAnalyzer(MotionAnalyzer analyzer, int workers)
{
    private const double FrozenRing = 20;

    private sealed class PatchGeometry
    {
        public required PatchDto Patch { get; init; }
        public required (int U, int V, int Ou, int Ov, byte B, byte G, byte R)[] Seam { get; init; }
        public Box Placement { get; set; }
        public int BuildFrame { get; set; } = -1;
        public byte[]? BuildRing { get; set; }
        public (int Dx, int Dy)[] RingOffsets { get; set; } = [];
        public double SeamBuild { get; set; } = double.NaN;
    }

    public List<PatchSample> Samples { get; } = [];

    public void Run()
    {
        var clock = analyzer.Clock;
        var data = analyzer.Data;
        var width = analyzer.Recording.Width;
        var height = analyzer.Recording.Height;
        var perFrame = new List<(OBlock Block, int PatchId)>[clock.Count];
        var geometry = new Dictionary<int, PatchGeometry>();
        for (var f = 0; f < clock.Count; f++)
        {
            perFrame[f] = [];
            if (!analyzer.HideIdentical) continue;
            foreach (var block in analyzer.Timeline.At(clock.Mid(f)).Displayed)
            {
                if (block.Cover is not { } cover || block.PatchBox is not { } box) continue;
                if (!data.Patches.TryGetValue(cover.Id, out var patch)) continue;
                if (!geometry.TryGetValue(cover.Id, out var g))
                {
                    var file = data.PathOf("patches", patch.File);
                    if (!File.Exists(file)) continue;
                    g = Load(patch, file);
                    g.Placement = box;
                    g.BuildFrame = patch.CaptureAtMs is { } at ? clock.FrameAt(at) : -1;
                    g.RingOffsets = RingOffsets(box);
                    geometry[cover.Id] = g;
                }
                perFrame[f].Add((block, cover.Id));
            }
        }
        if (geometry.Count == 0)
        {
            analyzer.Metrics["patches"] = new { patches = 0 };
            return;
        }

        using var pool = new StaPool(workers, workers * 2, "patch-analyze");
        var buildFrames = geometry.Values.Where(static g => g.BuildFrame >= 0).GroupBy(static g => g.BuildFrame).ToList();
        var buildTasks = buildFrames.Select(group => pool.Run(() =>
        {
            var pixels = Imaging.ToBgra(Imaging.DecodeJpeg(analyzer.Recording.FramePath(group.Key)), out var stride);
            foreach (var g in group)
            {
                g.SeamBuild = SeamError(g, g.Placement, pixels, stride, width, height);
                g.BuildRing = SampleRing(g, g.Placement, pixels, stride, width, height);
            }
            return 0;
        })).ToList();
        Task.WaitAll(buildTasks);

        var results = new List<PatchSample>[clock.Count];
        var frameTasks = Enumerable.Range(0, clock.Count).Where(f => perFrame[f].Count > 0).Select(f => pool.Run(() =>
        {
            var pixels = Imaging.ToBgra(Imaging.DecodeJpeg(analyzer.Recording.FramePath(f)), out var stride);
            var list = new List<PatchSample>();
            foreach (var (block, id) in perFrame[f])
            {
                var g = geometry[id];
                var placement = block.PatchBox!.Value;
                var seam = SeamError(g, placement, pixels, stride, width, height);
                var ring = g.BuildRing is { } buildRing
                    ? RingDifference(g, placement, buildRing, pixels, stride, width, height)
                    : double.NaN;
                list.Add(new PatchSample(f, id, block.Key, seam, g.SeamBuild, ring, analyzer.Moving[f], placement, block.Short));
            }
            results[f] = list;
            return 0;
        })).ToList();
        Task.WaitAll(frameTasks);
        foreach (var list in results)
            if (list is not null) Samples.AddRange(list);

        var moving = Samples.Where(static s => s.Moving).ToList();
        var rest = Samples.Where(static s => !s.Moving).ToList();
        double Seconds(IEnumerable<PatchSample> samples) => Math.Round(samples.Sum(s => clock.Weight(s.Frame)) / 1000, 2);
        analyzer.Metrics["patches"] = new
        {
            patches = geometry.Count,
            samples = Samples.Count,
            seamCurrentMotion = MotionAnalyzer.Summary(moving.Select(static s => s.SeamCur)),
            seamCurrentRest = MotionAnalyzer.Summary(rest.Select(static s => s.SeamCur)),
            seamAtBuild = MotionAnalyzer.Summary(geometry.Values.Select(static g => g.SeamBuild)),
            seamDeltaMotion = MotionAnalyzer.Summary(moving.Select(static s => s.SeamCur - s.SeamBuild)),
            seamDeltaRest = MotionAnalyzer.Summary(rest.Select(static s => s.SeamCur - s.SeamBuild)),
            ringDiffMotion = MotionAnalyzer.Summary(moving.Select(static s => s.RingDiff)),
            ringDiffRest = MotionAnalyzer.Summary(rest.Select(static s => s.RingDiff)),
            patchSecondsMotion = Seconds(moving),
            patchSecondsRest = Seconds(rest),
            frozenSecondsMotion = Seconds(moving.Where(static s => s.RingDiff >= FrozenRing)),
            frozenSecondsRest = Seconds(rest.Where(static s => s.RingDiff >= FrozenRing)),
            frozenThreshold = FrozenRing,
        };

        foreach (var group in Samples.GroupBy(static s => (s.Key, s.PatchId)))
        {
            var ordered = group.OrderBy(static s => s.Frame).ToList();
            var start = -1;
            for (var k = 0; k < ordered.Count; k++)
            {
                var bad = ordered[k].RingDiff >= FrozenRing;
                var contiguous = k > 0 && ordered[k].Frame == ordered[k - 1].Frame + 1;
                if (start >= 0 && (!bad || !contiguous))
                {
                    Flush(ordered, start, k - 1);
                    start = -1;
                }
                if (bad && start < 0) start = k;
            }
            if (start >= 0) Flush(ordered, start, ordered.Count - 1);
        }
    }

    private void Flush(List<PatchSample> ordered, int from, int to)
    {
        var clock = analyzer.Clock;
        var duration = 0.0;
        double ring = 0;
        for (var k = from; k <= to; k++)
        {
            duration += clock.Weight(ordered[k].Frame);
            ring += ordered[k].RingDiff;
        }
        if (duration < 200) return;
        var mean = ring / (to - from + 1);
        var worst = ordered.Skip(from).Take(to - from + 1).MaxBy(static s => s.RingDiff)!;
        var truthBoxes = analyzer.TruthBoxesNear(worst.Frame, worst.PatchBox, 600);
        analyzer.Problems.Add(new Problem("ŁATKA", clock.Start[ordered[from].Frame], clock.End[ordered[to].Frame], worst.Frame,
            duration / 1000.0 * Math.Min(mean / FrozenRing, 3.0), worst.Text,
            $"łatka zbudowana z innej klatki: tło wokół różni się średnio o {mean:0} (szew {worst.SeamCur:0} vs {worst.SeamBuild:0} przy budowie) przez {duration:0} ms",
            truthBoxes, [], worst.PatchBox, $"p{worst.PatchId}"));
    }

    private static PatchGeometry Load(PatchDto patch, string file)
    {
        var bitmap = Imaging.DecodeAny(file);
        var pixels = Imaging.ToBgra(bitmap, out var stride);
        var w = bitmap.PixelWidth;
        var h = bitmap.PixelHeight;
        byte Alpha(int u, int v) => u < 0 || v < 0 || u >= w || v >= h ? (byte)0 : pixels[v * stride + u * 4 + 3];
        var seam = new List<(int, int, int, int, byte, byte, byte)>();
        (int Dx, int Dy)[] directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        for (var v = 0; v < h; v++)
        {
            for (var u = 0; u < w; u++)
            {
                if (Alpha(u, v) < 250) continue;
                foreach (var (dx, dy) in directions)
                {
                    if (Alpha(u + dx, v + dy) >= 250) continue;
                    for (var k = 1; k <= 16; k++)
                    {
                        var ou = u + k * dx;
                        var ov = v + k * dy;
                        if (Alpha(ou, ov) > 5) continue;
                        var p = v * stride + u * 4;
                        seam.Add((u, v, ou, ov, pixels[p], pixels[p + 1], pixels[p + 2]));
                        break;
                    }
                }
            }
        }
        var step = Math.Max(1, seam.Count / 800);
        return new PatchGeometry
        {
            Patch = patch,
            Seam = seam.Where((_, i) => i % step == 0).ToArray(),
        };
    }

    private static (int, int)[] RingOffsets(Box placement)
    {
        var outer = placement.Inflate(10);
        var inner = placement.Inflate(2);
        var result = new List<(int, int)>();
        for (var y = outer.Y; y < outer.Bottom; y += 3)
            for (var x = outer.X; x < outer.Right; x += 3)
                if (!inner.ContainsPoint(x, y)) result.Add((x - placement.X, y - placement.Y));
        return result.ToArray();
    }

    private static double SeamError(PatchGeometry g, Box placement, byte[] frame, int stride, int width, int height)
    {
        if (g.Seam.Length == 0) return double.NaN;
        var sx = g.Patch.W / Math.Max(1, g.Patch.PixelW);
        var sy = g.Patch.H / Math.Max(1, g.Patch.PixelH);
        double sum = 0;
        var count = 0;
        foreach (var (_, _, ou, ov, b, gr, r) in g.Seam)
        {
            var x = (int)Math.Floor(placement.X + (ou + 0.5) * sx);
            var y = (int)Math.Floor(placement.Y + (ov + 0.5) * sy);
            if (x < 0 || y < 0 || x >= width || y >= height) continue;
            var p = y * stride + x * 4;
            sum += (Math.Abs(frame[p] - b) + Math.Abs(frame[p + 1] - gr) + Math.Abs(frame[p + 2] - r)) / 3.0;
            count++;
        }
        return count == 0 ? double.NaN : sum / count;
    }

    private static byte[] SampleRing(PatchGeometry g, Box placement, byte[] frame, int stride, int width, int height)
    {
        var result = new byte[g.RingOffsets.Length * 3];
        for (var i = 0; i < g.RingOffsets.Length; i++)
        {
            var x = Math.Clamp(placement.X + g.RingOffsets[i].Dx, 0, width - 1);
            var y = Math.Clamp(placement.Y + g.RingOffsets[i].Dy, 0, height - 1);
            var p = y * stride + x * 4;
            result[i * 3] = frame[p];
            result[i * 3 + 1] = frame[p + 1];
            result[i * 3 + 2] = frame[p + 2];
        }
        return result;
    }

    private static double RingDifference(PatchGeometry g, Box placement, byte[] buildRing, byte[] frame, int stride, int width, int height)
    {
        if (g.RingOffsets.Length == 0) return double.NaN;
        var current = SampleRing(g, placement, frame, stride, width, height);
        double sum = 0;
        for (var i = 0; i < current.Length; i++) sum += Math.Abs(current[i] - buildRing[i]);
        return sum / current.Length;
    }
}
