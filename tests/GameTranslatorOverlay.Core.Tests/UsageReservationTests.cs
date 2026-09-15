using System.Collections.Concurrent;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public sealed class UsageReservationTests
{
    private const string SessionLimitMessage =
        "Osiągnięto limit znaków dla tej sesji. Zwiększ limit w ustawieniach albo zrestartuj sesję.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Different_in_flight_requests_cannot_reserve_more_than_the_shared_limit(bool separatePipeline)
    {
        using var fixture = new Fixture(100);
        var firstText = new string('a', 80);
        var secondText = new string('b', 80);
        var first = fixture.Pipeline.TranslateAsync([firstText], "en", "pl");
        var firstCall = await ExpectProviderCallAsync(first, fixture.Provider, 1);
        var otherPipeline = separatePipeline ? fixture.CreatePipeline() : fixture.Pipeline;

        var second = otherPipeline.TranslateAsync([secondText], "en", "pl");
        var denied = await ExpectNoNewProviderCallAsync(second, fixture.Provider, 1);

        AssertLimitDenied(Assert.Single(denied));
        Assert.False(first.IsCompleted);
        Assert.Equal(0, fixture.Usage.ApiCharacters);
        Assert.Equal(0, fixture.Usage.ApiRequests);
        Assert.Equal(0, fixture.Usage.FailedRequests);

        firstCall.Complete();
        AssertProviderResult(Assert.Single(await first), firstText);
        Assert.Equal(80, fixture.Usage.ApiCharacters);
        Assert.Equal(1, fixture.Usage.ApiRequests);
        Assert.Equal(1, fixture.Provider.CallCount);
    }

    [Fact]
    public async Task Identical_in_flight_text_and_batch_duplicates_share_one_reservation()
    {
        using var fixture = new Fixture(80);
        var source = new string('a', 80);
        var first = fixture.Pipeline.TranslateAsync([source], "en", "pl");
        var call = await ExpectProviderCallAsync(first, fixture.Provider, 1);

        var second = fixture.Pipeline.TranslateAsync([source, source], "en", "pl");

        Assert.False(second.IsCompleted);
        Assert.Equal(1, fixture.Provider.CallCount);
        call.Complete();
        AssertProviderResult(Assert.Single(await first), source);
        var sharedResults = await second;
        Assert.Equal(2, sharedResults.Count);
        Assert.All(sharedResults, outcome => AssertProviderResult(outcome, source));
        Assert.Equal(1, fixture.Provider.CallCount);
        Assert.Equal(80, fixture.Usage.ApiCharacters);
        Assert.Equal(1, fixture.Usage.ApiRequests);
        Assert.Equal(0, fixture.Usage.FailedRequests);
    }

    [Fact]
    public async Task A_waiter_receives_the_same_limit_denial_as_the_owner_of_a_later_chunk()
    {
        using var fixture = new Fixture(100, new TranslationPipelineOptions { MaxBatchSize = 1 });
        var firstText = new string('a', 80);
        var laterText = new string('b', 80);
        var owner = fixture.Pipeline.TranslateAsync([firstText, laterText], "en", "pl");
        var call = await ExpectProviderCallAsync(owner, fixture.Provider, 1);

        // The owner registered both texts, but its second chunk has not reached the provider.
        var waiter = fixture.Pipeline.TranslateAsync([laterText], "en", "pl");
        Assert.False(waiter.IsCompleted);
        Assert.Equal(1, fixture.Provider.CallCount);

        call.Complete();
        var ownerResults = await ExpectNoNewProviderCallAsync(owner, fixture.Provider, 1);
        var waiterResults = await ExpectNoNewProviderCallAsync(waiter, fixture.Provider, 1);

        Assert.Equal(2, ownerResults.Count);
        AssertProviderResult(ownerResults[0], firstText);
        AssertLimitDenied(ownerResults[1]);
        var sharedDenial = Assert.Single(waiterResults);
        AssertLimitDenied(sharedDenial);
        Assert.Equal(ownerResults[1].ErrorMessage, sharedDenial.ErrorMessage);
        Assert.Equal(80, fixture.Usage.ApiCharacters);
        Assert.Equal(1, fixture.Usage.ApiRequests);
        Assert.Equal(0, fixture.Usage.FailedRequests);
    }

    [Theory]
    [InlineData("provider-error")]
    [InlineData("cancel")]
    [InlineData("count-mismatch")]
    public async Task An_unsuccessful_request_releases_its_entire_reservation(string failureMode)
    {
        using var fixture = new Fixture(100);
        using var cancellation = new CancellationTokenSource();
        var first = fixture.Pipeline.TranslateAsync([new string('a', 100)], "en", "pl", cancellation.Token);
        var firstCall = await ExpectProviderCallAsync(first, fixture.Provider, 1);

        if (failureMode == "cancel")
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
        else
        {
            if (failureMode == "provider-error")
                firstCall.Fail(new TranslationException(TranslationFailureKind.Unknown, "Controlled provider failure"));
            else
                firstCall.CompleteWith([]);

            var failed = Assert.Single(await first);
            Assert.Equal(TranslationOrigin.Unavailable, failed.Origin);
            Assert.False(failed.IsTranslated);
            Assert.NotEqual(SessionLimitMessage, failed.ErrorMessage);
        }

        Assert.Equal(0, fixture.Usage.ApiCharacters);
        Assert.Equal(0, fixture.Usage.ApiRequests);
        Assert.Equal(failureMode == "cancel" ? 0 : 1, fixture.Usage.FailedRequests);

        var nextText = new string('b', 100);
        var next = fixture.Pipeline.TranslateAsync([nextText], "en", "pl");
        var nextCall = await ExpectProviderCallAsync(next, fixture.Provider, 2);
        nextCall.Complete();

        AssertProviderResult(Assert.Single(await next), nextText);
        Assert.Equal(2, fixture.Provider.CallCount);
        Assert.Equal(100, fixture.Usage.ApiCharacters);
        Assert.Equal(1, fixture.Usage.ApiRequests);
    }

    [Fact]
    public async Task Reset_clears_completed_usage_but_keeps_in_flight_reservations()
    {
        using var fixture = new Fixture(100);
        var warmup = fixture.Pipeline.TranslateAsync([new string('w', 10)], "en", "pl");
        (await ExpectProviderCallAsync(warmup, fixture.Provider, 1)).Complete();
        await warmup;
        Assert.Equal(10, fixture.Usage.ApiCharacters);

        var firstText = new string('a', 80);
        var first = fixture.Pipeline.TranslateAsync([firstText], "en", "pl");
        var firstCall = await ExpectProviderCallAsync(first, fixture.Provider, 2);

        fixture.Usage.Reset();
        Assert.Equal(0, fixture.Usage.ApiCharacters);
        Assert.Equal(0, fixture.Usage.ApiRequests);

        var second = fixture.Pipeline.TranslateAsync([new string('b', 30)], "en", "pl");
        var denied = await ExpectNoNewProviderCallAsync(second, fixture.Provider, 2);
        AssertLimitDenied(Assert.Single(denied));
        Assert.False(first.IsCompleted);
        Assert.Equal(0, fixture.Usage.FailedRequests);

        firstCall.Complete();
        AssertProviderResult(Assert.Single(await first), firstText);
        Assert.Equal(80, fixture.Usage.ApiCharacters);
        Assert.Equal(1, fixture.Usage.ApiRequests);
        Assert.Equal(2, fixture.Provider.CallCount);
    }

    [Fact]
    public async Task Lowering_the_limit_blocks_new_requests_but_preserves_the_admitted_request()
    {
        using var fixture = new Fixture(100);
        var source = new string('a', 80);
        var first = fixture.Pipeline.TranslateAsync([source], "en", "pl");
        var call = await ExpectProviderCallAsync(first, fixture.Provider, 1);

        fixture.Usage.SessionCharacterLimit = 50;
        var second = fixture.Pipeline.TranslateAsync(["b"], "en", "pl");
        var denied = await ExpectNoNewProviderCallAsync(second, fixture.Provider, 1);

        AssertLimitDenied(Assert.Single(denied));
        Assert.False(first.IsCompleted);
        call.Complete();
        AssertProviderResult(Assert.Single(await first), source);
        Assert.Equal(80, fixture.Usage.ApiCharacters);
        Assert.Equal(1, fixture.Usage.ApiRequests);
        Assert.Equal(0, fixture.Usage.FailedRequests);
        Assert.Equal(1, fixture.Provider.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cache_manual_corrections_and_glossary_work_when_the_entire_budget_is_used(bool completeReservation)
    {
        using var fixture = new Fixture(100);
        await fixture.Cache.StoreAsync(new NewCacheEntry(
            "Cached text", "Cached text", "en", "pl", "Z pamięci", fixture.Provider.Name));
        await fixture.Cache.SaveManualCorrectionAsync(new NewCacheEntry(
            "Manual text", "Manual text", "en", "pl", "Korekta", "manual"));
        fixture.Glossary.AddTerm(new GlossaryTerm("Armour", "Pancerz"));

        var reserved = fixture.Pipeline.TranslateAsync([new string('a', 100)], "en", "pl");
        var call = await ExpectProviderCallAsync(reserved, fixture.Provider, 1);
        if (completeReservation)
        {
            call.Complete();
            await reserved;
        }

        var local = fixture.Pipeline.TranslateAsync(["Cached text", "Manual text", "Armour"], "en", "pl");
        var outcomes = await ExpectNoNewProviderCallAsync(local, fixture.Provider, 1);

        Assert.Collection(outcomes,
            outcome =>
            {
                Assert.Equal(TranslationOrigin.Cache, outcome.Origin);
                Assert.Equal("Z pamięci", outcome.TranslatedText);
            },
            outcome =>
            {
                Assert.Equal(TranslationOrigin.Cache, outcome.Origin);
                Assert.Equal("Korekta", outcome.TranslatedText);
            },
            outcome =>
            {
                Assert.Equal(TranslationOrigin.Glossary, outcome.Origin);
                Assert.Equal("Pancerz", outcome.TranslatedText);
            });
        Assert.Equal(2, fixture.Usage.CacheHits);
        Assert.Equal(1, fixture.Usage.GlossaryHits);
        Assert.Equal(completeReservation ? 100 : 0, fixture.Usage.ApiCharacters);
        Assert.Equal(completeReservation ? 1 : 0, fixture.Usage.ApiRequests);

        call.Complete();
        await reserved;
        Assert.Equal(100, fixture.Usage.ApiCharacters);
        Assert.Equal(1, fixture.Usage.ApiRequests);
        Assert.Equal(0, fixture.Usage.FailedRequests);
        Assert.Equal(1, fixture.Provider.CallCount);
    }

    [Fact]
    public async Task A_stale_cache_miss_is_rechecked_after_the_previous_owner_finishes()
    {
        using var provider = new ControlledProvider();
        using var cache = new InstrumentedCache();
        var usage = new UsageTracker { SessionCharacterLimit = 1000 };
        var pipeline = new TranslationPipeline(
            new GlossaryService(), cache, provider, usage, new TranslationPipelineOptions());
        var source = new string('a', 80);
        var owner = pipeline.TranslateAsync([source], "en", "pl");
        var call = await ExpectProviderCallAsync(owner, provider, 1);

        cache.HoldNextLookup();
        var lateCaller = pipeline.TranslateAsync([source], "en", "pl");
        Assert.Same(cache.LookupCaptured, await Task.WhenAny(cache.LookupCaptured, lateCaller));
        Assert.Null(await cache.LookupCaptured);
        Assert.False(lateCaller.IsCompleted);

        // Caller B already read null, but cannot register ownership until A has both
        // stored its paid translation and removed its in-flight registration.
        call.Complete();
        AssertProviderResult(Assert.Single(await owner), source);
        Assert.Equal(1, cache.StoreCallCount);
        Assert.NotNull(await cache.Inner.LookupAsync(source, "en", "pl", string.Empty));

        cache.ReleaseLookup();
        var results = await ExpectNoNewProviderCallAsync(lateCaller, provider, 1);
        var reused = Assert.Single(results);

        Assert.Equal(TranslationOrigin.Cache, reused.Origin);
        Assert.Equal("PL:" + source, reused.TranslatedText);
        Assert.Null(reused.ErrorMessage);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, cache.StoreCallCount);
        Assert.Equal(80, usage.ApiCharacters);
        Assert.Equal(1, usage.ApiRequests);
        Assert.Equal(1, usage.CacheHits);
        Assert.Equal(0, usage.ReservedApiCharacters);
    }

    [Fact]
    public async Task Revoking_the_cache_write_epoch_blocks_persistence_but_counts_the_paid_response()
    {
        using var provider = new ControlledProvider { IgnoreCancellation = true };
        using var cache = new InstrumentedCache();
        using var epoch = new CancellationTokenSource();
        var usage = new UsageTracker { SessionCharacterLimit = 100 };
        var pipeline = new TranslationPipeline(
            new GlossaryService(), cache, provider, usage, new TranslationPipelineOptions(),
            cacheWriteCancellationToken: epoch.Token);
        var source = new string('a', 80);
        var request = pipeline.TranslateAsync([source], "en", "pl");
        var call = await ExpectProviderCallAsync(request, provider, 1);

        epoch.Cancel();
        call.Complete();
        try
        {
            AssertProviderResult(Assert.Single(await request), source);
        }
        catch (OperationCanceledException)
        {
            // Presentation may be canceled; the provider already returned a valid response.
        }

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(80, usage.ApiCharacters);
        Assert.Equal(1, usage.ApiRequests);
        Assert.Equal(0, usage.ReservedApiCharacters);
        Assert.Equal(0, usage.FailedRequests);
        Assert.Equal(0, cache.StoreCallCount);
        Assert.Empty(cache.StoreTokens);
        Assert.Equal(0, (await cache.Inner.GetStatsAsync()).TotalEntries);
    }

    [Fact]
    public async Task Ordinary_operation_cancellation_keeps_the_paid_cache_write_when_its_epoch_is_valid()
    {
        using var provider = new ControlledProvider { IgnoreCancellation = true };
        using var cache = new InstrumentedCache();
        using var operation = new CancellationTokenSource();
        var usage = new UsageTracker { SessionCharacterLimit = 100 };
        var pipeline = new TranslationPipeline(
            new GlossaryService(), cache, provider, usage, new TranslationPipelineOptions());
        var source = new string('a', 80);
        var request = pipeline.TranslateAsync([source], "en", "pl", operation.Token);
        var call = await ExpectProviderCallAsync(request, provider, 1);

        operation.Cancel();
        call.Complete();
        try
        {
            AssertProviderResult(Assert.Single(await request), source);
        }
        catch (OperationCanceledException)
        {
            // Canceling the caller must not discard a completed, paid provider response.
        }

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(80, usage.ApiCharacters);
        Assert.Equal(1, usage.ApiRequests);
        Assert.Equal(0, usage.ReservedApiCharacters);
        Assert.Equal(0, usage.FailedRequests);
        Assert.Equal(1, cache.StoreCallCount);
        Assert.False(Assert.Single(cache.StoreTokens).CanBeCanceled);
        var cached = await cache.Inner.LookupAsync(source, "en", "pl", string.Empty);
        Assert.NotNull(cached);
        Assert.Equal("PL:" + source, cached.TranslatedText);
    }

    [Fact]
    public void Completing_a_reservation_moves_it_to_usage_exactly_once()
    {
        var usage = new UsageTracker { SessionCharacterLimit = 100 };
        using var reservation = usage.TryReserveApiCharacters(80);
        Assert.NotNull(reservation);
        Assert.Equal(80, usage.ReservedApiCharacters);
        Assert.Equal(0, usage.ApiCharacters);
        Assert.Equal(0, usage.ApiRequests);

        reservation.Complete();
        reservation.Complete();
        reservation.Dispose();

        Assert.Equal(0, usage.ReservedApiCharacters);
        Assert.Equal(80, usage.ApiCharacters);
        Assert.Equal(1, usage.ApiRequests);
        Assert.Null(usage.TryReserveApiCharacters(21));
        using var remaining = usage.TryReserveApiCharacters(20);
        Assert.NotNull(remaining);
    }

    [Fact]
    public void Disposing_a_reservation_releases_it_and_late_completion_does_not_count_it()
    {
        var usage = new UsageTracker { SessionCharacterLimit = 100 };
        using var reservation = usage.TryReserveApiCharacters(80);
        Assert.NotNull(reservation);
        Assert.Equal(80, usage.ReservedApiCharacters);

        reservation.Dispose();
        reservation.Dispose();
        reservation.Complete();

        Assert.Equal(0, usage.ReservedApiCharacters);
        Assert.Equal(0, usage.ApiCharacters);
        Assert.Equal(0, usage.ApiRequests);
        using var replacement = usage.TryReserveApiCharacters(100);
        Assert.NotNull(replacement);
        Assert.Equal(100, usage.ReservedApiCharacters);
    }

    private static void AssertLimitDenied(TranslationOutcome outcome)
    {
        Assert.Equal(TranslationOrigin.Unavailable, outcome.Origin);
        Assert.False(outcome.IsTranslated);
        Assert.Null(outcome.TranslatedText);
        Assert.Equal(SessionLimitMessage, outcome.ErrorMessage);
    }

    private static void AssertProviderResult(TranslationOutcome outcome, string source)
    {
        Assert.Equal(TranslationOrigin.Provider, outcome.Origin);
        Assert.Equal(source, outcome.SourceText);
        Assert.Equal("PL:" + source, outcome.TranslatedText);
        Assert.Null(outcome.ErrorMessage);
    }

    private static async Task<ProviderCall> ExpectProviderCallAsync(
        Task<IReadOnlyList<TranslationOutcome>> result, ControlledProvider provider, int callNumber)
    {
        var entered = provider.CallEnteredAsync(callNumber);
        Assert.Same(entered, await Task.WhenAny(entered, result));
        return await entered;
    }

    private static async Task<IReadOnlyList<TranslationOutcome>> ExpectNoNewProviderCallAsync(
        Task<IReadOnlyList<TranslationOutcome>> result, ControlledProvider provider, int expectedCalls)
    {
        // Either the pipeline returns locally, or the forbidden provider invocation is observed.
        // No timeout or scheduler-dependent delay is needed to detect the original overspend.
        Assert.Same(result, await Task.WhenAny(result, provider.CallEnteredAsync(expectedCalls + 1)));
        Assert.Equal(expectedCalls, provider.CallCount);
        return await result;
    }

    private sealed class InstrumentedCache : ITranslationCache, IDisposable
    {
        private readonly TaskCompletionSource<CachedTranslation?> _lookupCaptured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _lookupReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _holdNextLookup;
        private int _storeCallCount;

        public InMemoryTranslationCache Inner { get; } = new();
        public ConcurrentQueue<CancellationToken> StoreTokens { get; } = new();
        public int StoreCallCount => Volatile.Read(ref _storeCallCount);
        public Task<CachedTranslation?> LookupCaptured => _lookupCaptured.Task;

        public void HoldNextLookup() => Interlocked.Exchange(ref _holdNextLookup, 1);
        public void ReleaseLookup() => _lookupReleased.TrySetResult();

        public async Task<CachedTranslation?> LookupAsync(
            string normalizedText, string sourceLanguage, string targetLanguage,
            string gameProfile, CancellationToken cancellationToken = default)
        {
            var snapshot = await Inner.LookupAsync(
                normalizedText, sourceLanguage, targetLanguage, gameProfile, cancellationToken);
            if (Interlocked.Exchange(ref _holdNextLookup, 0) != 0)
            {
                _lookupCaptured.TrySetResult(snapshot);
                await _lookupReleased.Task.WaitAsync(cancellationToken);
            }
            return snapshot;
        }

        public Task StoreAsync(NewCacheEntry entry, CancellationToken cancellationToken = default)
        {
            // Count entry before inspecting the token: a revoked epoch must stop in
            // the pipeline before any persistent cache implementation is entered.
            Interlocked.Increment(ref _storeCallCount);
            StoreTokens.Enqueue(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Inner.StoreAsync(entry, cancellationToken);
        }

        public Task SaveManualCorrectionAsync(NewCacheEntry entry, CancellationToken cancellationToken = default) =>
            Inner.SaveManualCorrectionAsync(entry, cancellationToken);

        public Task<CacheStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
            Inner.GetStatsAsync(cancellationToken);

        public Task<int> ClearAsync(bool keepManualCorrections, CancellationToken cancellationToken = default) =>
            Inner.ClearAsync(keepManualCorrections, cancellationToken);

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, bool keepManualCorrections,
            CancellationToken cancellationToken = default) =>
            Inner.DeleteOlderThanAsync(cutoff, keepManualCorrections, cancellationToken);

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken = default) =>
            Inner.ExportJsonAsync(cancellationToken);

        public Task<int> ImportJsonAsync(string json, CancellationToken cancellationToken = default) =>
            Inner.ImportJsonAsync(json, cancellationToken);

        public void Dispose() => ReleaseLookup();
    }

    private sealed class Fixture : IDisposable
    {
        public ControlledProvider Provider { get; } = new();
        public InMemoryTranslationCache Cache { get; } = new();
        public GlossaryService Glossary { get; } = new();
        public UsageTracker Usage { get; } = new();
        public TranslationPipeline Pipeline { get; }

        public Fixture(long limit, TranslationPipelineOptions? options = null)
        {
            Usage.SessionCharacterLimit = limit;
            Pipeline = CreatePipeline(options);
        }

        public TranslationPipeline CreatePipeline(TranslationPipelineOptions? options = null) =>
            new(Glossary, Cache, Provider, Usage, options ?? new TranslationPipelineOptions());

        public void Dispose() => Provider.Dispose();
    }

    private sealed class ProviderCall(IReadOnlyList<string> texts)
    {
        private readonly TaskCompletionSource<IReadOnlyList<string>> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<string>> Response => _response.Task;
        public void Complete() => CompleteWith(texts.Select(static text => "PL:" + text).ToArray());
        public void CompleteWith(IReadOnlyList<string> translations) => _response.TrySetResult(translations);
        public void Fail(TranslationException exception) => _response.TrySetException(exception);
    }

    private sealed class ControlledProvider : ITranslationProvider, IDisposable
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<ProviderCall>> _entries = new();
        private readonly ConcurrentBag<ProviderCall> _calls = [];
        private int _callCount;
        private int _disposed;

        public string Name => "ReservationFake";
        public bool RequiresApiKey => false;
        public int CallCount => Volatile.Read(ref _callCount);
        public bool IgnoreCancellation { get; init; }

        public Task<ProviderCall> CallEnteredAsync(int callNumber) => Entry(callNumber).Task;

        public async Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            var call = new ProviderCall(texts.ToArray());
            _calls.Add(call);
            var callNumber = Interlocked.Increment(ref _callCount);
            Entry(callNumber).TrySetResult(call);
            if (Volatile.Read(ref _disposed) != 0)
                call.Complete();
            return await call.Response.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "Controlled fake"));

        private TaskCompletionSource<ProviderCall> Entry(int callNumber) =>
            _entries.GetOrAdd(callNumber, static _ =>
                new TaskCompletionSource<ProviderCall>(TaskCreationOptions.RunContinuationsAsynchronously));

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            foreach (var call in _calls)
                call.Complete();
        }
    }
}