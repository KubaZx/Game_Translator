using GameTranslatorOverlay.Core.Profiles;

namespace GameTranslatorOverlay.Core.Tests;

public class ProfileCorpusRecipeTests
{
    private static GameProfile WithRecipe(CorpusRecipe recipe) => new()
    {
        Id = "synthetic-game",
        Name = "Synthetic Game",
        SourceLanguage = "en",
        Corpus = recipe,
    };

    private static CorpusRecipe ValidRecipe() => new()
    {
        Format = "unity-textasset",
        Container = "Game_Data/data.unity3d",
        File = "resources.assets",
        Sources =
        [
            new CorpusSourceRecipe { Id = "strings", Parser = "csv", Kind = "ui", Include = ["Strings*"], KeyColumn = "Key", TextColumn = "Text" },
            new CorpusSourceRecipe { Id = "subs", Parser = "srt", Kind = "subtitle", Include = ["*"], SpeakerPattern = @"^\[(?<speaker>[^\]]+)\]\s*" },
        ],
    };

    [Fact]
    public void Stary_profil_bez_sekcji_korpusu_dalej_dziala()
    {
        var profile = ProfileSerializer.FromJson("""{"id":"old","name":"Old","sourceLanguage":"en","processNames":["Old.exe"]}""");

        Assert.Null(profile.Corpus);
        Assert.Null(profile.Online);
        Assert.Empty(ProfileValidator.Validate(profile));
        Assert.DoesNotContain("corpus", ProfileSerializer.ToJson(profile));
    }

    [Fact]
    public void Profil_z_recepta_czyta_sie_z_json()
    {
        const string json = """
            {
              "id": "synthetic-game", "name": "Synthetic", "sourceLanguage": "en", "online": false,
              "corpus": {
                "format": "unity-textasset", "container": "Game_Data/data.unity3d", "file": "resources.assets",
                "sources": [
                  { "id": "dialog", "parser": "csv", "kind": "dialog", "include": ["* (en-US)"],
                    "keyColumn": "id", "textColumn": "text", "nodeColumns": ["file", "node"], "orderColumn": "lineNumber",
                    "speakerPattern": "^(?<speaker>[^:]{0,40}):\\s*", "stripRichText": false },
                  { "id": "subs", "parser": "srt", "kind": "subtitle", "include": ["*"], "exclude": ["*_fr"], "inheritSpeaker": true }
                ]
              }
            }
            """;

        var profile = ProfileSerializer.FromJson(json);

        Assert.False(profile.Online);
        var recipe = Assert.IsType<CorpusRecipe>(profile.Corpus);
        Assert.Equal(2, recipe.Sources.Count);
        Assert.Equal(["file", "node"], recipe.Sources[0].NodeColumns);
        Assert.False(recipe.Sources[0].StripRichText);
        Assert.True(recipe.Sources[1].StripRichText);
        Assert.True(recipe.Sources[1].InheritSpeaker);
        Assert.Equal(["*_fr"], recipe.Sources[1].Exclude);
        Assert.Empty(ProfileValidator.Validate(profile));
    }

    [Fact]
    public void Poprawna_recepta_przechodzi_walidacje()
    {
        Assert.Empty(ProfileValidator.Validate(WithRecipe(ValidRecipe())));
    }

    [Theory]
    [InlineData("C:/Games/data.unity3d")]
    [InlineData("/etc/data")]
    [InlineData("\\data.unity3d")]
    [InlineData("../outside/data.unity3d")]
    [InlineData("Game_Data/../../data")]
    public void Kontener_poza_folderem_gry_jest_bledem(string container)
    {
        var recipe = ValidRecipe();
        recipe.Container = container;

        Assert.NotEmpty(ProfileValidator.Validate(WithRecipe(recipe)));
    }

    [Fact]
    public void Brak_formatu_kontenera_i_zrodel_daje_bledy()
    {
        var errors = ProfileValidator.Validate(WithRecipe(new CorpusRecipe()));

        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void Pusty_plik_wewnatrz_kontenera_jest_bledem()
    {
        var recipe = ValidRecipe();
        recipe.File = "";

        Assert.NotEmpty(ProfileValidator.Validate(WithRecipe(recipe)));
    }

    [Fact]
    public void Bledne_zrodla_sa_raportowane_osobno()
    {
        var recipe = ValidRecipe();
        recipe.Sources =
        [
            new CorpusSourceRecipe { Id = "", Parser = "xml", Kind = "menu", Include = [] },
            new CorpusSourceRecipe { Id = "dup", Parser = "csv", Kind = "ui", Include = ["*"] },
            new CorpusSourceRecipe { Id = "dup", Parser = "srt", Kind = "subtitle", Include = ["*"], SpeakerPattern = "(" },
            new CorpusSourceRecipe { Id = "nogroup", Parser = "srt", Kind = "subtitle", Include = ["*"], SpeakerPattern = "^x" },
        ];

        var errors = ProfileValidator.Validate(WithRecipe(recipe));

        Assert.Contains(errors, static e => e.Contains("identyfikatora"));
        Assert.Contains(errors, static e => e.Contains("parser"));
        Assert.Contains(errors, static e => e.Contains("rodzaj"));
        Assert.Contains(errors, static e => e.Contains("include"));
        Assert.Contains(errors, static e => e.Contains("textColumn"));
        Assert.Contains(errors, static e => e.Contains("powtarza"));
        Assert.Contains(errors, static e => e.Contains("wyrażeniem"));
        Assert.Contains(errors, static e => e.Contains("grupę"));
    }

    [Fact]
    public void Dostarczony_profil_escape_academy_ma_poprawna_recepte()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "profiles", "escape-academy", "profile.json"));
        Assert.True(File.Exists(path), path);

        var profile = ProfileSerializer.FromJson(File.ReadAllText(path));

        Assert.Empty(ProfileValidator.Validate(profile));
        Assert.Equal(["Escape Academy.exe"], profile.ProcessNames);
        Assert.False(profile.Online);
        Assert.NotNull(profile.Corpus);
        Assert.Equal(["ui", "dialog", "subtitle"], profile.Corpus.Sources.Select(static s => s.Kind));
        Assert.Null(profile.Ocr);
        Assert.Null(profile.ChangeDetection);
    }
}
