using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;

namespace GameTranslatorOverlay.Core.Tests;

public class CorpusCacheMarkerTests
{
    [Fact]
    public void Znacznik_bez_zrodla_jest_taki_jak_dotad()
    {
        Assert.Equal("reflow-1", TranslationCacheContext.Build(TranslationQualityFlags.None));
        Assert.Equal("reflow-1;pg=m", TranslationCacheContext.Build(TranslationQualityFlags.None, playerGender: PlayerGender.Male));
        Assert.Equal("reflow-1;qa=numbers;qa-final;pg=f",
            TranslationCacheContext.Build(TranslationQualityFlags.NumbersChanged, final: true, playerGender: PlayerGender.Female));
    }

    [Fact]
    public void Zrodlo_korpusu_trafia_na_koniec_znacznika_i_wraca_przy_odczycie()
    {
        var marker = TranslationCacheContext.Build(
            TranslationQualityFlags.NumbersChanged, playerGender: PlayerGender.Female, source: TranslationCacheContext.CorpusSource);

        Assert.Equal("reflow-1;qa=numbers;pg=f;src=corpus", marker);
        var parsed = TranslationCacheContext.Parse(marker);
        Assert.Equal("reflow-1", parsed.Format);
        Assert.Equal(TranslationQualityFlags.NumbersChanged, parsed.QualityIssues);
        Assert.Equal(PlayerGender.Female, parsed.PlayerGender);
        Assert.Equal("corpus", parsed.Source);
        Assert.True(parsed.IsFromCorpus);
        Assert.True(parsed.NeedsQualityRetry);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("reflow-1")]
    [InlineData("reflow-1;pg=m")]
    [InlineData("reflow-1;src=")]
    public void Wpis_bez_zrodla_nie_jest_z_korpusu(string? context)
    {
        var parsed = TranslationCacheContext.Parse(context);

        Assert.Null(parsed.Source);
        Assert.False(parsed.IsFromCorpus);
    }

    [Fact]
    public void Nieznane_zrodlo_jest_czytane_ale_nie_jest_korpusem()
    {
        var parsed = TranslationCacheContext.Parse("reflow-1;src=import");

        Assert.Equal("import", parsed.Source);
        Assert.False(parsed.IsFromCorpus);
        Assert.Equal("reflow-1", parsed.Format);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a;b")]
    [InlineData("src=x")]
    [InlineData("z spacją")]
    public void Zrodlo_z_niedozwolonymi_znakami_jest_odrzucane(string source)
    {
        Assert.Throws<ArgumentException>(() => TranslationCacheContext.Build(TranslationQualityFlags.None, source: source));
    }

    [Fact]
    public void Wieloliniowy_wpis_z_korpusu_w_nowym_formacie_nie_jest_nieaktualny()
    {
        var marker = TranslationCacheContext.Build(TranslationQualityFlags.None, source: TranslationCacheContext.CorpusSource);

        Assert.False(TranslationCacheContext.IsStale(marker, "The Silver\nOrchard"));
        Assert.True(TranslationCacheContext.IsStale(marker, "Did you see it?", PlayerGender.Female));
        Assert.False(TranslationCacheContext.IsStale(marker, "Did you see it?", PlayerGender.Unknown));
    }

    [Fact]
    public void Klucz_korpusu_zdejmuje_znaczniki_i_zachowuje_wielkosc_liter_oraz_wiersze()
    {
        Assert.Equal("Press Start", CorpusTranslationKey.Normalize("<b>Press</b>   <color=#fff>Start</color>"));
        Assert.Equal("The Silver\nOrchard", CorpusTranslationKey.Normalize("The Silver<br>Orchard"));
        Assert.Equal("Line one\nLine two", CorpusTranslationKey.Normalize("Line one \r\n\r\n Line two"));
        Assert.Equal("RUMMAGE", CorpusTranslationKey.Normalize("RUMMAGE"));
        Assert.Equal(string.Empty, CorpusTranslationKey.Normalize(string.Empty));
        Assert.Equal(string.Empty, CorpusTranslationKey.DisplayText(null!));
    }

    [Fact]
    public void Klucz_wpisu_korpusu_liczony_jest_z_tekstu_EN()
    {
        var entry = new CorpusEntry { Key = "k", En = "<i>Hello</i>  there", Kind = CorpusEntryKind.Ui, Source = "Table" };

        Assert.Equal("Hello there", CorpusTranslationKey.For(entry));
    }

    [Fact]
    public void Kontekst_ze_scena_albo_notatkami_nie_jest_pusty()
    {
        Assert.True(TranslationContext.Empty.IsEmpty);
        Assert.False((TranslationContext.Empty with { Scene = "Dialogue" }).IsEmpty);
        Assert.False((TranslationContext.Empty with { TextNotes = [null, "speaker: Ann"] }).IsEmpty);
        Assert.True((TranslationContext.Empty with { Scene = "  ", TextNotes = [null, " "] }).IsEmpty);
        Assert.True(new TranslationContext(null, new List<GlossaryTerm>()).IsEmpty);
    }
}
