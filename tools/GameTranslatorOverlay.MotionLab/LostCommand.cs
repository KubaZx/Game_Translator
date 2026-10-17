using System.Diagnostics;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameTranslatorOverlay.App.Ocr;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Vision;
using Microsoft.Extensions.Logging;

internal sealed record LostOptions(string Out, string Placement, int OcrEvery, int MaxFrames, string Corpus, string Profile, int PeriodicEvery);

internal static class LostCommand
{
    private const double MaxShift = 72;
    private const double MotionThreshold = 0.12;
    private const double SceneCutThreshold = 0.55;
    private const double TruthSimilarity = 0.6;

    private sealed record Track(GlyphCover Cover, float[] Signature, int Absences = 0, int VelocityX = 0, int VelocityY = 0, bool Moving = false, double LineHeight = 0);

    private sealed class Followed
    {
        public required string Key { get; init; }
        public required string Source { get; init; }
        public RectPx Box { get; set; }
        public int LineHeight { get; set; }
        public required GlyphCover Cover { get; set; }
        public Track? State { get; set; }
        public Pending? Waiting { get; set; }
        public double MovedAtMs { get; set; } = double.NegativeInfinity;
    }

    private sealed record Pending(int Frame, LostTextFound? Found, GlyphCover Cover, int VelocityX, int VelocityY, double Step, int Event);

    private sealed record LossDto(
        int Event, int Frame, double TMs, string Strategy, string Key, string Source, string Reason, bool Moving, bool SceneCut,
        bool Travelling, bool LettersGone, int VelocityXPx, int VelocityYPx, Box Box, Box ReadArea, bool Eligible, bool BudgetAllowed,
        bool TruthInArea, bool TruthAnywhere, double? TruthDistancePx,
        ReadDto? ReadNow, bool? RetryNext, bool? RetryNextAtTruth, ReadDto? ReadNext, string? Outcome);

    private sealed record ReadDto(double OcrMs, int Width, int Height, int Lines, int Blocks, bool Found, Box? FoundBox, bool? AtTruth,
        bool CoverBuilt, string? Read);

    private sealed record PeriodicDto(int Frame, string Key, double OcrMs, int Width, int Height, bool Same, bool TruthPresent);

    private sealed record PassDto(
        string Strategy, int Frames, int MotionFrames, int TruthPairsInMotion, int CoveredInMotion, int TruthPairsStill, int CoveredStill,
        int WrongInMotion, int WrongStill, int Losses, int LossesInMotion, int Eligible, int Reads, int Recovered, int RecoveredAtTruth,
        int RecoveredWrong, int RecoveredLostNext, int RemovedAfterRead, int RebuiltUsed, int WrongWhileWaiting, double ReadMsMedian, double ReadMsP90, double ReadMsMax,
        double FullOcrMsMedian, int FullOcrCount);

    public static int Run(string recordingName, LostOptions options)
    {
        var recording = Recording.Load(recordingName);
        if (!File.Exists(recording.TruthPath(false))) throw new ArgumentException($"Brak prawdy bez podziału imion: najpierw MotionLab truth {recording.Name} --plain");
        var (_, truthFrames) = TruthCommand.Load(recording.TruthPath(false));
        var truth = truthFrames.ToDictionary(static t => t.I);
        Directory.CreateDirectory(options.Out);
        if (Directory.EnumerateFileSystemEntries(options.Out).Any()) throw new ArgumentException($"Katalog wyniku nie jest pusty: {options.Out}");

        var root = Path.Combine(Path.GetTempPath(), "gto-motionlab-lost-" + Guid.NewGuid().ToString("N"));
        using var loggers = LoggerFactory.Create(static b => b.SetMinimumLevel(LogLevel.Warning));
        var ocr = new WindowsOcrProvider();
        using var environment = LabEnvironment.Create(root,
            new LabOptions(null, options.Corpus, options.Profile, 0, options.Placement, "at-source", 0.4, 0, "auto"), ocr, loggers, keep: false);
        var orchestrator = environment.Orchestrator;
        var ignore = (IReadOnlyList<RelativeRegion>)(orchestrator.ActiveProfile?.Live?.IgnoreRegions ?? []);
        var upscale = OcrScaling.ResolvePreference(orchestrator.ActiveProfile?.Ocr?.Upscale, environment.Settings.OcrUpscale);
        var count = options.MaxFrames > 0 ? Math.Min(options.MaxFrames, recording.Frames.Count) : recording.Frames.Count;
        var patch = options.Placement == "cover";
        var lab = new Lab(recording, truth, ocr, orchestrator, ignore, upscale, patch, options, count);

        Console.WriteLine($"lost: {recording.Name}, {count} klatek, OCR co {options.OcrEvery} kl., łatki {(patch ? "pełne" : "lekkie")}");
        var watch = Stopwatch.StartNew();
        using var events = new JsonlWriter(Path.Combine(options.Out, "events.jsonl"));
        using var periodic = new JsonlWriter(Path.Combine(options.Out, "periodic.jsonl"));
        using var unsupported = new JsonlWriter(Path.Combine(options.Out, "unsupported.jsonl"));
        var before = Task.Run(() => lab.RunPass("bez", events, periodic, unsupported)).GetAwaiter().GetResult();
        Console.WriteLine($"  przebieg „bez”: {before.Losses} zgubień ({watch.Elapsed.TotalSeconds:0} s)");
        var after = Task.Run(() => lab.RunPass("odczyt", events, null, unsupported)).GetAwaiter().GetResult();
        Console.WriteLine($"  przebieg „odczyt”: {after.Recovered} odzyskanych z {after.Reads} odczytów ({watch.Elapsed.TotalSeconds:0} s)");
        var summary = new { recording = recording.Name, frames = count, options.OcrEvery, options.Placement, before, after, lab.PeriodicSummary };
        File.WriteAllText(Path.Combine(options.Out, "summary.json"), System.Text.Json.JsonSerializer.Serialize(summary, Json.Indented));
        File.WriteAllText(Path.Combine(options.Out, "RAPORT.md"), Report(recording.Name, count, options, before, after, lab), new UTF8Encoding(false));
        Console.WriteLine($"lost: gotowe w {watch.Elapsed.TotalSeconds:0} s → {options.Out}");
        return 0;
    }

    private static string Report(string name, int frames, LostOptions options, PassDto before, PassDto after, Lab lab)
    {
        static string P(int part, int all) => all == 0 ? "—" : $"{100.0 * part / all:0.0} %";
        var sb = new StringBuilder();
        sb.AppendLine($"# MotionLab lost — {name}");
        sb.AppendLine();
        sb.AppendLine($"{frames} klatek, pełny OCR co {options.OcrEvery} kl., łatki: {options.Placement}. Zgubienia śladu liczone tą samą ścieżką co sesja (`GlyphFollow`), odczyt lokalny przez `LostTextRecovery`, prawda z `truth.jsonl` (podobieństwo ≥ {TruthSimilarity}).");
        sb.AppendLine();
        sb.AppendLine("| Metryka | bez odczytu | z odczytem |");
        sb.AppendLine("|---|---:|---:|");
        sb.AppendLine($"| Pokrycie tekstu w ruchu (para klatka×blok prawdy) | {P(before.CoveredInMotion, before.TruthPairsInMotion)} ({before.CoveredInMotion}/{before.TruthPairsInMotion}) | {P(after.CoveredInMotion, after.TruthPairsInMotion)} ({after.CoveredInMotion}/{after.TruthPairsInMotion}) |");
        sb.AppendLine($"| Pokrycie w spoczynku | {P(before.CoveredStill, before.TruthPairsStill)} | {P(after.CoveredStill, after.TruthPairsStill)} |");
        sb.AppendLine($"| Bloki bez poparcia prawdy (klatki): ruch / spoczynek | {before.WrongInMotion} / {before.WrongStill} | {after.WrongInMotion} / {after.WrongStill} (w tym czekające na odczyt: {after.WrongWhileWaiting}) |");
        sb.AppendLine($"| Zgubienia śladu (w ruchu) | {before.Losses} ({before.LossesInMotion}) | {after.Losses} ({after.LossesInMotion}) |");
        sb.AppendLine($"| Uprawnione do odczytu / odczyty | {before.Eligible} / — | {after.Eligible} / {after.Reads} |");
        sb.AppendLine($"| Odzyskane (w miejscu prawdy / gdzie indziej) | — | {after.Recovered} ({after.RecoveredAtTruth} / {after.RecoveredWrong}) |");
        sb.AppendLine($"| Odzyskane, ale zgubione w następnej klatce | — | {after.RecoveredLostNext} |");
        sb.AppendLine($"| Odzyskane śladem odbudowanym z wycinka (reszta — dawnym śladem) | — | {after.RebuiltUsed} |");
        sb.AppendLine($"| Odczyt bez potwierdzenia → usunięty | — | {after.RemovedAfterRead} |");
        sb.AppendLine($"| OCR wycinka: mediana / p90 / max [ms] | — | {after.ReadMsMedian:0.0} / {after.ReadMsP90:0.0} / {after.ReadMsMax:0.0} |");
        sb.AppendLine($"| Pełny OCR klatki: mediana [ms] (n) | {before.FullOcrMsMedian:0.0} ({before.FullOcrCount}) | {after.FullOcrMsMedian:0.0} ({after.FullOcrCount}) |");
        sb.AppendLine();
        sb.AppendLine("## Zgubienia przebiegu „bez” — co dałyby strategie (prawda w obszarze odczytu)");
        sb.AppendLine();
        sb.AppendLine("| Grupa | n | prawda w obszarze | odczyt teraz: trafiony / fałszywy / pudło | ponowne śledzenie w nast. klatce: trafione / fałszywe | odczyt w nast. klatce: trafiony / fałszywy |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var row in lab.StrategyRows()) sb.AppendLine(row);
        sb.AppendLine();
        sb.AppendLine("## Wycinki wokół śledzonych bloków w ruchu (co " + options.PeriodicEvery + " kl.)");
        sb.AppendLine();
        sb.AppendLine(lab.PeriodicLine());
        return sb.ToString();
    }

    private sealed class Lab(
        Recording recording, IReadOnlyDictionary<int, TruthFrameDto> truth, WindowsOcrProvider ocr,
        GameTranslatorOverlay.App.Services.TranslationOrchestrator orchestrator, IReadOnlyList<RelativeRegion> ignore,
        UpscalePreference upscale, bool patch, LostOptions options, int count)
    {
        private readonly Dictionary<int, (IReadOnlyList<KeyedTextBlock> Blocks, double Ms)> _fullOcr = [];
        private readonly List<LossDto> _baseline = [];
        private readonly List<PeriodicDto> _periodic = [];
        private int _event;

        public object PeriodicSummary => new
        {
            reads = _periodic.Count,
            ocrMsMedian = Median(_periodic.Select(static p => p.OcrMs)),
            ocrMsP90 = Percentile(_periodic.Select(static p => p.OcrMs), 0.9),
            megapixelsMedian = Median(_periodic.Select(static p => p.Width * (double)p.Height / 1e6)),
            same = _periodic.Count(static p => p.Same),
            truthPresent = _periodic.Count(static p => p.TruthPresent),
        };

        public string PeriodicLine() =>
            $"{_periodic.Count} odczytów wycinka (blok + zapas max(16 px, wysokość linii)); OCR mediana {Median(_periodic.Select(static p => p.OcrMs)):0.0} ms, " +
            $"p90 {Percentile(_periodic.Select(static p => p.OcrMs), 0.9):0.0} ms, wycinek mediana {Median(_periodic.Select(static p => p.Width * (double)p.Height / 1e6)):0.00} Mpx; " +
            $"ten sam tekst w {_periodic.Count(static p => p.Same)}, prawda obecna w {_periodic.Count(static p => p.TruthPresent)}, " +
            $"tekst inny niż pokazany przy obecnej prawdzie: {_periodic.Count(static p => !p.Same && p.TruthPresent)}.";

        public IEnumerable<string> StrategyRows()
        {
            static string Row(string label, IReadOnlyList<LossDto> list)
            {
                var truthIn = list.Count(static e => e.TruthInArea);
                var readHit = list.Count(static e => e.ReadNow is { Found: true, AtTruth: true });
                var readFalse = list.Count(static e => e.ReadNow is { Found: true, AtTruth: not true });
                var readMiss = list.Count(static e => e.TruthInArea && e.ReadNow is { Found: false });
                var retryHit = list.Count(static e => e.RetryNext == true && e.RetryNextAtTruth == true);
                var retryFalse = list.Count(static e => e.RetryNext == true && e.RetryNextAtTruth != true);
                var nextHit = list.Count(static e => e.ReadNext is { Found: true, AtTruth: true });
                var nextFalse = list.Count(static e => e.ReadNext is { Found: true, AtTruth: not true });
                return $"| {label} | {list.Count} | {truthIn} | {readHit} / {readFalse} / {readMiss} | {retryHit} / {retryFalse} | {nextHit} / {nextFalse} |";
            }
            var motion = _baseline.Where(static e => e.Moving && !e.SceneCut).ToList();
            yield return Row("wszystkie zgubienia", _baseline);
            yield return Row("w ruchu, bez cięcia", motion);
            yield return Row("— jadące (travelling)", motion.Where(static e => e.Travelling).ToList());
            yield return Row("— stojące, litery są", motion.Where(static e => !e.Travelling && !e.LettersGone).ToList());
            yield return Row("— stojące, liter brak", motion.Where(static e => !e.Travelling && e.LettersGone).ToList());
            yield return Row("cięcie sceny", _baseline.Where(static e => e.SceneCut).ToList());
            yield return Row("bez ruchu", _baseline.Where(static e => !e.Moving).ToList());
        }

        public PassDto RunPass(string strategy, JsonlWriter events, JsonlWriter? periodic, JsonlWriter unsupported)
        {
            var reading = strategy != "bez";
            var followed = new Dictionary<string, Followed>(StringComparer.Ordinal);
            var validity = new LiveSceneValidity(SceneCutThreshold, MotionThreshold);
            var reads = new LostTextReads<int>(4, 2, TimeSpan.FromMilliseconds(300));
            var readMs = new List<double>();
            int wrongWaiting = 0, motionFrames = 0, pairsMotion = 0, coveredMotion = 0, pairsStill = 0, coveredStill = 0, wrongMotion = 0, wrongStill = 0;
            int losses = 0, lossesMotion = 0, eligible = 0, readsDone = 0, recovered = 0, recoveredAtTruth = 0, recoveredWrong = 0, lostNext = 0, removedAfterRead = 0;
            var pendingBaseline = new List<(LossDto Event, Followed Block, Track? State, GlyphFollowPlan Plan)>();
            var recoveredThisFrame = new HashSet<string>(StringComparer.Ordinal);
            var recoveredPreviousFrame = new HashSet<string>(StringComparer.Ordinal);
            var rebuiltUsed = 0;
            var decoder = new FrameDecoder(recording, count);
            for (var f = 0; f < count; f++)
            {
                var frame = decoder.Get(f);
                var frameRect = new RectPx(0, 0, frame.Width, frame.Height);
                truth.TryGetValue(f, out var t);
                var now = TimeSpan.FromMilliseconds(recording.RelativeMs(f));
                var sceneCut = false;
                var isMoving = false;
                if (f > 0 && t is not null)
                {
                    sceneCut = validity.Observe(new NoiseAwareAnalysis(t.Changed, t.Strong, t.Changed, null), true, tolerateMotion: true);
                    isMoving = sceneCut || t.Strong >= MotionThreshold;
                }
                if (isMoving && !sceneCut) motionFrames++;

                if (!reading && pendingBaseline.Count > 0)
                {
                    foreach (var (loss, block, state, plan) in pendingBaseline) EvaluateNext(loss, block, state, plan, frame, f, isMoving);
                    pendingBaseline.Clear();
                }

                (recoveredPreviousFrame, recoveredThisFrame) = (recoveredThisFrame, recoveredPreviousFrame);
                recoveredThisFrame.Clear();
                var present = new HashSet<string>(StringComparer.Ordinal);
                var skip = new HashSet<string>(StringComparer.Ordinal);
                if (f > 0)
                {
                    if (reading)
                    {
                        foreach (var block in followed.Values.Where(static b => b.Waiting is not null).ToList())
                        {
                            var waiting = block.Waiting!;
                            block.Waiting = null;
                            reads.Remove(block.Key, out _);
                            var resumed = sceneCut || waiting.Found is not { } found ? null
                                : LostTextRecovery.Resume(waiting.Cover, block.Box, found, area => Crop(frame, area), frameRect, MaxShift, patch);
                            if (resumed is null || followed.Values.Any(o => o != block && Overlaps(o.Box, resumed.Box)))
                            {
                                followed.Remove(block.Key);
                                removedAfterRead++;
                                continue;
                            }
                            var scaledLine = block.LineHeight * resumed.Match.Scale;
                            var linePx = resumed.Match.Scale == 1 ? block.LineHeight : Math.Max(1, (int)Math.Round(scaledLine));
                            var regionRect = new RectPx(0, 0, resumed.Region.Width, resumed.Region.Height);
                            var signature = GlyphCoverBuilder.Signature(resumed.Region,
                                GlyphFollow.SignatureArea(resumed.Box, linePx, frameRect).Offset(-resumed.Area.X, -resumed.Area.Y).Intersect(regionRect));
                            var cover = resumed.Cover with { Signature = signature };
                            block.Box = resumed.Box;
                            block.LineHeight = linePx;
                            block.Cover = cover;
                            block.State = new Track(cover, signature, 0, resumed.VelocityX, resumed.VelocityY,
                                Moving: resumed.VelocityX != 0 || resumed.VelocityY != 0, LineHeight: scaledLine);
                            present.Add(block.Key);
                            skip.Add(block.Key);
                            if (resumed.VelocityX != 0 || resumed.VelocityY != 0) block.MovedAtMs = now.TotalMilliseconds;
                            recovered++;
                            if (resumed.Rebuilt) rebuiltUsed++;
                            recoveredThisFrame.Add(block.Key);
                            if (TruthHit(f, block.Source, Box.From(resumed.Box))) recoveredAtTruth++;
                            else recoveredWrong++;
                        }
                    }
                    var lost = TrackFrame(followed, frame, frameRect, isMoving, present, now.TotalMilliseconds, skip);
                    foreach (var (block, plan, reason, lettersGone) in lost)
                    {
                        losses++;
                        if (isMoving && !sceneCut) lossesMotion++;
                        if (recoveredPreviousFrame.Contains(block.Key)) lostNext++;
                        var state = block.State;
                        var track = block.Cover.Track!;
                        var shiftX = plan.VelocityX * track.Step;
                        var shiftY = plan.VelocityY * track.Step;
                        var area = LostTextRecovery.ReadArea(block.Box, shiftX, shiftY, MaxShift * GlyphFollow.WideReach, frameRect);
                        var predicted = block.Box.Offset((int)Math.Round(shiftX), (int)Math.Round(shiftY));
                        var travelling = plan.Travelling || now.TotalMilliseconds - block.MovedAtMs <= 600;
                        var canRead = LostTextRecovery.ShouldRead(isMoving, sceneCut, hasTrack: true, screenFallback: false, travelling, lettersGone);
                        if (canRead) eligible++;
                        var budget = canRead && reads.CanBegin(block.Key, now);
                        var (truthIn, truthAny, truthDistance) = TruthNear(f, block.Source, Box.From(area), Box.From(predicted));
                        var number = ++_event;
                        if (!reading)
                        {
                            var readNow = Read(frame, frameRect, block, area, predicted, f, out _);
                            readMs.Add(readNow.OcrMs);
                            var loss = new LossDto(number, f, Math.Round(now.TotalMilliseconds), strategy, block.Key, block.Source, reason, isMoving, sceneCut,
                                travelling, lettersGone, (int)Math.Round(shiftX), (int)Math.Round(shiftY), Box.From(block.Box), Box.From(area), canRead,
                                budget, truthIn, truthAny, truthDistance, readNow, null, null, null, null);
                            pendingBaseline.Add((loss, block, state, plan));
                            followed.Remove(block.Key);
                            continue;
                        }
                        string outcome;
                        if (budget && reads.TryBegin(block.Key, now, () => number))
                        {
                            readsDone++;
                            var readNow = Read(frame, frameRect, block, area, predicted, f, out var found);
                            readMs.Add(readNow.OcrMs);
                            block.Waiting = new Pending(f, found, block.Cover, plan.VelocityX, plan.VelocityY, track.Step, number);
                            block.State = null;
                            outcome = found is null ? "odczyt bez tekstu" : "odczyt potwierdził";
                            events.Write(new LossDto(number, f, Math.Round(now.TotalMilliseconds), strategy, block.Key, block.Source, reason, isMoving, sceneCut,
                                travelling, lettersGone, (int)Math.Round(shiftX), (int)Math.Round(shiftY), Box.From(block.Box), Box.From(area), canRead,
                                budget, truthIn, truthAny, truthDistance, readNow, null, null, null, outcome));
                            continue;
                        }
                        outcome = canRead ? "limit — usunięty" : "usunięty";
                        events.Write(new LossDto(number, f, Math.Round(now.TotalMilliseconds), strategy, block.Key, block.Source, reason, isMoving, sceneCut,
                            travelling, lettersGone, (int)Math.Round(shiftX), (int)Math.Round(shiftY), Box.From(block.Box), Box.From(area), canRead,
                            budget, truthIn, truthAny, truthDistance, null, null, null, null, outcome));
                        followed.Remove(block.Key);
                    }
                    if (sceneCut)
                    {
                        foreach (var key in followed.Keys.Where(k => !present.Contains(k) && followed[k].Waiting is null).ToList()) followed.Remove(key);
                        foreach (var key in followed.Values.Where(static b => b.Waiting is not null).Select(static b => b.Key).ToList())
                        {
                            followed.Remove(key);
                            reads.Remove(key, out _);
                            removedAfterRead++;
                        }
                        var moved = followed.Values.Where(b => b.State is not null).ToDictionary(static b => b.Key, static b => b.State!.Moving, StringComparer.Ordinal);
                        var kept = SceneCutTracking.Retained(moved, followed.Keys);
                        foreach (var block in followed.Values) if (!kept.Contains(block.Key)) block.State = null;
                    }
                }

                if (f % options.OcrEvery == 0) Seed(followed, frame, f, present);

                if (periodic is not null && isMoving && !sceneCut && f % options.PeriodicEvery == 0)
                {
                    foreach (var block in followed.Values.Where(b => b.Waiting is null && present.Contains(b.Key)).ToList())
                        _periodic.Add(PeriodicRead(periodic, frame, frameRect, block, f));
                }

                if (t is null) continue;
                foreach (var tb in t.Blocks.Where(static b => b.Live))
                {
                    var covered = followed.Values.Any(b => b.Waiting is null && Supports(b, tb));
                    if (isMoving && !sceneCut) { pairsMotion++; if (covered) coveredMotion++; }
                    else if (!isMoving) { pairsStill++; if (covered) coveredStill++; }
                }
                foreach (var block in followed.Values)
                {
                    if (t.Blocks.Any(tb => Supports(block, tb))) continue;
                    if (isMoving && !sceneCut) wrongMotion++;
                    if (isMoving && !sceneCut) unsupported.Write(new { strategy, frame = f, block.Key, box = Box.From(block.Box), waiting = block.Waiting is not null, truth = t.Blocks.Where(tb => SameContent(block.Source, tb)).Select(static tb => tb.Box).ToList() });
                    if (block.Waiting is not null) wrongWaiting++;
                    else if (!isMoving) wrongStill++;
                }
            }
            if (!reading) foreach (var (loss, _, _, _) in pendingBaseline) _baseline.Add(loss);
            foreach (var loss in _baseline.Where(l => l.Strategy == strategy)) events.Write(loss);
            var full = _fullOcr.Values.Select(static v => v.Ms).ToList();
            return new PassDto(strategy, count, motionFrames, pairsMotion, coveredMotion, pairsStill, coveredStill, wrongMotion, wrongStill,
                losses, lossesMotion, eligible, readsDone, recovered, recoveredAtTruth, recoveredWrong, lostNext, removedAfterRead, rebuiltUsed, wrongWaiting,
                Median(readMs), Percentile(readMs, 0.9), readMs.Count == 0 ? 0 : readMs.Max(), Median(full), full.Count);
        }

        private void EvaluateNext(LossDto loss, Followed block, Track? state, GlyphFollowPlan plan, OcrBitmap frame, int f, bool isMoving)
        {
            var frameRect = new RectPx(0, 0, frame.Width, frame.Height);
            var track = block.Cover.Track!;
            var retryPlan = GlyphFollow.Plan(track, MaxShift, isMoving: true, fresh: false, 2 * plan.VelocityX, 2 * plan.VelocityY, moving: true);
            var area = GlyphFollow.SearchArea(track, retryPlan).Intersect(frameRect);
            bool? retry = null;
            bool? retryAtTruth = null;
            if (!area.IsEmpty)
            {
                var region = OcrBands.Crop(frame, area);
                var match = GlyphFollow.Locate(track, new GlyphField(region, area.X, area.Y), retryPlan, refreshOnly: false);
                var moved = GlyphFollow.Follow(block.Cover, match, region, area, patch);
                retry = moved is not null;
                if (moved is not null) retryAtTruth = TruthHit(f, block.Source, Box.From(GlyphFollow.FollowBox(block.Box, block.Cover, moved)));
            }
            var predicted = block.Box.Offset(2 * loss.VelocityXPx, 2 * loss.VelocityYPx);
            var readArea = LostTextRecovery.ReadArea(block.Box, 2 * loss.VelocityXPx, 2 * loss.VelocityYPx, MaxShift * GlyphFollow.WideReach, frameRect);
            var next = Read(frame, frameRect, block, readArea, predicted, f, out _);
            _baseline.Add(loss with { RetryNext = retry, RetryNextAtTruth = retryAtTruth, ReadNext = next });
        }

        private List<(Followed Block, GlyphFollowPlan Plan, string Reason, bool LettersGone)> TrackFrame(
            Dictionary<string, Followed> followed, OcrBitmap frame, RectPx frameRect, bool isMoving, HashSet<string> present, double nowMs,
            HashSet<string>? skip = null)
        {
            var lost = new List<(Followed, GlyphFollowPlan, string, bool)>();
            foreach (var block in followed.Values.Where(b => b.Waiting is null && skip?.Contains(b.Key) != true).ToList())
            {
                if (block.Cover.Track is not { } track) continue;
                var state = block.State;
                var fresh = state is null || !ReferenceEquals(state.Cover, block.Cover);
                var plan = GlyphFollow.Plan(track, MaxShift, isMoving, fresh, fresh ? 0 : state!.VelocityX, fresh ? 0 : state!.VelocityY, !fresh && state!.Moving);
                var area = GlyphFollow.SearchArea(track, plan).Intersect(frameRect);
                if (area.IsEmpty) continue;
                var region = OcrBands.Crop(frame, area);
                var regionRect = new RectPx(0, 0, region.Width, region.Height);
                var signature = GlyphCoverBuilder.Signature(region, GlyphFollow.SignatureArea(block.Box, block.LineHeight, frameRect).Offset(-area.X, -area.Y).Intersect(regionRect));
                var lineHeight = !fresh && state!.LineHeight > 0 && (int)Math.Round(state.LineHeight) == block.LineHeight ? state.LineHeight : block.LineHeight;
                if (!fresh && !isMoving && state!.Absences == 0 && state.Signature.Length > 0
                    && GlyphCover.SignatureDifference(state.Signature, signature) < 1.5)
                {
                    present.Add(block.Key);
                    continue;
                }
                var match = GlyphFollow.Locate(track, new GlyphField(region, area.X, area.Y), plan, refreshOnly: false);
                var refreshed = GlyphFollow.Follow(block.Cover, match, region, area, patch);
                if (match is not { IsConfident: true } m || refreshed is null)
                {
                    var absent = match is not { } miss || miss.LettersGone;
                    var absences = absent ? (fresh ? 0 : state!.Absences) + 1 : 0;
                    block.State = fresh ? new Track(block.Cover, [], absences) : state! with { Absences = absences, VelocityX = 0, VelocityY = 0 };
                    if (isMoving || absences >= 2) lost.Add((block, plan, "brak dopasowania", absent));
                    continue;
                }
                var box = GlyphFollow.FollowBox(block.Box, block.Cover, refreshed);
                var relocated = box != block.Box;
                if (!patch && !relocated)
                {
                    block.State = new Track(block.Cover, signature, 0, Moving: !isMoving && state is { Moving: true }, LineHeight: lineHeight);
                    present.Add(block.Key);
                    continue;
                }
                if (relocated && followed.Values.Any(o => o != block && o.Waiting is null && Overlaps(o.Box, box)))
                {
                    if (isMoving) lost.Add((block, plan, "nachodzi na inny blok", false));
                    continue;
                }
                var scaledLine = lineHeight * m.Scale;
                var linePx = m.Scale == 1 ? block.LineHeight : Math.Max(1, (int)Math.Round(scaledLine));
                var movedSignature = !relocated
                    ? signature
                    : GlyphCoverBuilder.Signature(region, GlyphFollow.SignatureArea(box, linePx, frameRect).Offset(-area.X, -area.Y).Intersect(regionRect));
                refreshed = refreshed with { Signature = movedSignature };
                block.Box = box;
                block.LineHeight = linePx;
                block.Cover = refreshed;
                if (isMoving && !m.IsStatic) block.MovedAtMs = nowMs;
                block.State = new Track(refreshed, movedSignature, 0, isMoving ? m.WorkDx : 0, isMoving ? m.WorkDy : 0,
                    Moving: isMoving ? !m.IsStatic : state is { Moving: true }, LineHeight: scaledLine);
                present.Add(block.Key);
            }
            return lost;
        }

        private void Seed(Dictionary<string, Followed> followed, OcrBitmap frame, int f, HashSet<string> present)
        {
            if (!_fullOcr.TryGetValue(f, out var result))
            {
                var watch = Stopwatch.StartNew();
                var lines = ocr.RecognizeAsync(frame, "en").GetAwaiter().GetResult().Lines;
                var ms = watch.Elapsed.TotalMilliseconds;
                var frameRect = new RectPx(0, 0, frame.Width, frame.Height);
                var blocks = LostTextRecovery.Blocks(frame, lines, frameRect, 1.0, frameRect, ignore, detectIcons: true)
                    .Where(block => orchestrator.ShouldTranslateLive(block.Text)).ToList();
                result = (LiveBlockKeyer.AssignKeys(blocks, orchestrator.CorpusIdentity), ms);
                _fullOcr[f] = result;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var keyed in result.Blocks)
            {
                seen.Add(keyed.Key);
                if (followed.TryGetValue(keyed.Key, out var existing) && existing.Waiting is not null) continue;
                var built = GlyphCoverBuilder.BuildForBlock(frame, keyed.Block, new RectPx(0, 0, frame.Width, frame.Height), 1.0, soft: false, patch);
                if (built?.Track is null)
                {
                    followed.Remove(keyed.Key);
                    continue;
                }
                var cover = built with { Anchor = keyed.Block.Box };
                if (existing is not null && Overlaps(existing.Box, keyed.Block.Box))
                {
                    existing.Box = keyed.Block.Box;
                    existing.LineHeight = TextBlockMetrics.MedianLineHeight(keyed.Block);
                    existing.Cover = cover;
                    continue;
                }
                if (followed.Values.Any(o => o.Key != keyed.Key && Overlaps(o.Box, keyed.Block.Box))) continue;
                followed[keyed.Key] = new Followed
                {
                    Key = keyed.Key,
                    Source = keyed.NormalizedText,
                    Box = keyed.Block.Box,
                    LineHeight = TextBlockMetrics.MedianLineHeight(keyed.Block),
                    Cover = cover,
                };
            }
            foreach (var key in followed.Keys.Where(k => !seen.Contains(k) && !present.Contains(k) && followed[k].Waiting is null).ToList())
                followed.Remove(key);
        }

        private ReadDto Read(OcrBitmap frame, RectPx frameRect, Followed block, RectPx area, RectPx predicted, int f, out LostTextFound? found)
        {
            found = null;
            if (area.IsEmpty) return new ReadDto(0, 0, 0, 0, 0, false, null, null, false, null);
            var crop = OcrBands.Crop(frame, area);
            var preferred = crop.Width >= 1000 || crop.Height >= 700 ? 0.0 : upscale.Preferred;
            var factor = OcrScaling.ComputeUpscale(crop.Width, crop.Height, ocr.MaxImageDimension, preferred, upscale.AllowAuto);
            var image = Math.Abs(factor - 1.0) > 0.001 ? Scale(crop, factor) : crop;
            var scaleBack = 1.0 / factor;
            var watch = Stopwatch.StartNew();
            var lines = ocr.RecognizeAsync(image, "en").GetAwaiter().GetResult().Lines;
            var ms = watch.Elapsed.TotalMilliseconds;
            var blocks = LostTextRecovery.Blocks(image, lines, area, scaleBack, frameRect, ignore, detectIcons: true);
            var same = LostTextRecovery.FindSame(block.Source, block.Box, blocks, predicted, area, frameRect);
            if (same is not null) found = LostTextRecovery.Rebuild(image, same, area, scaleBack, patch);
            var text = same?.Text ?? blocks.OrderBy(b => Box.From(b.Box).CenterDistance(Box.From(predicted))).FirstOrDefault()?.Text;
            return new ReadDto(Math.Round(ms, 2), image.Width, image.Height, lines.Count, blocks.Count, same is not null,
                same is null ? null : Box.From(same.Box), same is null ? null : TruthHit(f, block.Source, Box.From(same.Box)), found is not null,
                text is null ? null : TextTools.Short(text, 60));
        }

        private PeriodicDto PeriodicRead(JsonlWriter writer, OcrBitmap frame, RectPx frameRect, Followed block, int f)
        {
            var area = block.Box.Inflate(Math.Max(16, block.LineHeight)).Intersect(frameRect);
            var crop = OcrBands.Crop(frame, area);
            var preferred = crop.Width >= 1000 || crop.Height >= 700 ? 0.0 : upscale.Preferred;
            var factor = OcrScaling.ComputeUpscale(crop.Width, crop.Height, ocr.MaxImageDimension, preferred, upscale.AllowAuto);
            var image = Math.Abs(factor - 1.0) > 0.001 ? Scale(crop, factor) : crop;
            var watch = Stopwatch.StartNew();
            var lines = ocr.RecognizeAsync(image, "en").GetAwaiter().GetResult().Lines;
            var ms = watch.Elapsed.TotalMilliseconds;
            var blocks = LostTextRecovery.Blocks(image, lines, area, 1.0 / factor, frameRect, ignore, detectIcons: true);
            var same = blocks.Any(b => LiveReadingStabilizer.IsSameText(block.Source, b.Text));
            var result = new PeriodicDto(f, block.Key, Math.Round(ms, 2), image.Width, image.Height, same, TruthHit(f, block.Source, Box.From(block.Box)));
            writer.Write(result);
            return result;
        }

        private (bool InArea, bool Anywhere, double? Distance) TruthNear(int f, string source, Box area, Box predicted)
        {
            if (!truth.TryGetValue(f, out var t)) return (false, false, null);
            var matches = t.Blocks.Where(b => SameContent(source, b)).ToList();
            if (matches.Count == 0) return (false, false, null);
            var inArea = matches.Any(b => area.ContainsPoint(b.Box.Cx, b.Box.Cy));
            return (inArea, true, Math.Round(matches.Min(b => b.Box.CenterDistance(predicted)), 1));
        }

        private bool TruthHit(int f, string source, Box box) =>
            truth.TryGetValue(f, out var t) && t.Blocks.Any(b => SameContent(source, b) && Positioned(box, b.Box));

        private static bool Supports(Followed block, TruthBlockDto tb) => SameContent(block.Source, tb) && Positioned(Box.From(block.Box), tb.Box);

        private static bool Positioned(Box shown, Box truthBox) =>
            shown.IoU(truthBox) >= 0.3 || shown.Inflate((int)Math.Max(8, truthBox.H / 2.0)).ContainsPoint(truthBox.Cx, truthBox.Cy);

        private static bool SameContent(string source, TruthBlockDto tb)
        {
            if (TextTools.Similarity(source, tb.Norm) >= TruthSimilarity) return true;
            var lines = source.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return tb.Rows.Any(r => lines.Any(l => TextTools.Similarity(l, r.Norm) >= TruthSimilarity));
        }
    }

    private static bool Overlaps(RectPx a, RectPx b)
    {
        var overlap = a.Intersect(b);
        if (overlap.IsEmpty) return false;
        var smaller = Math.Min((long)a.Width * a.Height, (long)b.Width * b.Height);
        return smaller > 0 && (long)overlap.Width * overlap.Height * 10 >= smaller * 3;
    }

    private static OcrBitmap? Crop(OcrBitmap frame, RectPx area)
    {
        area = area.Intersect(new RectPx(0, 0, frame.Width, frame.Height));
        return area.IsEmpty ? null : OcrBands.Crop(frame, area);
    }

    private static OcrBitmap Scale(OcrBitmap source, double factor)
    {
        var bitmap = BitmapSource.Create(source.Width, source.Height, 96, 96, PixelFormats.Bgra32, null, source.PixelsBgra32, source.Stride);
        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(factor, factor));
        scaled.Freeze();
        return Imaging.ToOcrBitmap(scaled);
    }

    private static double Median(IEnumerable<double> values) => Percentile(values, 0.5);

    private static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0) return 0;
        return Math.Round(sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)], 2);
    }

    private sealed class FrameDecoder(Recording recording, int count)
    {
        private const int Ahead = 6;
        private readonly Dictionary<int, Task<OcrBitmap>> _tasks = [];
        private readonly StaPool _pool = new(3, 16, "lost-decode");

        public OcrBitmap Get(int index)
        {
            for (var k = index; k < Math.Min(count, index + Ahead); k++)
            {
                if (_tasks.ContainsKey(k)) continue;
                var path = recording.FramePath(k);
                _tasks[k] = _pool.Run(() => Imaging.ToOcrBitmap(Imaging.DecodeJpeg(path)));
            }
            var frame = _tasks[index].GetAwaiter().GetResult();
            _tasks.Remove(index);
            if (index == count - 1) _pool.Dispose();
            return frame;
        }
    }
}
