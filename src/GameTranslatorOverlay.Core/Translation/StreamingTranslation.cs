namespace GameTranslatorOverlay.Core.Translation;

public static class PartialResults
{
    public const int Reset = -1;

    internal const string ResetKey = "\u0000";

    public static TranslationOutcome ResetOutcome { get; } = new(string.Empty, string.Empty, null, TranslationOrigin.Provider);
}

public interface IStreamingTranslationProvider : IContextualTranslationProvider
{
    Task<IReadOnlyList<string>> TranslateStreamingAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        TranslationContext context,
        Action<int, string> onTranslation,
        CancellationToken cancellationToken = default);
}
