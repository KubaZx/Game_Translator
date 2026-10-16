namespace GameTranslatorOverlay.Core.Windows;

/// <summary>
/// Teksty paska statusu związane ze skrótem live. Wydzielone z App, żeby dało się sprawdzić,
/// że nie podpowiadamy skrótu, którego nie udało się zarejestrować (zajęty przez inny program
/// albo błędny w settings.json) — przycisk i zasobnik też go wtedy pomijają.
/// </summary>
public static class LiveHotkeyMessages
{
    /// <param name="windowCount">Liczba okien na odświeżonej liście.</param>
    /// <param name="rememberedGameSelected">Czy na liście zaznaczono zapamiętaną grę.</param>
    /// <param name="registeredLiveHotkey">Skrót live, tylko jeśli jest zarejestrowany; inaczej null.</param>
    public static string WindowListRefreshed(
        int windowCount, bool rememberedGameSelected, string? registeredLiveHotkey, string? registeredTranslateHotkey)
    {
        var prefix = $"Znaleziono {windowCount} okien.";
        if (!rememberedGameSelected)
        {
            return string.IsNullOrWhiteSpace(registeredTranslateHotkey)
                ? $"{prefix} Wybierz okno gry albo przetłumacz region z Pulpitu."
                : $"{prefix} Wybierz okno gry albo od razu użyj {registeredTranslateHotkey.Trim()}.";
        }

        return string.IsNullOrWhiteSpace(registeredLiveHotkey)
            ? $"{prefix} Zaznaczono ostatnią grę — kliknij Start live."
            : $"{prefix} Zaznaczono ostatnią grę — kliknij Start live albo wciśnij {registeredLiveHotkey.Trim()} w grze.";
    }
}
