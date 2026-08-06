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
}
