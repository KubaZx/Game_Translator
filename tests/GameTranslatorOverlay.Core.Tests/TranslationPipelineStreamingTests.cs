using System.Collections.Concurrent;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class TranslationPipelineStreamingTests
{
    private sealed class StreamingProvider(Func<string, int, string> translate)
        : IStreamingTranslationProvider, IRetryableTranslationProvider
    {
        private int _calls;

        public List<(IReadOnlyList<string> Texts, bool Streamed)> Requests { get; } = [];
        public Task? Gate { get; set; }
        public int GateAfter { get; set; } = int.MaxValue;
        public TranslationException? FailAfterStream { get; set; }
        public Func<string, int, string>? Final { get; set; }
        public bool ResetAfterStream { get; set; }

        public string Name => "Strumien";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
            TranslateWithContextAsync(texts, sourceLanguage, targetLanguage, TranslationContext.Empty, cancellationToken);

        public Task<IReadOnlyList<string>> TranslateWithContextAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, TranslationContext context,
            CancellationToken cancellationToken = default)
        {
            var call = ++_calls;
            Requests.Add((texts.ToList(), false));
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(t => (Final ?? translate)(t, call)).ToList());
        }

        public async Task<IReadOnlyList<string>> TranslateStreamingAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, TranslationContext context,
            Action<int, string> onTranslation, CancellationToken cancellationToken = default)
        {
            var call = ++_calls;
            Requests.Add((texts.ToList(), true));
            for (var i = 0; i < texts.Count; i++)
            {
                if (i == GateAfter && Gate is not null) await Gate.WaitAsync(cancellationToken);
                onTranslation(i, translate(texts[i], call));
            }
            if (GateAfter >= texts.Count && Gate is not null) await Gate.WaitAsync(cancellationToken);
            if (ResetAfterStream) onTranslation(PartialResults.Reset, string.Empty);
            if (FailAfterStream is { } failure) throw failure;
            return texts.Select(t => (Final ?? translate)(t, call)).ToList();
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private sealed class PlainProvider : ITranslationProvider
    {
        public string Name => "Zwykly";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(texts.Select(static t => "PL:" + t).ToList());

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private sealed class Partials
    {
        private readonly ConcurrentQueue<(int Index, TranslationOutcome Outcome)> _items = new();
        public IReadOnlyList<(int Index, TranslationOutcome Outcome)> Items => _items.ToList();
        public void Add(int index, TranslationOutcome outcome) => _items.Enqueue((index, outcome));
    }

    private static (TranslationPipeline Pipeline, InMemoryTranslationCache Cache, UsageTracker Usage) Create(
        ITranslationProvider provider, TranslationPipelineOptions? options = null)
    {
        var cache = new InMemoryTranslationCache();
        var usage = new UsageTracker();
        return (new TranslationPipeline(new GlossaryService(), cache, provider, usage, options ?? new TranslationPipelineOptions()), cache, usage);
    }

    private static string Polish(string text, int call) => text switch
    {
        "Open the lantern door" => "Otwórz drzwi latarni",
        "You have 150 gold coins." => call == 1 ? "Masz 15 złotych monet." : "Masz 150 złotych monet.",
        _ => "PL:" + text,
    };

    [Fact]
    public async Task Wczesne_wyniki_przechodza_przez_bramke_jakosci_i_nie_trafiaja_do_cache_przed_koncem()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StreamingProvider(Polish) { Gate = gate.Task };
        var (pipeline, cache, usage) = Create(provider);
        var partials = new Partials();
        string[] texts = ["Open the lantern door", "You have 150 gold coins.", "Open the lantern door"];

        var task = pipeline.TranslateAsync(texts, "en", "pl", knownLocal: null, partials.Add);
        await WaitUntilAsync(() => partials.Items.Count == 2);

        Assert.False(task.IsCompleted);
        Assert.Equal([0, 2], partials.Items.Select(static p => p.Index).Order());
        Assert.All(partials.Items, static p => Assert.Equal("Otwórz drzwi latarni", p.Outcome.TranslatedText));
        Assert.All(partials.Items, static p => Assert.Equal(TranslationOrigin.Provider, p.Outcome.Origin));
        Assert.Null(await cache.LookupAsync("Open the lantern door", "en", "pl", ""));
        Assert.Equal(0, usage.ApiRequests);

        gate.SetResult();
        var outcomes = await task;

        Assert.Equal(["Otwórz drzwi latarni", "Masz 150 złotych monet.", "Otwórz drzwi latarni"], outcomes.Select(static o => o.TranslatedText));
        Assert.Equal(2, partials.Items.Count);
        Assert.Equal([true, false], provider.Requests.Select(static r => r.Streamed));
        Assert.Equal("Otwórz drzwi latarni", (await cache.LookupAsync("Open the lantern door", "en", "pl", ""))!.TranslatedText);
        Assert.Equal(1, usage.QualityIssues.Retries);
    }

    [Fact]
    public async Task Uniewaznienie_calego_wywolania_dostawcy_zdejmuje_wczesne_linie_kazdego_jego_tekstu()
    {
        var provider = new StreamingProvider(Polish) { ResetAfterStream = true };
        var (pipeline, _, _) = Create(provider);
        var partials = new Partials();

        var outcomes = await pipeline.TranslateAsync(["Open the lantern door", "Light the beacon", "Open the lantern door"], "en", "pl",
            knownLocal: null, partials.Add);

        Assert.Equal([0, 1, 2], Reported(partials).Order());
        Assert.Equal([0, 1, 2], Withdrawn(partials).Order());
        Assert.All(partials.Items.Where(static p => p.Index < 0), static p => Assert.Null(p.Outcome.TranslatedText));
        Assert.Equal(["Otwórz drzwi latarni", "PL:Light the beacon", "Otwórz drzwi latarni"], outcomes.Select(static o => o.TranslatedText));
    }

    [Theory]
    [InlineData(false, 25)]
    [InlineData(true, 24)]
    public async Task Zla_liczba_wynikow_w_drugiej_partii_zdejmuje_tylko_jej_wczesne_linie(bool planned, int firstPartitionInputs)
    {
        var provider = new PartitionedProvider(partitionSize: 25, wrongPartition: 1);
        var (pipeline, _, _) = Create(provider, new TranslationPipelineOptions { SplitParagraphs = planned });
        var partials = new Partials();
        var texts = LanternFrame(planned);

        var outcomes = await pipeline.TranslateAsync(texts, "en", "pl", knownLocal: null, partials.Add);

        Assert.Equal(30, Assert.Single(provider.Requests).Count);
        var first = Enumerable.Range(0, firstPartitionInputs).ToArray();
        var second = Enumerable.Range(firstPartitionInputs, texts.Length - firstPartitionInputs).ToArray();
        Assert.Equal(second, Withdrawn(partials).Order());
        Assert.Equal(first, Reported(partials).Where(i => i < firstPartitionInputs).Order());
        Assert.Empty(ReportedAfterWithdrawal(partials));
        Assert.All(outcomes, static o => Assert.Equal(TranslationOrigin.Provider, o.Origin));
        Assert.All(outcomes, static o => Assert.StartsWith("PL:", o.TranslatedText));
    }

    [Theory]
    [InlineData(false, 25)]
    [InlineData(true, 24)]
    public async Task Po_zdjeciu_wczesnych_linii_jednej_partii_kolejna_partia_dalej_je_publikuje(bool planned, int firstPartitionInputs)
    {
        var provider = new PartitionedProvider(partitionSize: 25, wrongPartition: 0);
        var (pipeline, _, _) = Create(provider, new TranslationPipelineOptions { SplitParagraphs = planned });
        var partials = new Partials();
        var texts = LanternFrame(planned);

        var outcomes = await pipeline.TranslateAsync(texts, "en", "pl", knownLocal: null, partials.Add);

        var items = partials.Items;
        var lastWithdrawal = items.Select(static (p, k) => (p.Index, k)).Last(static p => p.Index < 0).k;
        Assert.Equal(Enumerable.Range(0, firstPartitionInputs), Withdrawn(partials).Order());
        Assert.Equal(Enumerable.Range(firstPartitionInputs, texts.Length - firstPartitionInputs),
            items.Skip(lastWithdrawal + 1).Select(static p => p.Index).Order());
        Assert.All(items.Skip(lastWithdrawal + 1), p => Assert.Equal(outcomes[p.Index].TranslatedText, p.Outcome.TranslatedText));
        Assert.Empty(ReportedAfterWithdrawal(partials));
    }

    private sealed class PartitionedProvider(int partitionSize, int wrongPartition) : IStreamingTranslationProvider
    {
        public List<IReadOnlyList<string>> Requests { get; } = [];

        public string Name => "Partie";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
            TranslateWithContextAsync(texts, sourceLanguage, targetLanguage, TranslationContext.Empty, cancellationToken);

        public Task<IReadOnlyList<string>> TranslateWithContextAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, TranslationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(texts.Select(static t => "PL:" + t).ToList());

        public Task<IReadOnlyList<string>> TranslateStreamingAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, TranslationContext context,
            Action<int, string> onTranslation, CancellationToken cancellationToken = default)
        {
            Requests.Add(texts.ToList());
            for (var (start, partition) = (0, 0); start < texts.Count; start += partitionSize, partition++)
            {
                var end = Math.Min(texts.Count, start + partitionSize);
                var wrong = partition == wrongPartition;
                for (var i = start; i < (wrong ? start + 1 : end); i++) onTranslation(i, "PL:" + texts[i]);
                if (!wrong) continue;
                for (var i = start; i < end; i++) onTranslation(PartialResults.Withdraw(i), string.Empty);
            }
            return TranslateWithContextAsync(texts, sourceLanguage, targetLanguage, context, cancellationToken);
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private static string[] LanternFrame(bool planned)
    {
        var texts = Enumerable.Range(0, planned ? 28 : 30).Select(static i => $"Lantern note {i} is here.").ToArray();
        if (!planned) return texts;
        texts[1] = "Open the gate\nClose the gate";
        texts[27] = "Light the beacon\nDouse the beacon";
        return texts;
    }

    private static IEnumerable<int> Reported(Partials partials) =>
        partials.Items.Where(static p => p.Index >= 0).Select(static p => p.Index);

    private static IEnumerable<int> Withdrawn(Partials partials) =>
        partials.Items.Select(static p => PartialResults.IsWithdrawal(p.Index, out var index) ? index : -1).Where(static i => i >= 0);

    private static List<int> ReportedAfterWithdrawal(Partials partials)
    {
        var withdrawn = new HashSet<int>();
        var late = new List<int>();
        foreach (var (index, _) in partials.Items)
        {
            if (PartialResults.IsWithdrawal(index, out var input)) withdrawn.Add(input);
            else if (withdrawn.Contains(index)) late.Add(index);
        }
        return late;
    }

    [Fact]
    public async Task Wynik_i_liczniki_sa_takie_same_jak_bez_strumienia()
    {
        string[] texts = ["Open the lantern door", "You have 150 gold coins.", "Light the beacon"];
        var streamingProvider = new StreamingProvider(Polish);
        var streaming = Create(streamingProvider);
        var plain = Create(new StreamingProvider(Polish));

        var streamed = await streaming.Pipeline.TranslateAsync(texts, "en", "pl", knownLocal: null, static (_, _) => { });
        var whole = await plain.Pipeline.TranslateAsync(texts, "en", "pl");

        Assert.Equal(whole.Select(static o => (o.TranslatedText, o.Origin, o.QualityWarning)),
            streamed.Select(static o => (o.TranslatedText, o.Origin, o.QualityWarning)));
        Assert.Equal(plain.Usage.ApiRequests, streaming.Usage.ApiRequests);
        Assert.Equal(plain.Usage.ApiCharacters, streaming.Usage.ApiCharacters);
        Assert.Equal(plain.Usage.QualityIssues.Retries, streaming.Usage.QualityIssues.Retries);
        Assert.Equal(plain.Usage.FailedRequests, streaming.Usage.FailedRequests);
        Assert.True(streamingProvider.Requests[0].Streamed);
        Assert.NotNull(streaming.Usage.Latency.Summarize(LatencyStage.ProviderFirstLine));
        Assert.Null(plain.Usage.Latency.Summarize(LatencyStage.ProviderFirstLine));
    }

    [Fact]
    public async Task Rozny_wynik_koncowy_wygrywa_z_wczesnym()
    {
        var provider = new StreamingProvider(static (t, _) => "Wcześnie " + t) { Final = static (t, _) => "Ostatecznie " + t };
        var (pipeline, cache, _) = Create(provider);
        var partials = new Partials();

        var outcome = Assert.Single(await pipeline.TranslateAsync(["Light the beacon"], "en", "pl", knownLocal: null, partials.Add));

        Assert.Equal("Wcześnie Light the beacon", Assert.Single(partials.Items).Outcome.TranslatedText);
        Assert.Equal("Ostatecznie Light the beacon", outcome.TranslatedText);
        Assert.Equal("Ostatecznie Light the beacon", (await cache.LookupAsync("Light the beacon", "en", "pl", ""))!.TranslatedText);
    }

    [Fact]
    public async Task Odmowa_po_wczesnych_wynikach_daje_blad_i_nic_nie_zapisuje()
    {
        var provider = new StreamingProvider(Polish)
        {
            FailAfterStream = new TranslationException(TranslationFailureKind.ContentRefused, "odmowa"),
        };
        var (pipeline, cache, usage) = Create(provider);
        var partials = new Partials();

        var outcome = Assert.Single(await pipeline.TranslateAsync(["Open the lantern door"], "en", "pl", knownLocal: null, partials.Add));

        Assert.Single(partials.Items);
        Assert.Null(outcome.TranslatedText);
        Assert.Equal(TranslationFailureKind.ContentRefused, outcome.FailureKind);
        Assert.Null(await cache.LookupAsync("Open the lantern door", "en", "pl", ""));
        Assert.Equal(1, usage.FailedRequests);
        Assert.Equal(0, usage.ApiRequests);
        Assert.Null(usage.Latency.Summarize(LatencyStage.ProviderFirstLine));
    }

    [Fact]
    public async Task Dostawca_bez_strumienia_nie_zglasza_wczesnych_wynikow()
    {
        var (pipeline, _, _) = Create(new PlainProvider());
        var partials = new Partials();

        var outcome = Assert.Single(await pipeline.TranslateAsync(["Light the beacon"], "en", "pl", knownLocal: null, partials.Add));

        Assert.Equal("PL:Light the beacon", outcome.TranslatedText);
        Assert.Empty(partials.Items);
    }

    [Fact]
    public async Task Bez_odbiorcy_dostawca_odpowiada_bez_strumienia()
    {
        var provider = new StreamingProvider(Polish);
        var (pipeline, _, _) = Create(provider);

        await pipeline.TranslateAsync(["Light the beacon"], "en", "pl");

        Assert.False(Assert.Single(provider.Requests).Streamed);
    }

    [Fact]
    public async Task Znane_lokalnie_teksty_nie_sa_zglaszane_jako_wczesne()
    {
        var provider = new StreamingProvider(Polish);
        var (pipeline, cache, _) = Create(provider);
        await cache.StoreAsync(new NewCacheEntry("Light the beacon", "Light the beacon", "en", "pl", "Zapal latarnię", "Strumien"));
        var partials = new Partials();

        var outcomes = await pipeline.TranslateAsync(["Light the beacon", "Open the lantern door"], "en", "pl", knownLocal: null, partials.Add);

        Assert.Equal(["Zapal latarnię", "Otwórz drzwi latarni"], outcomes.Select(static o => o.TranslatedText));
        var partial = Assert.Single(partials.Items);
        Assert.Equal(1, partial.Index);
        Assert.Equal(["Open the lantern door"], Assert.Single(provider.Requests).Texts);
    }

    [Fact]
    public async Task Blok_z_akapitami_jest_zglaszany_gdy_gotowe_sa_wszystkie_jednostki()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StreamingProvider(static (t, _) => "PL:" + t) { Gate = gate.Task, GateAfter = 2 };
        var (pipeline, _, _) = Create(provider, new TranslationPipelineOptions { SplitParagraphs = true });
        var partials = new Partials();
        const string block = "Options\nQuit\nYou need\nthree keys.";

        var task = pipeline.TranslateAsync([block, "Light the beacon"], "en", "pl", knownLocal: null, partials.Add);
        await WaitUntilAsync(() => provider.Requests.Count == 1);
        await Task.Delay(50);

        Assert.DoesNotContain(partials.Items, static p => p.Index == 0);
        gate.SetResult();
        var outcomes = await task;

        var blockPartial = Assert.Single(partials.Items, static p => p.Index == 0);
        Assert.Equal(outcomes[0].TranslatedText, blockPartial.Outcome.TranslatedText);
        Assert.Equal(block, blockPartial.Outcome.SourceText);
        Assert.Equal("PL:Light the beacon", Assert.Single(partials.Items, static p => p.Index == 1).Outcome.TranslatedText);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(5);
        }
    }
}
