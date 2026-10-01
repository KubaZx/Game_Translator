namespace GameTranslatorOverlay.Core.Usage;

/// <summary>
/// Pomiar „zmiana → napis” w trybie live (<see cref="LatencyStage.ChangeToText"/>). Pamięta
/// chwilę PIERWSZEJ próbki, która zauważyła zmianę obrazu od ostatniego przetworzenia
/// (to ona przełącza stabilizator z czystego na brudny), i oddaje ją przetworzeniu, które
/// tę zmianę obejmie. Czas liczymy do pokazania przetłumaczonych napisów — z czekaniem na
/// stabilizację, OCR, kolejkę tłumaczeń i dostawcę. Tylko liczby, bez tekstu.
/// Nie jest bezpieczny wątkowo: używa go wyłącznie pętla sesji live.
/// </summary>
public sealed class ChangeToTextTracker
{
    private TimeSpan? _pendingSince;

    /// <summary>Początek zmiany czekającej na przetworzenie; null, gdy obraz stoi od ostatniego przebiegu.</summary>
    public TimeSpan? PendingSince => _pendingSince;

    /// <summary>
    /// Próbka pokazała istotną zmianę. Liczy się tylko pierwsza — kolejne klatki tej samej
    /// animacji (pojawiający się dymek) nie skracają zmierzonego czasu.
    /// </summary>
    public void ObserveChange(TimeSpan sampledAt) => _pendingSince ??= sampledAt;

    /// <summary>
    /// Przetworzenie klatki startuje: zabiera początek zmiany, którą obejmie. Zmiany
    /// zauważone w trakcie tego przetworzenia zaczynają już nowy pomiar.
    /// </summary>
    public TimeSpan? BeginProcessing()
    {
        var origin = _pendingSince;
        _pendingSince = null;
        return origin;
    }

    /// <summary>
    /// Przetworzenie nie pokazało napisów dla zmiany, ale pętla od razu je powtórzy (czknięcie
    /// OCR, potwierdzenie odczytu, zmiana w trakcie tłumaczenia). Pomiar biegnie dalej od
    /// PIERWOTNEJ zmiany — gracz czeka od niej, nie od powtórki. Bez powtórki początek
    /// przepada, żeby cisza na ekranie nie zawyżyła pomiaru następnej, niezależnej zmiany.
    /// </summary>
    public void CarryOver(TimeSpan? origin)
    {
        if (origin is not { } value) return;
        if (_pendingSince is not { } pending || value < pending) _pendingSince = value;
    }

    /// <summary>
    /// Koniec przetworzenia, po którym pętla odświeży obraz. Pierwotny początek przechodzi
    /// dalej tylko przy prawdziwym ponownym odczycie TEJ SAMEJ zmiany: celowej powtórce
    /// (czknięcie OCR, potwierdzenie odczytu) albo klatce porzuconej przed pokazaniem
    /// (nowa scena, zniknięty tekst, błąd). Klatka przetworzona do końca, która po prostu
    /// nie miała czego przetłumaczyć (dialog zniknął, OCR nic nie znalazł), swoją zmianę
    /// już obsłużyła — nowa linia, która pojawiła się w trakcie, liczy się od własnej próbki.
    /// </summary>
    public void FinishProcessing(TimeSpan? origin, bool frameCompleted, bool rereadRequested)
    {
        if (rereadRequested || !frameCompleted) CarryOver(origin);
    }

    /// <summary>Po zminimalizowaniu gry albo zatrzymaniu pomiar nie ma sensu.</summary>
    public void Reset() => _pendingSince = null;

    /// <summary>Czas od zmiany do napisu w ms; null bez zmiany (np. okresowy przebieg kontrolny).</summary>
    public static double? Measure(TimeSpan? origin, TimeSpan shownAt) =>
        origin is { } start && shownAt >= start ? (shownAt - start).TotalMilliseconds : null;
}
