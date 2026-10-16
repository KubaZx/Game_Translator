using GameTranslatorOverlay.Core.Profiles;

namespace GameTranslatorOverlay.Core.Tests;

public class UserProfileRulesTests
{
    [Theory]
    [InlineData("Mroczna Latarnia: Opowieść o Strażniku", "mroczna-latarnia-opowiesc-o-strazniku")]
    [InlineData("Zażółć gęślą jaźń", "zazolc-gesla-jazn")]
    [InlineData("  --Hello__World!!  ", "hello-world")]
    [InlineData("Straße der Æther-Ødyssey", "strasse-der-aether-odyssey")]
    [InlineData("Latarnia 2 (wydanie rocznicowe)", "latarnia-2-wydanie-rocznicowe")]
    [InlineData("日本語", "")]
    [InlineData("", "")]
    public void Slug_ma_tylko_male_litery_ASCII_cyfry_i_myslniki(string name, string expected)
    {
        Assert.Equal(expected, ProfileIds.Slugify(name));
    }

    [Fact]
    public void Dlugi_slug_jest_skracany_bez_myslnika_na_koncu()
    {
        var slug = ProfileIds.Slugify("Mroczna Latarnia Opowieść o Strażniku Świateł Północy Rozdział Drugi");

        Assert.True(slug.Length <= ProfileIds.MaxLength, slug);
        Assert.False(slug.EndsWith('-'));
        Assert.Equal("mroczna-latarnia-opowiesc-o-strazniku-swiatel", slug);
    }

    [Fact]
    public void Slug_ciety_tuz_przed_myslnikiem_zachowuje_cale_ostatnie_slowo()
    {
        var name = new string('a', ProfileIds.MaxLength - 6) + " bbbbb cc";

        Assert.Equal(new string('a', ProfileIds.MaxLength - 6) + "-bbbbb", ProfileIds.Slugify(name));
    }

    [Fact]
    public void Zajety_identyfikator_dostaje_kolejny_numer_bez_wzgledu_na_wielkosc_liter()
    {
        var id = ProfileIds.CreateUnique("Mroczna Latarnia", "latarnia.exe", ["Mroczna-Latarnia", "mroczna-latarnia-2"]);

        Assert.Equal("mroczna-latarnia-3", id);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("Com1")]
    public void Zarezerwowana_nazwa_urzadzenia_Windows_nie_jest_folderem_profilu(string name)
    {
        var id = ProfileIds.CreateUnique(name, "latarnia.exe", []);

        Assert.Equal(name.ToLowerInvariant() + "-2", id);
    }

    [Fact]
    public void Nazwa_bez_liter_lacinskich_bierze_identyfikator_z_procesu()
    {
        Assert.Equal("latarnia", ProfileIds.CreateUnique("日本語", "Latarnia.exe", []));
        Assert.Equal(ProfileIds.Fallback, ProfileIds.CreateUnique("日本語", "日本.exe", []));
        Assert.Equal(ProfileIds.Fallback + "-2", ProfileIds.CreateUnique(null, null, [ProfileIds.Fallback]));
    }

    [Fact]
    public void Numer_przy_dlugim_identyfikatorze_miesci_sie_w_limicie()
    {
        var name = new string('a', 60);
        var first = ProfileIds.CreateUnique(name, null, []);

        var second = ProfileIds.CreateUnique(name, null, [first]);

        Assert.Equal(ProfileIds.MaxLength, first.Length);
        Assert.True(second.Length <= ProfileIds.MaxLength);
        Assert.EndsWith("-2", second);
    }

    [Theory]
    [InlineData("Mroczna Latarnia: Opowieść o Strażniku")]
    [InlineData("CON")]
    [InlineData("  ")]
    [InlineData("A/B\\C:D*E?F\"G<H>I|J")]
    public void Identyfikator_przechodzi_walidacje_profilu(string name)
    {
        var profile = new GameProfile
        {
            Id = ProfileIds.CreateUnique(name, "latarnia.exe", []),
            Name = "Mroczna Latarnia",
            SourceLanguage = "en",
        };

        Assert.Empty(ProfileValidator.Validate(profile));
    }

    [Fact]
    public void Nazwa_z_tytulu_okna_jest_przycinana_na_granicy_slowa()
    {
        var title = "Mroczna Latarnia: Opowieść o Strażniku Świateł Północy — Rozdział 2 — Wydanie rozszerzone z dodatkami";

        var name = ProfileNames.SuggestFromWindow(title, "latarnia.exe");

        Assert.True(name.Length <= ProfileNames.MaxLength, name);
        Assert.StartsWith("Mroczna Latarnia: Opowieść", name);
        Assert.False(name.EndsWith(' '));
        Assert.Null(ProfileNames.Validate(name, []));
    }

    [Theory]
    [InlineData("  Mroczna   Latarnia  ", "latarnia.exe", "Mroczna Latarnia")]
    [InlineData("", "Latarnia.exe", "Latarnia")]
    [InlineData(null, null, "")]
    public void Nazwa_z_okna_ma_uporzadkowane_odstepy(string? title, string? process, string expected)
    {
        Assert.Equal(expected, ProfileNames.SuggestFromWindow(title, process));
    }

    [Fact]
    public void Walidacja_nazwy_odrzuca_pusta_dluga_i_zajeta()
    {
        Assert.NotNull(ProfileNames.Validate("   ", []));
        Assert.NotNull(ProfileNames.Validate(new string('x', ProfileNames.MaxLength + 1), []));
        Assert.NotNull(ProfileNames.Validate("Zła\u0007nazwa", []));
        var duplicate = ProfileNames.Validate(" mroczna  LATARNIA ", ["Mroczna Latarnia"]);
        Assert.NotNull(duplicate);
        Assert.Contains("mroczna LATARNIA", duplicate);
        Assert.Null(ProfileNames.Validate("Mroczna Latarnia 2", ["Mroczna Latarnia"]));
    }
}
