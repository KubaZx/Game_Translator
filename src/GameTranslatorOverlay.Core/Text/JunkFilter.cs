namespace GameTranslatorOverlay.Core.Text;

/// <summary>
/// Odsiewa śmieciowe wyniki OCR, których nie warto tłumaczyć (pojedyncze znaki,
/// zbitki symboli, gołe liczby bez kontekstu). Fragmenty statystyk typu „+25%”
/// albo „10–15” przechodzą, bo niosą znaczenie w tooltipach gier.
/// </summary>
public static class JunkFilter
{
    private static readonly char[] StatMarkers = ['%', '+', '-', '–', '—', '/', '×', 'x', ':'];

    public static bool IsMeaningful(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.Trim();
        if (t.Length < 2) return false;

        var letters = 0;
        var digits = 0;
        var acceptable = 0;

        foreach (var ch in t)
        {
            if (char.IsLetter(ch)) letters++;
            else if (char.IsDigit(ch)) digits++;

            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || IsCommonPunctuation(ch))
            {
                acceptable++;
            }
        }

        if (letters == 0 && digits == 0) return false;

        if (letters == 0)
        {
            // Goła liczba (np. licznik FPS, ilość w stacku) — tłumaczenie nic nie wnosi.
            // Zostawiamy tylko fragmenty statystyk z wyraźnym znacznikiem (+10, 24%, 10–15, 3/5, x2).
            if (t.IndexOfAny(StatMarkers) < 0) return false;
        }

        // Zbitki symboli z pojedynczą literą/cyfrą w środku to niemal zawsze artefakt OCR.
        return acceptable >= (t.Length + 1) / 2;
    }

    private static bool IsCommonPunctuation(char ch) =>
        ch is '.' or ',' or ':' or ';' or '!' or '?' or '\'' or '"' or '(' or ')' or '[' or ']'
        or '%' or '+' or '-' or '–' or '—' or '/' or '×' or '’' or '“' or '”' or '&';
}
