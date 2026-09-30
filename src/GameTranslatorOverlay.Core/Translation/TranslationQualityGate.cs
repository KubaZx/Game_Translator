using System.Text;
using System.Text.RegularExpressions;

namespace GameTranslatorOverlay.Core.Translation;

/// <summary>Problemy wykryte w wyniku tłumaczenia przez <see cref="TranslationQualityGate"/>.</summary>
[Flags]
public enum TranslationQualityFlags
{
    None = 0,

    /// <summary>Dostawca zwrócił pusty wynik (same białe znaki).</summary>
    Empty = 1,

    /// <summary>Liczba z oryginału (cena, poziom, godzina, ilość) nie występuje w tłumaczeniu.</summary>
    NumbersChanged = 2,

    /// <summary>Wynik jest identyczny z angielskim oryginałem — dostawca niczego nie przetłumaczył.</summary>
    Untranslated = 4,

    /// <summary>Wynik jest wielokrotnie dłuższy od oryginału (model „rozgadał się” albo dopisał komentarz).</summary>
    Runaway = 8,
}

/// <summary>
/// Tania, czysta kontrola jakości tłumaczenia — bez sieci i bez modelu. Wyłapuje typowe
/// usterki dostawców (zwłaszcza modeli językowych): pusty wynik, zgubione lub zmienione
/// liczby, brak tłumaczenia i „rozgadaną” odpowiedź. Ma unikać fałszywych alarmów:
/// dodatkowe liczby w tłumaczeniu są dozwolone, a separatory tysięcy i przecinek dziesiętny
/// są normalizowane (polska typografia pisze „1 000” i „2,5”).
/// </summary>
public static class TranslationQualityGate
{
    public const string EmptyResultMessage = "Dostawca zwrócił pusty wynik.";

    private const int RunawayFactor = 3;
    private const int RunawaySlack = 30;
    private const int UntranslatedMinWords = 3;

    // Liczba z separatorami: kropka/przecinek między cyframi albo spacja (także twarda)
    // tylko przed dokładnie trzema cyframi — „5 10 razy” to dwie liczby, „1 000” jedna.
    private static readonly Regex NumberToken = new(
        @"\d+(?:(?:[.,]|[   ](?=\d{3}(?!\d)))\d+)*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DigitRun = new(@"\d+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Word = new(@"\p{L}+(?:['’]\p{L}+)?", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Angielskie słowa funkcyjne: identyczny wynik z nimi to na pewno nieprzetłumaczone
    // zdanie, a nie nazwa własna czy okrzyk, który po polsku brzmi tak samo („OK”, „Tokyo”).
    private static readonly HashSet<string> EnglishFunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "to", "of", "in", "on", "at", "for", "with", "from", "by",
        "is", "are", "was", "were", "be", "been", "have", "has", "had", "do", "does", "did", "will", "would",
        "can", "could", "should", "must", "you", "your", "we", "our", "they", "their", "it", "its", "this",
        "that", "these", "those", "not", "no", "what", "where", "when", "who", "why", "how", "i", "me", "my",
        "he", "she", "his", "her", "him", "them", "there", "here", "if", "then", "than", "so", "all",
    };

    /// <summary>Sprawdza wynik tłumaczenia względem tekstu wysłanego do dostawcy.</summary>
    public static TranslationQualityFlags Check(string sourceSent, string? translated)
    {
        if (string.IsNullOrWhiteSpace(translated)) return TranslationQualityFlags.Empty;
        sourceSent ??= string.Empty;

        var flags = TranslationQualityFlags.None;
        if (!NumbersPreserved(sourceSent, translated)) flags |= TranslationQualityFlags.NumbersChanged;
        if (LooksUntranslated(sourceSent, translated)) flags |= TranslationQualityFlags.Untranslated;
        if (translated.Trim().Length > RunawayFactor * sourceSent.Trim().Length + RunawaySlack)
            flags |= TranslationQualityFlags.Runaway;
        return flags;
    }

    /// <summary>Krótki polski opis problemów dla gracza (bez treści tekstu).</summary>
    public static string? Describe(TranslationQualityFlags flags)
    {
        if (flags == TranslationQualityFlags.None) return null;
        if (flags.HasFlag(TranslationQualityFlags.Empty)) return EmptyResultMessage;

        var parts = new List<string>(3);
        if (flags.HasFlag(TranslationQualityFlags.NumbersChanged)) parts.Add("liczby różnią się od oryginału");
        if (flags.HasFlag(TranslationQualityFlags.Untranslated)) parts.Add("tekst wygląda na nieprzetłumaczony");
        if (flags.HasFlag(TranslationQualityFlags.Runaway)) parts.Add("tłumaczenie jest podejrzanie długie");
        return "Możliwy błąd tłumaczenia: " + string.Join(", ", parts) + ".";
    }

    /// <summary>Liczba zgłoszonych problemów — mniej znaczy lepiej; pusty wynik jest zawsze najgorszy.</summary>
    public static int Severity(TranslationQualityFlags flags) =>
        flags.HasFlag(TranslationQualityFlags.Empty) ? int.MaxValue : System.Numerics.BitOperations.PopCount((uint)flags);

    private static bool NumbersPreserved(string source, string translated)
    {
        var sourceNumbers = NumberToken.Matches(source);
        if (sourceNumbers.Count == 0) return true;

        var available = new HashSet<string>(StringComparer.Ordinal);
        var runs = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in NumberToken.Matches(translated))
        {
            available.UnionWith(Interpretations(match.Value));
        }
        foreach (Match match in DigitRun.Matches(translated)) runs.Add(match.Value);

        foreach (Match match in sourceNumbers)
        {
            if (Interpretations(match.Value).Any(available.Contains)) continue;
            // Zapis zmieniony tylko separatorami („10:30” → „10.30”, „5,000” rozbite
            // w szyku zdania) — wszystkie ciągi cyfr nadal są w tłumaczeniu.
            if (DigitRun.Matches(match.Value).All(run => runs.Contains(run.Value))) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Możliwe wartości zapisu liczby w postaci kanonicznej (bez separatorów tysięcy,
    /// kropka dziesiętna). „1,000” to po angielsku tysiąc, ale „1.000” po polsku też —
    /// niejednoznaczny zapis daje obie interpretacje.
    /// </summary>
    private static IEnumerable<string> Interpretations(string token)
    {
        var compact = token.Replace(" ", string.Empty).Replace(" ", string.Empty).Replace(" ", string.Empty);
        var groups = compact.Split(['.', ','], StringSplitOptions.None);
        if (groups.Length == 1)
        {
            yield return compact;
            yield break;
        }

        var separators = compact.Where(static c => c is '.' or ',').ToArray();
        var thousandsShape = groups[0].Length is >= 1 and <= 3 && groups.Skip(1).All(static g => g.Length == 3);

        // Jeden rodzaj separatora w kształcie tysięcy: „1,000”, „1.000.000”.
        if (thousandsShape && separators.Distinct().Count() == 1)
            yield return string.Concat(groups);

        // Ostatni separator jako dziesiętny, wcześniejsze jako tysiące: „2.5”, „2,5”, „1,234.5”.
        var integerPart = string.Concat(groups[..^1]);
        yield return integerPart + "." + groups[^1];
    }

    private static bool LooksUntranslated(string source, string translated)
    {
        if (!string.Equals(CollapseWhitespace(source), CollapseWhitespace(translated), StringComparison.OrdinalIgnoreCase))
            return false;

        var words = Word.Matches(source).Select(static m => m.Value).ToList();
        return words.Count >= UntranslatedMinWords && words.Any(EnglishFunctionWords.Contains);
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>Nazwy flag w znaczniku cache (stabilne — zapisywane na dysku).</summary>
    internal static string ToMarker(TranslationQualityFlags flags)
    {
        var names = new List<string>(4);
        if (flags.HasFlag(TranslationQualityFlags.Empty)) names.Add("empty");
        if (flags.HasFlag(TranslationQualityFlags.NumbersChanged)) names.Add("numbers");
        if (flags.HasFlag(TranslationQualityFlags.Untranslated)) names.Add("untranslated");
        if (flags.HasFlag(TranslationQualityFlags.Runaway)) names.Add("runaway");
        return string.Join(',', names);
    }

    internal static TranslationQualityFlags FromMarker(string marker)
    {
        var flags = TranslationQualityFlags.None;
        foreach (var name in marker.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            flags |= name.ToLowerInvariant() switch
            {
                "empty" => TranslationQualityFlags.Empty,
                "numbers" => TranslationQualityFlags.NumbersChanged,
                "untranslated" => TranslationQualityFlags.Untranslated,
                "runaway" => TranslationQualityFlags.Runaway,
                // Nieznana nazwa (nowsza wersja aplikacji) — sam znacznik „qa=” i tak
                // oznacza wpis do ponownego tłumaczenia (TranslationCacheContext).
                _ => TranslationQualityFlags.None,
            };
        }
        return flags;
    }
}

/// <summary>
/// Dostawca, któremu opłaca się dać drugą szansę na ten sam tekst: model językowy
/// odpowiada niedeterministycznie, więc ponowne zapytanie często naprawia zgubioną liczbę
/// czy nieprzetłumaczone zdanie. Klasyczni tłumacze (DeepL, Azure, Google) zwróciliby to
/// samo — ponowienie byłoby tylko zapłaconym duplikatem, więc go nie implementują.
/// </summary>
public interface IRetryableTranslationProvider : ITranslationProvider;
