using System.Collections.Concurrent;
using System.Diagnostics;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Text;
using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.CorpusTool.Translation;

public enum CorpusSkip
{
    Glossary,
    Manual,
    Approved,
    Translated,
    Cached,
}

public sealed record CorpusProviderTraits(string Name, int MaxBatchSize, bool Contextual, bool GenderAware, bool Retryable)
{
    public static CorpusProviderTraits For(string id) => id switch
    {
        "deepl" => new("DeepL", 50, Contextual: true, GenderAware: false, Retryable: false),
        "llm" => new("LLM", 25, Contextual: true, GenderAware: true, Retryable: true),
        _ => new(MockTranslationProvider.ProviderName, 50, Contextual: false, GenderAware: false, Retryable: false),
    };

    public static CorpusProviderTraits Of(ITranslationProvider provider, int maxBatchSize) => new(
        provider.Name,
        maxBatchSize,
        provider is IContextualTranslationProvider,
        provider is IGenderAwareTranslationProvider,
        provider is IRetryableTranslationProvider);
}

public sealed record CorpusTranslatorSettings
{
    public required string ProfileId { get; init; }
    public string? GameName { get; init; }
    public string SourceLanguage { get; init; } = "en";
    public string TargetLanguage { get; init; } = "pl";
    public PlayerGender PlayerGender { get; init; }
    public int BatchSize { get; init; } = 25;
    public int Parallelism { get; init; } = 1;
    public int? Limit { get; init; }
    public bool Force { get; init; }
    public bool SkipCached { get; init; }
    public IReadOnlySet<CorpusEntryKind>? Kinds { get; init; }
    public int MaxConsecutiveFailures { get; init; } = 3;
    public int MaxSplitDepth { get; init; } = 3;
    public int MaxContextTerms { get; init; } = 40;
    public int MaxRecentTexts { get; init; } = 6;
    public int MaxRecentChars { get; init; } = 1500;
}

public interface ICorpusCacheStore
{
    Task<IReadOnlyList<CachedTranslation?>> PeekAsync(
        IReadOnlyList<string> keys, string sourceLanguage, string targetLanguage, string gameProfile, CancellationToken cancellationToken);

    Task<int> StoreAsync(IReadOnlyList<NewCacheEntry> entries, CancellationToken cancellationToken);
}

public sealed record CorpusPlan
{
    public required CorpusUniqueTexts Unique { get; init; }
    public required IReadOnlyDictionary<CorpusSkip, int> Skipped { get; init; }
    public required IReadOnlyList<CorpusTranslationBatch> Batches { get; init; }
    public required IReadOnlyDictionary<string, CachedTranslation> Replacing { get; init; }
    public required PlayerGender EffectiveGender { get; init; }
    public int ToTranslate { get; init; }
    public long Characters { get; init; }
    public int AddressingPlayer { get; init; }
    public int ShadowingGlobal { get; init; }
    public int ReplacingProfile { get; init; }
    public int ReplacingMock { get; init; }
    public int BeyondLimit { get; init; }
    public int WithMarkers { get; init; }
}

public sealed record CorpusBatchProgress(int Done, int Total, CorpusEntryKind Kind, int Texts, int Stored, string? Error, long ElapsedMs);

public sealed record CorpusRunReport
{
    public int Batches { get; init; }
    public int BatchesFailed { get; init; }
    public int TextsSent { get; init; }
    public long CharactersSent { get; init; }
    public int Stored { get; init; }
    public int Protected { get; init; }
    public int EmptyResults { get; init; }
    public int MarkerFailures { get; init; }
    public int FailedTexts { get; init; }
    public int QualityFlagged { get; init; }
    public int Retried { get; init; }
    public int RetryImproved { get; init; }
    public int Splits { get; init; }
    public required IReadOnlyDictionary<string, int> QualityFlags { get; init; }
    public long ElapsedMs { get; init; }
    public string? FatalError { get; init; }
    public bool Cancelled { get; init; }
    public bool Complete => FatalError is null && !Cancelled && BatchesFailed == 0;
}

public static class CorpusTranslationPlanning
{
    public static async Task<CorpusPlan> PlanAsync(
        IReadOnlyList<CorpusEntry> entries,
        CorpusProviderTraits traits,
        IGlossaryService glossary,
        ICorpusCacheStore? cache,
        CorpusTranslatorSettings settings,
        CancellationToken cancellationToken = default)
    {
        var unique = CorpusTranslationPlanner.Unique(entries, settings.Kinds);
        var gender = traits.GenderAware ? settings.PlayerGender : PlayerGender.Unknown;
        var keys = unique.Texts.Select(static t => t.Key).ToList();
        var existing = cache is null || keys.Count == 0
            ? new CachedTranslation?[keys.Count]
            : await cache.PeekAsync(keys, settings.SourceLanguage, settings.TargetLanguage, settings.ProfileId, cancellationToken)
                .ConfigureAwait(false);
        if (existing.Count != keys.Count) throw new InvalidOperationException("Cache zwrócił inną liczbę wyników niż liczba tekstów.");

        var skipped = Enum.GetValues<CorpusSkip>().ToDictionary(static s => s, static _ => 0);
        var replacing = new Dictionary<string, CachedTranslation>(StringComparer.Ordinal);
        var selected = new List<CorpusUniqueText>();
        int shadowing = 0, replacingProfile = 0, replacingMock = 0;
        for (var i = 0; i < unique.Texts.Count; i++)
        {
            var text = unique.Texts[i];
            var entry = existing[i];
            if (glossary.TryTranslateExact(text.Key, out _))
            {
                skipped[CorpusSkip.Glossary]++;
                continue;
            }
            if (entry is { IsManual: true })
            {
                skipped[CorpusSkip.Manual]++;
                continue;
            }
            if (entry is { IsApproved: true })
            {
                skipped[CorpusSkip.Approved]++;
                continue;
            }
            if (entry is not null)
            {
                var placeholder = entry.Provider.Equals(MockTranslationProvider.ProviderName, StringComparison.OrdinalIgnoreCase)
                    && !traits.Name.Equals(MockTranslationProvider.ProviderName, StringComparison.OrdinalIgnoreCase);
                var stale = placeholder || TranslationCacheContext.IsStale(entry.Context, text.Key, gender);
                var inProfile = entry.GameProfile.Equals(settings.ProfileId, StringComparison.Ordinal);
                if (inProfile)
                {
                    if (!stale && !settings.Force)
                    {
                        skipped[CorpusSkip.Translated]++;
                        continue;
                    }
                    replacing[text.Key] = entry;
                    if (placeholder) replacingMock++;
                    else replacingProfile++;
                }
                else if (!stale && settings.SkipCached)
                {
                    skipped[CorpusSkip.Cached]++;
                    continue;
                }
                else if (placeholder)
                {
                    replacingMock++;
                }
                else
                {
                    shadowing++;
                }
            }
            selected.Add(text);
        }

        var batches = CorpusTranslationPlanner.Batches(selected, Math.Min(settings.BatchSize, traits.MaxBatchSize));
        var beyondLimit = 0;
        if (settings.Limit is { } limit && selected.Count > limit)
        {
            var kept = new List<CorpusTranslationBatch>();
            var remaining = limit;
            foreach (var batch in batches)
            {
                if (remaining <= 0) break;
                if (batch.Texts.Count <= remaining)
                {
                    kept.Add(batch);
                    remaining -= batch.Texts.Count;
                    continue;
                }
                kept.Add(batch with { Texts = batch.Texts.Take(remaining).ToList(), Notes = batch.Notes.Take(remaining).ToList() });
                remaining = 0;
            }
            beyondLimit = selected.Count - limit;
            batches = kept;
        }

        var planned = batches.SelectMany(static b => b.Texts).ToList();
        var plannedKeys = planned.Select(static t => t.Key).ToHashSet(StringComparer.Ordinal);
        return new CorpusPlan
        {
            Unique = unique,
            Skipped = skipped,
            Batches = batches,
            Replacing = replacing.Where(pair => plannedKeys.Contains(pair.Key)).ToDictionary(static p => p.Key, static p => p.Value, StringComparer.Ordinal),
            EffectiveGender = gender,
            ToTranslate = planned.Count,
            Characters = planned.Sum(static t => (long)TextReflow.Unwrap(t.Key).Text.Length),
            AddressingPlayer = planned.Count(static t => PlayerGenders.AddressesPlayer(t.Key)),
            ShadowingGlobal = shadowing,
            ReplacingProfile = replacingProfile,
            ReplacingMock = replacingMock,
            BeyondLimit = beyondLimit,
            WithMarkers = planned.Count(static t => TextMarkers.HasMarkers(t.Key)),
        };
    }
}

public sealed class CorpusTranslator(
    ITranslationProvider provider,
    IGlossaryService glossary,
    ICorpusCacheStore cache,
    CorpusTranslatorSettings settings)
{
    private const TranslationQualityFlags NotExampleIssues = TranslationQualityFlags.Untranslated | TranslationQualityFlags.Runaway;

    private readonly SemaphoreSlim _storeGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, int> _qualityFlags = new(StringComparer.Ordinal);
    private int _done;
    private int _batchesFailed;
    private int _consecutiveFailures;
    private int _textsSent;
    private long _charactersSent;
    private int _stored;
    private int _protected;
    private int _empty;
    private int _markerFailures;
    private int _failedTexts;
    private int _qualityFlagged;
    private int _retried;
    private int _retryImproved;
    private int _splits;
    private string? _fatal;

    public async Task<CorpusRunReport> RunAsync(
        CorpusPlan plan, IProgress<CorpusBatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var chains = new ConcurrentQueue<List<CorpusTranslationBatch>>(plan.Batches
            .GroupBy(static b => b.Chain)
            .OrderBy(static g => g.Key)
            .Select(static g => g.OrderBy(static b => b.Sequence).ToList()));
        var total = plan.Batches.Count;
        var workers = Enumerable.Range(0, Math.Clamp(settings.Parallelism, 1, 16)).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested && chains.TryDequeue(out var chain))
                {
                    var memory = new DialogMemory(Math.Max(0, settings.MaxRecentTexts), Math.Max(0, settings.MaxRecentChars));
                    foreach (var batch in chain)
                    {
                        if (stop.IsCancellationRequested) return;
                        await RunBatchAsync(plan, batch, memory, total, progress, stop).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }, CancellationToken.None)).ToList();
        await Task.WhenAll(workers).ConfigureAwait(false);
        watch.Stop();

        return new CorpusRunReport
        {
            Batches = _done,
            BatchesFailed = _batchesFailed,
            TextsSent = _textsSent,
            CharactersSent = Interlocked.Read(ref _charactersSent),
            Stored = _stored,
            Protected = _protected,
            EmptyResults = _empty,
            MarkerFailures = _markerFailures,
            FailedTexts = _failedTexts,
            QualityFlagged = _qualityFlagged,
            Retried = _retried,
            RetryImproved = _retryImproved,
            Splits = _splits,
            QualityFlags = new SortedDictionary<string, int>(_qualityFlags, StringComparer.Ordinal),
            ElapsedMs = watch.ElapsedMilliseconds,
            FatalError = _fatal,
            Cancelled = _fatal is null && cancellationToken.IsCancellationRequested,
        };
    }

    private async Task RunBatchAsync(
        CorpusPlan plan, CorpusTranslationBatch batch, DialogMemory memory, int total,
        IProgress<CorpusBatchProgress>? progress, CancellationTokenSource stop)
    {
        var watch = Stopwatch.StartNew();
        var count = batch.Texts.Count;
        var reflowed = batch.Texts.Select(static t => TextReflow.Unwrap(t.Key)).ToList();
        var sent = reflowed.Select(static r => r.Text).ToList();

        string?[] translations;
        try
        {
            Interlocked.Add(ref _textsSent, count);
            Interlocked.Add(ref _charactersSent, sent.Sum(static t => (long)t.Length));
            var failures = new List<TranslationException>();
            translations = await SendResilientAsync(sent, batch.Notes, batch.Scene, memory, failures, 0, stop.Token).ConfigureAwait(false);
            if (translations.All(static t => t is null))
            {
                throw failures.LastOrDefault()
                    ?? new TranslationException(TranslationFailureKind.Unknown, "Dostawca nie przetłumaczył żadnego tekstu partii.");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }
        catch (TranslationException ex)
        {
            Interlocked.Increment(ref _batchesFailed);
            Interlocked.Add(ref _failedTexts, count);
            var consecutive = Interlocked.Increment(ref _consecutiveFailures);
            if (IsFatal(ex.Kind) || consecutive >= Math.Max(1, settings.MaxConsecutiveFailures))
            {
                lock (_gate)
                {
                    _fatal ??= IsFatal(ex.Kind)
                        ? $"{ex.Kind}: {ex.Message}"
                        : $"{consecutive} kolejne partie nieudane, ostatnio {ex.Kind}: {ex.Message}";
                }
                await stop.CancelAsync().ConfigureAwait(false);
            }
            progress?.Report(new CorpusBatchProgress(Interlocked.Increment(ref _done), total, batch.Kind, count, 0,
                $"{ex.Kind}: {ex.Message}", watch.ElapsedMilliseconds));
            return;
        }
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        var missing = translations.Select(static t => t is null).ToArray();
        var missingCount = missing.Count(static m => m);
        if (missingCount > 0) Interlocked.Add(ref _failedTexts, missingCount);

        var raw = new string[count];
        var results = new string[count];
        var issues = new TranslationQualityFlags[count];
        var markersOk = new bool[count];
        for (var i = 0; i < count; i++)
        {
            raw[i] = translations[i] ?? string.Empty;
            results[i] = TextReflow.Rewrap(raw[i], reflowed[i].Plan);
            issues[i] = TranslationQualityGate.Check(sent[i], results[i]);
            markersOk[i] = TextMarkers.Preserved(batch.Texts[i].Key, results[i]);
        }

        if (provider is IRetryableTranslationProvider)
        {
            await RetryFlaggedAsync(plan, batch, sent, reflowed, raw, results, issues, markersOk, missing, memory, stop.Token).ConfigureAwait(false);
        }

        memory.RememberResults(Enumerable.Range(0, count)
            .Where(i => !missing[i] && !issues[i].HasFlag(TranslationQualityFlags.Empty))
            .Select(i => (new RecentExchange(sent[i], raw[i].Trim()), (issues[i] & NotExampleIssues) == TranslationQualityFlags.None && markersOk[i])));

        var entries = new List<NewCacheEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var text = batch.Texts[i];
            if (missing[i]) continue;
            if (issues[i].HasFlag(TranslationQualityFlags.Empty))
            {
                Interlocked.Increment(ref _empty);
                continue;
            }
            if (!markersOk[i])
            {
                Interlocked.Increment(ref _markerFailures);
                continue;
            }
            if (issues[i] != TranslationQualityFlags.None)
            {
                Interlocked.Increment(ref _qualityFlagged);
                foreach (var flag in Enum.GetValues<TranslationQualityFlags>())
                {
                    if (flag != TranslationQualityFlags.None && issues[i].HasFlag(flag)) _qualityFlags.AddOrUpdate(flag.ToString(), 1, static (_, n) => n + 1);
                }
            }
            var final = false;
            if (plan.Replacing.TryGetValue(text.Key, out var replaced) && replaced.GameProfile.Equals(settings.ProfileId, StringComparison.Ordinal))
            {
                var previous = TranslationCacheContext.Parse(replaced.Context);
                final = previous.NeedsQualityRetry || (previous.QualityFinal && issues[i] != TranslationQualityFlags.None);
            }
            entries.Add(new NewCacheEntry(
                text.SourceText,
                text.Key,
                settings.SourceLanguage,
                settings.TargetLanguage,
                results[i],
                provider.Name,
                GameProfile: settings.ProfileId,
                Context: TranslationCacheContext.Build(issues[i], final, plan.EffectiveGender, TranslationCacheContext.CorpusSource)));
        }

        var written = 0;
        if (entries.Count > 0)
        {
            await _storeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                written = await cache.StoreAsync(entries, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _storeGate.Release();
            }
            Interlocked.Add(ref _stored, written);
            Interlocked.Add(ref _protected, entries.Count - written);
        }
        progress?.Report(new CorpusBatchProgress(Interlocked.Increment(ref _done), total, batch.Kind, count, written,
            missingCount > 0 ? $"{missingCount} tekstów bez tłumaczenia" : null, watch.ElapsedMilliseconds));
    }

    private async Task<string?[]> SendResilientAsync(
        IReadOnlyList<string> texts, IReadOnlyList<string?> notes, string scene, DialogMemory memory,
        List<TranslationException> failures, int depth, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendAsync(texts, notes, scene, memory, cancellationToken).ConfigureAwait(false);
            if (result.Count != texts.Count)
            {
                throw new TranslationException(TranslationFailureKind.Unknown,
                    $"Dostawca zwrócił {result.Count} tłumaczeń dla {texts.Count} tekstów.");
            }
            return [.. result];
        }
        catch (TranslationException ex) when (IsSplittable(ex.Kind))
        {
            failures.Add(ex);
            if (texts.Count == 1 || depth >= Math.Max(0, settings.MaxSplitDepth)) return new string?[texts.Count];
            Interlocked.Increment(ref _splits);
            var half = texts.Count / 2;
            var first = await SendResilientAsync(texts.Take(half).ToList(), notes.Take(half).ToList(), scene, memory, failures,
                depth + 1, cancellationToken).ConfigureAwait(false);
            var second = await SendResilientAsync(texts.Skip(half).ToList(), notes.Skip(half).ToList(), scene, memory, failures,
                depth + 1, cancellationToken).ConfigureAwait(false);
            return [.. first, .. second];
        }
    }

    private static bool IsSplittable(TranslationFailureKind kind) =>
        kind is TranslationFailureKind.Unknown or TranslationFailureKind.ContentRefused or TranslationFailureKind.TextTooLong;

    private async Task RetryFlaggedAsync(
        CorpusPlan plan, CorpusTranslationBatch batch, List<string> sent, List<(string Text, ReflowPlan Plan)> reflowed,
        string[] raw, string[] results, TranslationQualityFlags[] issues, bool[] markersOk, bool[] missing, DialogMemory memory,
        CancellationToken cancellationToken)
    {
        var flagged = Enumerable.Range(0, sent.Count)
            .Where(i => !missing[i] && !issues[i].HasFlag(TranslationQualityFlags.Empty)
                        && (issues[i] != TranslationQualityFlags.None || !markersOk[i])
                        && !AlreadyRetried(plan, batch.Texts[i].Key))
            .ToList();
        if (flagged.Count == 0) return;

        Interlocked.Add(ref _retried, flagged.Count);
        IReadOnlyList<string> retried;
        try
        {
            var texts = flagged.Select(i => sent[i]).ToList();
            Interlocked.Add(ref _charactersSent, texts.Sum(static t => (long)t.Length));
            retried = await SendAsync(texts, flagged.Select(i => batch.Notes[i]).ToList(), batch.Scene, memory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TranslationException)
        {
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        if (retried.Count != flagged.Count) return;

        for (var k = 0; k < flagged.Count; k++)
        {
            var i = flagged[k];
            var candidateRaw = retried[k] ?? string.Empty;
            var candidate = TextReflow.Rewrap(candidateRaw, reflowed[i].Plan);
            var candidateIssues = TranslationQualityGate.Check(sent[i], candidate);
            if (candidateIssues.HasFlag(TranslationQualityFlags.Empty)) continue;
            var candidateMarkers = TextMarkers.Preserved(batch.Texts[i].Key, candidate);
            var better = (candidateMarkers && !markersOk[i])
                || (candidateMarkers == markersOk[i]
                    && TranslationQualityGate.Severity(candidateIssues) < TranslationQualityGate.Severity(issues[i]));
            if (!better) continue;
            raw[i] = candidateRaw;
            results[i] = candidate;
            issues[i] = candidateIssues;
            markersOk[i] = candidateMarkers;
            Interlocked.Increment(ref _retryImproved);
        }
    }

    private bool AlreadyRetried(CorpusPlan plan, string key)
    {
        if (!plan.Replacing.TryGetValue(key, out var replaced)) return false;
        if (!replaced.GameProfile.Equals(settings.ProfileId, StringComparison.Ordinal)) return false;
        var context = TranslationCacheContext.Parse(replaced.Context);
        return context.NeedsQualityRetry || context.QualityFinal;
    }

    private Task<IReadOnlyList<string>> SendAsync(
        IReadOnlyList<string> texts, IReadOnlyList<string?> notes, string scene, DialogMemory memory, CancellationToken cancellationToken)
    {
        if (provider is not IContextualTranslationProvider contextual)
        {
            return provider.TranslateBatchAsync(texts, settings.SourceLanguage, settings.TargetLanguage, cancellationToken);
        }
        var terms = glossary.FindTermsIn(texts, settings.MaxContextTerms);
        var context = new TranslationContext(string.IsNullOrWhiteSpace(settings.GameName) ? null : settings.GameName.Trim(), terms)
        {
            RecentTexts = memory.SourcesExcluding(texts),
            RecentExchanges = memory.Excluding(texts),
            GlossaryTerms = PersistableTermsFor(terms),
            PlayerGender = settings.PlayerGender,
            Scene = scene,
            TextNotes = notes.Count == texts.Count ? notes : [],
        };
        return contextual.TranslateWithContextAsync(texts, settings.SourceLanguage, settings.TargetLanguage, context, cancellationToken);
    }

    private IReadOnlyList<GlossaryTerm> PersistableTermsFor(IReadOnlyList<GlossaryTerm> found)
    {
        if (found.Count == 0) return [];
        var persistable = glossary.PersistableTerms;
        if (persistable.Count == 0) return [];
        var set = persistable.ToHashSet();
        return found.Any(set.Contains) ? persistable : [];
    }

    private static bool IsFatal(TranslationFailureKind kind) => kind is TranslationFailureKind.MissingApiKey
        or TranslationFailureKind.InvalidApiKey
        or TranslationFailureKind.QuotaExceeded
        or TranslationFailureKind.ModelNotFound
        or TranslationFailureKind.InvalidConfiguration;
}
