namespace GameTranslatorOverlay.Core.Translation;

public static class PartialResults
{
    public const int Reset = -1;

    private const int FirstWithdrawal = -2;

    public static TranslationOutcome ResetOutcome { get; } = new(string.Empty, string.Empty, null, TranslationOrigin.Provider);

    public static int Withdraw(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, int.MaxValue - 1);
        return FirstWithdrawal - index;
    }

    public static bool IsWithdrawal(int code, out int index)
    {
        index = code <= FirstWithdrawal ? FirstWithdrawal - code : -1;
        return index >= 0;
    }
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
