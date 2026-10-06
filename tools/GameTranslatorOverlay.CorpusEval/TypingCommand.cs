using System.Globalization;
using System.Text;
using System.Text.Json;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.CorpusEval;

internal sealed class TypingStats
{
    public int Lines { get; set; }
    public int Readings { get; set; }
    public int ProviderTexts { get; set; }
    public int LinesSnappedBeforeEnd { get; set; }
    public double TypedShareAtFirstSnapSum { get; set; }
    public int ProviderTextsAfterFirstSnap { get; set; }
    public int LinesWithIdentityChange { get; set; }
    public int LinesLostAfterSnap { get; set; }
    public int LinesOtherKeyAfterSnap { get; set; }
    public int DisplayChanges { get; set; }
    public int DisplayChangesOtherThanLayout { get; set; }
    public int LinesEarlyOtherKey { get; set; }
    public int LinesEarlyOtherKeyViaLabel { get; set; }
    public double MeanTypedShareAtFirstSnap => LinesSnappedBeforeEnd == 0 ? 0 : Math.Round(TypedShareAtFirstSnapSum / LinesSnappedBeforeEnd, 3);
}

internal static class TypingCommand
{
    public static int Run(EvalArgs args)
    {
        var corpus = CorpusJsonl.ReadFile(args.Required("corpus"));
        var samples = EvalData.ReadSamples(args.Required("ocr"));
        var outDir = args.Required("out");
        var privateDir = args.Required("private");
        var wrap = args.Int("wrap", 48);
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(privateDir);
        var snapper = new CorpusSnapper(CorpusIndex.Build(corpus), CorpusFeatures.Off(args) ? CorpusFeatures.Disabled(CorpusSnapOptions.Default) : CorpusSnapOptions.Default);

        var clean = corpus
            .Where(static e => e.Kind is CorpusEntryKind.Dialog or CorpusEntryKind.Subtitle)
            .Select(static e => (Text: string.Join(' ', CorpusTranslationKey.Normalize(e.En).Split('\n')), Truth: CorpusText.MatchKey(e.En)))
            .Where(static t => t.Text.Length > 0)
            .DistinctBy(static t => t.Truth)
            .Select(t => (Text: Wrap(t.Text, wrap), t.Truth))
            .ToList();
        var ocr = samples
            .Where(static s => s.Kind is "dialog" or "subtitle")
            .Select(static s => (Text: string.Join('\n', s.Blocks.Where(static b => b.OverlapsText).Select(static b => b.Text)), Truth: CorpusText.MatchKey(s.Truth)))
            .Where(static t => t.Text.Length > 0)
            .ToList();

        var problems = new List<string>();
        var results = new Dictionary<string, TypingStats>();
        foreach (var (mode, cuts) in new (string, Func<string, IEnumerable<int>>)[] { ("co 3 znaki", EveryThird), ("przy przerwach (interpunkcja)", AtPauses) })
        {
            results[$"korpus, {mode}"] = Simulate($"korpus/{mode}", clean, snapper, cuts, problems);
            results[$"ocr, {mode}"] = Simulate($"ocr/{mode}", ocr, snapper, cuts, problems);
        }

        var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(Path.Combine(outDir, "pisanie.json"), JsonSerializer.Serialize(new { wrap, results }, json));
        var md = Markdown(results, wrap);
        File.WriteAllText(Path.Combine(outDir, "pisanie.md"), md);
        File.WriteAllLines(Path.Combine(privateDir, "pisanie-problemy.tsv"), problems, new UTF8Encoding(false));
        Console.WriteLine(md);
        return 0;
    }

    private static IEnumerable<int> EveryThird(string text)
    {
        var total = CorpusText.LetterOrDigitCount(text);
        for (var typed = 3; typed < total; typed += 3) yield return typed;
    }

    private static IEnumerable<int> AtPauses(string text)
    {
        var total = CorpusText.LetterOrDigitCount(text);
        var seen = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i])) seen++;
            if (text[i] is ',' or '.' or '!' or '?' or ';' or ':' or '-' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]) && seen < total)
            {
                yield return seen;
            }
        }
    }

    private static string Wrap(string text, int width)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return string.Join('\n', lines);
    }

    private static TypingStats Simulate(string set, IReadOnlyList<(string Text, string Truth)> lines, CorpusSnapper snapper,
        Func<string, IEnumerable<int>> cuts, List<string> problems)
    {
        var stats = new TypingStats();
        foreach (var (text, truth) in lines)
        {
            var provider = new CountingMock();
            var pipeline = new TranslationPipeline(new GlossaryService(), new InMemoryTranslationCache(), provider, new UsageTracker(),
                new TranslationPipelineOptions { GameProfile = "typing", Corpus = snapper, SplitParagraphs = true });
            var total = CorpusText.LetterOrDigitCount(text);
            stats.Lines++;
            var snapped = false;
            string? identity = null;
            string? display = null;
            var providerAtSnap = 0;
            var lost = false;
            var otherKey = false;
            var identityChanged = false;
            var earlyOther = false;
            var earlyViaLabel = false;
            var trace = new List<string>();
            foreach (var typed in cuts(text).Distinct().Order())
            {
                var reading = Cut(text, typed);
                stats.Readings++;
                if (TextNormalizer.Normalize(reading).Length == 0) continue;
                var outcome = pipeline.TranslateAsync([reading], "en", "pl").GetAwaiter().GetResult()[0];
                var keys = outcome.Parts?.Where(static p => p.FromCorpus).Select(static p => CorpusText.MatchKey(p.CacheKey!)).ToList() ?? [];
                var whole = keys.Count > 0 && outcome.Parts!.All(static p => p.IsLiteral || p.FromCorpus);
                var currentIdentity = pipeline.CorpusIdentity(reading);
                var onTruth = whole && keys.All(k => k == truth) && currentIdentity is not null;
                trace.Add($"{typed}:{(onTruth ? "T" : keys.Count == 0 ? "-" : "X")}");
                if (!snapped)
                {
                    if (keys.Any(k => k != truth))
                    {
                        earlyOther = true;
                        if (pipeline.Options.Corpus!.SnapBlock(reading).Segments.Any(static s => s.Match.IsLabel)) earlyViaLabel = true;
                    }
                    if (!onTruth) continue;
                    snapped = true;
                    identity = currentIdentity;
                    display = outcome.TranslatedText;
                    providerAtSnap = provider.Texts;
                    stats.LinesSnappedBeforeEnd++;
                    stats.TypedShareAtFirstSnapSum += (double)typed / total;
                    continue;
                }
                if (!onTruth)
                {
                    if (keys.Count == 0) lost = true;
                    else otherKey = true;
                    continue;
                }
                if (currentIdentity != identity)
                {
                    identityChanged = true;
                    identity = currentIdentity;
                }
                if (outcome.TranslatedText != display)
                {
                    stats.DisplayChanges++;
                    if (Flatten(outcome.TranslatedText) != Flatten(display)) stats.DisplayChangesOtherThanLayout++;
                    display = outcome.TranslatedText;
                }
            }
            stats.ProviderTexts += provider.Texts;
            if (earlyOther) stats.LinesEarlyOtherKey++;
            if (earlyViaLabel) stats.LinesEarlyOtherKeyViaLabel++;
            if (!snapped) continue;
            var afterSnap = provider.Texts - providerAtSnap;
            stats.ProviderTextsAfterFirstSnap += afterSnap;
            if (lost) stats.LinesLostAfterSnap++;
            if (otherKey) stats.LinesOtherKeyAfterSnap++;
            if (identityChanged) stats.LinesWithIdentityChange++;
            if (lost || otherKey || afterSnap > 0 || identityChanged || earlyViaLabel)
            {
                problems.Add(string.Join('\t', set, lost ? "zgubione" : "", otherKey ? "inny-klucz" : "", identityChanged ? "inna-tozsamosc" : "",
                    earlyViaLabel ? "wczesna-etykieta" : "", afterSnap, string.Join(' ', trace), Escape(text)));
            }
        }
        return stats;
    }

    private static string Flatten(string? text) => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Cut(string text, int letters)
    {
        var seen = 0;
        var end = 0;
        while (end < text.Length && seen < letters)
        {
            if (char.IsLetterOrDigit(text[end])) seen++;
            end++;
        }
        while (end < text.Length && !char.IsLetterOrDigit(text[end]) && !char.IsWhiteSpace(text[end])) end++;
        return text[..end];
    }

    private static string Escape(string text) => text.Replace('\t', ' ').Replace("\n", " / ");

    private static string Markdown(Dictionary<string, TypingStats> results, int wrap)
    {
        var c = CultureInfo.InvariantCulture;
        var md = new StringBuilder();
        md.AppendLine("# Dialog pisany literami: kolejne odczyty tej samej linii");
        md.AppendLine();
        md.AppendLine(string.Create(c, $"Każda linia dialogu/napisów korpusu (zawinięta co {wrap} znaków) i każdy odczyt OCR linii dialogu z próbek syntetycznych jest „dopisywany” (cięcie co 3 znaki albo tylko w przerwach po interpunkcji, jak przy animacji pisania z pauzami); każdy kolejny odczyt idzie przez TranslationPipeline (Mock, pusty cache na linię). „Trafienie” = cały odczyt przyciągnięty do właściwej linii. Liczby bez tekstów gry."));
        md.AppendLine();
        md.AppendLine("| zbiór | linie | odczyty | teksty do dostawcy (wszystkie odczyty) | linie trafione przed końcem | średnio wpisane przy 1. trafieniu | teksty do dostawcy po 1. trafieniu | linie ze zmianą klucza nakładki po trafieniu | linie z późniejszym odczytem bez trafienia / z innym kluczem | zmiany wyświetlanego tekstu po trafieniu: wszystkie / poza układem wierszy | linie z wcześniejszym przyciągnięciem do innego tekstu (w tym przez nową etykietę) |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---|---|---|");
        foreach (var (name, s) in results)
        {
            md.AppendLine(string.Create(c,
                $"| {name} | {s.Lines} | {s.Readings} | {s.ProviderTexts} | {s.LinesSnappedBeforeEnd} | {s.MeanTypedShareAtFirstSnap:P0} | {s.ProviderTextsAfterFirstSnap} | {s.LinesWithIdentityChange} | {s.LinesLostAfterSnap} / {s.LinesOtherKeyAfterSnap} | {s.DisplayChanges} / {s.DisplayChangesOtherThanLayout} | {s.LinesEarlyOtherKey} ({s.LinesEarlyOtherKeyViaLabel}) |"));
        }
        return md.ToString();
    }
}
