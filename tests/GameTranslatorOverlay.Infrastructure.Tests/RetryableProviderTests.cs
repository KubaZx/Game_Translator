using GameTranslatorOverlay.Core.Translation;
using GameTranslatorOverlay.Infrastructure.Providers;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public class RetryableProviderTests
{
    [Theory]
    [InlineData(typeof(ClaudeTranslationProvider), true)]
    [InlineData(typeof(OpenAiCompatibleTranslationProvider), true)]
    [InlineData(typeof(DeepLTranslationProvider), false)]
    [InlineData(typeof(AzureTranslatorProvider), false)]
    [InlineData(typeof(GoogleTranslateProvider), false)]
    public void Tylko_modele_jezykowe_dostaja_druga_szanse(Type providerType, bool retryable)
    {
        // Klasyczny tłumacz zwróciłby to samo — ponowienie byłoby zapłaconym duplikatem.
        Assert.Equal(retryable, typeof(IRetryableTranslationProvider).IsAssignableFrom(providerType));
    }
}
