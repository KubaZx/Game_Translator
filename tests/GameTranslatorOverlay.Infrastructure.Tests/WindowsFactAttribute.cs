namespace GameTranslatorOverlay.Infrastructure.Tests;

/// <summary>
/// [Fact] dla testów, które mają sens tylko na Windows (DPAPI, Windows OCR).
/// Wcześniej takie testy robiły <c>if (!OperatingSystem.IsWindows()) return;</c>
/// i na Linuksie raportowały się jako Passed — co fałszywie sugerowało, że
/// szyfrowanie sekretów zostało sprawdzone. Teraz xUnit pokazuje je jako Skipped
/// z czytelnym powodem, a pełny przebieg robi job build-test na windows-latest.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = WindowsOnly.SkipReason;
        }
    }
}

/// <summary>Odpowiednik <see cref="WindowsFactAttribute"/> dla testów parametryzowanych.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = WindowsOnly.SkipReason;
        }
    }
}

internal static class WindowsOnly
{
    // Jeden wspólny tekst, żeby w raporcie testów łatwo było wyszukać takie pominięcia.
    public const string SkipReason = "Test wymaga Windows (DPAPI / Windows OCR) — na tym systemie jest pomijany.";
}
