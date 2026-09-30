using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

/// <summary>
/// Terminy z trybu prywatnego (sessionOnly) działają lokalnie i jako podpowiedź, ale nigdy
/// nie trafiają do trwałego glosariusza DeepL — także po wyłączeniu trybu prywatnego.
/// </summary>
public class GlossarySessionTermsTests
{
    private sealed class ContextualProvider : IContextualTranslationProvider
    {
        public List<TranslationContext> Contexts { get; } = [];

        public string Name => "Contextual";
        public bool RequiresApiKey => false;

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(texts.Select(static t => "PL:" + t).ToList());

        public Task<IReadOnlyList<string>> TranslateWithContextAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
            TranslationContext context, CancellationToken cancellationToken = default)
        {
            lock (Contexts) Contexts.Add(context);
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(static t => "CTX:" + t).ToList());
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    private static (TranslationPipeline Pipeline, ContextualProvider Provider) CreatePipeline(GlossaryService glossary)
    {
        var provider = new ContextualProvider();
        var pipeline = new TranslationPipeline(
            glossary, new InMemoryTranslationCache(), provider, new UsageTracker(), new TranslationPipelineOptions());
        return (pipeline, provider);
    }

    [Fact]
    public void PersistableTerms_pomija_terminy_sesyjne()
    {
        var glossary = new GlossaryService();
        glossary.AddTerm(new GlossaryTerm("Waystone", "Kamień drogi"));
        glossary.AddTerm(new GlossaryTerm("Meet me at the docks", "Spotkajmy się w dokach"), sessionOnly: true);

        Assert.Equal(2, glossary.TermCount);
        Assert.Equal(2, glossary.AllTerms.Count);
        Assert.Equal(["Waystone"], glossary.PersistableTerms.Select(static t => t.Source));
        Assert.True(glossary.TryTranslateExact("Meet me at the docks", out var local));
        Assert.Equal("Spotkajmy się w dokach", local);
    }

    [Fact]
    public void Clear_usuwa_takze_terminy_sesyjne()
    {
        var glossary = new GlossaryService();
        glossary.AddTerm(new GlossaryTerm("Secret", "Sekret"), sessionOnly: true);

        glossary.Clear();

        Assert.Empty(glossary.AllTerms);
        Assert.Empty(glossary.PersistableTerms);
        Assert.False(glossary.TryTranslateExact("Secret", out _));
    }

    [Fact]
    public async Task Termin_prywatny_sam_nie_wyzwala_glosariusza_DeepL()
    {
        var glossary = new GlossaryService();
        glossary.AddTerm(new GlossaryTerm("Waystone", "Kamień drogi"));
        glossary.AddTerm(new GlossaryTerm("Kaelen", "Kaelen", CaseSensitive: true), sessionOnly: true);
        var (pipeline, provider) = CreatePipeline(glossary);

        await pipeline.TranslateAsync(["Kaelen waits for you"], "en", "pl");

        var context = Assert.Single(provider.Contexts);
        // Podpowiedź dla LLM idzie razem z tekstem i niczego nie utrwala — może zostać.
        Assert.Equal(["Kaelen"], context.Terms.Select(static t => t.Source));
        Assert.Empty(context.GlossaryTerms);
    }

    [Fact]
    public async Task Glosariusz_DeepL_nie_zawiera_terminow_prywatnych()
    {
        var glossary = new GlossaryService();
        glossary.AddTerm(new GlossaryTerm("Waystone", "Kamień drogi"));
        glossary.AddTerm(new GlossaryTerm("Kaelen", "Kaelen", CaseSensitive: true), sessionOnly: true);
        var (pipeline, provider) = CreatePipeline(glossary);

        await pipeline.TranslateAsync(["Kaelen found a Waystone"], "en", "pl");

        var context = Assert.Single(provider.Contexts);
        Assert.Equal(2, context.Terms.Count);
        Assert.Equal(["Waystone"], context.GlossaryTerms.Select(static t => t.Source));
    }
}
