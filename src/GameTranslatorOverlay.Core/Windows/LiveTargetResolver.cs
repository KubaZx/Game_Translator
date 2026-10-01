namespace GameTranslatorOverlay.Core.Windows;

/// <summary>Okno-kandydat do trybu live: uchwyt, tytuł, proces (np. „game.exe”) i pole powierzchni w px².</summary>
public sealed record WindowCandidate(nint Handle, string Title, string ProcessName, long Area);

public enum LiveTargetReason
{
    /// <summary>Nie znaleziono okna — użytkownik musi przełączyć się do gry.</summary>
    None,

    /// <summary>Aktywne (pierwszoplanowe) okno w chwili wciśnięcia skrótu.</summary>
    Foreground,

    /// <summary>Okno procesu gry zapamiętanego przy poprzednim starcie live.</summary>
    RememberedProcess,

    /// <summary>Okno procesu wymienionego w którymś profilu gry.</summary>
    ProfileProcess,
}

public sealed record LiveTargetResolution(WindowCandidate? Window, LiveTargetReason Reason, string? Message)
{
    public bool Found => Window is not null;
}

/// <summary>
/// Wybór okna gry dla skrótu „start live” — bez Alt+Tab do aplikacji i klikania listy.
/// Czysta logika: App podaje listę okien, uchwyt okna pierwszoplanowego (odczytany
/// synchronicznie w chwili skrótu) i zapamiętaną grę; tu zapada decyzja.
/// </summary>
public static class LiveTargetResolver
{
    public const string NoTargetMessage = "Przełącz się do gry i wciśnij skrót ponownie.";

    /// <summary>
    /// Procesy powłoki Windows: pulpit, pasek zadań, menu Start, wyszukiwarka, klawiatura
    /// ekranowa. Skrót wciśnięty np. tuż po kliknięciu paska zadań nie może uruchomić
    /// tłumaczenia pulpitu — wtedy szukamy dalej (zapamiętana gra, profile).
    /// </summary>
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "ShellExperienceHost",
        "SearchHost",
        "StartMenuExperienceHost",
        "ApplicationFrameHost",
        "TextInputHost",
    };

    public static LiveTargetResolution Resolve(
        IReadOnlyList<WindowCandidate> windows,
        nint foreground,
        string? rememberedProcess,
        string? rememberedTitle,
        IEnumerable<string>? profileProcessNames,
        string? ownProcessName = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var ownProcess = NormalizeProcessName(ownProcessName);

        bool IsEligible(WindowCandidate window)
        {
            var process = NormalizeProcessName(window.ProcessName);
            if (process.Length == 0) return false;
            if (ShellProcesses.Contains(process)) return false;
            // Własne okna (panel wyników, nakładka, okno główne) nigdy nie są „grą”.
            return ownProcess.Length == 0 || !process.Equals(ownProcess, StringComparison.OrdinalIgnoreCase);
        }

        var eligible = windows.Where(IsEligible).ToList();

        // (a) Aktywne okno — najczęstszy przypadek: gracz jest w grze i wciska skrót.
        if (foreground != 0 && eligible.FirstOrDefault(w => w.Handle == foreground) is { } active)
        {
            return new LiveTargetResolution(active, LiveTargetReason.Foreground, null);
        }

        // (b) Zapamiętana gra — skrót wciśnięty z okna tłumacza, przeglądarki albo pulpitu.
        var remembered = NormalizeProcessName(rememberedProcess);
        if (remembered.Length > 0)
        {
            var sameProcess = eligible
                .Where(w => NormalizeProcessName(w.ProcessName).Equals(remembered, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sameProcess.Count > 0)
            {
                // Gra z launcherem/oknem konsoli ma kilka okien: najpierw to samo okno co
                // ostatnio (po tytule), potem największe — okno gry jest zwykle największe.
                var byTitle = string.IsNullOrWhiteSpace(rememberedTitle)
                    ? null
                    : sameProcess.Where(w => string.Equals(w.Title.Trim(), rememberedTitle.Trim(), StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(static w => w.Area)
                        .FirstOrDefault();
                return new LiveTargetResolution(
                    byTitle ?? Largest(sameProcess), LiveTargetReason.RememberedProcess, null);
            }
        }

        // (c) Proces znany z któregoś profilu gry.
        var profileProcesses = new HashSet<string>(
            (profileProcessNames ?? []).Select(NormalizeProcessName).Where(static p => p.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        if (profileProcesses.Count > 0)
        {
            var profileWindows = eligible
                .Where(w => profileProcesses.Contains(NormalizeProcessName(w.ProcessName)))
                .ToList();
            if (profileWindows.Count > 0)
            {
                return new LiveTargetResolution(Largest(profileWindows), LiveTargetReason.ProfileProcess, null);
            }
        }

        // (d) Nie zgadujemy na ślepo (np. „największe okno”) — to mogłaby być przeglądarka
        // z prywatną treścią, która poleciałaby do dostawcy tłumaczeń.
        return new LiveTargetResolution(null, LiveTargetReason.None, NoTargetMessage);
    }

    /// <summary>„Game.EXE ” → „Game”: porównania nazw procesów nie zależą od wielkości liter ani sufiksu.</summary>
    public static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return string.Empty;
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4].TrimEnd();
        return name;
    }

    /// <summary>Czy dwie nazwy procesu oznaczają ten sam program (wielkość liter i „.exe” bez znaczenia).</summary>
    public static bool SameProcess(string? left, string? right)
    {
        var a = NormalizeProcessName(left);
        return a.Length > 0 && a.Equals(NormalizeProcessName(right), StringComparison.OrdinalIgnoreCase);
    }

    private static WindowCandidate Largest(List<WindowCandidate> windows) =>
        windows.OrderByDescending(static w => w.Area).First();
}
