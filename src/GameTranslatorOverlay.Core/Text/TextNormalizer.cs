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
