namespace GameTranslatorOverlay.Core.Translation;

public interface ITranslationProvider
{
    string Name { get; }
    bool RequiresApiKey { get; }

    /// <summary>Tłumaczy partię tekstów. Kolejność wyników odpowiada kolejności wejścia.</summary>
    Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);

    Task<ProviderStatus> TestConnectionAsync(CancellationToken cancellationToken = default);
}

public sealed record ProviderStatus(bool IsOk, string Message, long? CharactersUsed = null, long? CharacterLimit = null);

public enum TranslationFailureKind
{
    MissingApiKey,
    InvalidApiKey,
    QuotaExceeded,
    RateLimited,
    NetworkError,
    Timeout,
    TextTooLong,
    ServiceUnavailable,
    InvalidRequest,
    Unknown,
}

public sealed class TranslationException(TranslationFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public TranslationFailureKind Kind { get; } = kind;

    public string UserFriendlyMessage => Kind switch
    {
        TranslationFailureKind.MissingApiKey => "Brak klucza API. Dodaj klucz dostawcy w ustawieniach aplikacji.",
        TranslationFailureKind.InvalidApiKey => "Klucz API został odrzucony przez dostawcę. Sprawdź, czy klucz jest poprawny.",
        TranslationFailureKind.QuotaExceeded => "Limit znaków u dostawcy tłumaczeń został wyczerpany. Tłumaczenia online wznowią się po odnowieniu limitu.",
        TranslationFailureKind.RateLimited => "Dostawca tłumaczeń chwilowo ogranicza liczbę zapytań. Spróbuj ponownie za moment.",
        TranslationFailureKind.NetworkError => "Brak połączenia z internetem albo dostawca tłumaczeń jest nieosiągalny.",
        TranslationFailureKind.Timeout => "Dostawca tłumaczeń nie odpowiedział w wyznaczonym czasie.",
        TranslationFailureKind.TextTooLong => "Tekst jest zbyt długi dla dostawcy tłumaczeń.",
        TranslationFailureKind.ServiceUnavailable => "Usługa tłumaczeń jest chwilowo niedostępna. Spróbuj ponownie później.",
        TranslationFailureKind.InvalidRequest => "Dostawca tłumaczeń odrzucił żądanie. Szczegóły znajdziesz w logu diagnostycznym.",
        _ => "Nieoczekiwany błąd tłumaczenia. Szczegóły znajdziesz w logu diagnostycznym.",
    };
}
