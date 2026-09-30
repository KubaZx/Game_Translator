using System.Text.RegularExpressions;

namespace GameTranslatorOverlay.Core.Translation;

/// <summary>
/// Płeć postaci gracza wybrana w ustawieniach. Po angielsku „you” nie ma rodzaju, a po polsku
/// musi go mieć („zrobiłeś/zrobiłaś”, „gotowy/gotowa”) — bez tej wskazówki model zgaduje.
/// </summary>
public enum PlayerGender
{
    Unknown = 0,
    Male,
    Female,
}

/// <summary>
/// Dostawca, który potrafi odmienić zwroty do gracza według <see cref="TranslationContext.PlayerGender"/>
/// (modele językowe). Klasyczni tłumacze (DeepL, Azure, Google) nie mają takiej opcji — dla nich
/// płeć gracza nie zmienia wyniku, więc nie wpływa też na znacznik i ważność wpisów cache.
/// </summary>
public interface IGenderAwareTranslationProvider
{
}

public static partial class PlayerGenders
{
    public const string UnknownSetting = "unknown";
    public const string MaleSetting = "male";
    public const string FemaleSetting = "female";

    /// <summary>
    /// Wartość z settings.json; brak pola (stary plik), literówka albo nieznana wartość to
    /// <see cref="PlayerGender.Unknown"/> — lepiej brak wskazówki niż zła forma.
    /// </summary>
    public static PlayerGender Parse(string? setting) => setting?.Trim().ToLowerInvariant() switch
    {
        MaleSetting => PlayerGender.Male,
        FemaleSetting => PlayerGender.Female,
        _ => PlayerGender.Unknown,
    };

    public static string ToSetting(PlayerGender gender) => gender switch
    {
        PlayerGender.Male => MaleSetting,
        PlayerGender.Female => FemaleSetting,
        _ => UnknownSetting,
    };

    /// <summary>
    /// Czy tekst zwraca się do gracza (you/your/yours/yourself jako osobne słowo, bez względu
    /// na wielkość liter). Tylko takie tłumaczenia zależą od płci gracza — reszty cache nie
    /// trzeba tłumaczyć ponownie po zmianie ustawienia.
    /// </summary>
    public static bool AddressesPlayer(string? text) =>
        !string.IsNullOrEmpty(text) && AddressWords().IsMatch(text);

    [GeneratedRegex(@"\b(?:you|your|yours|yourself)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressWords();
}
