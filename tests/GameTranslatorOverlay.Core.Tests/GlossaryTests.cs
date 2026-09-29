using GameTranslatorOverlay.Core.Glossary;

namespace GameTranslatorOverlay.Core.Tests;

public class GlossaryTests
{
    private static GlossaryService CreateService(params GlossaryTerm[] terms)
    {
        var service = new GlossaryService();
        service.LoadDocument(new GlossaryDocument { Name = "test", Terms = [.. terms] });
        return service;
    }

    [Fact]
    public void TryTranslateExact_dopasowuje_caly_tekst_bez_wzgledu_na_wielkosc_liter()
    {
        var service = CreateService(new GlossaryTerm("Energy Shield", "Tarcza energetyczna"));

        Assert.True(service.TryTranslateExact("energy shield", out var translation));
        Assert.Equal("Tarcza energetyczna", translation);
    }

    [Fact]
    public void TryTranslateExact_nie_dopasowuje_fragmentu_dluzszego_tekstu()
    {
        var service = CreateService(new GlossaryTerm("Armour", "Pancerz"));

        Assert.False(service.TryTranslateExact("Armour: 320", out _));
        Assert.False(service.TryTranslateExact("Increased Armour", out _));
    }

    [Fact]
    public void Priorytet_rozstrzyga_konflikt_terminow()
    {
        var service = CreateService(
            new GlossaryTerm("Spirit", "Duch", Priority: 0),
            new GlossaryTerm("Spirit", "Esencja ducha", Priority: 10));

        Assert.True(service.TryTranslateExact("Spirit", out var translation));
        Assert.Equal("Esencja ducha", translation);
    }

    [Fact]
    public void DetectConflicts_wykrywa_ten_sam_termin_z_roznymi_tlumaczeniami()
    {
        var service = CreateService(
            new GlossaryTerm("Spirit", "Duch"),
            new GlossaryTerm("spirit", "Esencja"),
            new GlossaryTerm("Armour", "Pancerz"));

        var conflicts = service.DetectConflicts();

        var conflict = Assert.Single(conflicts);
        Assert.Equal(2, conflict.Targets.Count);
    }

    [Fact]
    public void AddTerm_dziala_w_locie()
    {
        var service = CreateService();
        service.AddTerm(new GlossaryTerm("Waystone", "Kamień drogi"));

        Assert.True(service.TryTranslateExact("Waystone", out var translation));
        Assert.Equal("Kamień drogi", translation);
    }

    [Fact]
    public void Serializer_wykonuje_pelny_roundtrip()
    {
        var document = new GlossaryDocument
        {
            Name = "poe2",
            SourceLanguage = "en",
            TargetLanguage = "pl",
            Version = 2,
            Terms = [new GlossaryTerm("Stun", "Ogłuszenie", Priority: 5, Note: "mechanika")],
        };

        var restored = GlossarySerializer.FromJson(GlossarySerializer.ToJson(document));

        Assert.Equal("poe2", restored.Name);
        Assert.Equal(2, restored.Version);
        var term = Assert.Single(restored.Terms);
        Assert.Equal("Stun", term.Source);
        Assert.Equal("Ogłuszenie", term.Target);
        Assert.Equal(5, term.Priority);
    }

    [Fact]
    public void Validator_wykrywa_puste_pola()
    {
        var document = new GlossaryDocument
        {
            Name = "",
            Terms = [new GlossaryTerm("", "Pancerz"), new GlossaryTerm("Armour", "")],
        };

        var errors = GlossaryValidator.Validate(document);

        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void FromJson_rzuca_zrozumialy_blad_dla_pustego_pliku()
    {
        Assert.Throws<FormatException>(() => GlossarySerializer.FromJson("null"));
    }

    [Fact]
    public void Priorytet_dziala_takze_miedzy_terminem_dokladnym_a_bez_wielkosci_liter()
    {
        var service = CreateService(
            new GlossaryTerm("Spirit", "Duch", CaseSensitive: true, Priority: 0),
            new GlossaryTerm("spirit", "Esencja ducha", Priority: 10));

        Assert.True(service.TryTranslateExact("Spirit", out var translation));
        Assert.Equal("Esencja ducha", translation);
    }

    [Fact]
    public void Przy_rownym_priorytecie_wygrywa_termin_dokladny()
    {
        var service = CreateService(
            new GlossaryTerm("spirit", "Esencja ducha", Priority: 5),
            new GlossaryTerm("Spirit", "Duch", CaseSensitive: true, Priority: 5));

        Assert.True(service.TryTranslateExact("Spirit", out var exact));
        Assert.Equal("Duch", exact);
        Assert.True(service.TryTranslateExact("SPIRIT", out var insensitive));
        Assert.Equal("Esencja ducha", insensitive);
    }

    [Fact]
    public void Termin_z_podwojna_lub_twarda_spacja_trafia_w_znormalizowany_tekst()
    {
        var service = CreateService(
            new GlossaryTerm("Energy  Shield", "Tarcza energetyczna"),
            new GlossaryTerm("Life Flask", "Flakon życia"));

        Assert.True(service.TryTranslateExact("Energy Shield", out var shield));
        Assert.Equal("Tarcza energetyczna", shield);
        Assert.True(service.TryTranslateExact("Life Flask", out var flask));
        Assert.Equal("Flakon życia", flask);
    }

    [Fact]
    public void FindTermsIn_znajduje_terminy_wewnatrz_zdan_jako_cale_slowa()
    {
        var service = CreateService(
            new GlossaryTerm("Armour", "Pancerz"),
            new GlossaryTerm("Arm", "Ramię"),
            new GlossaryTerm("Waystone", "Kamień drogi"));

        var terms = service.FindTermsIn(["+25% increased Armour", "Nothing here"]);

        var term = Assert.Single(terms);
        Assert.Equal("Pancerz", term.Target);
    }

    [Fact]
    public void FindTermsIn_preferuje_dluzsza_fraze_nad_jej_fragmentem()
    {
        var service = CreateService(
            new GlossaryTerm("Shield", "Tarcza"),
            new GlossaryTerm("Energy Shield", "Tarcza energetyczna"));

        var onlyPhrase = service.FindTermsIn(["+40 to maximum Energy Shield"]);
        var both = service.FindTermsIn(["Energy Shield protects your Shield"]);

        Assert.Equal(["Energy Shield"], onlyPhrase.Select(static t => t.Source));
        Assert.Equal(["Energy Shield", "Shield"], both.Select(static t => t.Source));
    }

    [Fact]
    public void FindTermsIn_respektuje_wielkosc_liter_i_limit()
    {
        var service = CreateService(
            new GlossaryTerm("Rage", "Szał", CaseSensitive: true),
            new GlossaryTerm("Stun", "Ogłuszenie"),
            new GlossaryTerm("Freeze", "Zamrożenie"));

        Assert.Empty(service.FindTermsIn(["The rage of the storm"]));
        Assert.Single(service.FindTermsIn(["Rage builds up"]));
        Assert.Single(service.FindTermsIn(["stun and freeze"], maxTerms: 1));
    }
}
