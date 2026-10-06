using System.Text.Json;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.CorpusTool.Safety;
using GameTranslatorOverlay.CorpusTool.Unity;

namespace GameTranslatorOverlay.CorpusTool.Tests;

public sealed class CorpusExtractorTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _game;
    private readonly string _profileFile;

    public CorpusExtractorTests()
    {
        _game = _temp.Combine("Synthetic Game");
        var serialized = SyntheticUnity.SerializedFile(RecipeRunnerTests.Assets()
            .Select(static a => new SyntheticAsset(a.Name, a.Script)).ToList());
        _temp.WriteFile("Synthetic Game/Game_Data/data.unity3d",
            SyntheticUnity.Bundle([new SyntheticUnity.BundleFile("resources.assets", serialized)], blockSize: 128));
        _temp.WriteFile("Synthetic Game/Synthetic Game.exe", [0x4D, 0x5A]);
        _profileFile = WriteProfile(new GameProfile
        {
            Id = "synthetic-game",
            Name = "Synthetic Game",
            ProcessNames = ["Synthetic Game.exe"],
            SourceLanguage = "en",
            Corpus = RecipeRunnerTests.Recipe(),
        });
    }

    public void Dispose() => _temp.Dispose();

    private string WriteProfile(GameProfile profile, string name = "profile.json")
    {
        var path = _temp.Combine("profiles", profile.Id, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ProfileSerializer.ToJson(profile));
        return path;
    }

    private ExtractOptions Options(string? output = null) => new()
    {
        Command = "extract",
        ProfileId = "synthetic-game",
        ProfilesDirectory = _temp.Combine("profiles"),
        GameDirectory = _game,
        DataDirectory = _temp.Combine("data"),
        OutputPath = output,
        StatsPath = _temp.Combine("stats", "stats.json"),
    };

    private Dictionary<string, (long Size, DateTime Written)> Snapshot() =>
        Directory.EnumerateFiles(_game, "*", SearchOption.AllDirectories)
            .ToDictionary(static f => f, static f => (new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)));

    [Fact]
    public void Ekstrakcja_zapisuje_korpus_poza_gra_i_niczego_w_grze_nie_zmienia()
    {
        var before = Snapshot();

        var report = new CorpusExtractor(new FakeProcessLister("explorer")).Run(Options());

        Assert.Equal(before, Snapshot());
        Assert.Equal(Path.Combine(_temp.Combine("data"), "corpus", "synthetic-game.corpus.jsonl"), report.OutputPath);
        var entries = CorpusJsonl.ReadFile(report.OutputPath);
        Assert.Equal(report.Entries, entries.Count);
        Assert.Contains(entries, static e => e.Kind == CorpusEntryKind.Subtitle && e.Speaker == "MIRA");
        Assert.Contains(entries, static e => e.Kind == CorpusEntryKind.Dialog && e.Node == "Talk/Start");
        Assert.Equal("unityfs", report.ContainerKind);
        Assert.Equal(7, report.TextAssets);
        Assert.Equal(3, report.Kinds.Count);

        var stats = File.ReadAllText(_temp.Combine("stats", "stats.json"));
        Assert.DoesNotContain("Hello", stats);
        Assert.Equal(report.Entries, JsonDocument.Parse(stats).RootElement.GetProperty("entries").GetInt32());
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(report.OutputPath)!, "*.tmp"));
    }

    [Fact]
    public void Uruchomiona_gra_blokuje_ekstrakcje()
    {
        var error = Assert.Throws<RefusedException>(() => new CorpusExtractor(new FakeProcessLister("Synthetic Game")).Run(Options()));

        Assert.Contains("uruchomiona", error.Message);
        Assert.False(File.Exists(Path.Combine(_temp.Combine("data"), "corpus", "synthetic-game.corpus.jsonl")));
    }

    [Fact]
    public void Wynik_w_folderze_gry_jest_odrzucany()
    {
        var before = Snapshot();

        Assert.Throws<RefusedException>(() =>
            new CorpusExtractor(new FakeProcessLister()).Run(Options(Path.Combine(_game, "corpus.jsonl"))));
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Statystyki_w_folderze_gry_sa_odrzucane()
    {
        var options = Options() with { StatsPath = Path.Combine(_game, "stats.json") };

        Assert.Throws<RefusedException>(() => new CorpusExtractor(new FakeProcessLister()).Run(options));
        Assert.False(File.Exists(Path.Combine(_game, "stats.json")));
    }

    [Fact]
    public void Anti_cheat_w_folderze_gry_blokuje_ekstrakcje()
    {
        _temp.WriteFile("Synthetic Game/EasyAntiCheat/Settings.json", [0]);

        var error = Assert.Throws<RefusedException>(() => new CorpusExtractor(new FakeProcessLister()).Run(Options()));

        Assert.Contains("ADR-014", error.Message);
    }

    [Fact]
    public void Uruchomiona_gra_jest_sprawdzana_przed_czytaniem_naglowkow_kontenerow()
    {
        _temp.WriteFile("Synthetic Game/Paks/secret.pak", GameFolderGuardTests.PakTail(encrypted: true));

        var error = Assert.Throws<RefusedException>(() => new CorpusExtractor(new FakeProcessLister("Synthetic Game")).Run(Options()));

        Assert.Contains("uruchomiona", error.Message);
        Assert.DoesNotContain("zaszyfrowany", error.Message);
    }

    private ExtractOptions SubfolderOptions(string root, string? output = null, string? stats = null)
    {
        var serialized = SyntheticUnity.SerializedFile(RecipeRunnerTests.Assets()
            .Select(static a => new SyntheticAsset(a.Name, a.Script)).ToList());
        _temp.WriteFile(root + "/Game_Data/data.unity3d",
            SyntheticUnity.Bundle([new SyntheticUnity.BundleFile("resources.assets", serialized)], blockSize: 128));
        _temp.WriteFile(root + "/Synthetic Game.exe", [0x4D, 0x5A]);
        var recipe = RecipeRunnerTests.Recipe();
        recipe.Container = "data.unity3d";
        WriteProfile(new GameProfile
        {
            Id = "synthetic-sub",
            Name = "Synthetic Game",
            ProcessNames = ["Synthetic Game.exe"],
            SourceLanguage = "en",
            Corpus = recipe,
        });
        return Options(output) with
        {
            ProfileId = "synthetic-sub",
            GameDirectory = _temp.Combine([.. root.Split('/'), "Game_Data"]),
            StatsPath = stats ?? _temp.Combine("stats", "stats.json"),
        };
    }

    [Fact]
    public void Folder_gry_wskazany_podfolderem_dziala()
    {
        var report = new CorpusExtractor(new FakeProcessLister("explorer")).Run(SubfolderOptions("steamapps/common/Synthetic Game"));

        Assert.True(report.Entries > 0);
    }

    [Theory]
    [InlineData("steamapps/common/Synthetic Game", "steamapps/common/Synthetic Game/EasyAntiCheat/Settings.json")]
    [InlineData("Games/Synthetic Game", "Games/Synthetic Game/BattlEye/BEClient_x64.dll")]
    public void Anti_cheat_w_folderze_nadrzednym_gry_blokuje_ekstrakcje(string root, string marker)
    {
        var options = SubfolderOptions(root);
        _temp.WriteFile(marker, [0]);

        var error = Assert.Throws<RefusedException>(() => new CorpusExtractor(new FakeProcessLister()).Run(options));

        Assert.Contains("anti-cheat", error.Message);
        Assert.False(File.Exists(options.ResolveOutputPath("synthetic-sub")));
    }

    [Fact]
    public void Anti_cheat_innej_gry_w_bibliotece_nie_blokuje()
    {
        var options = SubfolderOptions("steamapps/common/Synthetic Game");
        _temp.WriteFile("steamapps/common/Other Game/EasyAntiCheat/Settings.json", [0]);

        Assert.True(new CorpusExtractor(new FakeProcessLister()).Run(options).Entries > 0);
    }

    [Theory]
    [InlineData("steamapps/common/Synthetic Game/corpus.jsonl", null)]
    [InlineData("steamapps/common/Other Game/corpus.jsonl", null)]
    [InlineData(null, "steamapps/common/Synthetic Game/stats.json")]
    [InlineData(null, "steamapps/common/stats.json")]
    [InlineData("Games/Synthetic Game/corpus.jsonl", null)]
    public void Wynik_i_statystyki_pod_steamapps_common_i_w_katalogu_gry_sa_odrzucane(string? output, string? stats)
    {
        var root = (output ?? stats)!.StartsWith("Games/", StringComparison.Ordinal) ? "Games/Synthetic Game" : "steamapps/common/Synthetic Game";
        var outputPath = output is null ? null : _temp.Combine(output.Split('/'));
        var statsPath = stats is null ? null : _temp.Combine(stats.Split('/'));
        var options = SubfolderOptions(root, outputPath, statsPath);

        Assert.Throws<RefusedException>(() => new CorpusExtractor(new FakeProcessLister()).Run(options));
        Assert.False(outputPath is not null && File.Exists(outputPath));
        Assert.False(statsPath is not null && File.Exists(statsPath));
    }

    [Fact]
    public void Profil_wykluczony_i_profil_online_sa_odrzucane()
    {
        WriteProfile(new GameProfile { Id = "path-of-exile-2", Name = "PoE2", SourceLanguage = "en", Corpus = RecipeRunnerTests.Recipe() });
        WriteProfile(new GameProfile { Id = "online-game", Name = "Online", SourceLanguage = "en", Online = true, Corpus = RecipeRunnerTests.Recipe() });

        Assert.Throws<RefusedException>(() => new CorpusExtractor(new FakeProcessLister()).Run(Options() with { ProfileId = "path-of-exile-2" }));
        Assert.Throws<RefusedException>(() => new CorpusExtractor(new FakeProcessLister()).Run(Options() with { ProfileId = "online-game" }));
    }

    [Fact]
    public void Profil_bez_recepty_brak_profilu_i_zly_format_sa_odrzucane()
    {
        WriteProfile(new GameProfile { Id = "no-recipe", Name = "No recipe", SourceLanguage = "en" });
        var otherFormat = RecipeRunnerTests.Recipe();
        otherFormat.Format = "unreal-locres";
        WriteProfile(new GameProfile { Id = "unreal", Name = "Unreal", SourceLanguage = "en", Corpus = otherFormat });
        var missingContainer = RecipeRunnerTests.Recipe();
        missingContainer.Container = "Game_Data/none.unity3d";
        WriteProfile(new GameProfile { Id = "missing", Name = "Missing", SourceLanguage = "en", Corpus = missingContainer });

        Assert.Throws<RefusedException>(() => new CorpusExtractor(new FakeProcessLister()).Run(Options() with { ProfileId = "no-recipe" }));
        Assert.Throws<RefusedException>(() => new CorpusExtractor(new FakeProcessLister()).Run(Options() with { ProfileId = "unknown-id" }));
        Assert.Throws<RefusedException>(() => new CorpusExtractor(new FakeProcessLister()).Run(Options() with { ProfileId = "unreal" }));
        Assert.Throws<RefusedException>(() => new CorpusExtractor(new FakeProcessLister()).Run(Options() with { ProfileId = "missing" }));
    }

    [Fact]
    public void Niepoprawna_recepta_jest_odrzucana_przed_odczytem()
    {
        var escaping = RecipeRunnerTests.Recipe();
        escaping.Container = "../outside.unity3d";
        var file = WriteProfile(new GameProfile { Id = "escaping", Name = "Escaping", SourceLanguage = "en", Corpus = escaping });

        var error = Assert.Throws<RefusedException>(() =>
            new CorpusExtractor(new FakeProcessLister()).Run(Options() with { ProfileId = null, ProfileFile = file }));

        Assert.Contains("niepoprawny", error.Message);
    }

    [Fact]
    public void Zaszyfrowany_kontener_unity_daje_odmowe()
    {
        var serialized = SyntheticUnity.SerializedFile([new SyntheticAsset("Strings_Main", [1])]);
        _temp.WriteFile("Synthetic Game/Game_Data/data.unity3d",
            SyntheticUnity.Bundle([new SyntheticUnity.BundleFile("resources.assets", serialized)], extraFlags: 0x1000));

        Assert.Throws<RefusedException>(() => new CorpusExtractor(new FakeProcessLister()).Run(Options()));
    }

    [Fact]
    public void Raport_liczy_unikalne_teksty_bez_wielkosci_liter()
    {
        var report = CorpusExtractor.BuildReport("p", "out", new UnityReadResult("serialized", "2020", 22, 3, []),
            new Parsing.RecipeResult(
            [
                new CorpusEntry { Key = "a", En = "Hello", Kind = CorpusEntryKind.Ui, Source = "s" },
                new CorpusEntry { Key = "b", En = "HELLO", Kind = CorpusEntryKind.Ui, Source = "s" },
                new CorpusEntry { Key = "c", En = "Bye", Kind = CorpusEntryKind.Subtitle, Speaker = "Ann", Source = "s" },
            ], []), 1, 2, 3);

        Assert.Equal(3, report.Entries);
        Assert.Equal(2, report.UniqueTexts);
        Assert.Equal(8, report.UniqueCharacters);
        Assert.Equal(1, report.Speakers);
        Assert.Equal(1, report.Kinds["ui"].UniqueTexts);
    }
}

public class ExtractOptionsTests
{
    [Fact]
    public void Bez_argumentow_i_z_help_pokazuje_pomoc()
    {
        Assert.True(ExtractOptions.Parse([]).Help);
        Assert.True(ExtractOptions.Parse(["extract", "--help"]).Help);
    }

    [Fact]
    public void Czyta_wszystkie_opcje_i_domyslna_sciezke_wyniku()
    {
        var options = ExtractOptions.Parse(["extract", "--profile", "escape-academy", "--game-dir", "G", "--data-dir", "D", "--stats", "S", "--profiles-dir", "P"]);

        Assert.Equal("escape-academy", options.ProfileId);
        Assert.Equal("G", options.GameDirectory);
        Assert.Equal("S", options.StatsPath);
        Assert.Equal("P", options.ProfilesDirectory);
        Assert.Equal(Path.GetFullPath(Path.Combine("D", "corpus", "escape-academy.corpus.jsonl")), options.ResolveOutputPath("escape-academy"));
        Assert.Equal(Path.GetFullPath("x.jsonl"), (options with { OutputPath = "x.jsonl" }).ResolveOutputPath("escape-academy"));
        Assert.EndsWith("GameTranslatorOverlay", ExtractOptions.DefaultDataDirectory);
    }

    [Theory]
    [InlineData("extract", "--profile")]
    [InlineData("extract", "--game-dir", "G")]
    [InlineData("extract", "--profile", "x")]
    [InlineData("translate", "--profile", "x", "--game-dir", "G")]
    [InlineData("--profile", "x", "--game-dir", "G")]
    [InlineData("extract", "--profile", "x", "--game-dir", "G", "--bogus", "1")]
    public void Bledne_argumenty_daja_wyjatek(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ExtractOptions.Parse(args));
    }

    [Fact]
    public void Plik_profilu_zastepuje_identyfikator()
    {
        var options = ExtractOptions.Parse(["extract", "--profile-file", "p.json", "--game-dir", "G", "--out", "o.jsonl"]);

        Assert.Equal("p.json", options.ProfileFile);
        Assert.Equal("o.jsonl", options.OutputPath);
    }
}
