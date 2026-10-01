using GameTranslatorOverlay.Infrastructure.Settings;

namespace GameTranslatorOverlay.Infrastructure.Tests;

/// <summary>
/// Zmiana samego wyglądu nie może przebudowywać pipeline'u (czyści pamięć dialogu i anuluje
/// płatne tłumaczenia w locie), a każda zmiana wpływająca na tłumaczenie musi.
/// </summary>
public class PipelineSnapshotTests
{
    public static TheoryData<Action<AppSettings>> PresentationChanges => new()
    {
        s => s.OverlayFontSize = 22,
        s => s.OverlayFontFamily = "Consolas",
        s => s.OverlayBackgroundOpacity = 0.4,
        s => s.OverlayPlacement = "cover",
        s => s.LiveDisplayMode = "subtitle",
        s => s.ResultDisplayMode = "overlay",
        s => s.ShowOverlayNotices = false,
        s => s.LiveToggleHotkey = "Ctrl+Alt+F9",
        s => s.LastGameProcess = "game.exe",
        s => s.LastGameTitle = "Gra",
        s => s.DisclaimerAcknowledged = true,
    };

    public static TheoryData<Action<AppSettings>> PipelineChanges => new()
    {
        s => s.Provider = "Claude",
        s => s.TargetLanguage = "de",
        s => s.CacheOnlyMode = true,
        s => s.PrivateMode = true,
        s => s.ActiveProfileId = "path-of-exile-2",
        s => s.PlayerGender = "female",
        s => s.SessionCharacterLimit = 1000,
        s => s.ClaudeModel = "claude-haiku-4-5",
        s => s.LlmModel = "qwen2.5:7b",
    };

    [Theory]
    [MemberData(nameof(PresentationChanges))]
    public void Zmiana_wygladu_nie_zmienia_migawki_pipelineu(Action<AppSettings> change)
    {
        var settings = new AppSettings();
        var before = settings.PipelineSnapshot();

        change(settings);

        Assert.Equal(before, settings.PipelineSnapshot());
    }

    [Theory]
    [MemberData(nameof(PipelineChanges))]
    public void Zmiana_tlumaczenia_zmienia_migawke_pipelineu(Action<AppSettings> change)
    {
        var settings = new AppSettings();
        var before = settings.PipelineSnapshot();

        change(settings);

        Assert.NotEqual(before, settings.PipelineSnapshot());
    }
}
