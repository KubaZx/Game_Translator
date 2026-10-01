using System.Globalization;

namespace GameTranslatorOverlay.ProviderEval;

/// <summary>Parsowanie wiersza poleceń; bez plików, sieci i zmiennych środowiskowych.</summary>
internal sealed record ProviderEvalOptions(
    string CorpusPath,
    IReadOnlyList<string> Providers,
    string OutputDirectory,
    int? Limit,
    string? Variant,
    string? GlossaryPath,
    string? GameName,
    bool DeepLGlossary,
    bool Help)
{
    public static readonly IReadOnlyList<string> KnownProviders = ["mock", "deepl", "azure", "google", "claude", "llm"];

    public const string DefaultCorpus = "eval/en-pl.sample.jsonl";
    public const string DefaultOutput = "eval/out";

    public static ProviderEvalOptions Parse(string[] args)
    {
        string corpus = DefaultCorpus, output = DefaultOutput;
        string? variant = null, glossary = null, game = null;
        int? limit = null;
        var providers = new List<string> { "mock" };
        bool deepLGlossary = true, help = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!seen.Add(arg)) throw new ArgumentException($"Powtórzona opcja: {arg}.");
            string Value()
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Brak wartości opcji {arg}.");
                return args[i];
            }
            switch (arg)
            {
                case "--corpus": corpus = Value(); break;
                case "--out": output = Value(); break;
                case "--variant": variant = Value().Trim(); break;
                case "--glossary": glossary = Value(); break;
                case "--game": game = Value().Trim(); break;
                case "--no-deepl-glossary": deepLGlossary = false; break;
                case "--limit":
                    if (!int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
                        throw new ArgumentException("--limit wymaga liczby całkowitej ≥ 1.");
                    limit = parsed;
                    break;
                case "--providers":
                    providers = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(static p => p.ToLowerInvariant()).Distinct().ToList();
                    var unknown = providers.Where(p => !KnownProviders.Contains(p)).ToList();
                    if (providers.Count == 0 || unknown.Count > 0)
                        throw new ArgumentException(
                            $"Nieznany dostawca: {string.Join(", ", unknown)}. Dostępni: {string.Join(", ", KnownProviders)}.");
                    break;
                case "--help" or "-h": help = true; break;
                default: throw new ArgumentException($"Nieznana opcja: {arg}.");
            }
        }

        return new ProviderEvalOptions(corpus, providers, output, limit, variant, glossary, game, deepLGlossary, help);
    }
}
