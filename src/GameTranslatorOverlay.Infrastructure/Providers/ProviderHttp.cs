using System.Net.Http.Json;
using System.Text.Json;
using GameTranslatorOverlay.Core.Translation;
using Microsoft.Extensions.Logging;

namespace GameTranslatorOverlay.Infrastructure.Providers;

/// <summary>Wspólne ustawienia zapytań HTTP wszystkich dostawców tłumaczeń.</summary>
public class HttpProviderOptions
{
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Górny limit czekania między ponowieniami. Nagłówek Retry-After powyżej tej wartości
    /// oznacza rezygnację z retry — użytkownik dostaje od razu czytelny błąd zamiast
    /// zawieszonej na godziny aplikacji.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>Nieudana odpowiedź dostawcy: status i (krótka) treść błędu do klasyfikacji.</summary>
internal sealed record ProviderHttpFailure(int StatusCode, string Body)
{
    public bool BodyContains(string fragment) => Body.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Wspólna pętla zapytań dostawców: timeout na próbę, ograniczone ponawianie 429/5xx
/// z limitem Retry-After oraz mapowanie awarii na <see cref="TranslationException"/>.
/// Nie loguje adresów, nagłówków ani treści — mogą zawierać klucz albo tekst z ekranu.
/// </summary>
internal static class ProviderHttp
{
    private const int MaxErrorBodyChars = 4096;

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        Func<HttpRequestMessage> createRequest,
        string providerName,
        HttpProviderOptions options,
        Func<ProviderHttpFailure, TranslationException> mapError,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<ProviderHttpFailure, bool>? isRetryable = null,
        bool allowRetry = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(options.RequestTimeout);

                using var request = createRequest();
                response = await httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new TranslationException(TranslationFailureKind.Timeout,
                    $"{providerName} nie odpowiedział w ciągu {options.RequestTimeout.TotalSeconds:0} s.");
            }
            catch (HttpRequestException ex)
            {
                throw new TranslationException(TranslationFailureKind.NetworkError,
                    $"Nie udało się połączyć z {providerName}. Sprawdź połączenie z internetem.", ex);
            }
            catch (FormatException)
            {
                // Nagłówek z niedozwolonym znakiem (np. klucz wklejony razem z końcem linii).
                // Bez wyjątku wewnętrznego — jego treść mogłaby zawierać fragment klucza.
                throw new TranslationException(TranslationFailureKind.InvalidConfiguration,
                    $"Klucz API lub ustawienia dostawcy {providerName} zawierają niedozwolone znaki.");
            }

            if (response.IsSuccessStatusCode) return response;

            using (response)
            {
                var failure = new ProviderHttpFailure(
                    (int)response.StatusCode,
                    await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false));

                var retryable = isRetryable?.Invoke(failure) ?? (failure.StatusCode == 429 || failure.StatusCode >= 500);
                var retryAfter = response.Headers.RetryAfter?.Delta;

                // Serwer/pośrednik może przysłać absurdalny Retry-After (godziny) —
                // wtedy nie ponawiamy, tylko od razu zwracamy czytelny błąd.
                if (retryAfter > options.MaxRetryDelay)
                {
                    retryable = false;
                }

                if (allowRetry && retryable && attempt < options.MaxRetries)
                {
                    var delay = retryAfter ?? TimeSpan.FromSeconds(attempt + 1);
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                    logger.LogInformation("{Provider} zwrócił {Status} — ponawiam za {Delay} (próba {Attempt}/{Max})",
                        providerName, failure.StatusCode, delay, attempt + 1, options.MaxRetries);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw mapError(failure);
            }
        }
    }

    /// <summary>
    /// Odczyt JSON-a z udanej odpowiedzi. HTTP 200 z nie-JSON-owym ciałem to zwykle captive
    /// portal hotelowego Wi-Fi albo proxy — błąd sieci, nie surowy wyjątek deserializacji.
    /// </summary>
    public static async Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage response, string providerName, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new TranslationException(TranslationFailureKind.NetworkError,
                $"{providerName} zwrócił odpowiedź w nieoczekiwanym formacie (przechwycenie przez portal/proxy sieci?).", ex);
        }
    }

    /// <summary>
    /// Zamienia wynik próby połączenia na <see cref="ProviderStatus"/>. Anulowanie przez
    /// użytkownika przechodzi dalej; każda awaria dostawcy staje się czytelnym komunikatem.
    /// </summary>
    public static async Task<ProviderStatus> TestAsync(Func<Task<ProviderStatus>> probe)
    {
        try
        {
            return await probe().ConfigureAwait(false);
        }
        catch (TranslationException ex)
        {
            // Dla timeoutu i braku sieci techniczny komunikat jest konkretniejszy
            // (nazwa dostawcy, limit czasu); pozostałe rodzaje mają gotowy tekst dla gracza.
            var message = ex.Kind is TranslationFailureKind.Timeout or TranslationFailureKind.NetworkError
                ? ex.Message
                : ex.UserFriendlyMessage;
            return new ProviderStatus(false, message);
        }
    }

    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Length > MaxErrorBodyChars ? body[..MaxErrorBodyChars] : body;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
