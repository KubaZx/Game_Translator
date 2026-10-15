using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Text;

public static class TextBlockSplitter
{
    public static IReadOnlyList<TextBlock> SplitAlong(IReadOnlyList<TextBlock> blocks, IReadOnlyList<RectPx> anchors)
    {
        if (anchors.Count < 2) return blocks;
        List<TextBlock>? result = null;
        for (var b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            var owners = block.Lines.Count < 2 ? null : block.Lines.Select(line => OwnerOf(line.Box, anchors)).ToArray();
            if (owners is null || owners.Any(static o => o < 0) || owners.Distinct().Count() < 2)
            {
                result?.Add(block);
                continue;
            }
            result ??= blocks.Take(b).ToList();
            foreach (var part in block.Lines.Select((line, i) => (Line: line, Owner: owners[i])).GroupBy(static x => x.Owner))
                result.AddRange(TextBlockGrouper.Group(part.Select(static x => x.Line).ToList()));
        }
        return result ?? blocks;
    }

    public static IReadOnlyList<TextBlock> SplitLabels(IReadOnlyList<TextBlock> blocks, Func<RectPx, int> textColorOf, int minChannelDifference = 80)
    {
        List<TextBlock>? result = null;
        for (var b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            var ordered = block.Lines.OrderBy(static l => l.Box.Y).ToList();
            if (ordered.Count < 2 || !IsLabel(ordered[0], ordered[1]) || !DifferentColor(ordered[0], ordered.Skip(1), textColorOf, minChannelDifference))
            {
                result?.Add(block);
                continue;
            }
            result ??= blocks.Take(b).ToList();
            result.AddRange(TextBlockGrouper.Group([ordered[0]]));
            result.AddRange(TextBlockGrouper.Group(ordered.Skip(1).ToList()));
        }
        return result ?? blocks;
    }

    private static bool IsLabel(OcrLine label, OcrLine next) =>
        label.Words.Count is >= 1 and <= 3 && label.Text.Trim().Length <= 30 && label.Box.Bottom <= next.Box.Y + label.Box.Height / 4;

    private static bool DifferentColor(OcrLine label, IEnumerable<OcrLine> rest, Func<RectPx, int> textColorOf, int minChannelDifference)
    {
        var labelColor = textColorOf(label.Box);
        if (labelColor < 0) return false;
        var others = rest.Select(l => textColorOf(l.Box)).Where(static c => c >= 0).ToList();
        if (others.Count == 0) return false;
        foreach (var other in others)
        {
            var difference = Math.Max(Math.Abs(((labelColor >> 16) & 0xFF) - ((other >> 16) & 0xFF)),
                Math.Max(Math.Abs(((labelColor >> 8) & 0xFF) - ((other >> 8) & 0xFF)), Math.Abs((labelColor & 0xFF) - (other & 0xFF))));
            if (difference < minChannelDifference) return false;
        }
        return true;
    }

    private static int OwnerOf(RectPx line, IReadOnlyList<RectPx> anchors)
    {
        var owner = -1;
        long best = 0;
        var area = (long)line.Width * line.Height;
        for (var k = 0; k < anchors.Count; k++)
        {
            var overlap = anchors[k].Intersect(line);
            var shared = overlap.IsEmpty ? 0 : (long)overlap.Width * overlap.Height;
            if (shared * 2 < area || shared <= best) continue;
            best = shared;
            owner = k;
        }
        return owner;
    }
}
