using System.Globalization;
using System.Text.RegularExpressions;
using GameTranslatorOverlay.Core.Glossary;

namespace GameTranslatorOverlay.Core.Evaluation;

public enum TranslationCheckKind
{
    /// <summary>Liczba ze źródła zniknęła albo pojawiła się nowa („+15%” → „+51%”).</summary>
    Numbers,

    /// <summary>Forma grzecznościowa („Pan”, „Pani”, „Państwo”) zamiast „ty”.</summary>
    FormalAddress,

    /// <summary>Formy czasownika/przymiotnika w innym rodzaju niż oczekiwany (expect_gender).</summary>
    Gender,

    /// <summary>Termin ze słownika nie został użyty (brak rdzenia polskiego tłumaczenia).</summary>
    Glossary,

    /// <summary>Inna liczba niepustych wierszy niż w oryginale (zlepione albo zgubione akapity).</summary>
    Paragraphs,

    /// <summary>Tłumaczenie podejrzanie krótkie albo długie względem oryginału.</summary>
    Length,
}

public sealed record TranslationCheckIssue(TranslationCheckKind Kind, string Message);

/// <summary>Wejście jednej kontroli: oryginał, tłumaczenie, opcjonalnie referencja i oczekiwania.</summary>
public sealed record TranslationCheckInput(
    string Source,
    string Hypothesis,
    string? Reference = null,
    string? ExpectGender = null,
    IReadOnlyList<GlossaryTerm>? Terms = null);

public sealed record TranslationCheckResult(double LengthRatio, IReadOnlyList<TranslationCheckIssue> Issues)
{
    public bool Passed => Issues.Count == 0;
}

/// <summary>
/// Heurystyczne kontrole tłumaczeń EN→PL, uzupełniające chrF o błędy, których metryka
/// znakowa prawie nie widzi: zmieniona liczba w statystyce przedmiotu, „Pan” zamiast „ty”,
/// zły rodzaj mówiącego, zignorowany termin słownika. Kontrola zgłasza problem tylko wtedy,
/// gdy ma dowód — brak form rodzajowych w zdaniu nie jest błędem.
/// </summary>
public static partial class TranslationChecks
{
    /// <summary>Granice stosunku długości (bez białych znaków) tłumaczenia do oryginału.</summary>
    public const double MinLengthRatio = 0.5;
    public const double MaxLengthRatio = 2.0;

    public static TranslationCheckResult Run(TranslationCheckInput input)
    {
        var issues = new List<TranslationCheckIssue>();
        AddIfNotNull(issues, CheckNumbers(input.Source, input.Hypothesis));
        AddIfNotNull(issues, CheckFormalAddress(input.Source, input.Hypothesis, input.Reference));
        AddIfNotNull(issues, CheckGender(input.Hypothesis, input.ExpectGender));
        issues.AddRange(CheckGlossary(input.Source, input.Hypothesis, input.Terms ?? []));
        AddIfNotNull(issues, CheckParagraphs(input.Source, input.Hypothesis));
        var ratio = LengthRatio(input.Source, input.Hypothesis);
        AddIfNotNull(issues, CheckLength(ratio));
        return new TranslationCheckResult(ratio, issues);
    }

    private static void AddIfNotNull(List<TranslationCheckIssue> issues, TranslationCheckIssue? issue)
    {
        if (issue is not null) issues.Add(issue);
    }

    // ---- Liczby ----

    // Liczba z separatorami tysięcy (ten sam separator w każdej grupie: „1,000”, „1 000”,
    // „1.000.000”) albo zwykła liczba; obie z opcjonalną częścią dziesiętną („1.5”, „1,5”).
    [GeneratedRegex(@"(?<int>\d{1,3}(?<sep>[,.\u00A0\u202F ])\d{3}(?:\k<sep>\d{3})*)(?!\d)(?:[.,](?<frac>\d+))?|(?<int>\d+)(?:[.,](?<frac>\d+))?",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex NumberPattern();

    // To samo bez spacji jako separatora tysięcy: „Buy 3 100-gold potions” to dwie liczby (3 i 100),
    // a nie 3100. Która interpretacja jest dobra, wiadomo dopiero po porównaniu z drugim tekstem.
    [GeneratedRegex(@"(?<int>\d{1,3}(?<sep>[,.])\d{3}(?:\k<sep>\d{3})*)(?!\d)(?:[.,](?<frac>\d+))?|(?<int>\d+)(?:[.,](?<frac>\d+))?",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex NumberPatternWithoutSpaceGrouping();

    /// <summary>
    /// Liczby w tekście w postaci kanonicznej: bez separatorów tysięcy, z kropką dziesiętną
    /// („1,5” i „1.5” → „1.5”; „1,000” i „1 000” → „1000”). Polski zapis dziesiętny z przecinkiem
    /// nie jest więc błędem. Znak minus jest pomijany (tłumacz może zamienić go na półpauzę
    /// w zakresie „10–15”). Niejednoznaczne „1.500” traktujemy jak tysiące.
    /// </summary>
    public static IReadOnlyList<string> ExtractNumbers(string? text) => ExtractNumbers(text, spaceGrouping: true);

    private static IReadOnlyList<string> ExtractNumbers(string? text, bool spaceGrouping)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var numbers = new List<string>();
        var pattern = spaceGrouping ? NumberPattern() : NumberPatternWithoutSpaceGrouping();
        foreach (Match match in pattern.Matches(text))
        {
            var integerPart = new string(match.Groups["int"].Value.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
            if (integerPart.Length == 0) integerPart = "0";
            var fraction = match.Groups["frac"].Value.TrimEnd('0');
            numbers.Add(fraction.Length == 0 ? integerPart : $"{integerPart}.{fraction}");
        }
        return numbers;
    }

    /// <summary>
    /// Porównuje liczby dwa razy: ze spacją jako separatorem tysięcy („1 000” = „1,000”) i bez
    /// („3 100-gold” = 3 i 100). Zgłasza problem tylko wtedy, gdy obie interpretacje się nie
    /// zgadzają — spacja między dwiema liczbami nie może dawać fałszywego alarmu. Komunikat
    /// pochodzi z interpretacji z mniejszą liczbą różnic.
    /// </summary>
    public static TranslationCheckIssue? CheckNumbers(string source, string hypothesis)
    {
        var (missing, extra) = CompareNumbers(source, hypothesis, spaceGrouping: true);
        if (missing.Count == 0 && extra.Count == 0) return null;
        var (missingPlain, extraPlain) = CompareNumbers(source, hypothesis, spaceGrouping: false);
        if (missingPlain.Count == 0 && extraPlain.Count == 0) return null;
        if (missingPlain.Count + extraPlain.Count < missing.Count + extra.Count) (missing, extra) = (missingPlain, extraPlain);

        var parts = new List<string>();
        if (missing.Count > 0) parts.Add("brakuje " + string.Join(", ", missing));
        if (extra.Count > 0) parts.Add("nadmiarowe " + string.Join(", ", extra));
        return new TranslationCheckIssue(TranslationCheckKind.Numbers, "Liczby: " + string.Join("; ", parts) + ".");
    }

    private static (List<string> Missing, List<string> Extra) CompareNumbers(string source, string hypothesis, bool spaceGrouping)
    {
        var sourceNumbers = ExtractNumbers(source, spaceGrouping);
        var hypothesisNumbers = ExtractNumbers(hypothesis, spaceGrouping);
        return (MultisetDifference(sourceNumbers, hypothesisNumbers), MultisetDifference(hypothesisNumbers, sourceNumbers));
    }

    private static List<string> MultisetDifference(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var remaining = right.GroupBy(static x => x, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.Count(), StringComparer.Ordinal);
        var difference = new List<string>();
        foreach (var item in left)
        {
            if (remaining.TryGetValue(item, out var count) && count > 0) remaining[item] = count - 1;
            else difference.Add(item);
        }
        return difference;
    }

    // ---- Forma grzecznościowa ----

    // Formy „Pan/Pani/Państwo” w przypadkach, w których służą do zwracania się do rozmówcy.
    private static readonly HashSet<string> FormalForms = new(StringComparer.OrdinalIgnoreCase)
    {
        "pan", "pana", "panu", "panem", "panie",
        "pani", "panią",
        "państwo", "państwa", "państwu", "państwem",
    };

    // Angielskie zwroty, które poprawnie tłumaczy się właśnie przez „pan/pani” („Yes, sir.” →
    // „Tak, panie.”) — wtedy forma nie jest złamaniem zasady „ty”. Tylko jednoznaczne: „master”,
    // „miss”, „lady”, „lord” czy „mistress” to w grach zwykłe słowa („Master Volume”, „Don't
    // miss”, „Lord of Ashes”), a pojedyncze wystąpienie wyłączałoby kontrolę dla całej linii.
    private static readonly HashSet<string> HonorificSourceWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "sir", "sirs", "madam", "madame", "ma'am", "mister", "mr", "mrs", "ms",
        "milord", "milady", "m'lord", "m'lady", "lordship", "ladyship", "majesty", "highness",
    };

    // Dwuwyrazowe zwroty do rozmówcy z tymi dwuznacznymi słowami („Yes, my lord.” → „Tak, panie.”).
    [GeneratedRegex(@"\b(?:my|good)\s+(?:lord|lady|liege|master|mistress)\b|\bladies\s+and\s+gentlemen\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HonorificSourcePhrase();

    [GeneratedRegex(@"[\p{L}']+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    /// <summary>
    /// Gra jest tłumaczona na „ty”. Zgłasza „Pan/Pani/Państwo” w tłumaczeniu, chyba że:
    /// oryginał zawiera jednoznaczny zwrot grzecznościowy (sir, madam, my lord…), forma stoi przed nazwą
    /// pisaną wielką literą („Pan Ciemności”, „pani Anna” — tytuł, nie zwrot do gracza)
    /// albo ta sama forma występuje w referencji.
    /// </summary>
    public static TranslationCheckIssue? CheckFormalAddress(string source, string hypothesis, string? reference = null)
    {
        if (WordPattern().Matches(source).Any(m => HonorificSourceWords.Contains(m.Value.TrimEnd('.')))
            || HonorificSourcePhrase().IsMatch(source))
            return null;

        var referenceForms = reference is null
            ? []
            : WordPattern().Matches(reference).Select(static m => m.Value.ToLowerInvariant())
                .Where(FormalForms.Contains).ToHashSet(StringComparer.Ordinal);

        var words = WordPattern().Matches(hypothesis);
        var found = new List<string>();
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i].Value;
            if (!FormalForms.Contains(word)) continue;
            if (referenceForms.Contains(word.ToLowerInvariant())) continue;
            if (i + 1 < words.Count && char.IsUpper(words[i + 1].Value[0])
                && IsSameSentence(hypothesis, words[i].Index + word.Length, words[i + 1].Index))
                continue;
            found.Add(word);
        }
        return found.Count == 0
            ? null
            : new TranslationCheckIssue(TranslationCheckKind.FormalAddress,
                $"Forma grzecznościowa zamiast „ty”: {string.Join(", ", found.Distinct(StringComparer.OrdinalIgnoreCase))}.");
    }

    private static bool IsSameSentence(string text, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (text[i] is '.' or '!' or '?' or '…' or ':' or '\n' or '"' or '„' or '”') return false;
        }
        return true;
    }

    // ---- Rodzaj ----

    // Rzeczowniki w narzędniku kończące się na „-łem” („z aniołem”, „nad stołem”), które nie są
    // formą czasownika — bez tej listy byłyby fałszywym dowodem rodzaju męskiego. Tylko całe
    // słowa: końcówki typu „-szałem”, „-osłem”, „-ciałem” mają też czasowniki (usłyszałem,
    // niosłem, chciałem).
    private static readonly HashSet<string> MasculineLookingNouns = new(StringComparer.Ordinal)
    {
        "ciałem", "stołem", "kołem", "aniołem", "popiołem", "kościołem", "żywiołem", "dołem", "czołem",
        "piekłem", "diabłem", "kanałem", "sygnałem", "generałem", "admirałem", "materiałem", "kapitałem", "upałem",
        "zapałem", "kryształem", "ideałem", "potencjałem", "rytuałem", "arsenałem", "masłem", "hasłem", "krzesłem",
        "wiosłem", "rzemiosłem", "węzłem", "osłem", "posłem", "orłem", "mułem", "szałem", "wałem", "mydłem",
        "źródłem", "światłem", "godłem", "berłem", "zawałem",
        "tytułem", "skrzydłem", "szkłem", "kotłem", "wołem", "sokołem", "mozołem", "zespołem", "morałem",
        "pedałem", "chochołem",
    };

    // Końcówki rzeczowników w narzędniku, którymi nie kończy się żaden czasownik: „dział”
    // (rozdziałem, udziałem), „mysł” (pomysłem, zmysłem, umysłem, przemysłem), „dzieło”
    // (dziełem, arcydziełem), „strzał” (wystrzałem), „ogół/szczegół” — oraz przysłówek „ogółem”.
    private static readonly string[] MasculineLookingNounEndings = ["działem", "mysłem", "dziełem", "strzałem", "gółem"];

    // „-łam” bywa też trybem rozkazującym „łamać” z przedrostkiem („przełam”, „złam”, „połam”)
    // i czasem teraźniejszym czasowników na „-łać” („wołam”, „działam”, „wysyłam”) — żadne
    // z nich nie mówi nic o rodzaju. „wyłam” może być też „ja wyłam” (wyć), ale to rzadkie,
    // a brak dowodu jest lepszy niż fałszywy dowód.
    private static readonly HashSet<string> NonGenderedLamWords = new(StringComparer.Ordinal)
    {
        "złam", "przełam", "wyłam", "odłam", "połam", "załam", "nadłam", "ułam", "obłam", "rozłam", "włam",
    };

    private static readonly string[] NonGenderedLamEndings = ["wołam", "działam", "syłam"];

    /// <summary>
    /// Dowody rodzaju w tekście: czasowniki w 1. i 2. osobie czasu przeszłego
    /// (-łam/-łaś żeński, -łem/-łeś męski), trybu przypuszczającego (-łabym/-łabyś żeński,
    /// -łbym/-łbyś męski) oraz „gotowa”/„gotowy”. Znane rzeczowniki w narzędniku („pomysłem”),
    /// tryb rozkazujący („przełam”) i czas teraźniejszy („wołam”) nie są dowodem. „expect_gender” w korpusie
    /// opisuje rodzaj, którego wymagają te formy w danej linii (mówiącego albo adresata).
    /// </summary>
    public static (int Feminine, int Masculine) GenderEvidence(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (0, 0);
        int feminine = 0, masculine = 0;
        foreach (Match match in WordPattern().Matches(text))
        {
            var word = match.Value.ToLowerInvariant();
            if (word == "gotowa") feminine++;
            else if (word == "gotowy") masculine++;
            else if (word.Length >= 6 && (EndsWith(word, "łabym") || EndsWith(word, "łabyś"))) feminine++;
            else if (word.Length >= 5 && (EndsWith(word, "łbym") || EndsWith(word, "łbyś"))) masculine++;
            else if (word.Length >= 5 && (EndsWith(word, "łam") || EndsWith(word, "łaś"))
                     && !NonGenderedLamWords.Contains(word) && !NonGenderedLamEndings.Any(e => EndsWith(word, e)))
                feminine++;
            else if (word.Length >= 5 && (EndsWith(word, "łem") || EndsWith(word, "łeś"))
                     && !MasculineLookingNouns.Contains(word) && !MasculineLookingNounEndings.Any(e => EndsWith(word, e)))
                masculine++;
        }
        return (feminine, masculine);
    }

    private static bool EndsWith(string word, string ending) => word.EndsWith(ending, StringComparison.Ordinal);

    /// <summary>
    /// Zgłasza zły rodzaj tylko przy dowodzie: w tłumaczeniu są formy przeciwnego rodzaju
    /// i żadnej formy oczekiwanej. Zdanie bez form rodzajowych („Idę.”) nie jest błędem.
    /// </summary>
    public static TranslationCheckIssue? CheckGender(string hypothesis, string? expectGender)
    {
        var expected = expectGender?.Trim().ToLowerInvariant();
        if (expected is not ("f" or "m")) return null;
        var (feminine, masculine) = GenderEvidence(hypothesis);
        var (expectedCount, oppositeCount) = expected == "f" ? (feminine, masculine) : (masculine, feminine);
        if (oppositeCount == 0 || expectedCount > 0) return null;
        return new TranslationCheckIssue(TranslationCheckKind.Gender, expected == "f"
            ? "Rodzaj: oczekiwano form żeńskich (-łam/-łaś, gotowa), są męskie."
            : "Rodzaj: oczekiwano form męskich (-łem/-łeś, gotowy), są żeńskie.");
    }

    // ---- Słownik ----

    /// <summary>
    /// Rdzeń polskiego słowa do porównania z odmianą: pierwsze min(5, długość − 2) liter,
    /// małymi literami („Tarcza” → „tarc”, „Energii” → „energ”). Słowa krótsze niż 4 litery
    /// nie mają rdzenia (null) — „z”, „do”, „się” niczego nie dowodzą.
    /// </summary>
    public static string? PolishStem(string word)
    {
        var letters = word.Trim();
        if (letters.Length < 4) return null;
        return letters[..Math.Min(5, letters.Length - 2)].ToLowerInvariant();
    }

    /// <summary>
    /// Dla każdego terminu, którego angielski tekst występuje w oryginale (całe słowa, bez
    /// rozróżniania wielkości liter), sprawdza, czy tłumaczenie zawiera słowa zaczynające się
    /// od rdzeni wszystkich ≥4-literowych słów polskiego tłumaczenia terminu. Rdzeń zamiast
    /// dokładnego tekstu — „Tarcza Energii” w zdaniu bywa „Tarczy Energii”.
    /// </summary>
    public static IReadOnlyList<TranslationCheckIssue> CheckGlossary(string source, string hypothesis, IReadOnlyList<GlossaryTerm> terms)
    {
        if (terms.Count == 0) return [];
        var hypothesisWords = WordPattern().Matches(hypothesis).Select(static m => m.Value.ToLowerInvariant()).ToList();
        var issues = new List<TranslationCheckIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term.Source) || string.IsNullOrWhiteSpace(term.Target)) continue;
            if (!seen.Add(term.Source.Trim())) continue;
            if (!ContainsPhrase(source, term.Source.Trim())) continue;

            var stems = WordPattern().Matches(term.Target).Select(static m => PolishStem(m.Value))
                .OfType<string>().ToList();
            if (stems.Count == 0) continue;

            var missing = stems.Where(stem => !hypothesisWords.Any(word => word.StartsWith(stem, StringComparison.Ordinal))).ToList();
            if (missing.Count > 0)
            {
                issues.Add(new TranslationCheckIssue(TranslationCheckKind.Glossary,
                    $"Słownik: „{term.Source.Trim()}” → „{term.Target.Trim()}” nieużyty."));
            }
        }
        return issues;
    }

    private static bool ContainsPhrase(string text, string phrase)
    {
        var start = 0;
        while (start <= text.Length - phrase.Length)
        {
            var index = text.IndexOf(phrase, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var end = index + phrase.Length;
            if ((index == 0 || !char.IsLetterOrDigit(text[index - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end])))
                return true;
            start = index + 1;
        }
        return false;
    }

    // ---- Akapity i długość ----

    public static int ParagraphCount(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Count(static line => !string.IsNullOrWhiteSpace(line));

    /// <summary>
    /// Pipeline przywraca w tłumaczeniu tyle wierszy, ile miał oryginał z OCR (nakładka musi
    /// pasować do okienka gry). Inna liczba wierszy oznacza zlepione albo zgubione akapity.
    /// </summary>
    public static TranslationCheckIssue? CheckParagraphs(string source, string hypothesis)
    {
        var expected = ParagraphCount(source);
        var actual = ParagraphCount(hypothesis);
        return expected == actual
            ? null
            : new TranslationCheckIssue(TranslationCheckKind.Paragraphs,
                $"Wiersze: oryginał {expected}, tłumaczenie {actual}.");
    }

    /// <summary>Stosunek długości tłumaczenia do oryginału bez białych znaków (0, gdy oryginał pusty).</summary>
    public static double LengthRatio(string? source, string? hypothesis)
    {
        var sourceLength = CountNonWhitespace(source);
        return sourceLength == 0 ? 0 : (double)CountNonWhitespace(hypothesis) / sourceLength;
    }

    private static int CountNonWhitespace(string? text) => string.IsNullOrEmpty(text) ? 0 : text.Count(static ch => !char.IsWhiteSpace(ch));

    /// <summary>
    /// Polski tekst jest zwykle 1,0–1,4× dłuższy od angielskiego. Poza 0,5–2,0× to najczęściej
    /// ucięta odpowiedź, pominięte zdanie albo dopisany komentarz modelu.
    /// </summary>
    public static TranslationCheckIssue? CheckLength(double ratio) =>
        ratio is >= MinLengthRatio and <= MaxLengthRatio
            ? null
            : new TranslationCheckIssue(TranslationCheckKind.Length,
                $"Długość: {ratio.ToString("0.00", CultureInfo.InvariantCulture)}× oryginału (poza {MinLengthRatio.ToString("0.0", CultureInfo.InvariantCulture)}–{MaxLengthRatio.ToString("0.0", CultureInfo.InvariantCulture)}).");
}
