using System.Text.Json;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

/// <summary>Pamięć dialogu i płeć gracza w prompcie modeli językowych (Claude, zgodny z OpenAI).</summary>
public class DialogMemoryPromptTests
{
    private static JsonElement Payload(string message)
    {
        using var document = JsonDocument.Parse(message[(message.IndexOf('\n') + 1)..]);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Poprzednie_pary_trafiaja_jako_previous_ze_zrodlem_i_tlumaczeniem()
    {
        var message = LlmTranslationPrompt.BuildUserMessage(
            ["Let's go."],
            [new RecentExchange("Are you ready?", "Jesteś gotowa?"), new RecentExchange("I'm ready.", "Jestem gotowa.")]);

        var root = Payload(message);
        Assert.StartsWith("Translate these 1 strings:", message);
        Assert.False(root.TryGetProperty("previous_lines", out _));
        var previous = root.GetProperty("previous").EnumerateArray().ToList();
        Assert.Equal(2, previous.Count);
        Assert.Equal("Are you ready?", previous[0].GetProperty("source").GetString());
        Assert.Equal("Jesteś gotowa?", previous[0].GetProperty("translation").GetString());
        Assert.Equal("Jestem gotowa.", previous[1].GetProperty("translation").GetString());
        Assert.Equal(["Let's go."], root.GetProperty("texts").EnumerateArray().Select(static e => e.GetString()));
        // Polskie znaki czytelne dla modelu, bez sekwencji \uXXXX.
        Assert.Contains("Jesteś gotowa?", message);
    }

    [Fact]
    public void Bez_poprzednich_par_wiadomosc_zawiera_tylko_teksty()
    {
        var root = Payload(LlmTranslationPrompt.BuildUserMessage(["Hello"], []));

        Assert.False(root.TryGetProperty("previous", out _));
        Assert.Equal("Hello", root.GetProperty("texts")[0].GetString());
    }

    [Fact]
    public void Prompt_systemowy_opisuje_previous_jako_wlasne_wczesniejsze_tlumaczenia()
    {
        var prompt = LlmTranslationPrompt.BuildSystemPrompt("en", "pl", TranslationContext.Empty);

        Assert.Contains("\"previous\"", prompt);
        Assert.Contains("translations you already", prompt);
        Assert.Contains("form of address", prompt);
        Assert.Contains("spelling of names", prompt);
        Assert.Contains("Never translate or return them", prompt);
        Assert.DoesNotContain("previous_lines", prompt);
    }

    [Theory]
    [InlineData(PlayerGender.Female, "player character is female", "zrobiłaś")]
    [InlineData(PlayerGender.Male, "player character is male", "zrobiłeś")]
    public void Plec_gracza_dodaje_regule_form_zwrotu_do_gracza(PlayerGender gender, string rule, string example)
    {
        var context = new TranslationContext(null, []) { PlayerGender = gender };

        var prompt = LlmTranslationPrompt.BuildSystemPrompt("en", "pl", context);

        Assert.Contains(rule, prompt);
        Assert.Contains(example, prompt);
    }

    [Fact]
    public void Nieznana_plec_gracza_nie_dodaje_reguly()
    {
        var prompt = LlmTranslationPrompt.BuildSystemPrompt("en", "pl", TranslationContext.Empty);

        Assert.DoesNotContain("player character is", prompt);
        Assert.DoesNotContain("zrobiłaś", prompt);
        Assert.DoesNotContain("zrobiłeś", prompt);
    }

    [Fact]
    public void Parser_nie_bierze_echa_previous_za_tlumaczenia()
    {
        const string echo = """{"previous": [{"source": "Are you ready?", "translation": "Jesteś gotowa?"}]}""";
        Assert.Null(LlmTranslationPrompt.ParseTranslations(echo, 1));

        const string echoWithTexts = """{"previous": [{"source": "A", "translation": "B"}], "texts": ["Hello"]}""";
        Assert.Null(LlmTranslationPrompt.ParseTranslations(echoWithTexts, 1));

        // Lista par w miejscu listy tłumaczeń też nie jest odpowiedzią.
        Assert.Null(LlmTranslationPrompt.ParseTranslations(
            """{"translations": [{"source": "Hello", "translation": "Cześć"}]}""", 1));
    }

    [Fact]
    public void Tylko_dostawcy_LLM_sa_swiadomi_plci_gracza()
    {
        Assert.True(typeof(IGenderAwareTranslationProvider).IsAssignableFrom(typeof(ClaudeTranslationProvider)));
        Assert.True(typeof(IGenderAwareTranslationProvider).IsAssignableFrom(typeof(OpenAiCompatibleTranslationProvider)));
        Assert.False(typeof(IGenderAwareTranslationProvider).IsAssignableFrom(typeof(DeepLTranslationProvider)));
        Assert.False(typeof(IGenderAwareTranslationProvider).IsAssignableFrom(typeof(AzureTranslatorProvider)));
        Assert.False(typeof(IGenderAwareTranslationProvider).IsAssignableFrom(typeof(GoogleTranslateProvider)));
        Assert.False(typeof(IGenderAwareTranslationProvider).IsAssignableFrom(typeof(MockTranslationProvider)));
    }
}
