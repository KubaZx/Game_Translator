using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Glossary;

public interface IGlossaryService
{
    int TermCount { get; }
    IReadOnlyList<GlossaryTerm> AllTerms { get; }

    void LoadDocument(GlossaryDocument document);
    void AddTerm(GlossaryTerm term);
    void Clear();

    /// <summary>
    /// Tłumaczy tekst lokalnie, jeżeli CAŁY tekst jest terminem ze słownika
    /// (np. tooltip „Energy Shield”). Nigdy nie podmienia fragmentów słów.
    /// </summary>
    bool TryTranslateExact(string normalizedText, out string translation);

    /// <summary>
    /// Zwraca terminy, które występują WEWNĄTRZ podanych tekstów jako całe słowa lub frazy
    /// (np. „Energy Shield” w „+40 to maximum Energy Shield”). Dłuższe frazy mają
    /// pierwszeństwo: „Shield” nie jest zgłaszany, jeśli jedynym wystąpieniem jest
    /// fragment dopasowanej już frazy „Energy Shield”. Służy jako wskazówka terminologii
    /// dla dostawców, którzy potrafią ją wykorzystać (modele językowe).
    /// </summary>
    IReadOnlyList<GlossaryTerm> FindTermsIn(IReadOnlyList<string> texts, int maxTerms = 40);

    IReadOnlyList<GlossaryConflict> DetectConflicts();
}

public sealed class GlossaryService : IGlossaryService
{
    private readonly Lock _gate = new();
    private readonly List<GlossaryTerm> _terms = [];
    private readonly Dictionary<string, GlossaryTerm> _exactCaseSensitive = [];
    private readonly Dictionary<string, GlossaryTerm> _exactCaseInsensitive = new(StringComparer.OrdinalIgnoreCase);

    public int TermCount
    {
        get { lock (_gate) return _terms.Count; }
    }

    public IReadOnlyList<GlossaryTerm> AllTerms
    {
        get { lock (_gate) return _terms.ToList(); }
    }

    public void LoadDocument(GlossaryDocument document)
    {
        lock (_gate)
        {
            foreach (var term in document.Terms)
            {
                AddTermCore(term);
            }
        }
    }

    public void AddTerm(GlossaryTerm term)
    {
        lock (_gate)
        {
            AddTermCore(term);
        }
    }

    private void AddTermCore(GlossaryTerm term)
    {
        if (string.IsNullOrWhiteSpace(term.Source) || string.IsNullOrWhiteSpace(term.Target)) return;

        // Klucz normalizujemy tak samo jak tekst z OCR (twarde spacje, wielokrotne odstępy,
        // znaki zerowej szerokości) — termin „Energy␣␣Shield” z ręcznie edytowanego JSON-a
        // inaczej nigdy by nie trafił, bo wejście pipeline'u jest już znormalizowane.
        var normalizedSource = TextNormalizer.Normalize(term.Source);
        if (normalizedSource.Length == 0) return;

        var normalized = term with { Source = normalizedSource, Target = term.Target.Trim() };
        _terms.Add(normalized);

        var map = normalized.CaseSensitive ? _exactCaseSensitive : _exactCaseInsensitive;
        if (!map.TryGetValue(normalizedSource, out var existing) || ShouldReplace(existing, normalized))
        {
            map[normalizedSource] = normalized;
        }
    }

    private static bool ShouldReplace(GlossaryTerm existing, GlossaryTerm candidate) =>
        candidate.Priority >= existing.Priority;

    public void Clear()
    {
        lock (_gate)
        {
            _terms.Clear();
            _exactCaseSensitive.Clear();
            _exactCaseInsensitive.Clear();
        }
    }

    public bool TryTranslateExact(string normalizedText, out string translation)
    {
        var key = TextNormalizer.Normalize(normalizedText);
        lock (_gate)
        {
            _exactCaseSensitive.TryGetValue(key, out var sensitive);
            _exactCaseInsensitive.TryGetValue(key, out var insensitive);

            // Priorytet obowiązuje między obiema mapami: termin bez rozróżniania wielkości
            // liter z wyższym priorytetem wygrywa z dokładnym. Przy remisie wygrywa termin
            // dokładny (case-sensitive) jako bardziej szczegółowy.
            var winner = (sensitive, insensitive) switch
            {
                ({ } s, { } i) => i.Priority > s.Priority ? i : s,
                ({ } s, null) => s,
                (null, { } i) => i,
                _ => null,
            };

            if (winner is not null)
            {
                translation = winner.Target;
                return true;
            }
        }

        translation = string.Empty;
        return false;
    }

    public IReadOnlyList<GlossaryTerm> FindTermsIn(IReadOnlyList<string> texts, int maxTerms = 40)
    {
        if (texts.Count == 0 || maxTerms <= 0) return [];

        List<GlossaryTerm> candidates;
        lock (_gate)
        {
            if (_exactCaseSensitive.Count == 0 && _exactCaseInsensitive.Count == 0) return [];
            candidates = [.. _exactCaseSensitive.Values, .. _exactCaseInsensitive.Values];
        }

        // Dłuższe frazy najpierw, potem wyższy priorytet, a przy remisie termin dokładny.
        candidates.Sort(static (a, b) =>
        {
            var byLength = b.Source.Length.CompareTo(a.Source.Length);
            if (byLength != 0) return byLength;
            var byPriority = b.Priority.CompareTo(a.Priority);
            if (byPriority != 0) return byPriority;
            return b.CaseSensitive.CompareTo(a.CaseSensitive);
        });

        var normalizedTexts = texts.Select(TextNormalizer.Normalize).ToArray();
        var covered = normalizedTexts.Select(static t => new bool[t.Length]).ToArray();
        var found = new List<GlossaryTerm>();

        foreach (var term in candidates)
        {
            var comparison = term.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var matched = false;

            for (var t = 0; t < normalizedTexts.Length; t++)
            {
                var text = normalizedTexts[t];
                var start = 0;
                while (start <= text.Length - term.Source.Length)
                {
                    var index = text.IndexOf(term.Source, start, comparison);
                    if (index < 0) break;

                    var end = index + term.Source.Length;
                    if (IsWordBoundary(text, index - 1) && IsWordBoundary(text, end)
                        && !IsCovered(covered[t], index, end))
                    {
                        Array.Fill(covered[t], true, index, term.Source.Length);
                        matched = true;
                    }
                    start = index + 1;
                }
            }

            if (matched)
            {
                found.Add(term);
                if (found.Count >= maxTerms) break;
            }
        }

        return found;
    }

    private static bool IsWordBoundary(string text, int index) =>
        index < 0 || index >= text.Length || !char.IsLetterOrDigit(text[index]);

    private static bool IsCovered(bool[] covered, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (covered[i]) return true;
        }
        return false;
    }

    public IReadOnlyList<GlossaryConflict> DetectConflicts()
    {
        lock (_gate)
        {
            return _terms
                .GroupBy(static t => t.Source, StringComparer.OrdinalIgnoreCase)
                .Select(static g => new GlossaryConflict(
                    g.Key,
                    g.Select(static t => t.Target).Distinct(StringComparer.Ordinal).ToList()))
                .Where(static c => c.Targets.Count > 1)
                .ToList();
        }
    }
}
