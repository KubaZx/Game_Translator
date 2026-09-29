using GameTranslatorOverlay.Core.Caching;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class LatencyMonitorTests
{
    [Fact]
    public void Brak_pomiarow_daje_pusty_opis_i_raport()
    {
        var monitor = new LatencyMonitor();

        Assert.Null(monitor.Summarize(LatencyStage.Provider));
        Assert.Equal(string.Empty, monitor.Describe("DeepL"));
        Assert.Equal(string.Empty, monitor.Report("DeepL"));
    }

    [Fact]
    public void Mediana_p90_i_maksimum_z_probek()
    {
        var monitor = new LatencyMonitor();
        foreach (var ms in new double[] { 500, 100, 300, 200, 400, 600, 700, 800, 900, 1000 })
            monitor.Record(LatencyStage.Provider, ms);

        var summary = monitor.Summarize(LatencyStage.Provider)!;

        Assert.Equal(10, summary.Count);
        Assert.Equal(550, summary.MedianMs, precision: 6);
        Assert.Equal(910, summary.P90Ms, precision: 6);
        Assert.Equal(1000, summary.MaxMs);
        Assert.Equal(1000, summary.LastMs);
    }

    [Fact]
    public void Okno_trzyma_tylko_ostatnie_probki()
    {
        var monitor = new LatencyMonitor(window: 3);
        foreach (var ms in new double[] { 5000, 10, 20, 30 })
            monitor.Record(LatencyStage.Ocr, ms);

        var summary = monitor.Summarize(LatencyStage.Ocr)!;

        Assert.Equal(3, summary.Count);
        Assert.Equal(30, summary.MaxMs);
        Assert.Equal(20, summary.MedianMs);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void Bledne_wartosci_sa_pomijane(double value)
    {
        var monitor = new LatencyMonitor();
        monitor.Record(LatencyStage.Capture, value);
        Assert.Null(monitor.Summarize(LatencyStage.Capture));
    }

    [Fact]
    public void Etapy_sa_liczone_osobno_i_reset_czysci_wszystko()
    {
        var monitor = new LatencyMonitor();
        monitor.Record(LatencyStage.Ocr, 100);
        monitor.Record(LatencyStage.NewText, 900);

        Assert.Equal(100, monitor.Summarize(LatencyStage.Ocr)!.MedianMs);
        Assert.Equal(900, monitor.Summarize(LatencyStage.NewText)!.MedianMs);

        monitor.Reset();
        Assert.Null(monitor.Summarize(LatencyStage.Ocr));
        Assert.Null(monitor.Summarize(LatencyStage.NewText));
    }

    [Theory]
    [InlineData(0.4, "0 ms")]
    [InlineData(95.4, "95 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(1000, "1 s")]
    [InlineData(1450, "1,45 s")]
    [InlineData(12340, "12,3 s")]
    public void Format_czasu_po_polsku(double ms, string expected) =>
        Assert.Equal(expected, LatencyMonitor.FormatMs(ms));

    [Fact]
    public void Opis_zawiera_nazwe_dostawcy_i_p90_dopiero_przy_kilku_probkach()
    {
        var monitor = new LatencyMonitor();
        monitor.Record(LatencyStage.Provider, 400);
        Assert.Equal("DeepL: 400 ms", monitor.Describe("DeepL"));

        for (var i = 0; i < 4; i++) monitor.Record(LatencyStage.Provider, 400);
        Assert.Equal("DeepL: 400 ms (p90 400 ms)", monitor.Describe("DeepL"));
    }

    [Fact]
    public void Raport_ma_linie_dla_zmierzonych_etapow()
    {
        var monitor = new LatencyMonitor();
        monitor.Record(LatencyStage.NewText, 1200);
        monitor.Record(LatencyStage.Ocr, 80);

        var lines = monitor.Report("DeepL").Split(Environment.NewLine);

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Nowy tekst (klatka → napis): mediana 1,2 s", lines[1]);
        Assert.StartsWith("OCR: mediana 80 ms", lines[2]);
    }

    [Fact]
    public void Reset_licznikow_uzycia_zeruje_tez_czasy()
    {
        var usage = new UsageTracker();
        usage.Latency.Record(LatencyStage.Provider, 300);

        usage.Reset();

        Assert.Null(usage.Latency.Summarize(LatencyStage.Provider));
    }

    private sealed class SlowProvider(TranslationException? failure = null) : ITranslationProvider
    {
        public string Name => "Slow";
        public bool RequiresApiKey => false;

        public async Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            await Task.Delay(40, cancellationToken);
            if (failure is not null) throw failure;
            return texts.Select(static t => "PL:" + t).ToList();
        }

        public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderStatus(true, "ok"));
    }

    [Fact]
    public async Task Pipeline_mierzy_udane_zapytanie_do_dostawcy_a_nie_cache()
    {
        var usage = new UsageTracker();
        var pipeline = new TranslationPipeline(
            new GlossaryService(), new InMemoryTranslationCache(), new SlowProvider(), usage, new TranslationPipelineOptions());

        await pipeline.TranslateAsync(["Hello there"], "en", "pl");
        await pipeline.TranslateAsync(["Hello there"], "en", "pl"); // z cache — bez pomiaru

        var summary = usage.Latency.Summarize(LatencyStage.Provider)!;
        Assert.Equal(1, summary.Count);
        Assert.True(summary.MedianMs >= 30, $"zmierzono {summary.MedianMs} ms");
    }

    [Fact]
    public async Task Pipeline_nie_mierzy_nieudanego_zapytania()
    {
        var usage = new UsageTracker();
        var pipeline = new TranslationPipeline(
            new GlossaryService(), new InMemoryTranslationCache(),
            new SlowProvider(new TranslationException(TranslationFailureKind.Timeout, "timeout")),
            usage, new TranslationPipelineOptions());

        await pipeline.TranslateAsync(["Hello there"], "en", "pl");

        Assert.Null(usage.Latency.Summarize(LatencyStage.Provider));
    }
}
