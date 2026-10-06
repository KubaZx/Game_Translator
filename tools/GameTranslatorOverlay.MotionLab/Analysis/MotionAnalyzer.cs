using System.Text.RegularExpressions;

internal sealed record Problem(
    string Type, double StartMs, double EndMs, int Frame, double Score, string Text, string Detail,
    IReadOnlyList<Box> TruthBoxes, IReadOnlyList<Box> OverlayBoxes, Box? PatchBox, string Subject);

internal sealed record TrackSample(int Frame, bool Covered, bool Shown);

internal sealed class Ratio
{
    public double Covered { get; private set; }
    public double Total { get; private set; }
    public void Add(bool covered, double weight)
    {
        Total += weight;
        if (covered) Covered += weight;
    }
    public double? Percent => Total <= 0 ? null : Math.Round(100.0 * Covered / Total, 1);
    public double Seconds => Math.Round(Total / 1000.0, 1);
}

internal sealed record StaleRun(string Key, string Text, double StartMs, double EndMs, int StartFrame, int EndFrame, string Reason, double? MovedPx, Box Box)
{
    public double DurationMs => EndMs - StartMs;
}

internal sealed record LatencyItem(int TrackId, string Text, bool Static, bool Typing, bool StartedInMotion, bool AtStart, double StartMs, int StartFrame, double? LatencyMs, double? FromTypingEndMs);

internal sealed record HudLoss(int TrackId, string Text, int Frame, double TimeMs, string Cause, double? CaptureAngle, double? ProbeAngle, int? Seq, string? Status);

internal sealed record BlockMatch(bool Valid, double PosErr, Box? Union, IReadOnlyList<TruthObs> Rows, bool StaticRows, string Reason, double? MovedPx);

internal sealed class FrameMatch
{
    public Dictionary<TruthObs, OBlock> Covered { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<TruthObs, (OBlock Block, double Distance)> Shown { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<OBlock, BlockMatch> Blocks { get; } = new(ReferenceEqualityComparer.Instance);
}

internal sealed partial class MotionAnalyzer
{
    public const double MotionStrong = 0.12;
    public const double AngleThreshold = 0.5;
    private const int MinTrackFrames = 3;
    private const double MinTrackMs = 300;
    public const double AtStartMs = 1500;

    private readonly Dictionary<int, UpdateDto> _updatesBySeq;
    private readonly int[] _stepOfFrame;
    private readonly List<(TruthTrack Track, TruthObs Obs)>[] _obsAtStep;
    private readonly HashSet<string> _truthKeyHashes;
    private readonly List<string> _truthNorms;
    private readonly Dictionary<string, bool> _everSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Seq, int Frame), FrameMatch> _matches = [];
    private readonly Dictionary<TruthObs, TruthTrack> _trackOf = new(ReferenceEqualityComparer.Instance);

    public MotionAnalyzer(ReplayData data, Recording recording, IReadOnlyList<TruthFrameDto> truth)
    {
        Data = data;
        Recording = recording;
        Truth = truth.OrderBy(static t => t.I).ToList();
        Clock = new FrameClock(data.Shown, recording.NominalIntervalMs / Math.Max(0.01, data.Header.Speed));
        HideIdentical = data.Header.Placement.Equals("cover", StringComparison.OrdinalIgnoreCase);
        Timeline = OverlayTimeline.Build(data.Updates, HideIdentical);
        Tracks = TrackBuilder.Build(Truth, recording.Width, 2);
        _updatesBySeq = data.Updates.ToDictionary(static u => u.Seq);
        _stepOfFrame = new int[Clock.Count];
        var step = -1;
        for (var f = 0; f < Clock.Count; f++)
        {
            while (step + 1 < Truth.Count && Truth[step + 1].I <= f) step++;
            _stepOfFrame[f] = step;
        }
        _obsAtStep = Enumerable.Range(0, Truth.Count).Select(static _ => new List<(TruthTrack, TruthObs)>()).ToArray();
        foreach (var track in Tracks)
        {
            foreach (var obs in track.Obs)
            {
                _obsAtStep[obs.Step].Add((track, obs));
                _trackOf[obs] = track;
            }
        }
        Moving = new bool[Clock.Count];
        Angle = new double[Clock.Count];
        StrongFraction = new double[Clock.Count];
        for (var f = 0; f < Clock.Count; f++)
        {
            if (_stepOfFrame[f] < 0) continue;
            var frame = Truth[_stepOfFrame[f]];
            StrongFraction[f] = frame.Strong;
            Moving[f] = frame.Strong >= MotionStrong;
            Angle[f] = frame.Angle ?? 0;
        }
        _truthKeyHashes = Truth.SelectMany(static t => t.Blocks).Select(static b => b.KeyHash).ToHashSet(StringComparer.Ordinal);
        _truthNorms = Truth.SelectMany(TrackBuilder.RowsOf).Select(static r => r.Norm).Distinct(StringComparer.Ordinal).ToList();
        (Steps, StepOffsetMs) = AlignSteps();
    }

    public ReplayData Data { get; }
    public Recording Recording { get; }
    public IReadOnlyList<TruthFrameDto> Truth { get; }
    public FrameClock Clock { get; }
    public OverlayTimeline Timeline { get; }
    public List<TruthTrack> Tracks { get; }
    public bool HideIdentical { get; }
    public bool[] Moving { get; }
    public double[] Angle { get; }
    public double[] StrongFraction { get; }
    public IReadOnlyList<MotionStep> Steps { get; }
    public double StepOffsetMs { get; }

    public Dictionary<int, List<TrackSample>> Samples { get; } = [];
    public List<LatencyItem> Latencies { get; } = [];
    public List<StaleRun> StaleRuns { get; } = [];
    public List<HudLoss> HudLosses { get; } = [];
    public List<Problem> Problems { get; } = [];
    public Dictionary<string, object?> Metrics { get; } = new(StringComparer.Ordinal);

    public bool IsAngled(int frame) => Math.Abs(Angle[frame]) >= AngleThreshold;

    public double RecordingMs(int frame) => Recording.RelativeMs(Math.Min(frame, Recording.Frames.Count - 1));

    public MotionStep? StepAt(int frame)
    {
        var t = RecordingMs(frame);
        return Steps.FirstOrDefault(s => t >= s.StartMs && t < s.EndMs);
    }

    public IReadOnlyList<(TruthTrack Track, TruthObs Obs)> ObsAt(int frame) =>
        frame >= 0 && frame < _stepOfFrame.Length && _stepOfFrame[frame] >= 0 ? _obsAtStep[_stepOfFrame[frame]] : [];

    public bool Eligible(TruthTrack track) =>
        track.Live && track.ObservedCount >= MinTrackFrames && track.First < Clock.Count
        && Clock.End[Math.Min(track.Last, Clock.Count - 1)] - Clock.Start[track.First] >= MinTrackMs;

    public static bool GeometryMatch(Box overlay, Box row) =>
        overlay.IoU(row) >= 0.3 || overlay.Inflate(Math.Max(8, row.H / 2)).ContainsPoint(row.Cx, row.Cy);

    private bool IsDisplayed(OBlock block) => !(HideIdentical && block.Same);

    public bool EverSeen(OBlock o)
    {
        var cacheKey = o.KeyHash + "|" + (o.Src ?? string.Empty);
        if (_everSeen.TryGetValue(cacheKey, out var known)) return known;
        var seen = _truthKeyHashes.Contains(o.KeyHash);
        if (!seen && o.Src is { Length: > 0 } src)
        {
            foreach (var line in TextTools.Lines(src))
            {
                foreach (var norm in _truthNorms)
                {
                    if (!TextTools.LengthCompatible(norm, line)) continue;
                    if (TextTools.Similarity(line, norm) >= 0.6)
                    {
                        seen = true;
                        break;
                    }
                }
                if (seen) break;
            }
        }
        _everSeen[cacheKey] = seen;
        return seen;
    }

    public (int Frame, double? TruthAngle, double? ProbeAngle, bool Partial)? CaptureOf(int seq)
    {
        if (!_updatesBySeq.TryGetValue(seq, out var update) || update.Diag is not { } diag) return null;
        var captureAt = update.EmitMs - diag.CaptureToUpdateMs;
        var frame = Clock.FrameAt(captureAt);
        var probe = Data.Ocr.LastOrDefault(o => o.EndMs <= update.EmitMs + 1 && o.StartMs >= captureAt - 5 && !o.Failed);
        return (frame, Angle[frame], probe?.Angle, diag.PartialOcr);
    }

    public bool AngledOrigin(OBlock block) =>
        CaptureOf(block.OriginSeq) is { } origin && !origin.Partial
        && (Math.Abs(origin.TruthAngle ?? 0) >= AngleThreshold || Math.Abs(origin.ProbeAngle ?? 0) >= AngleThreshold);

    public FrameMatch Match(OState state, int frame)
    {
        if (_matches.TryGetValue((state.Seq, frame), out var cached)) return cached;
        var result = new FrameMatch();
        var rows = ObsAt(frame);
        foreach (var o in state.Covering)
        {
            var byKey = rows.Where(r => r.Obs.BlockKeyHash == o.KeyHash).Select(static r => r.Obs).ToList();
            var lines = o.Src is { } src ? TextTools.Lines(src) : [];
            List<TruthObs> matched;
            int needed;
            if (byKey.Count > 0)
            {
                matched = byKey;
                needed = Math.Max(1, (byKey.Count + 1) / 2);
            }
            else
            {
                matched = [];
                foreach (var line in lines)
                {
                    TruthObs? best = null;
                    var bestDistance = double.MaxValue;
                    foreach (var (_, obs) in rows)
                    {
                        if (matched.Contains(obs) || !TextTools.LengthCompatible(line, obs.Norm)) continue;
                        if (TextTools.Similarity(line, obs.Norm) < 0.6) continue;
                        var distance = o.Box.CenterDistance(obs.Box);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = obs;
                        }
                    }
                    if (best is not null) matched.Add(best);
                }
                needed = Math.Max(1, (lines.Count + 1) / 2);
            }
            var geometry = matched.Where(r => GeometryMatch(o.Box, r.Box)).ToList();
            foreach (var row in geometry) result.Covered.TryAdd(row, o);
            foreach (var row in matched)
            {
                if (!o.Box.Inflate(Math.Max(40, 2 * row.Box.H)).ContainsPoint(row.Box.Cx, row.Box.Cy)) continue;
                var distance = o.Box.CenterDistance(row.Box);
                if (!result.Shown.TryGetValue(row, out var existing) || distance < existing.Distance) result.Shown[row] = (o, distance);
            }
            Box? union = null;
            var posErr = double.NaN;
            var complete = byKey.Count > 0 || (lines.Count > 0 && matched.Count == lines.Count);
            if (matched.Count > 0 && complete)
            {
                var u = matched.Aggregate(default(Box), static (acc, r) => acc.Union(r.Box));
                union = u;
                var distance = Offset(o.Box, u);
                if (distance <= 0.25 * Recording.Width) posErr = distance;
            }
            var valid = geometry.Count >= needed;
            string reason;
            double? moved = null;
            if (valid) reason = "ok";
            else if (!EverSeen(o)) reason = "nieznany w prawdzie";
            else if (geometry.Count > 0) reason = "częściowo nieaktualny";
            else if (matched.Count > 0)
            {
                reason = "przesunięty";
                moved = Json.R(matched.Min(r => NearestDistance(o.Box, r.Box)));
            }
            else reason = "zniknął";
            var staticRows = matched.Count > 0 && matched.Count(r => _trackOf.TryGetValue(r, out var t) && t.Static) * 2 >= matched.Count;
            result.Blocks[o] = new BlockMatch(valid, posErr, union, matched, staticRows, reason, moved);
        }
        _matches[(state.Seq, frame)] = result;
        return result;
    }

    public void Run()
    {
        EvaluateSamples();
        EvaluatePositions();
        EvaluateLatency();
        EvaluateStale();
        EvaluateHud();
        EvaluateCost();
        EvaluateDisplay();
        EvaluateSteps();
    }

    private void EvaluateSamples()
    {
        var coverage = NewRatios();
        var holes = 0;
        var holeMs = 0.0;
        var tracksWithHoles = 0;
        foreach (var track in Tracks)
        {
            if (!Eligible(track)) continue;
            var list = new List<TrackSample>();
            foreach (var obs in track.Obs)
            {
                var f = obs.Frame;
                if (f >= Clock.Count) break;
                var match = Match(Timeline.At(Clock.Mid(f)), f);
                var covered = match.Covered.ContainsKey(obs);
                var shown = match.Shown.ContainsKey(obs);
                list.Add(new TrackSample(f, covered, shown));
                AddRatio(coverage, covered, Clock.Weight(f), track.Static, Moving[f], IsAngled(f));
            }
            Samples[track.Id] = list;
            var trackHoles = 0;
            var seenShown = false;
            var inHole = false;
            double currentHole = 0;
            foreach (var sample in list)
            {
                if (sample.Shown)
                {
                    if (inHole)
                    {
                        trackHoles++;
                        holeMs += currentHole;
                        AddTrackProblem("MIGANIE", track, sample.Frame, currentHole, 0.6 + currentHole / 1000.0,
                            $"tłumaczenie zniknęło na {currentHole:0} ms przy obecnym tekście");
                    }
                    inHole = false;
                    currentHole = 0;
                    seenShown = true;
                }
                else if (seenShown)
                {
                    inHole = true;
                    currentHole += Clock.Weight(sample.Frame);
                }
            }
            holes += trackHoles;
            if (trackHoles > 0) tracksWithHoles++;
            AddUncoveredProblems(track, list);
        }

        Metrics["coverage"] = RatiosToObject(coverage);
        Metrics["flicker"] = new { holes, tracksWithHoles, holeSeconds = Math.Round(holeMs / 1000, 2) };
        Metrics["tracks"] = new
        {
            all = Tracks.Count,
            eligible = Tracks.Count(Eligible),
            eligibleStatic = Tracks.Count(t => Eligible(t) && t.Static),
            eligibleMoving = Tracks.Count(t => Eligible(t) && !t.Static),
            shortLive = Tracks.Count(t => t.Live && !Eligible(t)),
            typing = Tracks.Count(t => Eligible(t) && t.Typing),
        };
        var angled = Enumerable.Range(0, Clock.Count).Count(IsAngled);
        Metrics["angle"] = new
        {
            frames = Clock.Count,
            angledFrames = angled,
            angledPercent = Math.Round(100.0 * angled / Math.Max(1, Clock.Count), 1),
            coverageAngled = coverage["angled"].Percent,
            coverageStraight = coverage["straight"].Percent,
            coverageStaticAngled = coverage["staticAngled"].Percent,
            coverageStaticStraight = coverage["staticStraight"].Percent,
            liveOcrCalls = Data.Ocr.Count,
            liveOcrProbed = Data.Ocr.Count(static o => o.Angle is not null),
            liveOcrAngled = Data.Ocr.Count(static o => Math.Abs(o.Angle ?? 0) >= AngleThreshold),
            liveOcrFull = Data.Ocr.Count(o => o.W >= Recording.Width && o.H >= Recording.Height),
            liveOcrFullAngled = Data.Ocr.Count(o => Math.Abs(o.Angle ?? 0) >= AngleThreshold && o.W >= Recording.Width && o.H >= Recording.Height),
            liveOcrFullProbed = Data.Ocr.Count(o => o.Angle is not null && o.W >= Recording.Width && o.H >= Recording.Height),
            liveOcrPartialAngled = Data.Ocr.Count(o => Math.Abs(o.Angle ?? 0) >= AngleThreshold && o.W < Recording.Width),
        };
    }

    private void EvaluatePositions()
    {
        var samples = new List<(double Err, bool Valid, bool Static, bool Moving, bool Angled, bool AngledOrigin)>();
        var runs = new Dictionary<string, (int Start, double Duration, double ErrSum, int Count, OBlock Block, Box Union)>(StringComparer.Ordinal);
        void Flush(string key, int endFrame)
        {
            if (!runs.Remove(key, out var run) || run.Duration < 200) return;
            var mean = run.ErrSum / run.Count;
            var mid = (run.Start + endFrame) / 2;
            var truthBoxes = TruthBoxesNear(mid, run.Union, 900);
            Problems.Add(new Problem("POZYCJA", Clock.Start[run.Start], Clock.End[endFrame], mid,
                run.Duration / 1000.0 * Math.Min(mean / 40.0, 3.0), run.Block.Short,
                $"tłumaczenie {mean:0} px od oryginału (średnio) przez {run.Duration:0} ms{(AngledOrigin(run.Block) ? " — box z pełnego OCR z kątem tekstu" : string.Empty)}",
                truthBoxes.Count > 0 ? truthBoxes : [run.Union], [run.Block.Box], run.Block.PatchBox, run.Block.Key));
        }

        for (var f = 0; f < Clock.Count; f++)
        {
            var state = Timeline.At(Clock.Mid(f));
            var match = Match(state, f);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in state.Displayed)
            {
                if (!match.Blocks.TryGetValue(o, out var bm) || double.IsNaN(bm.PosErr) || bm.Union is not { } union) continue;
                samples.Add((bm.PosErr, bm.Valid, bm.StaticRows, Moving[f], IsAngled(f), AngledOrigin(o)));
                var bad = bm.PosErr > Math.Max(16, 0.6 * bm.Rows.Min(static r => r.Box.H));
                if (!bad) continue;
                seen.Add(o.Key);
                runs[o.Key] = runs.TryGetValue(o.Key, out var run)
                    ? run with { Duration = run.Duration + Clock.Weight(f), ErrSum = run.ErrSum + bm.PosErr, Count = run.Count + 1, Block = o }
                    : (f, Clock.Weight(f), bm.PosErr, 1, o, union);
            }
            foreach (var key in runs.Keys.Where(k => !seen.Contains(k)).ToList()) Flush(key, Math.Max(0, f - 1));
        }
        foreach (var key in runs.Keys.ToList()) Flush(key, Clock.Count - 1);

        Metrics["position"] = new
        {
            covered = Summary(samples.Where(static p => p.Valid).Select(static p => p.Err)),
            coveredStatic = Summary(samples.Where(static p => p.Valid && p.Static).Select(static p => p.Err)),
            coveredMovingText = Summary(samples.Where(static p => p.Valid && !p.Static).Select(static p => p.Err)),
            coveredCameraMotion = Summary(samples.Where(static p => p.Valid && p.Moving).Select(static p => p.Err)),
            coveredCameraRest = Summary(samples.Where(static p => p.Valid && !p.Moving).Select(static p => p.Err)),
            anyContentMatch = Summary(samples.Select(static p => p.Err)),
            anyContentMatchStatic = Summary(samples.Where(static p => p.Static).Select(static p => p.Err)),
            anyContentMatchMovingText = Summary(samples.Where(static p => !p.Static).Select(static p => p.Err)),
            anyCameraMotion = Summary(samples.Where(static p => p.Moving).Select(static p => p.Err)),
            anyCameraRest = Summary(samples.Where(static p => !p.Moving).Select(static p => p.Err)),
            anyAngledFrames = Summary(samples.Where(static p => p.Angled).Select(static p => p.Err)),
            anyStraightFrames = Summary(samples.Where(static p => !p.Angled).Select(static p => p.Err)),
            fromAngledFullOcr = Summary(samples.Where(static p => p.AngledOrigin).Select(static p => p.Err)),
            fromStraightOcr = Summary(samples.Where(static p => !p.AngledOrigin).Select(static p => p.Err)),
            fromAngledFullOcrOver16Px = samples.Count(static p => p.AngledOrigin && p.Err > 16),
            fromStraightOcrOver16Px = samples.Count(static p => !p.AngledOrigin && p.Err > 16),
        };
    }

    private static Dictionary<string, Ratio> NewRatios() => new(StringComparer.Ordinal)
    {
        ["all"] = new(), ["static"] = new(), ["moving"] = new(), ["cameraMotion"] = new(), ["cameraRest"] = new(),
        ["staticCameraMotion"] = new(), ["staticCameraRest"] = new(), ["movingCameraMotion"] = new(), ["movingCameraRest"] = new(),
        ["angled"] = new(), ["straight"] = new(), ["staticAngled"] = new(), ["staticStraight"] = new(),
    };

    private static void AddRatio(Dictionary<string, Ratio> ratios, bool covered, double weight, bool isStatic, bool moving, bool angled)
    {
        ratios["all"].Add(covered, weight);
        ratios[isStatic ? "static" : "moving"].Add(covered, weight);
        ratios[moving ? "cameraMotion" : "cameraRest"].Add(covered, weight);
        ratios[(isStatic ? "static" : "moving") + (moving ? "CameraMotion" : "CameraRest")].Add(covered, weight);
        ratios[angled ? "angled" : "straight"].Add(covered, weight);
        if (isStatic) ratios[angled ? "staticAngled" : "staticStraight"].Add(covered, weight);
    }

    private static object RatiosToObject(Dictionary<string, Ratio> ratios) =>
        ratios.ToDictionary(static kv => kv.Key, static kv => (object)new { percent = kv.Value.Percent, seconds = kv.Value.Seconds });

    public static object Summary(IEnumerable<double> values)
    {
        var (median, p90, max, count) = Stats.Summary(values);
        return new { median = Json.R(median), p90 = Json.R(p90), max = Json.R(max), count };
    }

    private void AddUncoveredProblems(TruthTrack track, List<TrackSample> list)
    {
        var start = -1;
        double duration = 0;
        var initial = true;
        for (var k = 0; k <= list.Count; k++)
        {
            var uncovered = k < list.Count && !list[k].Covered;
            if (uncovered)
            {
                if (start < 0) start = k;
                duration += Clock.Weight(list[k].Frame);
                continue;
            }
            if (start >= 0 && duration >= 300)
            {
                var mid = list[(start + k - 1) / 2].Frame;
                var typingHold = initial && track.Typing && list[k - 1].Frame <= track.TypingEndFrame + 3;
                var atStart = initial && Clock.Start[list[start].Frame] < AtStartMs;
                var kind = initial ? (atStart ? "BRAK (start sesji)" : typingHold ? "BRAK (pisanie literami)" : "BRAK (opóźnienie)") : "BRAK (zgubione)";
                var score = duration / 1000.0 * (typingHold ? 0.4 : atStart ? 0.3 : 1.0);
                AddTrackProblem(kind, track, mid, duration, score,
                    $"{kind}: brak tłumaczenia przez {duration:0} ms ({(track.Static ? "tekst stały" : "tekst ruchomy")})",
                    Clock.Start[list[start].Frame], Clock.End[list[k - 1].Frame]);
            }
            if (k < list.Count) initial = false;
            start = -1;
            duration = 0;
        }
    }

    private void AddTrackProblem(string type, TruthTrack track, int frame, double durationMs, double score, string detail,
        double? start = null, double? end = null)
    {
        frame = Math.Clamp(frame, 0, Clock.Count - 1);
        var obs = track.Obs.LastOrDefault(o => o.Frame <= frame) ?? track.Obs[0];
        var state = Timeline.At(Clock.Mid(frame));
        var overlay = state.Displayed.Where(o => o.Box.CenterDistance(obs.Box) <= Math.Max(400, 3 * obs.Box.W)).Select(static o => o.Box).ToList();
        var endMs = end ?? Clock.Start[frame];
        var startMs = start ?? endMs - durationMs;
        Problems.Add(new Problem(type, startMs, endMs, frame, score, track.Short, detail, [obs.Box], overlay, null, $"t{track.Id}"));
    }

    private void EvaluateLatency()
    {
        foreach (var track in Tracks)
        {
            if (!Eligible(track)) continue;
            var startFrame = track.First;
            var startMs = Clock.Start[startFrame];
            double? found = null;
            foreach (var obs in track.Obs)
            {
                var f = obs.Frame;
                if (f >= Clock.Count) break;
                var times = new List<double> { Clock.Start[f] };
                times.AddRange(Timeline.ChangesBetween(Clock.Start[f], Clock.End[f]).Select(static s => s.T));
                foreach (var t in times)
                {
                    if (Match(Timeline.At(t), f).Covered.ContainsKey(obs))
                    {
                        found = t - startMs;
                        break;
                    }
                }
                if (found is not null) break;
            }
            var startedInMotion = Enumerable.Range(startFrame, Math.Min(5, Clock.Count - startFrame)).Any(f => Moving[f]);
            double? fromTyping = track.Typing && found is { } latency
                ? startMs + latency - Clock.Start[Math.Min(track.TypingEndFrame, Clock.Count - 1)]
                : null;
            Latencies.Add(new LatencyItem(track.Id, track.Short, track.Static, track.Typing, startedInMotion, startMs < AtStartMs,
                Json.R(startMs), startFrame, found is { } value ? Json.R(value) : null, fromTyping is { } typing ? Json.R(typing) : null));
        }
        var shown = Latencies.Where(static l => l.LatencyMs is not null && !l.AtStart).ToList();
        Metrics["latency"] = new
        {
            episodes = Latencies.Count(static l => !l.AtStart),
            neverShown = Latencies.Count(static l => l.LatencyMs is null && !l.AtStart),
            neverShownStatic = Latencies.Count(static l => l.LatencyMs is null && !l.AtStart && l.Static),
            all = Summary(shown.Select(static l => l.LatencyMs!.Value)),
            nonTyping = Summary(shown.Where(static l => !l.Typing).Select(static l => l.LatencyMs!.Value)),
            nonTypingStatic = Summary(shown.Where(static l => !l.Typing && l.Static).Select(static l => l.LatencyMs!.Value)),
            nonTypingMoving = Summary(shown.Where(static l => !l.Typing && !l.Static).Select(static l => l.LatencyMs!.Value)),
            startedInMotion = Summary(shown.Where(static l => l.StartedInMotion && !l.Typing).Select(static l => l.LatencyMs!.Value)),
            startedAtRest = Summary(shown.Where(static l => !l.StartedInMotion && !l.Typing).Select(static l => l.LatencyMs!.Value)),
            typingFromEnd = Summary(shown.Where(static l => l.FromTypingEndMs is not null).Select(static l => l.FromTypingEndMs!.Value)),
            atSessionStart = Summary(Latencies.Where(static l => l.AtStart && l.LatencyMs is not null).Select(static l => l.LatencyMs!.Value)),
            atSessionStartNeverShown = Latencies.Count(static l => l.AtStart && l.LatencyMs is null),
            list = Latencies.OrderBy(static l => l.StartMs).ToList(),
        };
    }

    private void EvaluateStale()
    {
        var open = new Dictionary<string, (double Start, int StartFrame, string Reason, double? Moved, OBlock Block)>(StringComparer.Ordinal);
        var lastEnd = new Dictionary<string, (double End, int Frame)>(StringComparer.Ordinal);
        void Close(string key)
        {
            if (!open.Remove(key, out var run)) return;
            var (end, frame) = lastEnd[key];
            if (end > run.Start)
                StaleRuns.Add(new StaleRun(key, run.Block.Short, run.Start, end, run.StartFrame, frame, run.Reason, run.Moved, run.Block.Box));
        }

        for (var f = 0; f < Clock.Count; f++)
        {
            var points = new List<double> { Clock.Start[f] };
            points.AddRange(Timeline.ChangesBetween(Clock.Start[f], Clock.End[f]).Select(static s => s.T));
            points.Add(Clock.End[f]);
            for (var p = 0; p + 1 < points.Count; p++)
            {
                var a = points[p];
                var b = points[p + 1];
                if (b <= a) continue;
                var state = Timeline.At(a);
                var match = Match(state, f);
                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (var o in state.Displayed)
                {
                    present.Add(o.Key);
                    var bm = match.Blocks[o];
                    if (bm.Valid)
                    {
                        Close(o.Key);
                        continue;
                    }
                    if (!open.ContainsKey(o.Key)) open[o.Key] = (a, f, bm.Reason, bm.MovedPx, o);
                    lastEnd[o.Key] = (b, f);
                }
                foreach (var key in open.Keys.Where(k => !present.Contains(k)).ToList()) Close(key);
            }
        }
        foreach (var key in open.Keys.ToList()) Close(key);

        var known = StaleRuns.Where(static r => r.Reason != "nieznany w prawdzie").ToList();
        Metrics["stale"] = new
        {
            seconds = Math.Round(known.Sum(static r => r.DurationMs) / 1000, 2),
            events = known.Count(static r => r.DurationMs >= 200),
            shortEvents = known.Count(static r => r.DurationMs < 200),
            disappearedSeconds = Math.Round(known.Where(static r => r.Reason == "zniknął").Sum(static r => r.DurationMs) / 1000, 2),
            movedSeconds = Math.Round(known.Where(static r => r.Reason == "przesunięty").Sum(static r => r.DurationMs) / 1000, 2),
            partialSeconds = Math.Round(known.Where(static r => r.Reason == "częściowo nieaktualny").Sum(static r => r.DurationMs) / 1000, 2),
            inCameraMotionSeconds = Math.Round(known.Sum(r => Enumerable.Range(r.StartFrame, r.EndFrame - r.StartFrame + 1)
                .Where(f => Moving[f]).Sum(f => Math.Max(0, Math.Min(r.EndMs, Clock.End[f]) - Math.Max(r.StartMs, Clock.Start[f])))) / 1000, 2),
            unknownInTruthSeconds = Math.Round(StaleRuns.Where(static r => r.Reason == "nieznany w prawdzie").Sum(static r => r.DurationMs) / 1000, 2),
            unknownInTruthEvents = StaleRuns.Count(static r => r.Reason == "nieznany w prawdzie" && r.DurationMs >= 200),
            longest = known.OrderByDescending(static r => r.DurationMs).Take(10)
                .Select(r => new { r.Text, startMs = Json.R(r.StartMs), durationMs = Json.R(r.DurationMs), r.StartFrame, r.Reason, r.MovedPx })
                .ToList(),
        };
        foreach (var run in StaleRuns.Where(static r => r.DurationMs >= 300))
        {
            var weight = run.Reason == "nieznany w prawdzie" ? 0.5 : 1.5;
            var mid = (run.StartFrame + run.EndFrame) / 2;
            var truthBoxes = TruthBoxesNear(mid, run.Box, 900);
            var detail = run.Reason switch
            {
                "przesunięty" => $"tłumaczenie zostaje w starym miejscu ({run.MovedPx:0} px od tekstu) przez {run.DurationMs:0} ms",
                "częściowo nieaktualny" => $"blok tłumaczenia zawiera wiersze, których już nie ma na ekranie, przez {run.DurationMs:0} ms",
                "zniknął" => $"tłumaczenie wisi {run.DurationMs:0} ms po zniknięciu tekstu",
                _ => $"tłumaczenie odczytu, którego prawda nie zna, przez {run.DurationMs:0} ms",
            };
            Problems.Add(new Problem("NIEAKTUALNE", run.StartMs, run.EndMs, mid, run.DurationMs / 1000.0 * weight, run.Text, detail,
                truthBoxes, [run.Box], null, run.Key));
        }
    }

    private void EvaluateHud()
    {
        var hudTracks = Tracks.Where(t => Eligible(t) && t.Static && !t.Typing).ToList();
        foreach (var track in hudTracks)
        {
            if (!Samples.TryGetValue(track.Id, out var list)) continue;
            for (var k = 1; k < list.Count; k++)
            {
                if (!list[k - 1].Covered || list[k].Covered || list[k].Frame != list[k - 1].Frame + 1) continue;
                var f = list[k].Frame;
                var change = Timeline.ChangesBetween(Clock.Mid(f - 1), Clock.Mid(f) + 0.001).LastOrDefault();
                if (change is null)
                {
                    HudLosses.Add(new HudLoss(track.Id, track.Short, f, Json.R(Clock.Start[f]), "zmiana prawdy (bez aktualizacji)", null, null, null, null));
                    continue;
                }
                _updatesBySeq.TryGetValue(change.Seq, out var update);
                var capture = CaptureOf(change.Seq);
                string cause;
                if (update?.Clear == true) cause = "czyszczenie nakładki (zmiana widoku)";
                else if (capture is null) cause = "usunięcie lokalne (bez OCR)";
                else if (capture.Value.Partial) cause = "odczyt wycinka";
                else if (Math.Abs(capture.Value.TruthAngle ?? 0) >= AngleThreshold || Math.Abs(capture.Value.ProbeAngle ?? 0) >= AngleThreshold)
                    cause = "pełny odczyt z kątem tekstu";
                else cause = "pełny odczyt bez kąta";
                HudLosses.Add(new HudLoss(track.Id, track.Short, f, Json.R(Clock.Start[f]), cause, capture?.TruthAngle, capture?.ProbeAngle,
                    change.Seq, update?.Status));
            }
        }
        var garbled = new Dictionary<string, (string Src, string Hud, int Frames)>(StringComparer.Ordinal);
        for (var f = 0; f < Clock.Count; f++)
        {
            var state = Timeline.At(Clock.Mid(f));
            foreach (var x in ObsAt(f))
            {
                if (!x.Track.Static || !Eligible(x.Track)) continue;
                foreach (var o in state.Displayed)
                {
                    if (o.Src is not { } src || o.KeyHash == x.Obs.BlockKeyHash) continue;
                    if (!x.Obs.Box.Inflate(Math.Max(16, 2 * x.Obs.Box.H)).ContainsPoint(o.Box.Cx, o.Box.Cy)) continue;
                    var similarity = TextTools.Lines(src).Select(l => TextTools.Similarity(l, x.Track.Norm)).DefaultIfEmpty(0).Max();
                    if (similarity is < 0.3 or >= 0.95) continue;
                    var previous = garbled.TryGetValue(o.Key, out var g) ? g.Frames : 0;
                    garbled[o.Key] = (src, x.Track.Norm, previous + 1);
                }
            }
        }
        var hudWeight = new Ratio();
        foreach (var track in hudTracks)
            if (Samples.TryGetValue(track.Id, out var list))
                foreach (var sample in list) hudWeight.Add(sample.Covered, Clock.Weight(sample.Frame));
        Metrics["hud"] = new
        {
            staticTracks = hudTracks.Count,
            coverage = hudWeight.Percent,
            losses = HudLosses.Count,
            lossesByCause = HudLosses.GroupBy(static l => l.Cause).ToDictionary(static g => g.Key, static g => g.Count()),
            lossesAtAngledFrames = HudLosses.Count(l => IsAngled(l.Frame)),
            lossesByAngledFullOcr = HudLosses.Count(static l => l.Cause == "pełny odczyt z kątem tekstu"),
            lossesInCameraMotion = HudLosses.Count(l => Moving[Math.Clamp(l.Frame, 0, Clock.Count - 1)]),
            garbledReadings = garbled.Count,
            garbledExamples = garbled.Values.OrderByDescending(static g => g.Frames).Take(8)
                .Select(static g => new { src = TextTools.Short(g.Src, 40), hud = TextTools.Short(g.Hud, 40), frames = g.Frames }).ToList(),
            list = HudLosses.Take(80).ToList(),
        };
    }

    [GeneratedRegex(@"mocne (\d+)%")]
    private static partial Regex StrongPattern();

    private void EvaluateCost()
    {
        var diag = Data.Updates.Where(static u => u.Diag is not null).Select(static u => u.Diag!).ToList();
        var observe = Data.Updates.Where(static u => u.Status.StartsWith("Live: obserwuję", StringComparison.Ordinal)).ToList();
        var motionSkips = observe.Count(u => StrongPattern().Match(u.Status) is { Success: true } m && int.Parse(m.Groups[1].Value) >= 12);
        var ocrStarts = Data.Ocr.Where(static o => o.StartMs >= 0).Select(static o => o.StartMs).Order().ToList();
        var gaps = new List<(double Start, double Duration)>();
        for (var k = 1; k < ocrStarts.Count; k++)
            if (ocrStarts[k] - ocrStarts[k - 1] >= 1000) gaps.Add((ocrStarts[k - 1], ocrStarts[k] - ocrStarts[k - 1]));
        var gapsInMotion = gaps.Count(g => Moving[Clock.FrameAt(g.Start + g.Duration / 2)]);
        var full = Data.Ocr.Count(o => o.W >= Recording.Width && o.H >= Recording.Height);
        Metrics["cost"] = new
        {
            completedFrames = diag.Count,
            partialFrames = diag.Count(static d => d.PartialOcr),
            fullFrames = diag.Count(static d => !d.PartialOcr),
            sceneCutFrames = diag.Count(static d => d.SceneCut),
            whiffFrames = diag.Count(static d => d.WhiffSuspected),
            screenFallbackFrames = diag.Count(static d => d.UsedScreenFallback),
            ocrCalls = Data.Ocr.Count,
            ocrFullFrame = full,
            ocrPartial = Data.Ocr.Count - full,
            ocrDiscarded = Math.Max(0, Data.Ocr.Count - diag.Count),
            captureToUpdateMs = Summary(diag.Select(static d => d.CaptureToUpdateMs)),
            captureMs = Summary(diag.Select(static d => (double)d.CaptureMs)),
            ocrMs = Summary(diag.Select(static d => (double)d.OcrMs)),
            ocrOperationMs = Summary(diag.Where(static d => d.OcrOperationMs is not null).Select(static d => d.OcrOperationMs!.Value)),
            ocrFullOperationMs = Summary(Data.Ocr.Where(o => o.W >= Recording.Width).Select(static o => o.EndMs - o.StartMs)),
            ocrPartialOperationMs = Summary(Data.Ocr.Where(o => o.W < Recording.Width).Select(static o => o.EndMs - o.StartMs)),
            translateMs = Summary(diag.Select(static d => (double)d.TranslateMs)),
            glyphCoverMs = Summary(diag.Where(static d => d.GlyphCoverMs > 0).Select(static d => d.GlyphCoverMs)),
            glyphCoverWaitMs = Summary(diag.Select(static d => d.GlyphCoverWaitMs)),
            sceneChecksPerFrame = Summary(diag.Select(static d => (double)(d.OcrSceneChecks + d.TranslationSceneChecks))),
            clears = Data.Updates.Count(static u => u.Clear),
            clearsInCameraMotion = Data.Updates.Count(u => u.Clear && Moving[Clock.FrameAt(u.AppliedMs)]),
            hides = Data.Updates.Count(static u => u.Hide),
            observeSamples = observe.Count,
            observeSamplesInMotion = motionSkips,
            ocrGapsOver1s = gaps.Count,
            ocrGapsOver1sInMotion = gapsInMotion,
            ocrGapSeconds = Math.Round(gaps.Sum(static g => g.Duration) / 1000, 1),
            ocrGapMaxMs = gaps.Count == 0 ? 0 : Json.R(gaps.Max(static g => g.Duration)),
            ocrGapList = gaps.OrderByDescending(static g => g.Duration).Take(10)
                .Select(g => new { startMs = Json.R(g.Start), durationMs = Json.R(g.Duration), frame = Clock.FrameAt(g.Start) }).ToList(),
        };
    }

    private void EvaluateDisplay()
    {
        var late = Data.Shown.Where(static s => s.SetMs >= 0).Select(static s => s.SetMs - s.DueMs).ToList();
        var lateRender = Data.Shown.Where(static s => s.RenderMs >= 0).Select(static s => s.RenderMs - s.DueMs).ToList();
        var recreations = 0;
        var lastElement = new Dictionary<string, int>(StringComparer.Ordinal);
        var fading = 0;
        foreach (var shown in Data.Shown)
        {
            var current = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var element in shown.Elements)
            {
                if (element.Key is null) continue;
                current[element.Key] = element.Element;
                if (lastElement.TryGetValue(element.Key, out var previous) && previous != element.Element) recreations++;
                if (element.Opacity < 0.99) fading++;
            }
            lastElement = current;
        }
        Metrics["display"] = new
        {
            frames = Data.Shown.Count,
            lateSetMs = Summary(late),
            lateRenderMs = Summary(lateRender),
            decodeStalls = Data.Shown.Count(static s => s.DecodeWaitMs > 1),
            overlayHiddenFrames = Data.Shown.Count(static s => !s.OverlayVisible),
            elementRecreations = recreations,
            elementsFadingSamples = fading,
            fidelity = Data.Header.Fidelity,
        };
    }

    private (IReadOnlyList<MotionStep> Steps, double Offset) AlignSteps()
    {
        var clock = Recording.LoadStepClock();
        if (clock.Count == 0) return ([], 0);
        var end = Recording.RelativeMs(Recording.Frames.Count - 1) + Recording.NominalIntervalMs;
        var best = 1000.0;
        var bestScore = double.MinValue;
        for (var offset = 0.0; offset <= 3000; offset += 50)
        {
            var steps = Recording.PlaceSteps(clock, offset, end);
            var agree = 0;
            var total = 0;
            for (var f = 0; f < Recording.Frames.Count && f < _stepOfFrame.Length; f++)
            {
                var t = RecordingMs(f);
                var step = steps.FirstOrDefault(s => t >= s.StartMs && t < s.EndMs);
                if (step is null || step.Label == "koniec") continue;
                total++;
                if (step.IsMotion == Moving[f]) agree++;
            }
            var score = total == 0 ? 0 : (double)agree / total;
            if (score > bestScore + 1e-9)
            {
                bestScore = score;
                best = offset;
            }
        }
        return (Recording.PlaceSteps(clock, best, end), best);
    }

    private void EvaluateSteps()
    {
        if (Steps.Count == 0)
        {
            Metrics["steps"] = null;
            return;
        }
        var rows = new List<object>();
        foreach (var step in Steps)
        {
            var frames = Enumerable.Range(0, Clock.Count).Where(f => RecordingMs(f) >= step.StartMs && RecordingMs(f) < step.EndMs).ToHashSet();
            if (frames.Count == 0) continue;
            var all = new Ratio();
            var hud = new Ratio();
            var moving = new Ratio();
            foreach (var track in Tracks)
            {
                if (!Samples.TryGetValue(track.Id, out var list)) continue;
                foreach (var sample in list)
                {
                    if (!frames.Contains(sample.Frame)) continue;
                    var weight = Clock.Weight(sample.Frame);
                    all.Add(sample.Covered, weight);
                    (track.Static ? hud : moving).Add(sample.Covered, weight);
                }
            }
            var from = Clock.Start[frames.Min()];
            var to = Clock.End[frames.Max()];
            var stale = StaleRuns.Where(static r => r.Reason != "nieznany w prawdzie").Sum(r => Overlap(r.StartMs, r.EndMs, from, to));
            rows.Add(new
            {
                step.Label,
                startS = Math.Round(step.StartMs / 1000, 1),
                endS = Math.Round(step.EndMs / 1000, 1),
                motion = step.IsMotion,
                cameraMotionFrames = frames.Count(f => Moving[f]),
                angledFrames = frames.Count(IsAngled),
                coverage = all.Percent,
                coverageStatic = hud.Percent,
                coverageMoving = moving.Percent,
                staleSeconds = Math.Round(stale / 1000, 2),
                hudLosses = HudLosses.Count(l => frames.Contains(l.Frame)),
                clears = Data.Updates.Count(u => u.Clear && u.AppliedMs >= from && u.AppliedMs < to),
            });
        }
        Metrics["steps"] = new { offsetMs = StepOffsetMs, rows };
    }

    private static double Overlap(double a0, double a1, double b0, double b1) => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));

    public static double Offset(Box overlay, Box truth)
    {
        var dy = Math.Abs(overlay.Cy - truth.Cy);
        var dx = Math.Min(Math.Abs(overlay.Cx - truth.Cx), Math.Min(Math.Abs(overlay.X - truth.X), Math.Abs(overlay.Right - truth.Right)));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static double NearestDistance(Box box, Box other)
    {
        var dx = Math.Max(0, Math.Max(box.X - other.Right, other.X - box.Right));
        var dy = Math.Max(0, Math.Max(box.Y - other.Bottom, other.Y - box.Bottom));
        return Math.Sqrt((double)dx * dx + (double)dy * dy);
    }

    public IReadOnlyList<Box> TruthBoxesNear(int frame, Box around, double radius) =>
        ObsAt(frame).Where(x => x.Track.Live && x.Obs.Box.CenterDistance(around) <= radius).Select(static x => x.Obs.Box).ToList();
}
