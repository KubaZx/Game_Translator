using GameTranslatorOverlay.Core.Windows;

namespace GameTranslatorOverlay.Core.Tests;

public class LiveTargetResolverTests
{
    private const string Own = "GameTranslatorOverlay.App.exe";

    private static readonly WindowCandidate Game = new(101, "Witcher 3", "witcher3.exe", 1920L * 1080);
    private static readonly WindowCandidate Browser = new(102, "Przepisy — Firefox", "firefox.exe", 1280L * 900);
    private static readonly WindowCandidate Desktop = new(103, "Program Manager", "explorer.exe", 2560L * 1440);
    private static readonly WindowCandidate Translator = new(104, "GameTranslatorOverlay", "GameTranslatorOverlay.App.exe", 1100L * 700);

    private static LiveTargetResolution Resolve(
        IReadOnlyList<WindowCandidate> windows,
        nint foreground,
        string? rememberedProcess = null,
        string? rememberedTitle = null,
        IEnumerable<string>? profileProcesses = null) =>
        LiveTargetResolver.Resolve(windows, foreground, rememberedProcess, rememberedTitle, profileProcesses, Own);

    [Fact]
    public void Aktywne_okno_wygrywa_z_zapamietana_gra_i_profilem()
    {
        var result = Resolve([Game, Browser], Browser.Handle,
            rememberedProcess: "witcher3.exe", profileProcesses: ["witcher3.exe"]);

        Assert.Same(Browser, result.Window);
        Assert.Equal(LiveTargetReason.Foreground, result.Reason);
        Assert.Null(result.Message);
        Assert.True(result.Found);
    }

    [Fact]
    public void Aktywne_okno_gry_jest_wybierane_bez_zadnej_pamieci()
    {
        var result = Resolve([Browser, Game], Game.Handle);

        Assert.Same(Game, result.Window);
        Assert.Equal(LiveTargetReason.Foreground, result.Reason);
    }

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("ShellExperienceHost.exe")]
    [InlineData("SearchHost.exe")]
    [InlineData("StartMenuExperienceHost.exe")]
    [InlineData("ApplicationFrameHost.exe")]
    [InlineData("TextInputHost.exe")]
    [InlineData("EXPLORER")]
    public void Okno_powloki_Windows_na_pierwszym_planie_jest_pomijane(string shellProcess)
    {
        var shell = new WindowCandidate(200, "Powłoka", shellProcess, 4000L * 4000);

        var result = Resolve([shell, Game], shell.Handle, rememberedProcess: "witcher3.exe");

        Assert.Same(Game, result.Window);
        Assert.Equal(LiveTargetReason.RememberedProcess, result.Reason);
    }

    [Fact]
    public void Pulpit_na_pierwszym_planie_bez_pamieci_i_profili_daje_komunikat()
    {
        var result = Resolve([Desktop, Browser], Desktop.Handle);

        Assert.Null(result.Window);
        Assert.False(result.Found);
        Assert.Equal(LiveTargetReason.None, result.Reason);
        Assert.Equal("Przełącz się do gry i wciśnij skrót ponownie.", result.Message);
    }

    [Fact]
    public void Wlasne_okno_tlumacza_na_pierwszym_planie_przechodzi_do_zapamietanej_gry()
    {
        var result = Resolve([Translator, Browser, Game], Translator.Handle, rememberedProcess: "witcher3.exe");

        Assert.Same(Game, result.Window);
        Assert.Equal(LiveTargetReason.RememberedProcess, result.Reason);
    }

    [Fact]
    public void Wlasny_proces_nie_jest_wybierany_nawet_gdy_pasuje_do_pamieci()
    {
        var result = Resolve([Translator], Translator.Handle, rememberedProcess: "GameTranslatorOverlay.App");

        Assert.Null(result.Window);
        Assert.Equal(LiveTargetReason.None, result.Reason);
    }

    [Fact]
    public void Aktywne_okno_spoza_listy_przechodzi_do_zapamietanej_gry()
    {
        // Np. okno narzędziowe albo okno, które zniknęło między skrótem a wyliczeniem okien.
        var result = Resolve([Browser, Game], foreground: 999, rememberedProcess: "witcher3.exe");

        Assert.Same(Game, result.Window);
        Assert.Equal(LiveTargetReason.RememberedProcess, result.Reason);
    }

    [Fact]
    public void Brak_aktywnego_okna_przechodzi_do_zapamietanej_gry()
    {
        var result = Resolve([Browser, Game], foreground: 0, rememberedProcess: "witcher3.exe");

        Assert.Same(Game, result.Window);
    }

    [Fact]
    public void Kilka_okien_zapamietanego_procesu_najpierw_wybiera_zgodny_tytul()
    {
        var launcher = new WindowCandidate(301, "Launcher", "game.exe", 3000L * 2000);
        var main = new WindowCandidate(302, "Gra — rozdział 2", "game.exe", 800L * 600);

        var result = Resolve([launcher, main], foreground: 0, rememberedProcess: "game.exe", rememberedTitle: "gra — ROZDZIAŁ 2");

        Assert.Same(main, result.Window);
        Assert.Equal(LiveTargetReason.RememberedProcess, result.Reason);
    }

    [Fact]
    public void Kilka_okien_zapamietanego_procesu_bez_zgodnego_tytulu_wybiera_najwieksze()
    {
        var console = new WindowCandidate(301, "Konsola", "game.exe", 640L * 480);
        var main = new WindowCandidate(302, "Gra — rozdział 3", "game.exe", 1920L * 1080);
        var small = new WindowCandidate(303, "Czat", "game.exe", 300L * 200);

        var result = Resolve([console, main, small], foreground: 0, rememberedProcess: "game.exe", rememberedTitle: "Gra — rozdział 2");

        Assert.Same(main, result.Window);
    }

    [Fact]
    public void Brak_zapamietanego_tytulu_wybiera_najwieksze_okno_procesu()
    {
        var console = new WindowCandidate(301, "Konsola", "game.exe", 640L * 480);
        var main = new WindowCandidate(302, "Gra", "game.exe", 1920L * 1080);

        var result = Resolve([console, main], foreground: 0, rememberedProcess: "game.exe", rememberedTitle: null);

        Assert.Same(main, result.Window);
    }

    [Fact]
    public void Profil_gry_jest_ostatnim_wyjsciem_gdy_zapamietanej_gry_nie_ma()
    {
        var other = new WindowCandidate(401, "Inna gra", "othergame.exe", 1024L * 768);
        var bigger = new WindowCandidate(402, "Inna gra — mapa", "othergame.exe", 1920L * 1080);

        var result = Resolve([Browser, other, bigger], Desktop.Handle,
            rememberedProcess: "witcher3.exe", profileProcesses: ["nieistniejaca.exe", "OtherGame.exe"]);

        Assert.Same(bigger, result.Window);
        Assert.Equal(LiveTargetReason.ProfileProcess, result.Reason);
        Assert.Null(result.Message);
    }

    [Fact]
    public void Zapamietana_gra_ma_pierwszenstwo_przed_profilem()
    {
        var profiled = new WindowCandidate(401, "Inna gra", "othergame.exe", 4000L * 3000);

        var result = Resolve([profiled, Game], foreground: 0,
            rememberedProcess: "witcher3.exe", profileProcesses: ["othergame.exe"]);

        Assert.Same(Game, result.Window);
        Assert.Equal(LiveTargetReason.RememberedProcess, result.Reason);
    }

    [Fact]
    public void Profil_wskazujacy_proces_powloki_nie_wybiera_pulpitu()
    {
        var result = Resolve([Desktop], foreground: 0, profileProcesses: ["explorer.exe"]);

        Assert.Null(result.Window);
        Assert.Equal(LiveTargetResolver.NoTargetMessage, result.Message);
    }

    [Fact]
    public void Pusta_lista_okien_daje_komunikat()
    {
        var result = Resolve([], foreground: 5, rememberedProcess: "witcher3.exe", profileProcesses: ["witcher3.exe"]);

        Assert.Equal(LiveTargetReason.None, result.Reason);
        Assert.Equal(LiveTargetResolver.NoTargetMessage, result.Message);
    }

    [Theory]
    [InlineData("witcher3.exe")]
    [InlineData("WITCHER3.EXE")]
    [InlineData("witcher3")]
    [InlineData("Witcher3.Exe")]
    [InlineData("  witcher3.exe  ")]
    public void Nazwa_procesu_bez_znaczenia_wielkosci_liter_i_sufiksu_exe(string remembered)
    {
        var result = Resolve([Browser, Game], foreground: 0, rememberedProcess: remembered);

        Assert.Same(Game, result.Window);
        Assert.Equal(LiveTargetReason.RememberedProcess, result.Reason);
    }

    [Theory]
    [InlineData("WITCHER3")]
    [InlineData("witcher3.EXE")]
    public void Profil_dopasowuje_proces_bez_znaczenia_wielkosci_liter_i_sufiksu_exe(string profileProcess)
    {
        var result = Resolve([Browser, Game], foreground: 0, profileProcesses: [profileProcess]);

        Assert.Same(Game, result.Window);
        Assert.Equal(LiveTargetReason.ProfileProcess, result.Reason);
    }

    [Fact]
    public void Wlasny_proces_rozpoznawany_bez_sufiksu_exe()
    {
        var result = LiveTargetResolver.Resolve([Translator], Translator.Handle, null, null, null, "gametranslatoroverlay.app");

        Assert.Null(result.Window);
    }

    [Fact]
    public void Bez_nazwy_wlasnego_procesu_aktywne_okno_jest_wybierane()
    {
        var result = LiveTargetResolver.Resolve([Game], Game.Handle, null, null, null);

        Assert.Same(Game, result.Window);
    }

    [Fact]
    public void Okno_bez_nazwy_procesu_jest_pomijane()
    {
        var unnamed = new WindowCandidate(500, "Bez procesu", " ", 100);

        var result = Resolve([unnamed], unnamed.Handle);

        Assert.Null(result.Window);
    }

    [Theory]
    [InlineData("game.exe", "GAME", true)]
    [InlineData("game", "game.exe", true)]
    [InlineData("game.exe", "game2.exe", false)]
    [InlineData(null, "game.exe", false)]
    [InlineData("", "", false)]
    public void Porownanie_nazw_procesow(string? left, string? right, bool expected)
    {
        Assert.Equal(expected, LiveTargetResolver.SameProcess(left, right));
    }

    [Theory]
    [InlineData("Game.EXE ", "Game")]
    [InlineData("game", "game")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData(".exe", "")]
    public void Normalizacja_nazwy_procesu(string? input, string expected)
    {
        Assert.Equal(expected, LiveTargetResolver.NormalizeProcessName(input));
    }

    [Fact]
    public void Brak_listy_okien_rzuca_wyjatek()
    {
        Assert.Throws<ArgumentNullException>(() => LiveTargetResolver.Resolve(null!, 0, null, null, null));
    }
}
