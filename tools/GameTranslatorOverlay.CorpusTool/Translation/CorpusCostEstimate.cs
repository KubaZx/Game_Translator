using System.Globalization;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.CorpusTool.Translation;

public sealed record CorpusCostEstimate
{
    public const decimal InputCharsPerToken = 3.5m;
    public const decimal OutputCharsPerToken = 3.0m;
    public const decimal TranslationLengthRatio = 1.15m;
    public const long DeepLFreeMonthlyCharacters = 500_000;
    public const decimal DeepLGrowthUsdPerMillionCharacters = 27.50m;
    public const decimal DeepSeekFlashInputOffPeak = 0.15m;
    public const decimal DeepSeekFlashInputPeak = 0.30m;
    public const decimal DeepSeekFlashOutputOffPeak = 0.60m;
    public const decimal DeepSeekFlashOutputPeak = 1.20m;

    public long Characters { get; init; }
    public int Requests { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public decimal? LowUsd { get; init; }
    public decimal? HighUsd { get; init; }
    public required string Basis { get; init; }

    public static CorpusCostEstimate ForDeepL(CorpusPlan plan, decimal? usdPerMillionCharacters)
    {
        var characters = plan.Characters;
        if (usdPerMillionCharacters is { } price)
        {
            var cost = Usd(characters, price);
            return new CorpusCostEstimate
            {
                Characters = characters,
                Requests = plan.Batches.Count,
                LowUsd = cost,
                HighUsd = cost,
                Basis = string.Create(CultureInfo.InvariantCulture, $"stawka z --price-chars: {price} USD za 1 mln znaków"),
            };
        }
        return new CorpusCostEstimate
        {
            Characters = characters,
            Requests = plan.Batches.Count,
            LowUsd = 0m,
            HighUsd = Usd(characters, DeepLGrowthUsdPerMillionCharacters),
            Basis = string.Create(CultureInfo.InvariantCulture,
                $"DeepL API Free: 0 USD w limicie {DeepLFreeMonthlyCharacters} znaków miesięcznie " +
                $"({Percent(characters, DeepLFreeMonthlyCharacters)} limitu); ponad pakiet Growth ok. " +
                $"{DeepLGrowthUsdPerMillionCharacters} USD za 1 mln znaków (źródło wtórne, badanie 2026-10-05)"),
        };
    }

    public static CorpusCostEstimate ForMock(CorpusPlan plan) => new()
    {
        Characters = plan.Characters,
        Requests = plan.Batches.Count,
        LowUsd = 0m,
        HighUsd = 0m,
        Basis = "Mock: lokalnie, bez sieci",
    };

    public static CorpusCostEstimate ForLlm(
        CorpusPlan plan,
        CorpusTranslatorSettings settings,
        IGlossaryService glossary,
        string? endpointHost,
        bool local,
        decimal? inputPerMillion,
        decimal? outputPerMillion)
    {
        long inputChars = 0, outputChars = 0;
        var previous = new Dictionary<int, List<RecentExchange>>();
        foreach (var batch in plan.Batches.OrderBy(static b => b.Chain).ThenBy(static b => b.Sequence))
        {
            var texts = batch.Texts.Select(static t => TextReflow.Unwrap(t.Key).Text).ToList();
            var recent = previous.TryGetValue(batch.Chain, out var list) ? list : [];
            var context = new TranslationContext(settings.GameName, glossary.FindTermsIn(texts, settings.MaxContextTerms))
            {
                RecentExchanges = recent.TakeLast(settings.MaxRecentTexts).ToList(),
                PlayerGender = settings.PlayerGender,
                Scene = batch.Scene,
                TextNotes = batch.Notes,
            };
            inputChars += LlmTranslationPrompt.BuildSystemPrompt(settings.SourceLanguage, settings.TargetLanguage, context).Length;
            inputChars += LlmTranslationPrompt.BuildUserMessage(texts, context.RecentExchanges, batch.Notes).Length;
            var translated = texts.Sum(static t => (long)Math.Ceiling(t.Length * TranslationLengthRatio));
            outputChars += translated + 20 + 6L * texts.Count;
            previous[batch.Chain] = [.. recent, .. texts.Select(static t => new RecentExchange(t, new string('x', (int)Math.Ceiling(t.Length * TranslationLengthRatio))))];
        }

        var inputTokens = (long)Math.Ceiling(inputChars / InputCharsPerToken);
        var outputTokens = (long)Math.Ceiling(outputChars / OutputCharsPerToken);
        var estimate = new CorpusCostEstimate
        {
            Characters = plan.Characters,
            Requests = plan.Batches.Count,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Basis = "brak cennika — podaj --price-in i --price-out (USD za 1 mln tokenów)",
        };
        if (inputPerMillion is { } input && outputPerMillion is { } output)
        {
            var cost = Tokens(inputTokens, input) + Tokens(outputTokens, output);
            return estimate with
            {
                LowUsd = cost,
                HighUsd = cost,
                Basis = string.Create(CultureInfo.InvariantCulture, $"stawki z opcji: wejście {input}, wyjście {output} USD za 1 mln tokenów"),
            };
        }
        if (local)
        {
            return estimate with { LowUsd = 0m, HighUsd = 0m, Basis = "serwer na tym komputerze — bez opłat za tokeny" };
        }
        if (string.Equals(endpointHost, "api.deepseek.com", StringComparison.OrdinalIgnoreCase))
        {
            return estimate with
            {
                LowUsd = Tokens(inputTokens, DeepSeekFlashInputOffPeak) + Tokens(outputTokens, DeepSeekFlashOutputOffPeak),
                HighUsd = Tokens(inputTokens, DeepSeekFlashInputPeak) + Tokens(outputTokens, DeepSeekFlashOutputPeak),
                Basis = "cennik deepseek-flash z badania 2026-10-05 (poza szczytem – w szczycie, bez trafień cache, bez tokenów rozumowania)",
            };
        }
        return estimate;
    }

    private static decimal Usd(long characters, decimal perMillion) => Math.Round(characters * perMillion / 1_000_000m, 4);

    private static decimal Tokens(long tokens, decimal perMillion) => Math.Round(tokens * perMillion / 1_000_000m, 4);

    private static string Percent(long value, long of) =>
        (value * 100.0 / of).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
