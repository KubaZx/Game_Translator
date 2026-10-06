using System.Globalization;
using System.Text.RegularExpressions;

namespace GameTranslatorOverlay.CorpusTool.Parsing;

public sealed record SrtCue(int Index, int StartMs, int EndMs, string Text)
{
    public int DurationMs => Math.Max(0, EndMs - StartMs);
}

public static partial class SrtParser
{
    [GeneratedRegex(@"^\s*(\d{1,2}):(\d{1,2}):(\d{1,3})[,.](\d{1,3})\s*-->\s*(\d{1,2}):(\d{1,2}):(\d{1,3})[,.](\d{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex TimingLine();

    public static bool LooksLikeSrt(string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimStart().Split('\n', 3);
        return lines.Length >= 2 && int.TryParse(lines[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && TimingLine().IsMatch(lines[1]);
    }

    public static IReadOnlyList<SrtCue> Parse(string text)
    {
        var cues = new List<SrtCue>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            while (i < lines.Length && lines[i].Trim().Length == 0) i++;
            if (i >= lines.Length) break;

            var indexLine = lines[i].Trim();
            if (!int.TryParse(indexLine, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || i + 1 >= lines.Length)
            {
                i++;
                continue;
            }
            var timing = TimingLine().Match(lines[i + 1]);
            if (!timing.Success)
            {
                i++;
                continue;
            }
            i += 2;
            var textLines = new List<string>();
            while (i < lines.Length && lines[i].Trim().Length > 0)
            {
                textLines.Add(lines[i].TrimEnd());
                i++;
            }
            cues.Add(new SrtCue(index, ToMs(timing, 1), ToMs(timing, 5), string.Join('\n', textLines)));
        }
        return cues;
    }

    private static int ToMs(Match match, int firstGroup)
    {
        int Group(int offset) => int.Parse(match.Groups[firstGroup + offset].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups[firstGroup + 3].Value.PadRight(3, '0');
        var ms = int.Parse(fraction, CultureInfo.InvariantCulture);
        return ((Group(0) * 60 + Group(1)) * 60 + Group(2)) * 1000 + ms;
    }
}
