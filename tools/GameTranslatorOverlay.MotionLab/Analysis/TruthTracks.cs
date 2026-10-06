using GameTranslatorOverlay.Core.Text;

internal sealed record TruthRow(Box Box, string Norm, string BlockKeyHash, string? Identity, bool Live);

internal sealed record TruthObs(int Frame, int Step, Box Box, string Norm, string BlockKeyHash, string? Identity, bool Live, bool Bridged);

internal sealed class TruthTrack
{
    public int Id { get; init; }
    public List<TruthObs> Obs { get; } = [];
    public string Norm { get; set; } = string.Empty;
    public string? Identity { get; set; }
    public bool Live { get; set; }
    public bool Static { get; set; }
    public bool Typing { get; set; }
    public int TypingEndFrame { get; set; }
    public int First => Obs[0].Frame;
    public int Last => Obs[^1].Frame;
    public int ObservedCount => Obs.Count(static o => !o.Bridged);
    public string Short => TextTools.Short(Norm, 48);
}

internal static class TextTools
{
    private static readonly Dictionary<(string, string), double> Cache = [];
    private static readonly Lock Gate = new();

    public static double Similarity(string a, string b)
    {
        if (ReferenceEquals(a, b) || a == b) return 1.0;
        var key = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }
        var value = TextSimilarity.Ratio(Flat(a), Flat(b));
        lock (Gate)
        {
            if (Cache.Count > 400_000) Cache.Clear();
            Cache[key] = value;
        }
        return value;
    }

    public static bool LengthCompatible(string a, string b)
    {
        var longest = Math.Max(a.Length, b.Length);
        return Math.Abs(a.Length - b.Length) <= 0.4 * longest;
    }

    public static string Flat(string text) => text.Replace('\n', ' ');

    public static string Short(string text, int max)
    {
        var flat = Flat(text);
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    public static int Letters(string text) => text.Count(char.IsLetterOrDigit);

    public static bool Contains(string a, string b)
    {
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return Letters(shorter) >= 3 && longer.Contains(shorter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal static class TrackBuilder
{
    private const int LongGap = 15;

    public static IReadOnlyList<TruthRow> RowsOf(TruthFrameDto frame) =>
        frame.Blocks.SelectMany(static block => (block.Rows.Count > 0 ? block.Rows : [new TruthRowDto(block.Text, block.Norm, block.Box)])
            .Select(row => new TruthRow(row.Box, row.Norm, block.KeyHash, block.Identity, block.Live))).ToList();

    public static List<TruthTrack> Build(IReadOnlyList<TruthFrameDto> frames, int frameWidth, int maxGap)
    {
        var tracks = new List<TruthTrack>();
        var active = new List<(TruthTrack Track, int LastStep)>();
        for (var step = 0; step < frames.Count; step++)
        {
            var frame = frames[step];
            var rows = RowsOf(frame);
            active.RemoveAll(a => step - a.LastStep > LongGap + 1);
            var pairs = new List<(int Active, int Row, double Similarity, double Distance)>();
            for (var a = 0; a < active.Count; a++)
            {
                var last = active[a].Track.Obs[^1];
                var gap = step - active[a].LastStep - 1;
                for (var r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    var overlap = last.Box.IoU(row.Box);
                    var similarity = TextTools.Similarity(last.Norm, row.Norm);
                    if (TextTools.Contains(last.Norm, row.Norm)) similarity = Math.Max(similarity, overlap >= 0.3 ? 0.9 : 0.6);
                    var threshold = overlap >= 0.5 ? 0.45 : 0.6;
                    if (similarity < threshold && !IsGrowth(last.Norm, row.Norm)) continue;
                    if (gap > maxGap && (overlap < 0.5 || similarity < 0.6)) continue;
                    var distance = last.Box.CenterDistance(row.Box);
                    var allowed = similarity >= 0.85 ? 0.30 * frameWidth : 0.12 * frameWidth;
                    if (distance > allowed && !(IsGrowth(last.Norm, row.Norm) && Math.Abs(last.Box.X - row.Box.X) <= Math.Max(24, row.Box.H)
                        && Math.Abs(last.Box.Y - row.Box.Y) <= Math.Max(16, row.Box.H / 2))) continue;
                    var heightRatio = (double)Math.Max(last.Box.H, row.Box.H) / Math.Max(1, Math.Min(last.Box.H, row.Box.H));
                    if (heightRatio > 2.5) continue;
                    pairs.Add((a, r, Math.Max(similarity, IsGrowth(last.Norm, row.Norm) ? 0.6 : 0), distance));
                }
            }
            var usedActive = new HashSet<int>();
            var usedRow = new HashSet<int>();
            foreach (var pair in pairs.OrderByDescending(static p => p.Similarity).ThenBy(static p => p.Distance))
            {
                if (usedActive.Contains(pair.Active) || usedRow.Contains(pair.Row)) continue;
                usedActive.Add(pair.Active);
                usedRow.Add(pair.Row);
                var row = rows[pair.Row];
                var track = active[pair.Active].Track;
                track.Obs.Add(new TruthObs(frame.I, step, row.Box, row.Norm, row.BlockKeyHash, row.Identity, row.Live, false));
                active[pair.Active] = (track, step);
            }
            for (var r = 0; r < rows.Count; r++)
            {
                if (usedRow.Contains(r)) continue;
                var row = rows[r];
                var track = new TruthTrack { Id = tracks.Count + 1 };
                track.Obs.Add(new TruthObs(frame.I, step, row.Box, row.Norm, row.BlockKeyHash, row.Identity, row.Live, false));
                tracks.Add(track);
                active.Add((track, step));
            }
        }

        foreach (var track in tracks)
        {
            Bridge(track, frames);
            Summarize(track);
        }
        return tracks;
    }

    private static bool IsGrowth(string previous, string current) =>
        previous.Length >= 2 && current.Length > previous.Length
        && current.StartsWith(previous[..Math.Max(2, previous.Length - 2)], StringComparison.OrdinalIgnoreCase);

    private static void Bridge(TruthTrack track, IReadOnlyList<TruthFrameDto> frames)
    {
        var observed = track.Obs.ToList();
        track.Obs.Clear();
        for (var k = 0; k < observed.Count; k++)
        {
            track.Obs.Add(observed[k]);
            if (k + 1 >= observed.Count) continue;
            var a = observed[k];
            var b = observed[k + 1];
            for (var step = a.Step + 1; step < b.Step; step++)
            {
                var t = (double)(step - a.Step) / (b.Step - a.Step);
                var box = new Box(
                    (int)Math.Round(a.Box.X + (b.Box.X - a.Box.X) * t), (int)Math.Round(a.Box.Y + (b.Box.Y - a.Box.Y) * t),
                    (int)Math.Round(a.Box.W + (b.Box.W - a.Box.W) * t), (int)Math.Round(a.Box.H + (b.Box.H - a.Box.H) * t));
                track.Obs.Add(a with { Frame = frames[step].I, Step = step, Box = box, Bridged = true });
            }
        }
    }

    private static void Summarize(TruthTrack track)
    {
        var real = track.Obs.Where(static o => !o.Bridged).ToList();
        track.Norm = real.GroupBy(static o => o.Norm).OrderByDescending(static g => g.Count()).ThenByDescending(static g => g.Key.Length).First().Key;
        track.Identity = real.Where(static o => o.Identity is not null).GroupBy(static o => o.Identity!)
            .OrderByDescending(static g => g.Count()).Select(static g => g.Key).FirstOrDefault();
        track.Live = real.Count(static o => o.Live) * 2 >= real.Count;
        static double Median(IEnumerable<double> values)
        {
            var sorted = values.Order().ToList();
            return sorted[sorted.Count / 2];
        }
        var mx = Median(real.Select(static o => o.Box.Cx));
        var my = Median(real.Select(static o => o.Box.Cy));
        var ml = Median(real.Select(static o => (double)o.Box.X));
        var mr = Median(real.Select(static o => (double)o.Box.Right));
        var within = real.Count(o => Math.Abs(o.Box.Cy - my) <= 8
            && (Math.Abs(o.Box.Cx - mx) <= 8 || Math.Abs(o.Box.X - ml) <= 8 || Math.Abs(o.Box.Right - mr) <= 8));
        track.Static = real.Count >= 3 ? within >= real.Count * 0.9 : within == real.Count;
        var letters = real.Select(static o => TextTools.Letters(o.Norm)).ToList();
        var maxLetters = letters.Max();
        var decreases = 0;
        for (var k = 1; k < letters.Count; k++) if (letters[k] < letters[k - 1]) decreases++;
        if (real.Count >= 3 && maxLetters >= 12 && letters[0] <= maxLetters * 0.7 && decreases <= Math.Max(1, letters.Count / 10))
        {
            track.Typing = true;
            track.Static = within >= real.Count * 0.6 || real.All(o => Math.Abs(o.Box.X - real[0].Box.X) <= 12 && Math.Abs(o.Box.Y - real[0].Box.Y) <= 12);
            var end = real.FirstOrDefault(o => TextTools.Letters(o.Norm) >= maxLetters * 0.95);
            track.TypingEndFrame = end?.Frame ?? track.First;
        }
        else
        {
            track.TypingEndFrame = track.First;
        }
    }
}
