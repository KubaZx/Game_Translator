internal sealed record OBlock(string Key, string KeyHash, Box Box, string Text, string? Src, bool Same, int Misses, CoverDto? Cover, int OriginSeq)
{
    public string Short => TextTools.Short(Src ?? Text, 48);

    public Box? PatchBox => Cover is { } c
        ? new Box((int)Math.Floor(Box.X + c.X), (int)Math.Floor(Box.Y + c.Y), (int)Math.Ceiling(c.W), (int)Math.Ceiling(c.H))
        : null;
}

internal sealed record OState(double T, int Seq, IReadOnlyList<OBlock> Displayed, IReadOnlyList<OBlock> Covering, bool Hidden);

internal sealed class OverlayTimeline
{
    private readonly List<OState> _states;
    private readonly double[] _times;

    private OverlayTimeline(List<OState> states)
    {
        _states = states;
        _times = states.Select(static s => s.T).ToArray();
    }

    public IReadOnlyList<OState> States => _states;

    public OState At(double t)
    {
        var index = Array.BinarySearch(_times, t);
        if (index < 0) index = ~index - 1;
        else while (index + 1 < _times.Length && _times[index + 1] <= t) index++;
        return index < 0 ? _states[0] : _states[index];
    }

    public IEnumerable<OState> ChangesBetween(double from, double to)
    {
        var index = Array.BinarySearch(_times, from);
        if (index < 0) index = ~index;
        else while (index < _times.Length && _times[index] <= from) index++;
        for (; index < _times.Length && _times[index] < to; index++) yield return _states[index];
    }

    public static OverlayTimeline Build(IReadOnlyList<UpdateDto> updates, bool hideIdentical)
    {
        var states = new List<OState> { new(double.NegativeInfinity, 0, [], [], false) };
        var current = new Dictionary<string, OBlock>(StringComparer.Ordinal);
        var hidden = false;
        foreach (var update in updates)
        {
            var changed = false;
            if (update.Clear || update.Stopped)
            {
                current.Clear();
                changed = true;
            }
            if (update.Hide)
            {
                hidden = true;
                changed = true;
            }
            if (update.Blocks is { } blocks && !update.Stopped)
            {
                var next = new Dictionary<string, OBlock>(StringComparer.Ordinal);
                foreach (var block in blocks)
                {
                    current.TryGetValue(block.Key, out var previous);
                    var origin = previous is not null && previous.Box == block.Box
                        ? previous.OriginSeq
                        : update.Diag is not null ? update.Seq : previous?.OriginSeq ?? update.Seq;
                    var hash = block.Key.Split('#')[0];
                    next[block.Key] = new OBlock(block.Key, hash, block.Box, block.Text, block.Src, block.Same, block.Misses, block.Cover, origin);
                }
                current = next;
                hidden = hidden && current.Count == 0;
                changed = true;
            }
            if (!changed) continue;
            List<OBlock> covering = hidden ? [] : current.Values.ToList();
            var displayed = covering.Where(b => !(hideIdentical && b.Same)).ToList();
            states.Add(new OState(update.AppliedMs, update.Seq, displayed, covering, hidden));
        }
        return new OverlayTimeline(states);
    }
}

internal sealed class FrameClock
{
    public FrameClock(IReadOnlyList<ShownDto> shown, double nominalMs)
    {
        Count = shown.Count;
        Start = new double[Count];
        End = new double[Count];
        for (var i = 0; i < Count; i++)
        {
            var s = shown[i];
            Start[i] = s.RenderMs >= 0 ? s.RenderMs : s.SetMs >= 0 ? s.SetMs : s.DueMs;
        }
        for (var i = 0; i < Count; i++)
        {
            End[i] = i + 1 < Count ? Math.Max(Start[i], Start[i + 1]) : Start[i] + nominalMs;
        }
    }

    public int Count { get; }
    public double[] Start { get; }
    public double[] End { get; }

    public double Mid(int frame) => (Start[frame] + End[frame]) / 2;

    public double Weight(int frame) => End[frame] - Start[frame];

    public int FrameAt(double t)
    {
        var index = Array.BinarySearch(Start, t);
        if (index < 0) index = ~index - 1;
        return Math.Clamp(index, 0, Count - 1);
    }
}
