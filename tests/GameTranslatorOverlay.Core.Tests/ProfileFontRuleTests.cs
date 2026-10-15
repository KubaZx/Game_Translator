using GameTranslatorOverlay.Core.Profiles;

namespace GameTranslatorOverlay.Core.Tests;

public class ProfileFontRuleTests
{
    private static OverlayFontRule Rule(string? color = "#FBFA86", int tolerance = 40, bool? outlined = true, string? family = "Slab") =>
        new() { FontFamily = family, TextColor = color, ColorTolerance = tolerance, Outlined = outlined };

    private static OverlayFontRuleCheck Check(params OverlayFontRule[] rules) =>
        OverlayFontRuleSet.Validate(new OverlayProfileSettings { FontFamily = "Baza", FontRules = [.. rules] });

    [Fact]
    public void Reguly_kroju_sa_czytane_z_profilu()
    {
        var profile = ProfileSerializer.FromJson("""
            { "id": "gra", "name": "Gra", "sourceLanguage": "en",
              "overlay": { "fontFamily": "Baza",
                "fontRules": [ { "fontFamily": "Slab", "textColor": "#fbfa86", "colorTolerance": 25, "outlined": true,
                                 "minLetterHeight": 0.02, "maxLetterHeight": 0.05 } ] } }
            """);

        var rule = Assert.Single(profile.Overlay!.FontRules!);
        Assert.Equal("Slab", rule.FontFamily);
        Assert.Equal("#fbfa86", rule.TextColor);
        Assert.Equal(25, rule.ColorTolerance);
        Assert.True(rule.Outlined);
        Assert.Equal(0.02, rule.MinLetterHeight);
        Assert.Equal(0.05, rule.MaxLetterHeight);
        Assert.Empty(ProfileValidator.Validate(profile));
        var check = OverlayFontRuleSet.Validate(profile.Overlay);
        Assert.Same(rule, Assert.Single(check.Rules));
        Assert.Empty(check.Warnings);
    }

    [Fact]
    public void Domyslna_tolerancja_koloru_to_40()
    {
        var profile = ProfileSerializer.FromJson("""
            { "id": "gra", "name": "Gra", "overlay": { "fontRules": [ { "fontFamily": "Slab", "textColor": "#FFFF00" } ] } }
            """);

        Assert.Equal(OverlayFontRule.DefaultColorTolerance, Assert.Single(profile.Overlay!.FontRules!).ColorTolerance);
        Assert.Equal(40, OverlayFontRule.DefaultColorTolerance);
    }

    [Theory]
    [InlineData("#FBFA86", 0xFBFA86)]
    [InlineData("fbfa86", 0xFBFA86)]
    [InlineData(" #000000 ", 0x000000)]
    public void Kolor_zapisany_szesnastkowo_jest_czytany(string text, int expected)
    {
        Assert.True(OverlayFontRuleSet.TryParseColor(text, out var rgb));
        Assert.Equal(expected, rgb);
    }

    [Theory]
    [InlineData("#FBFA8")]
    [InlineData("#FBFA866")]
    [InlineData("#GGFA86")]
    [InlineData("yellow")]
    [InlineData("")]
    public void Zly_zapis_koloru_nie_jest_czytany(string text)
    {
        Assert.False(OverlayFontRuleSet.TryParseColor(text, out var rgb));
        Assert.Equal(-1, rgb);
    }

    [Fact]
    public void Zla_regula_jest_pomijana_z_ostrzezeniem_a_profil_zostaje_poprawny()
    {
        var good = Rule();
        var profile = new GameProfile
        {
            Id = "gra",
            Name = "Gra",
            Overlay = new OverlayProfileSettings { FontFamily = "Baza", FontRules = [Rule(color: "#XYZ"), good] },
        };

        Assert.Empty(ProfileValidator.Validate(profile));
        var check = OverlayFontRuleSet.Validate(profile.Overlay);

        Assert.Same(good, Assert.Single(check.Rules));
        var warning = Assert.Single(check.Warnings);
        Assert.Contains("overlay.fontRules[1]", warning, StringComparison.Ordinal);
        Assert.Contains("#XYZ", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void Tolerancja_poza_zakresem_odrzuca_regule(int tolerance)
    {
        var check = Check(Rule(tolerance: tolerance));

        Assert.Empty(check.Rules);
        Assert.Contains("colorTolerance", Assert.Single(check.Warnings), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("C:\\Fonts\\slab.ttf")]
    [InlineData("./#Slab")]
    public void Regula_bez_poprawnej_nazwy_kroju_jest_odrzucana(string? family)
    {
        var check = Check(Rule(family: family));

        Assert.Empty(check.Rules);
        Assert.Contains("fontFamily", Assert.Single(check.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Regula_bez_warunku_jest_odrzucana()
    {
        var check = Check(Rule(color: null, outlined: null));

        Assert.Empty(check.Rules);
        Assert.Contains("warunek", Assert.Single(check.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Pusta_regula_jest_odrzucana()
    {
        var check = Check(null!, Rule());

        Assert.Single(check.Rules);
        Assert.Contains("overlay.fontRules[1]", Assert.Single(check.Warnings), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0, null)]
    [InlineData(null, 1.5)]
    [InlineData(0.06, 0.03)]
    public void Wysokosc_liter_poza_zakresem_odrzuca_regule(double? min, double? max)
    {
        var rule = Rule();
        rule.MinLetterHeight = min;
        rule.MaxLetterHeight = max;

        var check = Check(rule);

        Assert.Empty(check.Rules);
        Assert.Contains("LetterHeight", Assert.Single(check.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Nieznany_kroj_reguly_jest_pomijany_z_ostrzezeniem()
    {
        var known = Rule(family: "Slab");
        var unknown = Rule(family: "Nie Ma Takiego");
        var overlay = new OverlayProfileSettings { FontFamily = "Baza", FontRules = [unknown, known] };

        var check = OverlayFontRuleSet.Validate(overlay, name => name is "Slab" or "Baza");

        Assert.Same(known, Assert.Single(check.Rules));
        var warning = Assert.Single(check.Warnings);
        Assert.Contains("Nie Ma Takiego", warning, StringComparison.Ordinal);
        Assert.Contains("overlay.fontRules[1]", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Nieznany_kroj_bazowy_daje_ostrzezenie_bez_odrzucania_regul()
    {
        var overlay = new OverlayProfileSettings { FontFamily = "Brak", FontRules = [Rule()] };

        var check = OverlayFontRuleSet.Validate(overlay, name => name == "Slab");

        Assert.Single(check.Rules);
        Assert.Contains("overlay.fontFamily", Assert.Single(check.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Brak_sekcji_nakladki_lub_regul_to_brak_regul_i_ostrzezen()
    {
        Assert.Empty(OverlayFontRuleSet.Validate(null).Rules);
        Assert.Empty(OverlayFontRuleSet.Validate(null).Warnings);
        var check = OverlayFontRuleSet.Validate(new OverlayProfileSettings { FontFamily = "Baza" });
        Assert.Empty(check.Rules);
        Assert.Empty(check.Warnings);
    }

    [Fact]
    public void Nadmiarowe_reguly_sa_pomijane()
    {
        var rules = Enumerable.Range(0, OverlayFontRuleSet.MaxRules + 2).Select(_ => Rule()).ToArray();

        var check = Check(rules);

        Assert.Equal(OverlayFontRuleSet.MaxRules, check.Rules.Count);
        Assert.Contains("nadmiarowe (2)", Assert.Single(check.Warnings), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xFBFA86, true)]
    [InlineData(0xD3D25E, true)]
    [InlineData(0xFFFFAE, true)]
    [InlineData(0xD3FA86, true)]
    [InlineData(0xD2FA86, false)]
    [InlineData(0xFBFAAF, false)]
    [InlineData(0xFBFA5D, false)]
    [InlineData(-1, false)]
    public void Kolor_pasuje_w_granicach_tolerancji_na_kazdym_kanale(int textRgb, bool expected)
    {
        Assert.Equal(expected, Rule().Matches(new TextStyleSample(textRgb, true, 0.04)));
    }

    [Fact]
    public void Tolerancja_zero_wymaga_dokladnego_koloru()
    {
        var rule = Rule(tolerance: 0);

        Assert.True(rule.Matches(new TextStyleSample(0xFBFA86, true, 0.04)));
        Assert.False(rule.Matches(new TextStyleSample(0xFBFA87, true, 0.04)));
    }

    [Fact]
    public void Warunek_obrysu_jest_sprawdzany_tylko_gdy_podany()
    {
        Assert.False(Rule(outlined: true).Matches(new TextStyleSample(0xFBFA86, false, 0.04)));
        Assert.True(Rule(outlined: false).Matches(new TextStyleSample(0xFBFA86, false, 0.04)));
        Assert.False(Rule(outlined: false).Matches(new TextStyleSample(0xFBFA86, true, 0.04)));
        Assert.True(Rule(outlined: null).Matches(new TextStyleSample(0xFBFA86, false, 0.04)));
        Assert.True(Rule(color: null, outlined: true).Matches(new TextStyleSample(0x123456, true, 0.04)));
    }

    [Theory]
    [InlineData(0.019, false)]
    [InlineData(0.02, true)]
    [InlineData(0.05, true)]
    [InlineData(0.051, false)]
    public void Wysokosc_liter_jest_sprawdzana_wlacznie_z_granicami(double height, bool expected)
    {
        var rule = Rule();
        rule.MinLetterHeight = 0.02;
        rule.MaxLetterHeight = 0.05;

        Assert.Equal(expected, rule.Matches(new TextStyleSample(0xFBFA86, true, height)));
    }

    [Fact]
    public void Wybor_rodziny_bierze_pierwsza_pasujaca_regule_albo_kroj_bazowy()
    {
        IReadOnlyList<OverlayFontRule> rules =
        [
            Rule(color: "#FF0000", family: "Czerwony"),
            Rule(family: "Slab"),
            Rule(color: null, outlined: true, family: "Obrys"),
        ];

        Assert.Equal("Slab", OverlayFontRuleSet.Resolve("Baza", rules, new TextStyleSample(0xFBFA86, true, 0.04)));
        Assert.Equal("Czerwony", OverlayFontRuleSet.Resolve("Baza", rules, new TextStyleSample(0xF01010, true, 0.04)));
        Assert.Equal("Obrys", OverlayFontRuleSet.Resolve("Baza", rules, new TextStyleSample(0xFAFAFA, true, 0.04)));
        Assert.Equal("Baza", OverlayFontRuleSet.Resolve("Baza", rules, new TextStyleSample(0xFAFAFA, false, 0.04)));
        Assert.Equal("Baza", OverlayFontRuleSet.Resolve("Baza", [], new TextStyleSample(0xFBFA86, true, 0.04)));
    }

    [Fact]
    public void Profil_escape_academy_rozpoznaje_styl_imienia_i_nie_lapie_innych_napisow()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "profiles", "escape-academy", "profile.json"));
        var profile = ProfileSerializer.FromJson(File.ReadAllText(path));

        Assert.Empty(ProfileValidator.Validate(profile));
        var check = OverlayFontRuleSet.Validate(profile.Overlay);
        Assert.Empty(check.Warnings);
        var rule = Assert.Single(check.Rules);
        Assert.Equal("Lexend Deca", profile.Overlay!.FontFamily);
        Assert.Equal("Zilla Slab", rule.FontFamily);

        string Family(int rgb, bool outlined) => OverlayFontRuleSet.Resolve("Lexend Deca", check.Rules, new TextStyleSample(rgb, outlined, 0.037));
        Assert.Equal("Zilla Slab", Family(0xFBFA86, true));
        Assert.Equal("Zilla Slab", Family(0xE5E47C, true));
        Assert.Equal("Lexend Deca", Family(0xFBFA86, false));
        Assert.Equal("Lexend Deca", Family(0xFAFADE, false));
        Assert.Equal("Lexend Deca", Family(0xFBFAC4, true));
        Assert.Equal("Lexend Deca", Family(0xFBFBFB, true));
        Assert.Equal("Lexend Deca", Family(0xF6F6F6, true));
        Assert.Equal("Lexend Deca", Family(0xCA1413, false));
        Assert.Equal("Lexend Deca", Family(0x0A0909, false));
    }

    [Fact]
    public void Kroj_z_regul_profilu_jest_dolaczony_z_licencja()
    {
        var fonts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GameTranslatorOverlay.App", "Fonts"));

        Assert.True(File.Exists(Path.Combine(fonts, "ZillaSlab-Bold.ttf")));
        Assert.Contains("SIL Open Font License", File.ReadAllText(Path.Combine(fonts, "ZillaSlab-OFL.txt")), StringComparison.Ordinal);
    }
}
