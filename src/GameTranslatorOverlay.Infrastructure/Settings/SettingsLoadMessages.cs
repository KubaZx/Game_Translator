namespace GameTranslatorOverlay.Infrastructure.Settings;

public sealed record SettingsLoadNotice(string Title, string Message, string? DefaultsButton, string? KeepButton);

public static class SettingsLoadMessages
{
    public const string StartFromDefaults = "Zacznij od ustawień domyślnych";
    public const string KeepFile = "Zostaw plik — poprawię go sam";
    public const string SavingBlockedStatus =
        "Ustawienia nie są zapisywane w tej sesji — plik settings.json został bez zmian. Zmiany działają do zamknięcia programu.";
    public const string SaveFailedStatus =
        "⚠ Nie udało się zapisać ustawień (plik jest zablokowany albo brakuje miejsca). Zmiana działa do zamknięcia programu.";

    private const string SafeModes =
        "Program wystartował w trybie prywatnym i Cache-only: nic nie wysyła do dostawcy tłumaczeń i nie zapisuje tłumaczeń na dysku, dopóki sam tego nie zmienisz.";

    public static SettingsLoadNotice? Describe(SettingsLoadResult result) => result.Status switch
    {
        SettingsLoadStatus.Unreadable => new SettingsLoadNotice(
            "Plik ustawień jest uszkodzony",
            "Nie udało się wczytać pliku settings.json — wygląda na uszkodzony (na przykład po ręcznej edycji).\n\n"
            + (result.BackupPath is { } backup
                ? $"Kopia pliku leży tutaj:\n{backup}\n\n"
                : "Nie udało się zrobić jego kopii, więc oryginał zostaje na swoim miejscu.\n\n")
            + SafeModes + "\n\n"
            + $"„{StartFromDefaults}” zapisze nowy plik z ustawieniami domyślnymi. "
            + $"„{KeepFile}” nie zmienia pliku — do końca sesji nic nie jest zapisywane do settings.json.",
            StartFromDefaults, KeepFile),
        SettingsLoadStatus.Unavailable => new SettingsLoadNotice(
            "Plik ustawień jest zajęty",
            "Nie udało się odczytać pliku settings.json — używa go inny program. Plik nie jest uszkodzony, więc nie robiłem jego kopii.\n\n"
            + SafeModes + "\n\n"
            + $"„{StartFromDefaults}” zastąpi plik ustawieniami domyślnymi. "
            + $"„{KeepFile}” nie zmienia pliku — do końca sesji nic nie jest zapisywane do settings.json.",
            StartFromDefaults, KeepFile),
        SettingsLoadStatus.NewerVersion => new SettingsLoadNotice(
            "Ustawienia z nowszej wersji programu",
            "Ustawienia pochodzą z nowszej wersji programu. Ta wersja ich nie zapisze, żeby niczego nie zgubić — "
            + "zmiany zrobione teraz działają tylko do zamknięcia programu.",
            null, null),
        SettingsLoadStatus.FieldsRepaired => new SettingsLoadNotice(
            "Poprawiono część ustawień",
            "Niektóre ustawienia w pliku settings.json miały nieprawidłową wartość i wróciły do domyślnych: "
            + string.Join(", ", result.RepairedFields) + ".\n\nPozostałe ustawienia zostały bez zmian.",
            null, null),
        _ => null,
    };
}
