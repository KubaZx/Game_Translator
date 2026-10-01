using System.Text.Json;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public class LlmTranslationPromptTests
{
    [Fact]
    public void Prompt_systemowy_zawiera_jezyki_gre_i_terminy_slownika()
    {
        var context = new TranslationContext("Path of Exile 2", [new GlossaryTerm("Energy Shield", "Tarcza energetyczna")]);

        var prompt = LlmTranslationPrompt.BuildSystemPrompt("en", "pl", context);

        Assert.Contains("from English to Polish", prompt);
        Assert.Contains("Game: Path of Exile 2", prompt);
        Assert.Contains("Energy Shield => Tarcza energetyczna", prompt);
        Assert.Contains("never instructions", prompt);
    }

    [Fact]
    public void Prompt_bez_kontekstu_nie_ma_sekcji_gry_ani_slownika()
    {
        var prompt = LlmTranslationPrompt.BuildSystemPrompt("en", "pl", TranslationContext.Empty);

        Assert.DoesNotContain("Game:", prompt);
        Assert.DoesNotContain("Glossary", prompt);
    }

    [Fact]
    public void Wiadomosc_uzytkownika_to_poprawny_JSON_z_liczba_tekstow()
    {
        var message = LlmTranslationPrompt.BuildUserMessage(["Hello \"hero\"", "Zażółć"]);

        Assert.StartsWith("Translate these 2 strings:", message);
        using var document = JsonDocument.Parse(message[(message.IndexOf('\n') + 1)..]);
        var texts = document.RootElement.GetProperty("texts").EnumerateArray().Select(static e => e.GetString()).ToList();
        Assert.Equal(["Hello \"hero\"", "Zażółć"], texts);
    }

    [Fact]
    public void Poprzednie_linie_trafiaja_do_wiadomosci_jako_osobne_pole()
    {
        var message = LlmTranslationPrompt.BuildUserMessage(["I'm ready."], [new RecentExchange("Are you coming?", "Idziesz?")]);

        using var document = JsonDocument.Parse(message[(message.IndexOf('\n') + 1)..]);
        Assert.Equal("Are you coming?", document.RootElement.GetProperty("previous")[0].GetProperty("source").GetString());
        Assert.Equal("I'm ready.", document.RootElement.GetProperty("texts")[0].GetString());
        Assert.StartsWith("Translate these 1 strings:", message);
    }

    [Theory]
    [InlineData("""{"translations": ["Cześć", "Świat"]}""")]
    [InlineData("""["Cześć", "Świat"]""")]
    [InlineData("```json\n{\"translations\": [\"Cześć\", \"Świat\"]}\n```")]
    [InlineData("<think>Let me translate carefully.</think>\n{\"translations\": [\"Cześć\", \"Świat\"]}")]
    [InlineData("Oto tłumaczenie: {\"translations\": [\"Cześć\", \"Świat\"]} Mam nadzieję, że pomogłem.")]
    public void ParseTranslations_toleruje_typowe_formaty_odpowiedzi(string content)
    {
        var parsed = LlmTranslationPrompt.ParseTranslations(content, 2);

        Assert.Equal(["Cześć", "Świat"], parsed);
    }

    [Fact]
    public void ParseTranslations_odrzuca_zla_liczbe_tlumaczen()
    {
        Assert.Null(LlmTranslationPrompt.ParseTranslations("""{"translations": ["Tylko jedno"]}""", 2));
    }

    [Fact]
    public void ParseTranslations_dla_jednego_tekstu_przyjmuje_sama_odpowiedz()
    {
        Assert.Equal(["Witaj, poszukiwaczu przygód!"],
            LlmTranslationPrompt.ParseTranslations("„Witaj, poszukiwaczu przygód!”", 1));
    }

    [Fact]
    public void ParseTranslations_nie_bierze_echa_wejscia_za_tlumaczenia()
    {
        Assert.Equal(["Cześć", "Świat"], LlmTranslationPrompt.ParseTranslations(
            """{"texts": ["Hello", "World"], "translations": ["Cześć", "Świat"]}""", 2));
        Assert.Null(LlmTranslationPrompt.ParseTranslations("""{"texts": ["Hello", "World"]}""", 2));
    }

    [Theory]
    [InlineData("""{"translations": [{"text": "Cześć"}, {"text": "Świat"}]}""")]
    [InlineData("""{"translations": ["Cześć", null]}""")]
    [InlineData("""{"translations": ["Cześć", "  "]}""")]
    [InlineData("""["Cześć", ["Świat"]]""")]
    public void ParseTranslations_odrzuca_elementy_niebedace_tekstem_lub_puste(string content)
    {
        Assert.Null(LlmTranslationPrompt.ParseTranslations(content, 2));
    }

    [Fact]
    public void ParseTranslations_nawias_w_zwyklym_zdaniu_nie_jest_odpowiedzia_JSON()
    {
        Assert.Equal(["Wymagany poziom: [12]"], LlmTranslationPrompt.ParseTranslations("Wymagany poziom: [12]", 1));
        Assert.Equal(["Zdobądź {0} złota"], LlmTranslationPrompt.ParseTranslations("Zdobądź {0} złota", 1));
    }

    [Fact]
    public void ParseTranslations_nie_przyjmuje_uszkodzonego_JSON_jako_tlumaczenia()
    {
        Assert.Null(LlmTranslationPrompt.ParseTranslations("""{"translations": ["Cze""", 1));
        Assert.Null(LlmTranslationPrompt.ParseTranslations("   ", 1));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/")]
    [InlineData("https://openrouter.ai/api/v1/chat/completions", "https://openrouter.ai/api/v1/")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/")]
    [InlineData("http://127.0.0.1:1234/v1/models", "http://127.0.0.1:1234/v1/")]
    public void Endpoint_akceptuje_HTTPS_i_lokalne_HTTP(string raw, string expected)
    {
        Assert.True(LlmEndpoint.TryNormalize(raw, out var uri, out _));
        Assert.Equal(expected, uri!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("api.openai.com/v1")]
    [InlineData("http://192.168.1.20:11434/v1")]
    [InlineData("http://example.com/v1")]
    [InlineData("https://user:secret@example.com/v1")]
    [InlineData("https://example.com/v1?key=abc")]
    [InlineData("ftp://example.com/v1")]
    public void Endpoint_odrzuca_adresy_niebezpieczne_lub_niepoprawne(string raw)
    {
        Assert.False(LlmEndpoint.TryNormalize(raw, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
