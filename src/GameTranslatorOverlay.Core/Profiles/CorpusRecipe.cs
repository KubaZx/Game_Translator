using System.Text.RegularExpressions;

namespace GameTranslatorOverlay.Core.Profiles;

public sealed class CorpusRecipe
{
    public string Format { get; set; } = string.Empty;
    public string Container { get; set; } = string.Empty;
    public string? File { get; set; }
    public List<CorpusSourceRecipe> Sources { get; set; } = [];
}

public sealed class CorpusSourceRecipe
{
    public string Id { get; set; } = string.Empty;
    public string Parser { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public List<string> Include { get; set; } = [];
    public List<string> Exclude { get; set; } = [];
    public string? KeyColumn { get; set; }
    public string? TextColumn { get; set; }
    public List<string> ContextColumns { get; set; } = [];
    public List<string> NodeColumns { get; set; } = [];
    public string? OrderColumn { get; set; }
    public string? SpeakerPattern { get; set; }
    public bool InheritSpeaker { get; set; }
    public bool StripRichText { get; set; } = true;
}

public static class CorpusRecipeValidator
{
    public static readonly IReadOnlyList<string> KnownParsers = ["csv", "srt"];
    public static readonly IReadOnlyList<string> KnownKinds = ["ui", "dialog", "subtitle"];

    public static IReadOnlyList<string> Validate(CorpusRecipe recipe)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(recipe.Format))
        {
            errors.Add("Recepta korpusu musi wskazywać rodzinę formatów (pole „corpus.format”).");
        }
        if (string.IsNullOrWhiteSpace(recipe.Container))
        {
            errors.Add("Recepta korpusu musi wskazywać kontener w folderze gry (pole „corpus.container”).");
        }
        else if (!IsSafeRelativePath(recipe.Container))
        {
            errors.Add($"Ścieżka „corpus.container” („{recipe.Container}”) musi być względna i nie może wychodzić poza folder gry.");
        }
        if (recipe.File is { } file && (file.Length == 0 || !IsSafeRelativePath(file)))
        {
            errors.Add($"Pole „corpus.file” („{file}”) musi być nazwą pliku wewnątrz kontenera.");
        }
        if (recipe.Sources.Count == 0)
        {
            errors.Add("Recepta korpusu musi mieć co najmniej jedno źródło tekstów (pole „corpus.sources”).");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < recipe.Sources.Count; i++)
        {
            var source = recipe.Sources[i];
            var label = string.IsNullOrWhiteSpace(source.Id) ? $"corpus.sources[{i}]" : $"źródło „{source.Id}”";

            if (string.IsNullOrWhiteSpace(source.Id))
            {
                errors.Add($"{label}: brak identyfikatora (pole „id”).");
            }
            else if (!ids.Add(source.Id))
            {
                errors.Add($"{label}: identyfikator się powtarza.");
            }
            if (!KnownParsers.Contains(source.Parser, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"{label}: nieznany parser „{source.Parser}” (dozwolone: {string.Join(", ", KnownParsers)}).");
            }
            if (!KnownKinds.Contains(source.Kind, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"{label}: nieznany rodzaj „{source.Kind}” (dozwolone: {string.Join(", ", KnownKinds)}).");
            }
            if (source.Include.Count == 0 || source.Include.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"{label}: potrzebny co najmniej jeden niepusty wzorzec nazw (pole „include”).");
            }
            if (source.Parser.Equals("csv", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(source.TextColumn))
            {
                errors.Add($"{label}: parser csv wymaga kolumny z tekstem (pole „textColumn”).");
            }
            if (source.SpeakerPattern is { } pattern)
            {
                errors.AddRange(ValidateSpeakerPattern(label, pattern));
            }
        }

        return errors;
    }

    private static IEnumerable<string> ValidateSpeakerPattern(string label, string pattern)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            return [$"{label}: wzorzec mówcy nie jest poprawnym wyrażeniem regularnym ({ex.Message})."];
        }
        return regex.GetGroupNames().Contains("speaker")
            ? []
            : [$"{label}: wzorzec mówcy musi mieć grupę „speaker”."];
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (Path.IsPathRooted(path) || path.Contains(':') || path[0] is '/' or '\\') return false;
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(static part => part is not ("." or ".."));
    }
}
