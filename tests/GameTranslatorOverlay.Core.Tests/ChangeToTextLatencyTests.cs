using GameTranslatorOverlay.Core.Usage;

namespace GameTranslatorOverlay.Core.Tests;

public class ChangeToTextLatencyTests
{
    [Fact]
    public void Opis_zaczyna_sie_od_czasu_zmiana_napis()
    {
        var monitor = new LatencyMonitor();
        monitor.Record(LatencyStage.Ocr, 90);
        monitor.Record(LatencyStage.NewText, 800);
        monitor.Record(LatencyStage.ChangeToText, 1100);

        Assert.Equal("Zmiana → napis: 1,1 s • Nowy tekst: 800 ms • OCR: 90 ms", monitor.Describe("DeepL"));
    }

    [Fact]
    public void Opis_pokazuje_p90_zmiany_przy_kilku_probkach()
    {
        var monitor = new LatencyMonitor();
        foreach (var ms in new double[] { 400, 500, 600, 700, 2000 })
            monitor.Record(LatencyStage.ChangeToText, ms);

        Assert.StartsWith("Zmiana → napis: 600 ms (p90 1,48 s)", monitor.Describe("DeepL"));
    }

    [Fact]
    public void Raport_ma_zmiane_na_pierwszej_pozycji()
    {
        var monitor = new LatencyMonitor();
        monitor.Record(LatencyStage.NewText, 1200);
        monitor.Record(LatencyStage.ChangeToText, 1500);

        var lines = monitor.Report("DeepL").Split(Environment.NewLine);

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Zmiana → napis (tryb live): mediana 1,5 s", lines[1]);
        Assert.StartsWith("Nowy tekst (klatka → napis): mediana 1,2 s", lines[2]);
    }

    [Fact]
    public void Bez_pomiaru_zmiany_opis_jest_jak_dotad()
    {
        var monitor = new LatencyMonitor();
        monitor.Record(LatencyStage.KnownText, 210);

        Assert.Equal("Znany: 210 ms", monitor.Describe("DeepL"));
        Assert.DoesNotContain("Zmiana", monitor.Report("DeepL"));
    }
}
