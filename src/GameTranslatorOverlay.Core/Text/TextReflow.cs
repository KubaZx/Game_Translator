using System.Text;

namespace GameTranslatorOverlay.Core.Text;

/// <summary>
/// Plan przywrócenia wierszy po tłumaczeniu: ile wierszy oryginału złożyło się na każdy akapit.
/// </summary>
public sealed record ReflowPlan(IReadOnlyList<int> ParagraphLineCounts)
{
    public bool ChangesLayout => ParagraphLineCounts.Any(static count => count > 1);
}

/// <summary>
/// Tekst z OCR jest zawinięty tak, jak w okienku gry: jedno zdanie dialogu bywa rozbite
/// na 2–3 wiersze. Tłumacze (np. DeepL) traktują nowy wiersz jak koniec zdania i tłumaczą
/// kawałki osobno, co psuje gramatykę. <see cref="Unwrap"/> skleja miękkie zawinięcia
/// w akapity (twarde podziały — menu, osobne zdania — zostają), a <see cref="Rewrap"/>
/// rozkłada tłumaczenie z powrotem na tyle samo wierszy, żeby nakładka pasowała do oryginału.
/// </summary>
public static class TextReflow
{
    /// <summary>Znacznik formatu wpisów cache tłumaczonych po sklejeniu wierszy.</summary>
    public const string FormatVersion = "reflow-1";

    private const string SentenceEnd = ".!?…:;";
    private const string ClosingMarks = "\"'”’»)]";

    // Słowa, po których zdanie na pewno trwa dalej — nawet jeśli kolejny wiersz zaczyna się
    // wielką literą (imię, nazwa własna): „talk to the\nBlacksmith”.
    private static readonly HashSet<string> ContinuationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "to", "of", "and", "or", "but", "nor", "with", "without", "for", "in", "on", "at",
        "from", "by", "into", "onto", "about", "as", "than", "that", "which", "who", "whose", "if", "when",
        "your", "my", "his", "her", "its", "our", "their", "this", "these", "those", "is", "are", "was",
        "were", "be", "been", "will", "would", "can", "could", "should", "must", "may", "might", "not",
        "you", "we", "they", "he", "she", "it", "i", "me", "him", "them", "us", "some", "any", "every",
        "each", "no", "all", "more", "most", "very", "so", "just", "also", "have", "has", "had", "do", "does",
    };

    public static (string Text, ReflowPlan Plan) Unwrap(string normalizedText)
    {
        var lines = normalizedText.Split('\n')
            .Select(static line => OcrTextRepair.Repair(line.Trim()))
            .Where(static line => line.Length > 0)
            .ToList();
        if (lines.Count == 0) return (string.Empty, new ReflowPlan([]));

        var paragraphs = new List<StringBuilder> { new(lines[0]) };
        var counts = new List<int> { 1 };
        for (var i = 1; i < lines.Count; i++)
        {
            var current = paragraphs[^1];
            var next = lines[i];
            if (IsSoftBreak(lines[i - 1], next))
            {
                if (EndsWithWordHyphen(current) && char.IsLower(next[0]))
                {
                    // „equip-\nment” → „equipment”: przeniesienie wyrazu, nie myślnik.
                    current.Length -= 1;
                    current.Append(next);
                }
                else
                {
                    current.Append(' ').Append(next);
                }
                counts[^1]++;
            }
            else
            {
                paragraphs.Add(new StringBuilder(next));
                counts.Add(1);
            }
        }

        return (string.Join('\n', paragraphs.Select(static p => p.ToString())), new ReflowPlan(counts));
    }

    /// <summary>
    /// Rozkłada tłumaczenie na wiersze wg planu. Gdy liczba akapitów w tłumaczeniu nie zgadza
    /// się z planem, tłumaczenie zostaje bez zmian — lepiej inny podział niż przesunięte zdania.
    /// </summary>
    public static string Rewrap(string translated, ReflowPlan plan)
    {
        if (!plan.ChangesLayout || string.IsNullOrWhiteSpace(translated)) return translated;

        var paragraphs = translated.Replace("\r\n", "\n").Split('\n');
        if (paragraphs.Length != plan.ParagraphLineCounts.Count) return translated;

        return string.Join('\n', paragraphs.Select((paragraph, i) =>
            WrapBalanced(paragraph.Trim(), plan.ParagraphLineCounts[i])));
    }

    /// <summary>
    /// Odwrotność <see cref="Rewrap"/> dla tekstu pisanego przez gracza (ręczna korekta): wiersze
    /// ułożone jak na ekranie skleja z powrotem w akapity planu, żeby w pamięci dialogu leżała
    /// ta sama postać co surowy wynik dostawcy. Przy jednym akapicie źródła całość jest jednym
    /// akapitem. Gdy gracz ułożył korektę w inną liczbę wierszy niż plan, wiersze dzielimy między
    /// akapity proporcjonalnie do planu (każdy akapit dostaje co najmniej jeden wiersz) — w pamięci
    /// nigdy nie ląduje więcej akapitów niż w źródle, bo model widziałby źle dopasowany przykład.
    /// Mniej wierszy niż akapitów nie da się dopasować — wtedy całość to jeden akapit.
    /// </summary>
    public static string ToParagraphs(string wrapped, ReflowPlan plan)
    {
        var lines = wrapped.Replace("\r\n", "\n").Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToList();
        var counts = plan.ParagraphLineCounts;
        if (counts.Count <= 1) return string.Join(' ', lines);
        if (lines.Count < counts.Count) return string.Join(' ', lines);

        // Przy zgodnej liczbie wierszy granice wypadają dokładnie na granicach planu.
        var planned = counts.Sum();
        var paragraphs = new List<string>(counts.Count);
        var start = 0;
        var cumulative = 0;
        for (var i = 0; i < counts.Count; i++)
        {
            cumulative += counts[i];
            var end = i == counts.Count - 1
                ? lines.Count
                : (int)Math.Round((double)cumulative * lines.Count / planned, MidpointRounding.AwayFromZero);
            end = Math.Clamp(end, start + 1, lines.Count - (counts.Count - 1 - i));
            paragraphs.Add(string.Join(' ', lines.Skip(start).Take(end - start)));
            start = end;
        }
        return string.Join('\n', paragraphs);
    }

    /// <summary>
    /// Dzieli tekst na najwyżej <paramref name="lineCount"/> wierszy o możliwie równej długości
    /// (minimalizacja sumy kwadratów odchyleń; wyrazy nigdy nie są dzielone).
    /// </summary>
    public static string WrapBalanced(string text, int lineCount)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = Math.Min(lineCount, words.Length);
        if (lines <= 1) return string.Join(' ', words);

        var total = words.Sum(static w => w.Length) + words.Length - 1;
        var target = (double)total / lines;

        // prefix[i] = długość i pierwszych wyrazów bez spacji na końcu
        var prefix = new int[words.Length + 1];
        for (var i = 0; i < words.Length; i++) prefix[i + 1] = prefix[i] + words[i].Length;
        double LineCost(int from, int to)
        {
            var length = prefix[to] - prefix[from] + (to - from - 1);
            var deviation = length - target;
            return deviation * deviation;
        }

        // cost[k, i] — najlepszy podział i pierwszych wyrazów na k wierszy
        var cost = new double[lines + 1, words.Length + 1];
        var split = new int[lines + 1, words.Length + 1];
        for (var k = 0; k <= lines; k++)
            for (var i = 0; i <= words.Length; i++)
                cost[k, i] = double.PositiveInfinity;
        cost[0, 0] = 0;

        for (var k = 1; k <= lines; k++)
        {
            for (var i = k; i <= words.Length - (lines - k); i++)
            {
                for (var j = k - 1; j < i; j++)
                {
                    if (double.IsPositiveInfinity(cost[k - 1, j])) continue;
                    var candidate = cost[k - 1, j] + LineCost(j, i);
                    if (candidate < cost[k, i])
                    {
                        cost[k, i] = candidate;
                        split[k, i] = j;
                    }
                }
            }
        }

        var breaks = new List<int>();
        for (int k = lines, i = words.Length; k > 0; i = split[k, i], k--)
        {
            breaks.Add(i);
        }
        breaks.Reverse();

        var result = new StringBuilder();
        var start = 0;
        foreach (var end in breaks)
        {
            if (result.Length > 0) result.Append('\n');
            result.AppendJoin(' ', words[start..end]);
            start = end;
        }
        return result.ToString();
    }

    /// <summary>
    /// Miękkie zawinięcie: poprzedni wiersz nie kończy zdania, a następny je kontynuuje
    /// (mała litera, przecinek na końcu albo słowo, po którym zdanie musi trwać dalej).
    /// Wiersze menu („Options”, „Quit”) zaczynają się wielką literą bez takiego słowa — zostają osobno.
    /// </summary>
    private static bool IsSoftBreak(string previous, string next)
    {
        var last = previous.TrimEnd(ClosingMarks.ToCharArray());
        if (last.Length == 0 || SentenceEnd.Contains(last[^1])) return false;
        if (!char.IsLetterOrDigit(next[0]) && next[0] is not ('(' or '"' or '\'' or '“')) return false;

        if (char.IsLower(next[0])) return true;
        if (previous[^1] is ',' or '-' or '–' or '—') return true;
        // „You need\n3 more keys.” — liczba po zwykłym słowie kontynuuje zdanie;
        // statystyki („+25% …”) i wiersze zaczynające się wielką literą zostają osobno.
        if (char.IsDigit(next[0]) && char.IsLower(previous[^1])) return true;

        var lastSpace = previous.LastIndexOf(' ');
        var lastWord = previous[(lastSpace + 1)..].Trim(ClosingMarks.ToCharArray());
        return ContinuationWords.Contains(lastWord);
    }

    private static bool EndsWithWordHyphen(StringBuilder text) =>
        text.Length >= 2 && text[^1] == '-' && char.IsLetter(text[^2]);
}

/// <summary>
/// Naprawa klasycznych pomyłek OCR w czcionkach gier, które psują tłumaczenie:
/// mała litera „l” zamiast „I” w skrótach („l'm” → „I'm”) i pionowa kreska zamiast
/// samotnego „I” („| have” → „I have”). Zmiany są celowo wąskie — tylko jednoznaczne przypadki.
/// </summary>
public static class OcrTextRepair
{
    private static readonly string[] ContractionSuffixes = ["'m", "'ll", "'ve", "'d", "’m", "’ll", "’ve", "’d"];

    public static string Repair(string line)
    {
        if (line.Length == 0) return line;
        var words = line.Split(' ');
        var changed = false;
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word == "|" && i + 1 < words.Length && words[i + 1].Length > 0 && char.IsLetter(words[i + 1][0]))
            {
                words[i] = "I";
                changed = true;
                continue;
            }
            if (word.Length >= 3 && word[0] is 'l' or '|' && ContractionSuffixes.Any(suffix =>
                    word.Length >= suffix.Length + 1
                    && word.AsSpan(1).StartsWith(suffix, StringComparison.Ordinal)
                    && (word.Length == suffix.Length + 1 || !char.IsLetter(word[suffix.Length + 1]))))
            {
                words[i] = "I" + word[1..];
                changed = true;
            }
        }
        return changed ? string.Join(' ', words) : line;
    }
}
