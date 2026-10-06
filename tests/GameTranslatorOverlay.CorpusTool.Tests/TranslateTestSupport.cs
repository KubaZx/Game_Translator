using System.Net;
using System.Text;
using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.CorpusTool.Translation;

namespace GameTranslatorOverlay.CorpusTool.Tests;

internal static class Corpus
{
    public static CorpusEntry Ui(string key, string en, string source = "Strings", string? context = null) =>
        new() { Key = key, En = en, Kind = CorpusEntryKind.Ui, Source = source, Context = context };

    public static CorpusEntry Dialog(string key, string en, string node, int order, string? speaker = null, string source = "Scene (en-US)") =>
        new() { Key = key, En = en, Kind = CorpusEntryKind.Dialog, Source = source, Node = node, Order = order, Speaker = speaker };

    public static CorpusEntry Subtitle(string source, string en, int order = 1, string? speaker = null) =>
        new() { Key = order.ToString(System.Globalization.CultureInfo.InvariantCulture), En = en, Kind = CorpusEntryKind.Subtitle, Source = source, Order = order, Speaker = speaker };

    public static CachedTranslation Cached(string key, string translated, string profile = "", string provider = "DeepL",
        string? context = "reflow-1", bool manual = false, bool approved = false) =>
        new(1, key, key, translated, provider, profile, manual, approved, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1)
        {
            Context = context,
        };
}

internal sealed class FakeCorpusStore : ICorpusCacheStore
{
    private readonly Lock _gate = new();
    private readonly List<CachedTranslation> _entries = [];

    public List<NewCacheEntry> Stored { get; } = [];
    public int PeekCalls { get; private set; }
    public Func<IReadOnlyList<NewCacheEntry>, int>? WrittenOverride { get; init; }

    public void Add(CachedTranslation entry)
    {
        lock (_gate) _entries.Add(entry);
    }

    public Task<IReadOnlyList<CachedTranslation?>> PeekAsync(
        IReadOnlyList<string> keys, string sourceLanguage, string targetLanguage, string gameProfile, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            PeekCalls++;
            IReadOnlyList<CachedTranslation?> result = keys.Select(key => _entries
                .Where(e => e.NormalizedText == key && (e.GameProfile == gameProfile || e.GameProfile.Length == 0))
                .OrderByDescending(static e => e.IsManual)
                .ThenByDescending(static e => e.IsApproved)
                .ThenBy(e => e.GameProfile == gameProfile ? 0 : 1)
                .FirstOrDefault()).ToList();
            return Task.FromResult(result);
        }
    }

    public Task<int> StoreAsync(IReadOnlyList<NewCacheEntry> entries, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Stored.AddRange(entries);
            return Task.FromResult(WrittenOverride?.Invoke(entries) ?? entries.Count);
        }
    }
}

internal class RecordingProvider(string name, Func<string, int, string> translate) : IContextualTranslationProvider
{
    private readonly Lock _gate = new();
    private int _calls;

    public List<(IReadOnlyList<string> Texts, TranslationContext Context)> Calls { get; } = [];
    public Func<int, Exception?>? Failure { get; init; }

    public string Name => name;
    public bool RequiresApiKey => false;

    public Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
        TranslateWithContextAsync(texts, sourceLanguage, targetLanguage, TranslationContext.Empty, cancellationToken);

    public Task<IReadOnlyList<string>> TranslateWithContextAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, TranslationContext context,
        CancellationToken cancellationToken = default)
    {
        int call;
        lock (_gate)
        {
            call = _calls++;
            Calls.Add((texts.ToList(), context));
        }
        if (Failure?.Invoke(call) is { } failure) return Task.FromException<IReadOnlyList<string>>(failure);
        IReadOnlyList<string> result = texts.Select(t => translate(t, call)).ToList();
        return Task.FromResult(result);
    }

    public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderStatus(true, "ok"));
}

internal sealed class GenderAwareRecordingProvider(string name, Func<string, int, string> translate)
    : RecordingProvider(name, translate), IGenderAwareTranslationProvider, IRetryableTranslationProvider;

internal sealed class FakeHttpHandler(Func<HttpRequestMessage, string, int, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly Lock _gate = new();

    public List<(string Url, string Body, string? Authorization)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        int attempt;
        lock (_gate)
        {
            attempt = Requests.Count;
            Requests.Add((request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
        }
        return responder(request, body, attempt);
    }

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
