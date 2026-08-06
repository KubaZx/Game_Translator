namespace GameTranslatorOverlay.Core.Translation;

/// <summary>
/// Deterministyczny dostawca do testów i pracy bez klucza API.
/// Oznacza teksty prefiksem języka docelowego, niczego nie wysyła do sieci.
/// </summary>
public sealed class MockTranslationProvider : ITranslationProvider
{
    public const string ProviderName = "Mock";

    public string Name => ProviderName;
    public bool RequiresApiKey => false;

    /// <summary>Symulowane opóźnienie odpowiedzi — przydatne do testów UI i anulowania.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var prefix = $"[{targetLanguage.ToUpperInvariant()}] ";
        return texts.Select(t => prefix + t).ToList();
    }

    public Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderStatus(true, "Dostawca testowy Mock — działa zawsze, bez internetu i bez klucza."));
}
