using System.Text;

namespace GameTranslatorOverlay.Core.Text;

/// <summary>
/// Normalizacja tekstu z OCR. Zachowuje znaki istotne dla statystyk gier
/// (+25%, 10–15, 1.5 seconds, Level 20, 3/5, x2, -10%), usuwa artefakty
/// (znaki zerowej szerokości, twarde spacje, nadmiarowe odstępy, puste linie).
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        // Tekst już znormalizowany (typowo: wynik poprzedniej normalizacji, który pipeline
        // przekazuje dalej, albo czysty wiersz OCR) wraca bez kopiowania — tryb live normalizuje
        // te same teksty w każdej klatce, kilka razy na tekst.
        return IsTriviallyNormalized(raw) ? raw : NormalizeFull(raw);
    }

    /// <summary>Pełna normalizacja bez skrótu dla tekstu już czystego (testy porównują obie ścieżki).</summary>
    internal static string NormalizeFull(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var text = raw.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            // Znaki zerowej szerokości i BOM — czyste artefakty.
            if (ch is '​' or '‌' or '‍' or '﻿')
            {
                continue;
            }
            // Twarda spacja → zwykła spacja.
            if (ch == ' ')
            {
                builder.Append(' ');
                continue;
            }
            if (char.IsControl(ch) && ch != '\n' && ch != '\r' && ch != '\t')
            {
                continue;
            }
            builder.Append(ch);
        }

        var lines = builder.ToString()
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(NormalizeLine)
            .Where(static line => line.Length > 0);

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Zachowawczy test „Normalize zwróciłby ten sam tekst”: tylko znaki z zakresu, w którym
    /// nic nie podlega zamianie (bez znaków sterujących, twardej spacji, znaków zerowej szerokości
    /// i znaków łączących — więc NFC niczego nie składa), pojedyncze spacje wewnątrz wierszy,
    /// bez pustych wierszy i bez odstępów na brzegach wiersza. Wszystko inne idzie pełną ścieżką.
    /// </summary>
    internal static bool IsTriviallyNormalized(string text)
    {
        var previous = '\n';
        foreach (var ch in text)
        {
            if (ch == ' ')
            {
                // Spacja na początku wiersza albo podwójna spacja.
                if (previous is ' ' or '\n') return false;
            }
            else if (ch == '\n')
            {
                // Pusty wiersz albo spacja na końcu wiersza.
                if (previous is ' ' or '\n') return false;
            }
            else if (!IsPlainCharacter(ch))
            {
                return false;
            }
            previous = ch;
        }
        // Spacja albo pusty wiersz na końcu tekstu.
        return previous is not (' ' or '\n');
    }

    // Widoczne ASCII oraz Latin-1 i Latin Extended-A od U+00A1 (bez U+0085 i twardej spacji
    // U+00A0) i bez miękkiego łącznika U+00AD (znak formatujący — zachowawczo pełna ścieżka).
    // Każdy z tych znaków jest w postaci NFC, żadne dwa nie składają się w inny znak, żaden nie
    // jest odstępem ani znakiem sterującym. Polskie litery mieszczą się w Latin Extended-A.
    private static bool IsPlainCharacter(char ch) =>
        (ch is (>= '\u0021' and <= '\u007E') or (>= '\u00A1' and <= '\u017F')) && ch != '\u00AD';

    public static string NormalizeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return string.Empty;

        var builder = new StringBuilder(line.Length);
        var previousWasSpace = false;

        foreach (var ch in line)
        {
            if (ch is ' ' or '\t')
            {
                if (!previousWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }
                previousWasSpace = true;
            }
            else
            {
                builder.Append(ch);
                previousWasSpace = false;
            }
        }

        return builder.ToString().TrimEnd();
    }
}
