using System.Globalization;
using System.Text;
using System.Text.Json;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.CorpusTool.Safety;
using GameTranslatorOverlay.Infrastructure.Caching;
using GameTranslatorOverlay.Infrastructure.Content;
using GameTranslatorOverlay.Infrastructure.Providers;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.CorpusTool.Translation;

public static class TranslateCommand
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitRefused = 3;
    public const int ExitPartial = 4;
    public const int ExitCancelled = 130;

    private static readonly JsonSerializerOptions StatsJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<int> RunAsync(
        TranslateOptions options,
        TextWriter output,
        TextWriter error,
        Func<EnvironmentTranslationProviders>? providerSource = null,
        CancellationToken cancellationToken = default)
    {
        var c = CultureInfo.InvariantCulture;
        var profile = ProfileLocator.Load(options.ProfileId, options.ProfileFile, options.ProfilesDirectory, options.ResolvedDataDirectory);
        var profileErrors = ProfileValidator.Validate(profile);
        if (profileErrors.Count > 0) throw new RefusedException("Profil jest niepoprawny: " + string.Join(" ", profileErrors));

        var corpusPath = options.ResolveCorpusPath(profile.Id);
        if (!File.Exists(corpusPath))
        {
            error.WriteLine($"Brak korpusu {corpusPath}. Najpierw uruchom: extract --profile {profile.Id} --game-dir <folder gry>.");
            return ExitError;
        }

        var cachePath = options.ResolvedCachePath;
        if ((OutputLocationGuard.CheckOutsideGame(cachePath, "Baza tłumaczeń", profile.ProcessNames)
             ?? OutputLocationGuard.CheckLocalData(cachePath)?.Replace("Plik wynikowy", "Baza tłumaczeń", StringComparison.Ordinal)) is { } cacheProblem)
        {
            throw new RefusedException(cacheProblem);
        }
        if (options.StatsPath is { } statsPath
            && (OutputLocationGuard.CheckOutsideGame(statsPath, "Plik statystyk", profile.ProcessNames)
                ?? OutputLocationGuard.CheckLocalData(statsPath)) is { } statsProblem)
        {
            throw new RefusedException(statsProblem);
        }

        if (options.Provider == "mock" && !options.DryRun && options.CachePath is null && options.DataDirectory is null)
        {
            throw new RefusedException(
                "Mock zapisuje atrapy „[PL] …”, które w aplikacji z prawdziwym dostawcą zasłaniają wpisy globalne. " +
                "Podaj jawnie --cache albo --data-dir (np. kopię bazy).");
        }

        var appSettings = LocalAppSettings.Read(options.ResolvedSettingsDirectory);
        if (appSettings.PrivateMode && !options.DryRun)
        {
            throw new RefusedException(
                $"Tryb prywatny jest włączony ({appSettings.SettingsPath}) — wpisy z wyprzedzeniem nie są zapisywane do bazy (ADR-014 pkt 7). " +
                "Wyłącz tryb prywatny w aplikacji albo uruchom z --dry-run.");
        }

        var entries = CorpusJsonl.ReadFile(corpusPath);
        var glossary = LoadGlossary(profile, options.ResolvedSettingsDirectory, appSettings, output);
        var gender = options.PlayerGender ?? appSettings.PlayerGender;

        using var providers = providerSource?.Invoke() ?? new EnvironmentTranslationProviders();
        var created = providers.Create(options.Provider, new EnvironmentProviderOptions
        {
            DeepLGlossary = options.DeepLGlossary,
            LlmServerOptions = options.LlmServerOptions,
            UseLlmPreset = options.LlmPreset,
        });
        if (created.Provider is null && !options.DryRun)
        {
            error.WriteLine($"Dostawca {options.Provider} niedostępny: {created.SkipReason} Klucze podaje się wyłącznie w zmiennych środowiskowych.");
            return ExitError;
        }

        var defaults = CorpusProviderTraits.For(options.Provider);
        var traits = created.Provider is { } provider ? CorpusProviderTraits.Of(provider, defaults.MaxBatchSize) : defaults;
        var batchSize = Math.Min(options.BatchSize ?? traits.MaxBatchSize, traits.MaxBatchSize);
        var settings = new CorpusTranslatorSettings
        {
            ProfileId = profile.Id,
            GameName = profile.Name,
            SourceLanguage = appSettings.SourceLanguage,
            TargetLanguage = appSettings.TargetLanguage,
            PlayerGender = gender,
            BatchSize = batchSize,
            Parallelism = options.Parallelism ?? (created.IsLocal && options.Provider == "llm" ? 1 : options.Provider == "mock" ? 4 : 3),
            Limit = options.Limit,
            Force = options.Force,
            SkipCached = options.SkipCached,
            Kinds = options.Kinds,
        };

        var cache = new SqliteTranslationCache(cachePath);
        var store = new SqliteCorpusCacheStore(cache);
        var cacheExisted = File.Exists(cachePath);

        output.WriteLine($"Profil: {profile.Id} ({profile.Name}); korpus: {corpusPath}");
        output.WriteLine($"Baza: {cachePath}{(cacheExisted ? string.Empty : " (nowa)")}; ustawienia: {(appSettings.Exists ? appSettings.SettingsPath : "brak pliku — domyślne")}");
        output.WriteLine(string.Create(c, $"Dostawca: {traits.Name}{Describe(created)}; partie do {batchSize}, równolegle {settings.Parallelism}; języki {settings.SourceLanguage}→{settings.TargetLanguage}; płeć gracza: {PlayerGenders.ToSetting(gender)}"));
        if (created.Provider is null) output.WriteLine($"Uwaga: dostawca niedostępny ({created.SkipReason}) — przebieg próbny liczy bez niego.");
        if (appSettings.PrivateMode) output.WriteLine("Uwaga: tryb prywatny jest włączony — prawdziwy przebieg odmówi zapisu.");

        var plan = await CorpusTranslationPlanning.PlanAsync(entries, traits, glossary, store, settings, cancellationToken).ConfigureAwait(false);
        PrintPlan(output, plan, traits);
        var estimate = options.Provider switch
        {
            "deepl" => CorpusCostEstimate.ForDeepL(plan, options.PricePerMillionCharacters),
            "llm" => CorpusCostEstimate.ForLlm(plan, settings, glossary, created.Endpoint, created.IsLocal,
                options.PriceInputPerMillion, options.PriceOutputPerMillion),
            _ => CorpusCostEstimate.ForMock(plan),
        };
        PrintEstimate(output, estimate);

        if (options.DryRun || plan.ToTranslate == 0)
        {
            output.WriteLine(options.DryRun ? "Przebieg próbny: nic nie wysłano i nic nie zapisano." : "Nie ma nic do tłumaczenia.");
            WriteStats(options.StatsPath, profile.Id, traits, created, plan, estimate, report: null);
            return ExitOk;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var translator = new CorpusTranslator(created.Provider!, glossary, store, settings);
        var progress = new InlineProgress<CorpusBatchProgress>(p => output.WriteLine(string.Create(c,
            $"[{p.Done}/{p.Total}] {Kind(p.Kind)}: {p.Texts} tekstów, zapisano {p.Stored}, {p.ElapsedMs} ms{(p.Error is null ? string.Empty : " — BŁĄD " + p.Error)}")));
        var report = await translator.RunAsync(plan, progress, cancellationToken).ConfigureAwait(false);
        PrintReport(output, report, created.Provider as OpenAiCompatibleTranslationProvider);
        WriteStats(options.StatsPath, profile.Id, traits, created, plan, estimate, report);

        if (report.FatalError is not null)
        {
            error.WriteLine($"Przerwano: {report.FatalError}. Zapisane wpisy zostają — ponowne uruchomienie pominie je.");
            return ExitError;
        }
        if (report.Cancelled) return ExitCancelled;
        return report.BatchesFailed > 0 ? ExitPartial : ExitOk;
    }

    private static GlossaryService LoadGlossary(GameProfile profile, string dataDirectory, LocalAppSettings settings, TextWriter output)
    {
        var glossary = new GlossaryService();
        var catalog = new GlossaryCatalog([
            Path.Combine(AppContext.BaseDirectory, "glossaries"),
            Path.Combine(dataDirectory, "glossaries"),
        ]);
        var (global, _) = catalog.TryLoad("global", settings.SourceLanguage, settings.TargetLanguage);
        if (global is not null) glossary.LoadDocument(global);
        if (profile.Glossary is { Length: > 0 } profileGlossary)
        {
            var (document, issue) = catalog.TryLoad(profileGlossary, settings.SourceLanguage, settings.TargetLanguage);
            if (document is not null) glossary.LoadDocument(document);
            else if (issue is not null) output.WriteLine($"Uwaga: słownik „{profileGlossary}”: {issue.Message}");
        }
        glossary.LoadDocument(new UserGlossaryStore(new AppPaths(dataDirectory)).Load(settings.SourceLanguage, settings.TargetLanguage));
        return glossary;
    }

    private static string Describe(EnvironmentProvider created)
    {
        var parts = new List<string>();
        if (created.Endpoint is { } endpoint) parts.Add(created.IsLocal ? $"{endpoint}, lokalnie" : endpoint);
        if (created.Model is { } model) parts.Add($"model {model}");
        if (created.ServerOptions is { } options)
        {
            parts.Add($"opcje serwera: {options.Describe()}{(created.PresetName is { } preset ? $" (preset {preset})" : string.Empty)}");
        }
        else if (created.Endpoint is not null)
        {
            parts.Add("opcje serwera: brak");
        }
        return parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";
    }

    private static void PrintPlan(TextWriter output, CorpusPlan plan, CorpusProviderTraits traits)
    {
        var c = CultureInfo.InvariantCulture;
        var unique = plan.Unique;
        output.WriteLine(string.Create(c, $"Korpus: {unique.Entries} wpisów, {unique.Texts.Count} unikalnych tekstów (powtórzenia {unique.Duplicates}, puste {unique.Empty})."));
        output.WriteLine(string.Create(c,
            $"Pominięte: słownik {plan.Skipped[CorpusSkip.Glossary]}, ręczne korekty {plan.Skipped[CorpusSkip.Manual]}, zatwierdzone {plan.Skipped[CorpusSkip.Approved]}, " +
            $"już w profilu {plan.Skipped[CorpusSkip.Translated]}, w cache (--skip-cached) {plan.Skipped[CorpusSkip.Cached]}."));
        var limited = plan.BeyondLimit > 0 ? string.Create(c, $"; poza --limit: {plan.BeyondLimit}") : string.Empty;
        output.WriteLine(string.Create(c,
            $"Do tłumaczenia: {plan.ToTranslate} tekstów, {plan.Characters} znaków, {plan.Batches.Count} partii " +
            $"(dialogi {Count(plan, CorpusEntryKind.Dialog)}, napisy {Count(plan, CorpusEntryKind.Subtitle)}, UI {Count(plan, CorpusEntryKind.Ui)}){limited}."));
        output.WriteLine(string.Create(c,
            $"Z tego: przesłoni wpis globalny {plan.ShadowingGlobal}, zastąpi wpis profilu {plan.ReplacingProfile}, zastąpi atrapę Mock {plan.ReplacingMock}, " +
            $"ze znacznikami ({{0}}, [X], %s) {plan.WithMarkers}, zwraca się do gracza {plan.AddressingPlayer}."));
        if (traits.GenderAware && plan.EffectiveGender == PlayerGender.Unknown && plan.AddressingPlayer > 0)
        {
            output.WriteLine("Płeć gracza nieznana: teksty z „you” dostaną formę bez wskazówki; po ustawieniu płci aplikacja przetłumaczy je ponownie raz.");
        }
    }

    private static int Count(CorpusPlan plan, CorpusEntryKind kind) =>
        plan.Batches.Where(b => b.Kind == kind).Sum(static b => b.Texts.Count);

    private static void PrintEstimate(TextWriter output, CorpusCostEstimate estimate)
    {
        var c = CultureInfo.InvariantCulture;
        var tokens = estimate.InputTokens is { } input
            ? string.Create(c, $", tokeny ok. {input} wejścia + {estimate.OutputTokens} wyjścia")
            : string.Empty;
        var cost = estimate.LowUsd is { } low
            ? estimate.HighUsd is { } high && high != low
                ? string.Create(c, $"{low:0.####}–{high:0.####} USD")
                : string.Create(c, $"{low:0.####} USD")
            : "nieznany";
        output.WriteLine(string.Create(c, $"Szacunek (±30%): {estimate.Requests} zapytań, {estimate.Characters} znaków{tokens}; koszt {cost} — {estimate.Basis}."));
    }

    private static void PrintReport(TextWriter output, CorpusRunReport report, OpenAiCompatibleTranslationProvider? llm)
    {
        var c = CultureInfo.InvariantCulture;
        var flags = report.QualityFlags.Count > 0
            ? string.Create(c, $" ({string.Join(", ", report.QualityFlags.Select(static p => string.Create(CultureInfo.InvariantCulture, $"{p.Key} {p.Value}")))})")
            : string.Empty;
        output.WriteLine(string.Create(c,
            $"Wynik: partie {report.Batches} (nieudane {report.BatchesFailed}), wysłano {report.TextsSent} tekstów / {report.CharactersSent} znaków, " +
            $"zapisano {report.Stored}, chronione wpisy {report.Protected}, puste wyniki {report.EmptyResults}, zgubione znaczniki {report.MarkerFailures}, " +
            $"teksty z nieudanych partii {report.FailedTexts}; kontrola jakości: {report.QualityFlagged} z uwagą{flags}" +
            $", ponowienia {report.Retried} (lepsze {report.RetryImproved}), podziały partii {report.Splits}; czas {report.ElapsedMs} ms."));
        if (llm is not null)
        {
            var usage = llm.Usage;
            output.WriteLine(string.Create(c,
                $"Tokeny (z odpowiedzi serwera): zapytania {usage.Requests}, wejście {usage.PromptTokens} (z cache {usage.CachedPromptTokens}), " +
                $"wyjście {usage.CompletionTokens} (rozumowanie {usage.ReasoningTokens})."));
        }
    }

    private static void WriteStats(
        string? path, string profileId, CorpusProviderTraits traits, EnvironmentProvider created, CorpusPlan plan,
        CorpusCostEstimate estimate, CorpusRunReport? report)
    {
        if (path is null) return;
        var usage = (created.Provider as OpenAiCompatibleTranslationProvider)?.Usage;
        var stats = new
        {
            profile = profileId,
            provider = traits.Name,
            model = created.Model,
            endpoint = created.Endpoint,
            serverOptions = created.ServerOptions?.Describe(),
            entries = plan.Unique.Entries,
            uniqueTexts = plan.Unique.Texts.Count,
            duplicates = plan.Unique.Duplicates,
            empty = plan.Unique.Empty,
            skipped = plan.Skipped.ToDictionary(static p => p.Key.ToString().ToLowerInvariant(), static p => p.Value),
            toTranslate = plan.ToTranslate,
            characters = plan.Characters,
            batches = plan.Batches.Count,
            shadowingGlobal = plan.ShadowingGlobal,
            replacingProfile = plan.ReplacingProfile,
            replacingMock = plan.ReplacingMock,
            beyondLimit = plan.BeyondLimit,
            withMarkers = plan.WithMarkers,
            addressingPlayer = plan.AddressingPlayer,
            estimate,
            report,
            tokens = usage is null ? null : new
            {
                requests = usage.Requests,
                prompt = usage.PromptTokens,
                cachedPrompt = usage.CachedPromptTokens,
                completion = usage.CompletionTokens,
                reasoning = usage.ReasoningTokens,
            },
        };
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(stats, StatsJson), new UTF8Encoding(false));
    }

    private static string Kind(CorpusEntryKind kind) => kind switch
    {
        CorpusEntryKind.Dialog => "dialog",
        CorpusEntryKind.Subtitle => "napisy",
        _ => "UI",
    };

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly Lock _gate = new();

        public void Report(T value)
        {
            lock (_gate) report(value);
        }
    }
}
