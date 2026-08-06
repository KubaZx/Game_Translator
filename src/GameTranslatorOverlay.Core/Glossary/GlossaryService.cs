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

        var normalizedSource = term.Source.Trim();
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
        var key = normalizedText.Trim();
        lock (_gate)
        {
            if (_exactCaseSensitive.TryGetValue(key, out var sensitive))
            {
                translation = sensitive.Target;
                return true;
            }
            if (_exactCaseInsensitive.TryGetValue(key, out var insensitive))
            {
                translation = insensitive.Target;
                return true;
            }
        }

        translation = string.Empty;
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
