using System.Globalization;
using System.Text;
using System.Text.Json;

internal static class ReportWriter
{
    public static void Write(string path, MotionAnalyzer analyzer, JsonElement metrics, IReadOnlyList<(Problem Problem, string Sheet)> worst)
    {
        var data = analyzer.Data;
        var header = data.Header;
        var summary = data.Summary;
        var sb = new StringBuilder();
        string V(string p, string unit = "") => Value(metrics, p, unit);
        string S(string p, string unit = " ms") => $"{V(p + ".median")} / {V(p + ".p90")} / {V(p + ".max")}{unit} (n={V(p + ".count")})";

        sb.AppendLine($"# MotionLab — {header.RecordingName}");
        sb.AppendLine();
        sb.AppendLine($"Pomiar: {header.Started:yyyy-MM-dd HH:mm}, {header.Frames} klatek {header.FrameW}×{header.FrameH}, tempo ×{header.Speed.ToString(CultureInfo.InvariantCulture)}, " +
            $"Mock {header.ProviderDelayMs} ms (tekst spoza bazy), baza: {(header.Cache is null ? "pusta (pamięć)" : "kopia")}, profil {header.Profile}, " +
            $"tryb {header.Placement}/{header.Live}, krycie {header.Opacity.ToString(CultureInfo.InvariantCulture)}, nakładka: prawdziwe OverlayWindow (przezroczyste na ekranie).");
        sb.AppendLine();
        sb.AppendLine($"Sesja: {header.Session.Fps.ToString(CultureInfo.InvariantCulture)} analiz/s, MotionThreshold {header.Session.MotionThreshold.ToString(CultureInfo.InvariantCulture)}, " +
            $"MaxMotionPause {header.Session.MaxMotionPauseMs:0} ms, SceneCut {header.Session.SceneCutThreshold.ToString(CultureInfo.InvariantCulture)}, " +
            $"łatki {(header.Session.BuildGlyphCovers ? "tak" : "nie")}, wykluczenie z capture {(header.Session.CaptureExclusionActive ? "działa" : "NIE działa")}.");
        if (summary is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Odtwarzanie: spóźnienie podmiany klatki mediana {summary.LateSetMedianMs.ToString(CultureInfo.InvariantCulture)} ms, p90 {summary.LateSetP90Ms.ToString(CultureInfo.InvariantCulture)}, max {summary.LateSetMaxMs.ToString(CultureInfo.InvariantCulture)}; " +
                $"do najbliższego renderu WPF mediana {summary.LateRenderMedianMs.ToString(CultureInfo.InvariantCulture)} ms (p90 {summary.LateRenderP90Ms.ToString(CultureInfo.InvariantCulture)}); " +
                $"przestoje dekodera {summary.DecodeStalls}; czas całego replay {summary.WallSeconds.ToString(CultureInfo.InvariantCulture)} s{(summary.Error is null ? string.Empty : $"; BŁĄD: {summary.Error}")}.");
        }
        if (header.Fidelity is { } fidelity)
        {
            sb.AppendLine($"Wierność obrazu (PrintWindow okna odtwarzania vs JPEG): {fidelity.CapturedW}×{fidelity.CapturedH}, średnia różnica {fidelity.MeanAbsDiff.ToString(CultureInfo.InvariantCulture)}, " +
                $"pikseli różnych o >3: {(fidelity.DiffFraction * 100).ToString("0.###", CultureInfo.InvariantCulture)}%{(fidelity.Fallback ? ", ZRZUT EKRANU" : string.Empty)}.");
        }
        sb.AppendLine();

        sb.AppendLine("## Metryki (definicje: tools/GameTranslatorOverlay.MotionLab/README.md)");
        sb.AppendLine();
        sb.AppendLine("| Metryka | Wartość |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Epizody tekstu tłumaczalnego (≥3 klatki) | {V("tracks.eligible")} (stałe {V("tracks.eligibleStatic")}, ruchome {V("tracks.eligibleMoving")}, pisane literami {V("tracks.typing")}; krótszych {V("tracks.shortLive")}) |");
        sb.AppendLine($"| **Pokrycie** — % czasu z tłumaczeniem | **{V("coverage.all.percent", " %")}** (z {V("coverage.all.seconds", " s")} tekst·czasu) |");
        sb.AppendLine($"| Pokrycie: teksty stałe (HUD) / ruchome | {V("coverage.static.percent", " %")} / {V("coverage.moving.percent", " %")} |");
        sb.AppendLine($"| Pokrycie: kamera w ruchu / w spoczynku | {V("coverage.cameraMotion.percent", " %")} ({V("coverage.cameraMotion.seconds", " s")}) / {V("coverage.cameraRest.percent", " %")} ({V("coverage.cameraRest.seconds", " s")}) |");
        sb.AppendLine($"| Pokrycie HUD: kamera w ruchu / w spoczynku | {V("coverage.staticCameraMotion.percent", " %")} / {V("coverage.staticCameraRest.percent", " %")} |");
        sb.AppendLine($"| Pokrycie tekstu ruchomego: kamera w ruchu / w spoczynku | {V("coverage.movingCameraMotion.percent", " %")} / {V("coverage.movingCameraRest.percent", " %")} |");
        sb.AppendLine($"| Pokrycie: klatki z kątem tekstu OCR / bez | {V("coverage.angled.percent", " %")} ({V("coverage.angled.seconds", " s")}) / {V("coverage.straight.percent", " %")} |");
        sb.AppendLine($"| Pokrycie HUD: klatki z kątem / bez | {V("coverage.staticAngled.percent", " %")} / {V("coverage.staticStraight.percent", " %")} |");
        sb.AppendLine($"| **Opóźnienie** pojawienia (mediana / p90 / max) | {S("latency.nonTyping")}; nigdy nie pokazane: {V("latency.neverShown")} z {V("latency.episodes")} (stałe: {V("latency.neverShownStatic")}) |");
        sb.AppendLine($"| Opóźnienie: teksty stałe / ruchome | {S("latency.nonTypingStatic")} / {S("latency.nonTypingMoving")} |");
        sb.AppendLine($"| Opóźnienie: tekst pojawił się w ruchu / w spoczynku | {S("latency.startedInMotion")} / {S("latency.startedAtRest")} |");
        sb.AppendLine($"| Opóźnienie od końca pisania literami | {S("latency.typingFromEnd")} |");
        sb.AppendLine($"| Tekst obecny od startu sesji (<1,5 s, poza statystyką) | {S("latency.atSessionStart")}; nigdy nie pokazane: {V("latency.atSessionStartNeverShown")} |");
        sb.AppendLine($"| **Nieaktualne** (tłumaczenie bez tekstu w tym miejscu) | **{V("stale.seconds", " s")}**, zdarzeń ≥200 ms: {V("stale.events")} (zniknął {V("stale.disappearedSeconds", " s")}, przesunięty {V("stale.movedSeconds", " s")}, częściowo {V("stale.partialSeconds", " s")}; w ruchu kamery {V("stale.inCameraMotionSeconds", " s")}); odczyty nieznane prawdzie: {V("stale.unknownInTruthSeconds", " s")} |");
        sb.AppendLine($"| **Położenie** środka (pokryte; mediana / p90 / max) | {S("position.covered", " px")} |");
        sb.AppendLine($"| Położenie (dowolne dopasowanie treści): HUD / tekst ruchomy | {S("position.anyContentMatchStatic", " px")} / {S("position.anyContentMatchMovingText", " px")} |");
        sb.AppendLine($"| Położenie: blok z pełnego OCR z kątem / prostego | {S("position.fromAngledFullOcr", " px")} / {S("position.fromStraightOcr", " px")}; >16 px: {V("position.fromAngledFullOcrOver16Px")} / {V("position.fromStraightOcrOver16Px")} próbek |");
        sb.AppendLine($"| **Miganie** (pokazany→ukryty→pokazany przy obecnym tekście) | {V("flicker.holes")} dziur w {V("flicker.tracksWithHoles")} epizodach, razem {V("flicker.holeSeconds", " s")}; przebudowy elementu nakładki: {V("display.elementRecreations")} |");
        sb.AppendLine($"| Kąt tekstu (truth) | {V("angle.angledFrames")} z {V("angle.frames")} klatek ({V("angle.angledPercent", " %")}); live OCR z kątem: {V("angle.liveOcrAngled")} z {V("angle.liveOcrProbed")} sprawdzonych (pełne klatki: {V("angle.liveOcrFullAngled")} z {V("angle.liveOcrFullProbed")} sprawdzonych, wycinki z kątem {V("angle.liveOcrPartialAngled")}) |");
        sb.AppendLine($"| HUD (stałe napisy, {V("hud.staticTracks")} epizodów): pokrycie | {V("hud.coverage", " %")} |");
        sb.AppendLine($"| HUD znika z nakładki (pokryty→niepokryty przy obecnym tekście) | {V("hud.losses")} razy (w ruchu kamery {V("hud.lossesInCameraMotion")}); w klatkach z kątem {V("hud.lossesAtAngledFrames")}; przez pełny odczyt z kątem {V("hud.lossesByAngledFullOcr")}; przyczyny: {Causes(metrics)} |");
        sb.AppendLine($"| HUD przekręcony (inny odczyt w miejscu HUD) | {V("hud.garbledReadings")} odczytów: {Garbled(metrics)} |");
        sb.AppendLine($"| **Łatki**: błąd szwu ruch / spoczynek (mediana, p90) | {V("patches.seamCurrentMotion.median")}, {V("patches.seamCurrentMotion.p90")} / {V("patches.seamCurrentRest.median")}, {V("patches.seamCurrentRest.p90")} (przy budowie: {V("patches.seamAtBuild.median")}) |");
        sb.AppendLine($"| Łatki: różnica tła wokół vs klatka budowy, ruch / spoczynek (mediana, p90) | {V("patches.ringDiffMotion.median")}, {V("patches.ringDiffMotion.p90")} / {V("patches.ringDiffRest.median")}, {V("patches.ringDiffRest.p90")} |");
        sb.AppendLine($"| Łatki „zamrożone” (tło wokół zmieniło się ≥{V("patches.frozenThreshold")}) ruch / spoczynek | {V("patches.frozenSecondsMotion", " s")} z {V("patches.patchSecondsMotion", " s")} / {V("patches.frozenSecondsRest", " s")} z {V("patches.patchSecondsRest", " s")} |");
        sb.AppendLine($"| Koszt: zakończone przebiegi (pełne / wycinki) | {V("cost.completedFrames")} ({V("cost.fullFrames")} / {V("cost.partialFrames")}); OCR wywołań {V("cost.ocrCalls")} (pełnych {V("cost.ocrFullFrame")}, porzuconych ~{V("cost.ocrDiscarded")}) |");
        sb.AppendLine($"| Koszt: klatka→aktualizacja (mediana / p90 / max) | {S("cost.captureToUpdateMs")} |");
        sb.AppendLine($"| Koszt: OCR pełny / wycinek (operacja) | {S("cost.ocrFullOperationMs")} / {S("cost.ocrPartialOperationMs")} |");
        sb.AppendLine($"| Koszt: łatki budowa / czekanie | {S("cost.glyphCoverMs")} / {S("cost.glyphCoverWaitMs")} |");
        sb.AppendLine($"| Cięcia sceny / czyszczenia nakładki (w ruchu) / whiff | {V("cost.sceneCutFrames")} / {V("cost.clears")} ({V("cost.clearsInCameraMotion")}) / {V("cost.whiffFrames")} |");
        sb.AppendLine($"| Przerwy bez OCR ≥1 s (w ruchu) | {V("cost.ocrGapsOver1s")} ({V("cost.ocrGapsOver1sInMotion")}), razem {V("cost.ocrGapSeconds", " s")}, najdłuższa {V("cost.ocrGapMaxMs", " ms")}; próbek „obserwuję” z mocnym ruchem ≥12%: {V("cost.observeSamplesInMotion")} |");
        sb.AppendLine();

        if (metrics.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine($"## Kroki ruchu (przesunięcie dopasowane do ruchu w klatkach: {Value(steps, "offsetMs")} ms od początku nagrania)");
            sb.AppendLine();
            sb.AppendLine("| Krok | Czas [s] | Klatki z ruchem | Klatki z kątem | Pokrycie | HUD | Ruchome | Nieaktualne [s] | Zniknięcia HUD | Czyszczenia |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var row in steps.GetProperty("rows").EnumerateArray())
            {
                sb.AppendLine($"| {Value(row, "label")} | {Value(row, "startS")}–{Value(row, "endS")} | {Value(row, "cameraMotionFrames")} | {Value(row, "angledFrames")} | " +
                    $"{Value(row, "coverage", " %")} | {Value(row, "coverageStatic", " %")} | {Value(row, "coverageMoving", " %")} | {Value(row, "staleSeconds")} | {Value(row, "hudLosses")} | {Value(row, "clears")} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"## {worst.Count} najgorszych momentów");
        sb.AppendLine();
        sb.AppendLine("| # | Typ | Czas [s] | Klatka | Krok / ruch / kąt | Tekst | Co się dzieje | Stykówka |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        for (var i = 0; i < worst.Count; i++)
        {
            var (problem, sheet) = worst[i];
            var frame = Math.Clamp(problem.Frame, 0, analyzer.Clock.Count - 1);
            var step = analyzer.StepAt(frame)?.Label ?? "—";
            var context = $"{step} / {(analyzer.Moving[frame] ? "ruch" : "spokój")} / {analyzer.Angle[frame].ToString("0.0", CultureInfo.InvariantCulture)}°";
            sb.AppendLine($"| {i + 1} | {problem.Type} | {(problem.StartMs / 1000).ToString("0.0", CultureInfo.InvariantCulture)}–{(problem.EndMs / 1000).ToString("0.0", CultureInfo.InvariantCulture)} | {frame} | {context} | " +
                $"{Escape(problem.Text)} | {Escape(problem.Detail)} | [{Path.GetFileName(sheet)}](sheets/{Path.GetFileName(sheet)}) |");
        }
        sb.AppendLine();

        sb.AppendLine("## Najdłuższe nieaktualne tłumaczenia");
        sb.AppendLine();
        foreach (var run in analyzer.StaleRuns.Where(static r => r.Reason != "nieznany w prawdzie").OrderByDescending(static r => r.DurationMs).Take(10))
            sb.AppendLine($"- {(run.StartMs / 1000).ToString("0.0", CultureInfo.InvariantCulture)} s (klatka {run.StartFrame}): „{Escape(run.Text)}” {run.Reason}{(run.MovedPx is { } moved ? $" o {moved:0} px" : string.Empty)}, {run.DurationMs:0} ms");
        sb.AppendLine();
        sb.AppendLine("## Najdłuższe opóźnienia i teksty bez tłumaczenia");
        sb.AppendLine();
        foreach (var item in analyzer.Latencies.Where(static l => !l.Typing).OrderByDescending(static l => l.LatencyMs ?? double.MaxValue).Take(12))
            sb.AppendLine($"- {(item.StartMs / 1000).ToString("0.0", CultureInfo.InvariantCulture)} s (klatka {item.StartFrame}): „{Escape(item.Text)}” {(item.Static ? "stały" : "ruchomy")}{(item.StartedInMotion ? ", pojawił się w ruchu" : string.Empty)}{(item.AtStart ? ", od startu sesji" : string.Empty)} — " +
                (item.LatencyMs is { } latency ? $"{latency:0} ms" : "**nigdy nie pokazany**"));
        sb.AppendLine();
        if (analyzer.HudLosses.Count > 0)
        {
            sb.AppendLine("## Zniknięcia HUD z nakładki");
            sb.AppendLine();
            foreach (var loss in analyzer.HudLosses.Take(25))
                sb.AppendLine($"- {(loss.TimeMs / 1000).ToString("0.0", CultureInfo.InvariantCulture)} s (klatka {loss.Frame}, kąt klatki {analyzer.Angle[Math.Clamp(loss.Frame, 0, analyzer.Clock.Count - 1)].ToString("0.0", CultureInfo.InvariantCulture)}°): „{Escape(loss.Text)}” — {loss.Cause}" +
                    $"{(loss.CaptureAngle is { } ca ? $", kąt klatki odczytu {ca.ToString("0.0", CultureInfo.InvariantCulture)}°" : string.Empty)}{(loss.ProbeAngle is { } pa ? $", kąt live OCR {pa.ToString("0.0", CultureInfo.InvariantCulture)}°" : string.Empty)}");
            sb.AppendLine();
        }
        sb.AppendLine("Pliki: metrics.json (wszystkie liczby), updates.jsonl, shown.jsonl, ocr.jsonl, frames\\c*.jpg (kompozyty), layers\\, patches\\, sheets\\.");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Causes(JsonElement metrics)
    {
        if (!metrics.TryGetProperty("hud", out var hud) || !hud.TryGetProperty("lossesByCause", out var causes)) return "—";
        var parts = causes.EnumerateObject().Select(static p => $"{p.Name} {p.Value}").ToList();
        return parts.Count == 0 ? "—" : string.Join(", ", parts);
    }

    private static string Garbled(JsonElement metrics)
    {
        if (!metrics.TryGetProperty("hud", out var hud) || !hud.TryGetProperty("garbledExamples", out var examples)) return "—";
        var parts = examples.EnumerateArray().Take(5).Select(e => $"„{Escape(Value(e, "src"))}” ({Value(e, "frames")} kl.)").ToList();
        return parts.Count == 0 ? "—" : string.Join(", ", parts);
    }

    private static string Escape(string text) => text.Replace("|", "\\|").Replace("\n", " ");

    public static string Value(JsonElement root, string path, string unit = "")
    {
        var current = root;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return "—";
        }
        return current.ValueKind switch
        {
            JsonValueKind.Number => current.GetDouble().ToString("0.#", CultureInfo.InvariantCulture) + unit,
            JsonValueKind.String when current.GetString() is "NaN" => "—",
            JsonValueKind.String => current.GetString() + unit,
            JsonValueKind.True => "tak",
            JsonValueKind.False => "nie",
            _ => "—",
        };
    }
}
