using GameTranslatorOverlay.Core.Glossary;

namespace GameTranslatorOverlay.Core.Tests;

/// <summary>
/// Dopasowanie terminów w tekście z gry: końcówki liczby mnogiej i dopełniacza, frazy
/// złamane do nowej linii, etykiety z dwukropkiem i zakres „label”.
/// </summary>
public class GlossaryMatchingTests
{
    private static GlossaryService CreateService(params GlossaryTerm[] terms)
    {
        var service = new GlossaryService();
        service.LoadDocument(new GlossaryDocument { Name = "test", Terms = [.. terms] });
        return service;
    }

    private static string[] Found(GlossaryService service, params string[] texts) =>
        service.FindTermsIn(texts).Select(static t => t.Source).ToArray();

    [Theory]
    [InlineData("Waystone", "You found 3 Waystones")]
    [InlineData("Support Gem", "Socket two Support Gems here")]
    [InlineData("Exalted Orb", "Trade 5 Exalted Orbs")]
    [InlineData("Elemental Resistance", "+10% to all Elemental Resistances")]
    [InlineData("Ability", "Your Abilities are ready")]
    [InlineData("Waystone", "The Waystone's power fades")]
    [InlineData("Waystone", "The Waystone’s power fades")]
    [InlineData("Waystone", "All Waystones' tiers")]
    [InlineData("Box", "Open the Boxes")]
    public void FindTermsIn_rozpoznaje_liczbe_mnoga_i_dopelniacz(string source, string text)
    {
        var service = CreateService(new GlossaryTerm(source, "X"));

        Assert.Equal([source], Found(service, text));
    }

    [Theory]
    [InlineData("Waystone", "Waystonesque design")]
    [InlineData("Orb", "Orbit the planet")]
    [InlineData("Ability", "Abilitiesque")]
    [InlineData("Gem", "Gemstone")]
    public void Koncowka_nadal_wymaga_granicy_slowa(string source, string text)
    {
        var service = CreateService(new GlossaryTerm(source, "X"));

        Assert.Empty(Found(service, text));
    }

    [Fact]
    public void Fraza_zlamana_do_nowej_linii_wygrywa_z_krotszym_terminem()
    {
        var service = CreateService(
            new GlossaryTerm("Shield", "Tarcza"),
            new GlossaryTerm("Energy Shield", "Tarcza energetyczna"));

        Assert.Equal(["Energy Shield"], Found(service, "+40 to maximum Energy\nShield"));
        Assert.Equal(["Energy Shield"], Found(service, "Energy \n Shields"));
    }

    [Fact]
    public void Termin_z_rozroznianiem_wielkosci_liter_dopasowuje_liczbe_mnoga()
    {
        var service = CreateService(new GlossaryTerm("Rune", "Runa", CaseSensitive: true));

        Assert.Equal(["Rune"], Found(service, "Collect Runes"));
        Assert.Empty(Found(service, "Collect runes"));
    }

    [Fact]
    public void TryTranslateExact_nie_tlumaczy_liczby_mnogiej()
    {
        var service = CreateService(new GlossaryTerm("Waystone", "Kamień drogi"));

        // Polskiej odmiany słownik nie zna — forma mnoga idzie do dostawcy z podpowiedzią.
        Assert.False(service.TryTranslateExact("Waystones", out _));
        Assert.Equal(["Waystone"], Found(service, "Waystones"));
    }

    [Fact]
    public void TryTranslateExact_akceptuje_termin_zlamany_do_nowej_linii()
    {
        var service = CreateService(new GlossaryTerm("Energy Shield", "Tarcza energetyczna"));

        Assert.True(service.TryTranslateExact("Energy\nShield", out var translation));
        Assert.Equal("Tarcza energetyczna", translation);
    }

    [Theory]
    [InlineData("Rarity:", "Rzadkość:")]
    [InlineData("Rarity :", "Rzadkość:")]
    [InlineData("rarity:", "Rzadkość:")]
    public void TryTranslateExact_zachowuje_dwukropek_etykiety(string text, string expected)
    {
        var service = CreateService(new GlossaryTerm("Rarity", "Rzadkość"));

        Assert.True(service.TryTranslateExact(text, out var translation));
        Assert.Equal(expected, translation);
    }

    [Fact]
    public void Sam_dwukropek_ani_dwukropek_w_srodku_nie_sa_etykieta()
    {
        var service = CreateService(new GlossaryTerm("Rarity", "Rzadkość"));

        Assert.False(service.TryTranslateExact(":", out _));
        Assert.False(service.TryTranslateExact("Rarity: Unique", out _));
    }

    [Fact]
    public void Termin_label_dziala_tylko_jako_caly_tekst()
    {
        var service = CreateService(new GlossaryTerm("Save", "Zapisz", Scope: GlossaryScope.Label));

        Assert.True(service.TryTranslateExact("Save", out var button));
        Assert.Equal("Zapisz", button);
        Assert.True(service.TryTranslateExact("Save:", out _));
        Assert.Empty(Found(service, "We must save the village"));
    }

    [Fact]
    public void Etykieta_nie_zaslania_podpowiedzi_z_innego_slownika()
    {
        var service = CreateService(
            new GlossaryTerm("Staff", "Personel"),
            new GlossaryTerm("Staff", "Kostur", Scope: GlossaryScope.Label));

        // Etykieta wczytana później wygrywa dla całego tekstu…
        Assert.True(service.TryTranslateExact("Staff", out var label));
        Assert.Equal("Kostur", label);
        // …ale w zdaniu podpowiedzią zostaje termin „any”.
        var hint = Assert.Single(service.FindTermsIn(["Ask the staff"]));
        Assert.Equal("Personel", hint.Target);
    }

    [Fact]
    public void Serializer_zachowuje_zakres_a_stary_JSON_bez_niego_dziala()
    {
        const string oldJson = """
            { "name": "old", "terms": [ { "source": "Save", "target": "Zapis" } ] }
            """;
        var old = GlossarySerializer.FromJson(oldJson);
        Assert.Null(Assert.Single(old.Terms).Scope);
        Assert.Empty(GlossaryValidator.Validate(old));
        Assert.DoesNotContain("scope", GlossarySerializer.ToJson(old));
        Assert.DoesNotContain("isLabelOnly", GlossarySerializer.ToJson(old), StringComparison.OrdinalIgnoreCase);

        var labelled = new GlossaryDocument { Name = "x", Terms = [new GlossaryTerm("Save", "Zapisz", Scope: "label")] };
        var roundtrip = GlossarySerializer.FromJson(GlossarySerializer.ToJson(labelled));
        Assert.True(Assert.Single(roundtrip.Terms).IsLabelOnly);
    }

    [Fact]
    public void Validator_odrzuca_nieznany_zakres()
    {
        var document = new GlossaryDocument { Name = "x", Terms = [new GlossaryTerm("Save", "Zapisz", Scope: "button")] };

        Assert.Contains(GlossaryValidator.Validate(document), static e => e.Contains("zakres", StringComparison.Ordinal));
    }

    [Fact]
    public void Przy_rownym_priorytecie_wygrywa_termin_pozniejszy()
    {
        var service = CreateService(
            new GlossaryTerm("Armour", "Zbroja"),
            new GlossaryTerm("Armour", "Pancerz"));

        Assert.True(service.TryTranslateExact("Armour", out var translation));
        Assert.Equal("Pancerz", translation);
        Assert.True(GlossaryPrecedence.Replaces(new GlossaryTerm("a", "1"), new GlossaryTerm("a", "2")));
        Assert.False(GlossaryPrecedence.Replaces(
            new GlossaryTerm("a", "1", CaseSensitive: true), new GlossaryTerm("a", "2")));
    }
}
