using System.Text.Json;
using GameTranslatorOverlay.Core.Glossary;

namespace GameTranslatorOverlay.Core.Evaluation;

/// <summary>Jedna linia korpusu ewaluacyjnego: tekst z gry i wzorcowe tłumaczenie.</summary>
public sealed record EvalLine(
    string Id,
    string Scene,
    string Kind,
    string Source,
    string Reference,
    string? ExpectGender,
    IReadOnlyList<GlossaryTerm> Terms);

/// <summary>
/// Korpus w formacie JSONL (jeden obiekt JSON na wiersz): id, scene, kind
/// (dialog|ui|item|quest), source, reference, opcjonalnie expect_gender ("f"/"m")
/// i terms ([{source, target}]). Puste wiersze są pomijane.
/// </summary>
public sealed class EvalCorpus
{
    public static readonly IReadOnlyList<string> Kinds = ["dialog", "ui", "item", "quest"];

    private EvalCorpus(IReadOnlyList<EvalLine> lines) => Lines = lines;

    public IReadOnlyList<EvalLine> Lines { get; }

    /// <summary>Wszystkie terminy z pól „terms” (bez powtórzeń źródła) — do słownika pipeline'u.</summary>
    public IReadOnlyList<GlossaryTerm> AllTerms =>
        Lines.SelectMany(static l => l.Terms)
            .GroupBy(static t => t.Source.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.First())
            .ToList();

    /// <summary>
    /// Linie w kolejności odtwarzania: sceny w kolejności pierwszego wystąpienia, a w scenie
    /// kolejność z pliku. Tak jak w grze — kolejne kwestie jednej rozmowy trafiają do dostawcy
    /// po sobie, więc kontekst ostatnich linii dialogu działa jak w aplikacji.
    /// </summary>
    public IReadOnlyList<EvalLine> InReplayOrder(int? limit = null)
    {
        var sceneOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in Lines)
        {
            sceneOrder.TryAdd(line.Scene, sceneOrder.Count);
        }
        // OrderBy jest stabilne — kolejność linii w scenie zostaje z pliku.
        var ordered = Lines.OrderBy(line => sceneOrder[line.Scene]);
        return (limit is > 0 ? ordered.Take(limit.Value) : ordered).ToList();
    }

    public static EvalCorpus Parse(string jsonl)
    {
        var lines = new List<EvalLine>();
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = jsonl.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i].Trim().TrimStart('﻿');
            if (row.Length == 0) continue;
            var number = i + 1;
            try
            {
                var line = ParseLine(row, number);
                if (!ids.Add(line.Id)) errors.Add($"Wiersz {number}: powtórzone id „{line.Id}”.");
                else lines.Add(line);
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                errors.Add(ex is JsonException ? $"Wiersz {number}: niepoprawny JSON ({ex.Message})." : ex.Message);
            }
        }

        if (errors.Count > 0) throw new FormatException(string.Join(Environment.NewLine, errors));
        if (lines.Count == 0) throw new FormatException("Korpus nie zawiera żadnej linii.");
        return new EvalCorpus(lines);
    }

    private static EvalLine ParseLine(string row, int number)
    {
        using var document = JsonDocument.Parse(row);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException($"Wiersz {number}: oczekiwano obiektu JSON.");

        string Required(string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
                throw new FormatException($"Wiersz {number}: brak pola tekstowego „{name}”.");
            return value.GetString()!;
        }

        var id = Required("id").Trim();
        var scene = Required("scene").Trim();
        var kind = Required("kind").Trim().ToLowerInvariant();
        if (!Kinds.Contains(kind))
            throw new FormatException($"Wiersz {number}: „kind” musi być jednym z: {string.Join(", ", Kinds)}.");
        var source = Required("source");
        var reference = Required("reference");

        string? gender = null;
        if (root.TryGetProperty("expect_gender", out var genderValue) && genderValue.ValueKind != JsonValueKind.Null)
        {
            gender = genderValue.ValueKind == JsonValueKind.String ? genderValue.GetString()?.Trim().ToLowerInvariant() : null;
            if (gender is not ("f" or "m"))
                throw new FormatException($"Wiersz {number}: „expect_gender” musi mieć wartość \"f\" albo \"m\".");
        }

        var terms = new List<GlossaryTerm>();
        if (root.TryGetProperty("terms", out var termsValue) && termsValue.ValueKind != JsonValueKind.Null)
        {
            if (termsValue.ValueKind != JsonValueKind.Array)
                throw new FormatException($"Wiersz {number}: „terms” musi być tablicą [{{\"source\", \"target\"}}].");
            foreach (var term in termsValue.EnumerateArray())
            {
                var termSource = term.ValueKind == JsonValueKind.Object && term.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                var termTarget = term.ValueKind == JsonValueKind.Object && term.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(termSource) || string.IsNullOrWhiteSpace(termTarget))
                    throw new FormatException($"Wiersz {number}: każdy termin musi mieć niepuste „source” i „target”.");
                terms.Add(new GlossaryTerm(termSource.Trim(), termTarget.Trim()));
            }
        }

        return new EvalLine(id, scene, kind, source, reference, gender, terms);
    }
}
