using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTranslatorOverlay.Core.Glossary;

/// <summary>
/// Termin słownika. <see cref="Scope"/> null albo „any” — termin działa wszędzie; „label” —
/// tylko jako cały tekst etykiety (przycisk „Save”, nagłówek „Trade”): nie jest podpowiadany
/// w zdaniach ani wysyłany do glosariusza DeepL, bo ogólne słowo w dialogu zwykle znaczy
/// coś innego („save the village” to nie „Zapis”).
/// </summary>
public sealed record GlossaryTerm(
    string Source,
    string Target,
    bool CaseSensitive = false,
    int Priority = 0,
    string? Note = null,
    string? Scope = null)
{
    /// <summary>Termin tylko dla całych etykiet (<see cref="GlossaryScope.Label"/>).</summary>
    [JsonIgnore]
    public bool IsLabelOnly => GlossaryScope.IsLabel(Scope);
}

public static class GlossaryScope
{
    public const string Any = "any";
    public const string Label = "label";

    public static bool IsLabel(string? scope) => string.Equals(scope?.Trim(), Label, StringComparison.OrdinalIgnoreCase);

    public static bool IsKnown(string? scope) =>
        string.IsNullOrWhiteSpace(scope)
        || string.Equals(scope.Trim(), Any, StringComparison.OrdinalIgnoreCase)
        || IsLabel(scope);
}

/// <summary>
/// Jedna reguła pierwszeństwa terminów o tym samym źródle — wspólna dla lokalnego
/// tłumaczenia (<see cref="GlossaryService"/>) i glosariusza DeepL. Dwie różne reguły
/// sprawiały, że DeepL tłumaczył termin inaczej niż tłumaczenie lokalne i podpowiedzi LLM.
/// </summary>
public static class GlossaryPrecedence
{
    /// <summary>
    /// Czy <paramref name="candidate"/> (wczytany później) wypiera <paramref name="existing"/>:
    /// wyższy priorytet wygrywa; przy remisie termin z rozróżnianiem wielkości liter wygrywa
    /// z terminem bez rozróżniania (jest bardziej szczegółowy), a przy pełnym remisie wygrywa
    /// termin późniejszy. Kolejność wczytywania to global → profil gry → użytkownik → sesja,
    /// więc późniejszy znaczy bardziej szczegółowy.
    /// </summary>
    public static bool Replaces(GlossaryTerm existing, GlossaryTerm candidate)
    {
        if (candidate.Priority != existing.Priority) return candidate.Priority > existing.Priority;
        return candidate.CaseSensitive || !existing.CaseSensitive;
    }
}

public sealed class GlossaryDocument
{
    public string Name { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "pl";
    public int Version { get; set; } = 1;
    public string? Description { get; set; }
    public List<GlossaryTerm> Terms { get; set; } = [];
}

public sealed record GlossaryConflict(string Source, IReadOnlyList<string> Targets);

public static class GlossarySerializer
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static GlossaryDocument FromJson(string json)
    {
        var document = JsonSerializer.Deserialize<GlossaryDocument>(json, JsonOptions);
        return document ?? throw new FormatException("Plik słownika jest pusty albo ma niepoprawny format JSON.");
    }

    public static string ToJson(GlossaryDocument document) => JsonSerializer.Serialize(document, JsonOptions);
}

public static class GlossaryValidator
{
    public static IReadOnlyList<string> Validate(GlossaryDocument document)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(document.Name))
        {
            errors.Add("Słownik musi mieć nazwę (pole „name”).");
        }
        if (string.IsNullOrWhiteSpace(document.SourceLanguage) || string.IsNullOrWhiteSpace(document.TargetLanguage))
        {
            errors.Add("Słownik musi mieć języki źródłowy i docelowy (pola „sourceLanguage”, „targetLanguage”).");
        }
        if (document.Version < 1)
        {
            errors.Add("Wersja słownika musi być liczbą całkowitą ≥ 1.");
        }

        for (var i = 0; i < document.Terms.Count; i++)
        {
            var term = document.Terms[i];
            if (string.IsNullOrWhiteSpace(term.Source))
            {
                errors.Add($"Termin nr {i + 1} ma pusty tekst źródłowy.");
            }
            if (string.IsNullOrWhiteSpace(term.Target))
            {
                errors.Add($"Termin nr {i + 1} („{term.Source}”) ma puste tłumaczenie.");
            }
            if (!GlossaryScope.IsKnown(term.Scope))
            {
                errors.Add($"Termin nr {i + 1} („{term.Source}”) ma nieznany zakres „{term.Scope}” (dozwolone: „any”, „label”).");
            }
        }

        return errors;
    }
}
