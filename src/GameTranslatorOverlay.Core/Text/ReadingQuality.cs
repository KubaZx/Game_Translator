namespace GameTranslatorOverlay.Core.Text;

/// <summary>
/// Ocena „czystości” odczytu OCR w skali 0–1. Śmieciowe odczyty nad ruchomą grafiką
/// („lRrgIé@ue”, „Pr016gue”, „•LastlPlaVed?OR08i2026”) mają znaki spoza alfabetu,
/// wielkie litery w środku słowa albo cyfry wklejone między litery — poprawny tekst
/// gry prawie nigdy. Dzięki temu gorszy odczyt nie wypiera lepszego na nakładce.
/// </summary>
public static class ReadingQuality
{
    public static double Score(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return 0;

        var bad = 0;
        var openBrackets = 0;
        for (var i = 0; i < t.Length; i++)
        {
            var ch = t[i];
            var previous = i > 0 ? t[i - 1] : ' ';
            var next = i + 1 < t.Length ? t[i + 1] : ' ';

            if (!(char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || IsCommonPunctuation(ch)))
            {
                bad++;
                continue;
            }

            // Zamykający nawias bez otwierającego („Prologue) *e.n”) to strzęp grafiki, nie tekst.
            if (ch is '(' or '[') openBrackets++;
            if (ch is ')' or ']')
            {
                if (openBrackets == 0) { bad++; continue; }
                openBrackets--;
            }

            // Wielka litera po małej wewnątrz słowa („PlaVed”) — wyjątek: po apostrofie/łączniku.
            if (char.IsUpper(ch) && char.IsLower(previous))
            {
                bad++;
                continue;
            }

            // Cyfra sklejona z literą („Pr016gue”, „OR08i”) — wyjątek: krótkie kody jak „Lv20”, „T3”
            // są rzadkie w zdaniach, a w dłuższych słowach to niemal zawsze artefakt.
            if (char.IsDigit(ch) && (char.IsLetter(previous) || char.IsLetter(next)))
            {
                bad++;
                continue;
            }

            // Znak zapytania w środku słowa („Last?lgyed”).
            if (ch == '?' && char.IsLetter(next))
            {
                bad++;
            }
        }

        return Math.Clamp(1.0 - (double)bad / t.Length, 0, 1);
    }

    private static bool IsCommonPunctuation(char ch) =>
        ch is '.' or ',' or ':' or ';' or '!' or '?' or '\'' or '"' or '(' or ')' or '[' or ']'
        or '%' or '+' or '-' or '–' or '—' or '/' or '×' or '’' or '“' or '”' or '&';
}
