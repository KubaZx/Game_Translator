using System.Text;
using System.Text.RegularExpressions;

namespace GameTranslatorOverlay.Core.Corpus;

public static partial class CorpusText
{
    [GeneratedRegex(@"<\s*/?\s*(?:b|i|u|s|color|size|material|quad|sprite|link|mark|font|align|alpha|cspace|indent|line-height|lowercase|uppercase|smallcaps|nobr|noparse|page|pos|space|style|sub|sup|voffset|width|margin|rotate|gradient)\b[^<>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RichTextTag();

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreakTag();

    public static string StripRichText(string text)
    {
        if (text.IndexOf('<') < 0) return text;
        var withBreaks = LineBreakTag().Replace(text, "\n");
        return RichTextTag().Replace(withBreaks, string.Empty);
    }

    public static string UnescapeLineBreaks(string text) =>
        text.Contains(@"\n", StringComparison.Ordinal) ? text.Replace(@"\n", "\n", StringComparison.Ordinal) : text;

    public static string MatchKey(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var source = StripRichText(UnescapeLineBreaks(text));
        var builder = new StringBuilder(source.Length);
        var pendingSpace = false;
        foreach (var raw in source)
        {
            if (raw is '​' or '‌' or '‍' or '﻿' or '­') continue;
            if (char.IsWhiteSpace(raw) || char.IsControl(raw))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            switch (raw)
            {
                case '‘' or '’' or '‚' or '‛' or '′' or '`' or '´':
                    builder.Append('\'');
                    break;
                case '“' or '”' or '„' or '″' or '«' or '»':
                    builder.Append('"');
                    break;
                case '‐' or '‑' or '‒' or '–' or '—' or '―' or '−':
                    builder.Append('-');
                    break;
                case '…':
                    builder.Append("...");
                    break;
                default:
                    builder.Append(char.ToLowerInvariant(raw));
                    break;
            }
        }
        return builder.ToString();
    }

    public static string LooseKey(string key)
    {
        var start = 0;
        var end = key.Length;
        while (start < end && !char.IsLetterOrDigit(key[start])) start++;
        while (end > start && !char.IsLetterOrDigit(key[end - 1])) end--;
        return start == 0 && end == key.Length ? key : key[start..end];
    }

    public static string DigitSignature(string text)
    {
        StringBuilder? builder = null;
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
            var touchesLetter = (start > 0 && char.IsLetter(text[start - 1])) || (i < text.Length && char.IsLetter(text[i]));
            if (touchesLetter) continue;
            builder ??= new StringBuilder();
            if (builder.Length > 0) builder.Append('|');
            builder.Append(text, start, i - start);
        }
        return builder?.ToString() ?? string.Empty;
    }

    public static int WordCount(string key)
    {
        var count = 0;
        var inWord = false;
        foreach (var ch in key)
        {
            if (ch == ' ')
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }
        return count;
    }

    public static int LetterOrDigitCount(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) count++;
        }
        return count;
    }

    internal static bool IsBoundary(string text, int index) =>
        index <= 0 || index >= text.Length || !char.IsLetterOrDigit(text[index - 1]) || !char.IsLetterOrDigit(text[index]);
}
