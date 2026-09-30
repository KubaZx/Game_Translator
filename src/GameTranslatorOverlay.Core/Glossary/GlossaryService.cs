using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Glossary;

public interface IGlossaryService
{
    int TermCount { get; }
    IReadOnlyList<GlossaryTerm> AllTerms { get; }

    /// <summary>
    /// Terminy, które wolno zapisać poza pamięcią aplikacji (glosariusz na koncie DeepL):
    /// wszystkie poza dodanymi z <c>sessionOnly: true</c> (tryb prywatny). Kolejność jak
    /// w <see cref="AllTerms"/>, czyli kolejność wczytania.
    /// </summary>
    IReadOnlyList<GlossaryTerm> PersistableTerms { get; }

    void LoadDocument(GlossaryDocument document);

    /// <summary>
    /// Dodaje termin w locie. <paramref name="sessionOnly"/> — termin z trybu prywatnego:
    /// działa lokalnie i jako podpowiedź, ale nigdy nie trafia do <see cref="PersistableTerms"/>.
    /// </summary>
    void AddTerm(GlossaryTerm term, bool sessionOnly = false);

    void Clear();

    /// <summary>
    /// Tłumaczy tekst lokalnie, jeżeli CAŁY tekst jest terminem ze słownika
    /// (np. tooltip „Energy Shield”, także złamany do nowej linii, albo etykieta
    /// z dwukropkiem: „Rarity:” → „Rzadkość:”). Nigdy nie podmienia fragmentów słów
    /// i nie tłumaczy form mnogich („Waystones”) — polskiej odmiany słownik nie zna.
    /// </summary>
    bool TryTranslateExact(string normalizedText, out string translation);

    /// <summary>
    /// Zwraca terminy, które występują WEWNĄTRZ podanych tekstów jako całe słowa lub frazy
    /// (np. „Energy Shield” w „+40 to maximum Energy Shield”). Dłuższe frazy mają
    /// pierwszeństwo: „Shield” nie jest zgłaszany, jeśli jedynym wystąpieniem jest
    /// fragment dopasowanej już frazy „Energy Shield”. Służy jako wskazówka terminologii
    /// dla dostawców, którzy potrafią ją wykorzystać (modele językowe). Ostatnie słowo
    /// terminu może mieć angielską końcówkę liczby mnogiej lub dopełniacza („Waystones”,
    /// „Exalted Orbs”, „Resistances”, „Waystone's”), a spacja w terminie pasuje do dowolnego
    /// odstępu, także końca linii. Terminy z zakresem „label” nie są zgłaszane.
    /// </summary>
    IReadOnlyList<GlossaryTerm> FindTermsIn(IReadOnlyList<string> texts, int maxTerms = 40);

    IReadOnlyList<GlossaryConflict> DetectConflicts();
}

public sealed class GlossaryService : IGlossaryService
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];

    // Mapy do tłumaczenia całego tekstu (wszystkie terminy) i do podpowiedzi w zdaniach
    // (bez etykiet). Osobne, bo etykieta „Save” nie może zasłonić podpowiedzi „Save”
    // z innego słownika tylko dlatego, że wygrała przy dokładnym dopasowaniu.
    private readonly Dictionary<string, GlossaryTerm> _exactCaseSensitive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GlossaryTerm> _exactCaseInsensitive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GlossaryTerm> _hintCaseSensitive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GlossaryTerm> _hintCaseInsensitive = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(GlossaryTerm Term, bool SessionOnly);

    // Angielskie końcówki liczby mnogiej i dopełniacza dopuszczalne po ostatnim słowie terminu
    // (apostrof prosty i typograficzny — OCR zwraca oba).
    private static readonly string[] Suffixes = ["s", "es", "'s", "’s", "s'", "s’"];

    public int TermCount
    {
        get { lock (_gate) return _entries.Count; }
    }

    public IReadOnlyList<GlossaryTerm> AllTerms
    {
        get { lock (_gate) return _entries.Select(static e => e.Term).ToList(); }
    }

    public IReadOnlyList<GlossaryTerm> PersistableTerms
    {
        get { lock (_gate) return _entries.Where(static e => !e.SessionOnly).Select(static e => e.Term).ToList(); }
    }

    public void LoadDocument(GlossaryDocument document)
    {
        lock (_gate)
        {
            foreach (var term in document.Terms)
            {
                AddTermCore(term, sessionOnly: false);
            }
        }
    }

    public void AddTerm(GlossaryTerm term, bool sessionOnly = false)
    {
        lock (_gate)
        {
            AddTermCore(term, sessionOnly);
        }
    }

    private void AddTermCore(GlossaryTerm term, bool sessionOnly)
    {
        if (string.IsNullOrWhiteSpace(term.Source) || string.IsNullOrWhiteSpace(term.Target)) return;

        // Klucz normalizujemy tak samo jak tekst z OCR (twarde spacje, wielokrotne odstępy,
        // znaki zerowej szerokości) — termin „Energy␣␣Shield” z ręcznie edytowanego JSON-a
        // inaczej nigdy by nie trafił, bo wejście pipeline'u jest już znormalizowane.
        var normalizedSource = TextNormalizer.Normalize(term.Source);
        if (normalizedSource.Length == 0) return;

        var normalized = term with { Source = normalizedSource, Target = term.Target.Trim() };
        _entries.Add(new Entry(normalized, sessionOnly));

        var key = LookupKey(normalizedSource);
        Put(normalized.CaseSensitive ? _exactCaseSensitive : _exactCaseInsensitive, key, normalized);
        if (!normalized.IsLabelOnly)
        {
            Put(normalized.CaseSensitive ? _hintCaseSensitive : _hintCaseInsensitive, key, normalized);
        }
    }

    private static void Put(Dictionary<string, GlossaryTerm> map, string key, GlossaryTerm term)
    {
        if (!map.TryGetValue(key, out var existing) || GlossaryPrecedence.Replaces(existing, term))
        {
            map[key] = term;
        }
    }

    /// <summary>
    /// Klucz dokładnego dopasowania: każdy ciąg odstępów (także koniec linii) jako jedna
    /// spacja — etykieta „Energy Shield” złamana przez OCR na dwie linie to nadal ten sam termin.
    /// </summary>
    private static string LookupKey(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) && (c != ' ' || (i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))))
            {
                return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            }
        }
        return text;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _exactCaseSensitive.Clear();
            _exactCaseInsensitive.Clear();
            _hintCaseSensitive.Clear();
            _hintCaseInsensitive.Clear();
        }
    }

    public bool TryTranslateExact(string normalizedText, out string translation)
    {
        var key = LookupKey(TextNormalizer.Normalize(normalizedText));
        lock (_gate)
        {
            if (FindExact(key) is { } winner)
            {
                translation = winner.Target;
                return true;
            }

            // Etykieta z dwukropkiem („Rarity:”, „Quality:”) — tłumaczymy termin i oddajemy
            // dwukropek; bez tego popularne etykiety statystyk zawsze szły do API.
            if (key.Length > 1 && key[^1] == ':' && FindExact(key[..^1].TrimEnd()) is { } labelled)
            {
                translation = labelled.Target + ":";
                return true;
            }
        }

        translation = string.Empty;
        return false;
    }

    private GlossaryTerm? FindExact(string key)
    {
        if (key.Length == 0) return null;

        _exactCaseSensitive.TryGetValue(key, out var sensitive);
        _exactCaseInsensitive.TryGetValue(key, out var insensitive);

        // Priorytet obowiązuje między obiema mapami: termin bez rozróżniania wielkości
        // liter z wyższym priorytetem wygrywa z dokładnym. Przy remisie wygrywa termin
        // dokładny (case-sensitive) jako bardziej szczegółowy — ta sama reguła co w DeepL.
        return (sensitive, insensitive) switch
        {
            ({ } s, { } i) => GlossaryPrecedence.Replaces(s, i) ? i : s,
            ({ } s, null) => s,
            (null, { } i) => i,
            _ => null,
        };
    }

    public IReadOnlyList<GlossaryTerm> FindTermsIn(IReadOnlyList<string> texts, int maxTerms = 40)
    {
        if (texts.Count == 0 || maxTerms <= 0) return [];

        List<GlossaryTerm> candidates;
        lock (_gate)
        {
            if (_hintCaseSensitive.Count == 0 && _hintCaseInsensitive.Count == 0) return [];
            candidates = [.. _hintCaseSensitive.Values, .. _hintCaseInsensitive.Values];
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
            var words = term.Source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;

            // Szukamy pierwszego słowa; jednowyrazowy termin na „-y” może wystąpić jako „-ies”
            // („Ability” → „Abilities”), więc wtedy szukamy samego rdzenia.
            var anchor = words.Length == 1 && EndsWithPluralY(words[0]) ? words[0][..^1] : words[0];
            var matched = false;

            for (var t = 0; t < normalizedTexts.Length; t++)
            {
                var text = normalizedTexts[t];
                var start = 0;
                while (start <= text.Length - anchor.Length)
                {
                    var index = text.IndexOf(anchor, start, comparison);
                    if (index < 0) break;

                    if (IsWordBoundary(text, index - 1))
                    {
                        var end = MatchAt(text, index, words, comparison);
                        if (end > index && !IsCovered(covered[t], index, end))
                        {
                            Array.Fill(covered[t], true, index, end - index);
                            matched = true;
                        }
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

    /// <summary>
    /// Dopasowuje słowa terminu od pozycji <paramref name="position"/>: między słowami dowolny
    /// ciąg odstępów (także koniec linii), po ostatnim słowie opcjonalna końcówka liczby mnogiej
    /// lub dopełniacza i granica słowa. Zwraca koniec dopasowania albo -1.
    /// </summary>
    private static int MatchAt(string text, int position, string[] words, StringComparison comparison)
    {
        var p = position;
        for (var w = 0; w < words.Length - 1; w++)
        {
            if (!RegionEquals(text, p, words[w], comparison)) return -1;
            p += words[w].Length;

            var afterSpace = p;
            while (afterSpace < text.Length && char.IsWhiteSpace(text[afterSpace])) afterSpace++;
            if (afterSpace == p) return -1;
            p = afterSpace;
        }
        return MatchLastWord(text, p, words[^1], comparison);
    }

    private static int MatchLastWord(string text, int position, string word, StringComparison comparison)
    {
        if (RegionEquals(text, position, word, comparison))
        {
            var end = position + word.Length;
            if (IsWordBoundary(text, end)) return end;
            foreach (var suffix in Suffixes)
            {
                if (RegionEquals(text, end, suffix, StringComparison.OrdinalIgnoreCase)
                    && IsWordBoundary(text, end + suffix.Length))
                {
                    return end + suffix.Length;
                }
            }
        }

        // „-y” → „-ies” („Ability” → „Abilities”); „Resistance” → „Resistances” obsługuje „s”.
        if (EndsWithPluralY(word))
        {
            var stem = word[..^1];
            var end = position + stem.Length + 3;
            if (RegionEquals(text, position, stem, comparison)
                && RegionEquals(text, position + stem.Length, "ies", StringComparison.OrdinalIgnoreCase)
                && IsWordBoundary(text, end))
            {
                return end;
            }
        }
        return -1;
    }

    private static bool EndsWithPluralY(string word) =>
        word.Length >= 2 && (word[^1] == 'y' || word[^1] == 'Y') && char.IsLetter(word[^2]);

    private static bool RegionEquals(string text, int position, string value, StringComparison comparison) =>
        position >= 0 && position + value.Length <= text.Length
        && string.Compare(text, position, value, 0, value.Length, comparison) == 0;

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
            return _entries
                .Select(static e => e.Term)
                .GroupBy(static t => t.Source, StringComparer.OrdinalIgnoreCase)
                .Select(static g => new GlossaryConflict(
                    g.Key,
                    g.Select(static t => t.Target).Distinct(StringComparer.Ordinal).ToList()))
                .Where(static c => c.Targets.Count > 1)
                .ToList();
        }
    }
}
