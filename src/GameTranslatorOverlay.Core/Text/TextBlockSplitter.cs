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
