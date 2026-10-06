using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.CorpusTool.Safety;

namespace GameTranslatorOverlay.CorpusTool.Tests;

internal sealed class FakeProcessLister(params string[] names) : IProcessLister
{
    public IReadOnlyCollection<string> RunningProcessNames() => names;
}

public class GameFolderGuardTests
{
    private static GameProfile Profile(string id, bool? online = null) => new() { Id = id, Name = id, Online = online };

    [Theory]
    [InlineData("path-of-exile")]
    [InlineData("Path-Of-Exile-2")]
    public void Path_of_exile_jest_na_twardej_liscie_wykluczen(string id)
    {
        var violation = Assert.Single(GameFolderGuard.CheckProfile(Profile(id)));
        Assert.Equal(GameFolderGuard.ExcludedGame, violation.Code);
    }

    [Fact]
    public void Profil_oznaczony_online_jest_blokowany_a_zwykly_przechodzi()
    {
        Assert.Equal(GameFolderGuard.OnlineGame, Assert.Single(GameFolderGuard.CheckProfile(Profile("some-game", online: true))).Code);
        Assert.Empty(GameFolderGuard.CheckProfile(Profile("some-game", online: false)));
        Assert.Empty(GameFolderGuard.CheckProfile(Profile("some-game")));
    }

    [Fact]
    public void Czysty_folder_gry_przechodzi()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("Game/Game.exe", [1]);
        temp.WriteFile("Game/Game_Data/data.unity3d", [2]);
        temp.WriteFile("Game/Paks/plain.pak", PakTail(encrypted: false));
        temp.WriteFile("Game/Paks/plain.utoc", UtocHead(0x01));

        Assert.Empty(GameFolderGuard.CheckFolder(temp.Combine("Game")));
    }

    [Theory]
    [InlineData("Game/EasyAntiCheat/settings.json", GameFolderGuard.AntiCheat)]
    [InlineData("Game/BattlEye/BEClient_x64.dll", GameFolderGuard.AntiCheat)]
    [InlineData("Game/bin/BEService_x64.exe", GameFolderGuard.AntiCheat)]
    [InlineData("Game/start_protected_game.exe", GameFolderGuard.AntiCheat)]
    [InlineData("Game/Content/Paks/pakchunk0.sig", GameFolderGuard.SignedContainer)]
    [InlineData("Game/Content.ggpk", GameFolderGuard.ExcludedGame)]
    [InlineData("Game/Bundles2/_.index.bin", GameFolderGuard.ExcludedGame)]
    [InlineData("Game/PathOfExileSteam.exe", GameFolderGuard.ExcludedGame)]
    public void Zabezpieczenia_i_gry_wykluczone_sa_wykrywane(string file, string code)
    {
        using var temp = new TempDirectory();
        temp.WriteFile(file, [0]);

        Assert.Contains(GameFolderGuard.CheckFolder(temp.Combine("Game")), v => v.Code == code);
    }

    [Fact]
    public void Zaszyfrowany_indeks_pak_i_kontener_iostore_sa_blokowane()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("Game/Paks/secret.pak", PakTail(encrypted: true));
        temp.WriteFile("Game/Paks/secret.utoc", UtocHead(0x03));
        temp.WriteFile("Game/Paks/signed.utoc", UtocHead(0x05));

        var violations = GameFolderGuard.CheckFolder(temp.Combine("Game"));

        Assert.Equal(2, violations.Count(static v => v.Code == GameFolderGuard.EncryptedContainer));
        Assert.Single(violations, static v => v.Code == GameFolderGuard.SignedContainer);
    }

    [Fact]
    public void Folder_o_nazwie_path_of_exile_jest_blokowany()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("Path of Exile 2", "logs"));

        Assert.Contains(GameFolderGuard.CheckFolder(temp.Combine("Path of Exile 2")), static v => v.Code == GameFolderGuard.ExcludedGame);
    }

    [Fact]
    public void Katalog_glowny_gry_to_folder_w_bibliotece_albo_folder_z_plikiem_gry_z_profilu()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("SteamLibrary/steamapps/common/Game/Game_Data/data.unity3d", [1]);
        temp.WriteFile("Epic Games/Other/Content/data.pak", [1]);
        temp.WriteFile("Games/Plain/Plain.exe", [0]);
        temp.WriteFile("Games/Plain/Data/x/data.bin", [1]);

        Assert.Equal(temp.Combine("SteamLibrary", "steamapps", "common", "Game"),
            GameFolderGuard.ResolveGameRoot(temp.Combine("SteamLibrary", "steamapps", "common", "Game", "Game_Data")));
        Assert.Equal(temp.Combine("Epic Games", "Other"), GameFolderGuard.ResolveGameRoot(temp.Combine("Epic Games", "Other", "Content")));
        Assert.Equal(temp.Combine("Games", "Plain"), GameFolderGuard.ResolveGameRoot(temp.Combine("Games", "Plain", "Data", "x"), ["Plain.exe"]));
        Assert.Equal(temp.Combine("Games", "Plain", "Data"), GameFolderGuard.ResolveGameRoot(temp.Combine("Games", "Plain", "Data")));
        Assert.Equal(temp.Combine("SteamLibrary", "steamapps", "common"), GameFolderGuard.LibraryDirectory(temp.Combine("SteamLibrary", "steamapps", "common", "x.json")));
        Assert.Null(GameFolderGuard.LibraryDirectory(temp.Combine("steamapps", "workshop", "x.json")));
        Assert.Null(GameFolderGuard.LibraryGameRoot(temp.Combine("SteamLibrary", "steamapps", "common")));
    }

    [Fact]
    public void Folder_nadrzedny_gry_jest_sprawdzany_bez_powtarzania_wskazanego_folderu()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("steamapps/common/Game/EasyAntiCheat/settings.json", [0]);
        temp.WriteFile("steamapps/common/Game/Content/Paks/secret.pak", PakTail(encrypted: true));
        temp.WriteFile("steamapps/common/Game/Game_Data/bin/pakchunk0.sig", [0]);
        temp.WriteFile("steamapps/common/Other/BattlEye/BEClient_x64.dll", [0]);

        var violations = GameFolderGuard.CheckFolder(temp.Combine("steamapps", "common", "Game", "Game_Data"));

        Assert.Single(violations, static v => v.Code == GameFolderGuard.SignedContainer);
        Assert.Single(violations, static v => v.Code == GameFolderGuard.AntiCheat && v.Message.Contains("EasyAntiCheat", StringComparison.Ordinal));
        Assert.Single(violations, static v => v.Code == GameFolderGuard.EncryptedContainer);
        Assert.DoesNotContain(violations, static v => v.Message.Contains("BattlEye", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Brak_folderu_jest_zglaszany()
    {
        Assert.Equal("missing", Assert.Single(GameFolderGuard.CheckFolder(Path.Combine(Path.GetTempPath(), "gto-missing-" + Guid.NewGuid().ToString("N")))).Code);
    }

    [Fact]
    public void Wykrywanie_naglowkow_pracuje_na_bajtach()
    {
        Assert.True(GameFolderGuard.IsPakIndexEncrypted(PakTail(encrypted: true)));
        Assert.False(GameFolderGuard.IsPakIndexEncrypted(PakTail(encrypted: false)));
        Assert.False(GameFolderGuard.IsPakIndexEncrypted(new byte[64]));
        Assert.Null(GameFolderGuard.UtocProtection(new byte[100]));
        Assert.Null(GameFolderGuard.UtocProtection(UtocHead(0x09)));
    }

    internal static byte[] PakTail(bool encrypted)
    {
        var tail = new byte[221];
        var magic = 16 + 1;
        tail[magic - 1] = encrypted ? (byte)1 : (byte)0;
        tail[magic] = 0xE1;
        tail[magic + 1] = 0x12;
        tail[magic + 2] = 0x6F;
        tail[magic + 3] = 0x5A;
        tail[magic + 4] = 11;
        return tail;
    }

    internal static byte[] UtocHead(byte flags)
    {
        var head = new byte[144];
        "-==--==--==--==-"u8.CopyTo(head);
        head[16] = 8;
        head[80] = flags;
        return head;
    }
}

public class RunningGameGuardTests
{
    [Fact]
    public void Wykrywa_proces_z_profilu_i_exe_z_folderu_gry()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("Launcher.exe", [0]);
        temp.WriteFile("Data/Hidden.exe", [0]);
        var profile = new GameProfile { Id = "g", Name = "G", ProcessNames = ["Synthetic Game.exe"] };

        Assert.Equal(["Synthetic Game"], RunningGameGuard.FindRunning(profile, temp.Path, new FakeProcessLister("synthetic game", "explorer")));
        Assert.Equal(["Launcher"], RunningGameGuard.FindRunning(profile, temp.Path, new FakeProcessLister("Launcher.exe")));
        Assert.Empty(RunningGameGuard.FindRunning(profile, temp.Path, new FakeProcessLister("Hidden", "explorer")));
        Assert.Equal(2, RunningGameGuard.CandidateProcessNames(profile, temp.Path).Count);
    }

    [Fact]
    public void Wykrywa_exe_z_katalogu_glownego_gry()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("Game/Launcher.exe", [0]);
        temp.WriteFile("Game/Data/readme.txt", [0]);
        var profile = new GameProfile { Id = "g", Name = "G" };

        Assert.Empty(RunningGameGuard.FindRunning(profile, temp.Combine("Game", "Data"), new FakeProcessLister("Launcher")));
        Assert.Equal(["Launcher"], RunningGameGuard.FindRunning(profile, temp.Combine("Game", "Data"), new FakeProcessLister("Launcher"), temp.Combine("Game")));
    }

    [Fact]
    public void Systemowa_lista_procesow_zawiera_biezacy_proces()
    {
        var names = new SystemProcessLister().RunningProcessNames();

        Assert.Contains(System.Diagnostics.Process.GetCurrentProcess().ProcessName, names, StringComparer.OrdinalIgnoreCase);
    }
}

public class OutputLocationGuardTests
{
    [Fact]
    public void Plik_w_folderze_gry_jest_odrzucany()
    {
        using var temp = new TempDirectory();
        var game = temp.Combine("Game");
        Directory.CreateDirectory(game);

        Assert.NotNull(OutputLocationGuard.Check(Path.Combine(game, "corpus.jsonl"), game));
        Assert.NotNull(OutputLocationGuard.Check(Path.Combine(game, "sub", "corpus.jsonl"), game));
        Assert.Null(OutputLocationGuard.Check(temp.Combine("GameData", "corpus.jsonl"), game));
    }

    [Fact]
    public void Repozytorium_jest_odrzucane_poza_eval_private()
    {
        using var temp = new TempDirectory();
        var repo = temp.Combine("repo");
        temp.WriteFile("repo/GameTranslatorOverlay.slnx", [0]);
        var game = temp.Combine("Game");

        Assert.NotNull(OutputLocationGuard.Check(Path.Combine(repo, "eval", "corpus.jsonl"), game));
        Assert.Null(OutputLocationGuard.Check(Path.Combine(repo, "eval", "private", "corpus.jsonl"), game));
    }

    [Fact]
    public void Prawdziwe_repozytorium_git_jest_rozpoznawane_a_pusty_katalog_git_nie()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("withgit/.git/HEAD", "ref: refs/heads/main"u8.ToArray());
        Directory.CreateDirectory(temp.Combine("emptygit", ".git"));
        temp.WriteFile("worktree/.git", "gitdir: ../elsewhere"u8.ToArray());
        temp.WriteFile("notgit/.git", "something else"u8.ToArray());

        Assert.Equal(temp.Combine("withgit"), OutputLocationGuard.FindRepositoryRoot(temp.Combine("withgit", "a", "b")));
        Assert.Equal(temp.Combine("worktree"), OutputLocationGuard.FindRepositoryRoot(temp.Combine("worktree")));
        Assert.Null(OutputLocationGuard.FindRepositoryRoot(temp.Combine("emptygit", "x")));
        Assert.Null(OutputLocationGuard.FindRepositoryRoot(temp.Combine("notgit")));
    }

    [Fact]
    public void Biblioteka_gier_i_folder_z_plikiem_gry_sa_odrzucane()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("Games/G/g.exe", [0]);

        Assert.NotNull(OutputLocationGuard.CheckOutsideGame(temp.Combine("steamapps", "common", "x.json"), "Plik", null));
        Assert.NotNull(OutputLocationGuard.CheckOutsideGame(temp.Combine("GOG Galaxy", "Games", "G", "x.json"), "Plik", null));
        Assert.NotNull(OutputLocationGuard.CheckOutsideGame(temp.Combine("Games", "G", "sub", "x.json"), "Plik", ["g.exe"]));
        Assert.NotNull(OutputLocationGuard.CheckOutsideGame(temp.Combine("Games", "G", "x.json"), "Plik", ["g"]));
        Assert.NotNull(OutputLocationGuard.CheckOutsideGame(temp.Combine("A", "x.json"), "Plik", null, temp.Combine("A")));
        Assert.Null(OutputLocationGuard.CheckOutsideGame(temp.Combine("Games", "G", "x.json"), "Plik", ["other.exe"]));
        Assert.Null(OutputLocationGuard.CheckOutsideGame(temp.Combine("steamapps", "x.json"), "Plik", null));
        Assert.Null(OutputLocationGuard.CheckOutsideGame(temp.Combine("data", "x.json"), "Plik", ["g.exe"], temp.Combine("A"), null));
    }

    [Fact]
    public void Prefiks_nazwy_nie_oznacza_zawierania()
    {
        using var temp = new TempDirectory();

        Assert.False(OutputLocationGuard.IsInside(temp.Combine("Gamebar", "x"), temp.Combine("Game")));
        Assert.True(OutputLocationGuard.IsInside(temp.Combine("Game"), temp.Combine("Game") + Path.DirectorySeparatorChar));
    }
}

public class ReadOnlyGameFileTests
{
    [Fact]
    public void Otwiera_tylko_do_odczytu_i_nie_blokuje_zapisu_ani_usuniecia()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("data.bin", [1, 2, 3, 4, 5]);

        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = ReadOnlyGameFile.Open(path))
        {
            Assert.False(reader.CanWrite);
            Assert.Equal(1, reader.ReadByte());
            writer.WriteByte(9);
        }

        using (ReadOnlyGameFile.Open(path))
        {
            File.Delete(path);
        }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Glowa_i_ogon_pliku_sa_czytane_bez_przekroczenia_rozmiaru()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("small.bin", [1, 2, 3]);

        Assert.Equal([1, 2, 3], ReadOnlyGameFile.ReadHead(path, 10));
        Assert.Equal([2, 3], ReadOnlyGameFile.ReadTail(path, 2));
    }
}

public class NetworkIsolationTests
{
    [Fact]
    public void Narzedzie_nie_odwoluje_sie_do_bibliotek_sieciowych()
    {
        var forbidden = new[] { "System.Net.Http", "System.Net.Sockets", "System.Net.Requests", "System.Net.WebClient", "System.Net.WebSockets", "System.Net.NameResolution" };

        var references = typeof(CorpusExtractor).Assembly.GetReferencedAssemblies().Select(static a => a.Name).ToList();

        Assert.DoesNotContain(references, name => forbidden.Contains(name));
        Assert.Contains("GameTranslatorOverlay.Core", references);
    }

    [Fact]
    public void Ekstrakcja_nie_korzysta_z_dostawcow_ani_z_sieci_a_tlumaczenie_tylko_przez_Infrastructure()
    {
        var tool = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "GameTranslatorOverlay.CorpusTool"));
        var sources = Directory.EnumerateFiles(tool, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                  && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        var translation = Path.Combine(tool, "Translation") + Path.DirectorySeparatorChar;
        var extraction = sources.Where(path => !path.StartsWith(translation, StringComparison.Ordinal) && Path.GetFileName(path) != "Program.cs").ToList();

        Assert.Contains(extraction, static path => path.EndsWith("CorpusExtractor.cs", StringComparison.Ordinal));
        Assert.Contains(sources, path => path.StartsWith(translation, StringComparison.Ordinal));
        foreach (var path in extraction)
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("GameTranslatorOverlay.Infrastructure", text);
            Assert.DoesNotContain("System.Net", text);
            Assert.DoesNotContain("HttpClient", text);
        }
        foreach (var path in sources)
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("System.Net", text);
            Assert.DoesNotContain("new HttpClient", text);
        }
    }
}
