using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTranslatorOverlay.Core.Glossary;

public sealed record GlossaryTerm(string Source, string Target, bool CaseSensitive = false, int Priority = 0, string? Note = null);

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
        }

        return errors;
    }
}
