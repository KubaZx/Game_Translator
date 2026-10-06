using System.Globalization;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.CorpusTool.Translation;

public sealed record TranslateOptions
{
    public static readonly IReadOnlyList<string> KnownProviders = ["deepl", "llm", "mock"];

    public bool Help { get; init; }
    public string? ProfileId { get; init; }
    public string? ProfileFile { get; init; }
    public string? ProfilesDirectory { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string? CorpusPath { get; init; }
    public string? DataDirectory { get; init; }
    public string? CachePath { get; init; }
    public string? StatsPath { get; init; }
    public int? Limit { get; init; }
    public int? BatchSize { get; init; }
    public int? Parallelism { get; init; }
    public bool DryRun { get; init; }
    public bool Force { get; init; }
    public bool SkipCached { get; init; }
    public bool DeepLGlossary { get; init; } = true;
    public PlayerGender? PlayerGender { get; init; }
    public IReadOnlySet<CorpusEntryKind>? Kinds { get; init; }
    public string? LlmThinking { get; init; }
    public string? LlmReasoningEffort { get; init; }
    public int? LlmMaxTokens { get; init; }
    public bool LlmJson { get; init; }
    public bool LlmPreset { get; init; } = true;
    public decimal? PriceInputPerMillion { get; init; }
    public decimal? PriceOutputPerMillion { get; init; }
    public decimal? PricePerMillionCharacters { get; init; }

    public string ResolvedDataDirectory => Path.GetFullPath(DataDirectory ?? ExtractOptions.DefaultDataDirectory);

    public string ResolvedCachePath => Path.GetFullPath(CachePath ?? Path.Combine(ResolvedDataDirectory, "cache.db"));

    public string ResolvedSettingsDirectory =>
        DataDirectory is null && CachePath is not null
            ? Path.GetDirectoryName(ResolvedCachePath)!
            : ResolvedDataDirectory;

    public string ResolveCorpusPath(string profileId) =>
        Path.GetFullPath(CorpusPath ?? Path.Combine(ResolvedDataDirectory, "corpus", $"{profileId}.corpus.jsonl"));

    public LlmServerOptions? LlmServerOptions =>
        LlmThinking is null && LlmReasoningEffort is null && LlmMaxTokens is null && !LlmJson
            ? null
            : new LlmServerOptions
            {
                Thinking = LlmThinking,
                ReasoningEffort = LlmReasoningEffort,
                MaxTokens = LlmMaxTokens,
                ResponseFormat = LlmJson ? "json_object" : null,
            };

    public static TranslateOptions Parse(IReadOnlyList<string> args)
    {
        var start = args.Count > 0 && args[0].Equals("translate", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var options = new TranslateOptions();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = start; i < args.Count; i++)
        {
            var name = args[i];
            if (name is "--help" or "-h" or "/?")
            {
                options = options with { Help = true };
                continue;
            }
            if (!seen.Add(name)) throw new ArgumentException($"Powtórzona opcja: {name}.");

            switch (name)
            {
                case "--dry-run": options = options with { DryRun = true }; continue;
                case "--force": options = options with { Force = true }; continue;
                case "--skip-cached": options = options with { SkipCached = true }; continue;
                case "--no-deepl-glossary": options = options with { DeepLGlossary = false }; continue;
                case "--llm-json": options = options with { LlmJson = true }; continue;
                case "--llm-no-preset": options = options with { LlmPreset = false }; continue;
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                throw new ArgumentException($"Opcja {name} wymaga wartości.");
            }
            var value = args[++i];
            options = name switch
            {
                "--profile" => options with { ProfileId = value.Trim() },
                "--profile-file" => options with { ProfileFile = value },
                "--profiles-dir" => options with { ProfilesDirectory = value },
                "--provider" => options with { Provider = ParseProvider(value) },
                "--corpus" => options with { CorpusPath = value },
                "--data-dir" => options with { DataDirectory = value },
                "--cache" => options with { CachePath = value },
                "--stats" => options with { StatsPath = value },
                "--limit" => options with { Limit = PositiveInt(name, value) },
                "--batch" => options with { BatchSize = PositiveInt(name, value) },
                "--parallel" => options with { Parallelism = Math.Min(16, PositiveInt(name, value)) },
                "--player-gender" => options with { PlayerGender = ParseGender(value) },
                "--kinds" => options with { Kinds = ParseKinds(value) },
                "--llm-thinking" => options with { LlmThinking = ServerValue(name, value) },
                "--llm-effort" => options with { LlmReasoningEffort = ServerValue(name, value) },
                "--llm-max-tokens" => options with { LlmMaxTokens = PositiveInt(name, value) },
                "--price-in" => options with { PriceInputPerMillion = Price(name, value) },
                "--price-out" => options with { PriceOutputPerMillion = Price(name, value) },
                "--price-chars" => options with { PricePerMillionCharacters = Price(name, value) },
                _ => throw new ArgumentException($"Nieznana opcja: {name}."),
            };
        }

        if (options.Help) return options;
        if (options.ProfileId is null && options.ProfileFile is null) throw new ArgumentException("Podaj --profile <id>.");
        if (options.Provider.Length == 0)
        {
            throw new ArgumentException($"Podaj --provider ({string.Join(" | ", KnownProviders)}).");
        }
        return options;
    }

    private static string ParseProvider(string value)
    {
        var id = value.Trim().ToLowerInvariant();
        return KnownProviders.Contains(id)
            ? id
            : throw new ArgumentException($"Nieznany dostawca: {value}. Dostępni: {string.Join(", ", KnownProviders)}.");
    }

    private static PlayerGender ParseGender(string value) => value.Trim().ToLowerInvariant() switch
    {
        PlayerGenders.MaleSetting => Core.Translation.PlayerGender.Male,
        PlayerGenders.FemaleSetting => Core.Translation.PlayerGender.Female,
        PlayerGenders.UnknownSetting => Core.Translation.PlayerGender.Unknown,
        _ => throw new ArgumentException("--player-gender: male, female albo unknown."),
    };

    private static IReadOnlySet<CorpusEntryKind> ParseKinds(string value)
    {
        var kinds = new HashSet<CorpusEntryKind>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!CorpusEntry.TryParseKind(part, out var kind)) throw new ArgumentException($"--kinds: nieznany rodzaj „{part}” (ui, dialog, subtitle).");
            kinds.Add(kind);
        }
        return kinds.Count > 0 ? kinds : throw new ArgumentException("--kinds: podaj co najmniej jeden rodzaj (ui, dialog, subtitle).");
    }

    private static int PositiveInt(string name, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1
            ? parsed
            : throw new ArgumentException($"{name} wymaga liczby całkowitej ≥ 1.");

    private static decimal Price(string name, string value) =>
        decimal.TryParse(value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"{name} wymaga liczby ≥ 0 (USD za 1 mln).");

    private static string ServerValue(string name, string value) =>
        Infrastructure.Providers.LlmServerOptions.IsValidValue(value)
            ? value.Trim()
            : throw new ArgumentException($"{name}: dozwolone litery, cyfry, „_”, „-”, „.” (do {Infrastructure.Providers.LlmServerOptions.MaxValueLength} znaków).");
}
