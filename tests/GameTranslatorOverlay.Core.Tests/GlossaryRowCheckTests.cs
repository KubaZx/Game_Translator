using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Profiles;

namespace GameTranslatorOverlay.Core.Tests;

public sealed class GlossaryRowCheckTests
{
    [Fact]
    public void Wiersz_z_terminem_bez_tlumaczenia_blokuje_zapis()
    {
        var problem = GlossaryRowCheck.FirstProblem([("Sword", "Miecz"), ("Shield", "  ")]);

        Assert.NotNull(problem);
        Assert.Equal(1, problem!.Index);
        Assert.Contains("Shield", problem.Message);
        Assert.Contains("nie ma tłumaczenia", problem.Message);
    }

    [Fact]
    public void Wiersz_z_tlumaczeniem_bez_terminu_blokuje_zapis()
    {
        var problem = GlossaryRowCheck.FirstProblem([(null, "Miecz")]);

        Assert.Equal(0, problem!.Index);
        Assert.Contains("nie ma terminu", problem.Message);
    }

    [Fact]
    public void Pusty_wiersz_i_pelne_wiersze_przechodza()
    {
        Assert.Null(GlossaryRowCheck.FirstProblem([("Sword", "Miecz"), ("", ""), (null, null)]));
    }
}

public sealed class ProfileGlossaryFieldTests
{
    private static GameProfile Profile(string? glossary) => new()
    {
        Id = "test-gra",
        Name = "Testowa gra",
        ProfileVersion = 1,
        SourceLanguage = "en",
        Glossary = glossary,
    };

    [Theory]
    [InlineData("..\\..\\poza")]
    [InlineData("../poza")]
    [InlineData("\\\\serwer\\udzial")]
    [InlineData("C:\\temp")]
    [InlineData("a b")]
    public void Walidator_odrzuca_pole_glossary_spoza_reguly_identyfikatora(string glossary)
    {
        var errors = ProfileValidator.Validate(Profile(glossary));

        Assert.Contains(errors, static e => e.Contains("glossary", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("path-of-exile-2")]
    [InlineData("moja_gra.v2")]
    public void Walidator_przepuszcza_poprawne_pole_glossary(string? glossary)
    {
        Assert.Empty(ProfileValidator.Validate(Profile(glossary)));
    }
}
