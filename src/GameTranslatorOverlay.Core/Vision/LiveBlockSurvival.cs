using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Vision;

/// <summary>
/// Blok utrzymywany na nakładce między przebiegami OCR trybu live.
/// <paramref name="Misses"/> zlicza kolejne przebiegi, w których OCR bloku nie widział.
/// </summary>
public sealed record LiveOverlayBlock(
    RectPx WindowRelativeBox,
    string TranslatedText,
    string NormalizedTranslation,
    int LineHeight,
    int ColorRgb,
    int BackgroundRgb = -1,
    int Misses = 0);

/// <summary>
/// Okres łaski dla bloków nakładki: Windows OCR potrafi na niezmienionej scenie
/// zwrócić pusty wynik (zmierzono na żywym PoE2 — 0 bloków między dwoma przebiegami
/// widzącymi komplet), a natychmiastowe zdejmowanie bloków zamienia takie czknięcia
/// w miganie całej nakładki. Blok znika dopiero po serii nieobecności — chyba że
/// scena naprawdę się zmieniła (cięcie), wtedy łaska nie obowiązuje.
/// </summary>
public static class LiveBlockSurvival
{
    /// <summary>
    /// Ułamek pola zgubionego bloku, który musi zająć świeżo rozpoznany blok,
    /// żeby zgubiony został natychmiast zdjęty (tekst zmienił się „w miejscu” —
    /// np. kolejna linia dialogu; bez eksmisji stary wisiałby obok nowego).
    /// </summary>
    private const double ClaimOverlapFraction = 0.4;

    /// <summary>
    /// Bloki, które przetrwają bieżący przebieg mimo nieobecności w jego wynikach.
    /// Zwraca pary (klucz, blok z podbitym licznikiem nieobecności).
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, LiveOverlayBlock>> Survivors(
        IReadOnlyDictionary<string, LiveOverlayBlock> displayed,
        IReadOnlySet<string> presentKeys,
        IReadOnlyCollection<RectPx> claimedBoxes,
        bool sceneCut,
        int missGrace)
    {
        if (sceneCut || missGrace <= 0 || displayed.Count == 0)
        {
            return [];
        }

        var survivors = new List<KeyValuePair<string, LiveOverlayBlock>>();
        foreach (var (key, block) in displayed)
        {
            if (presentKeys.Contains(key)) continue;
            if (block.Misses >= missGrace) continue;
            if (IsClaimed(block.WindowRelativeBox, claimedBoxes)) continue;
            survivors.Add(new(key, block with { Misses = block.Misses + 1 }));
        }

        return survivors;
    }

    private static bool IsClaimed(RectPx box, IReadOnlyCollection<RectPx> claimedBoxes)
    {
        long area = (long)box.Width * box.Height;
        if (area <= 0) return false;

        foreach (var claimed in claimedBoxes)
        {
            var overlap = box.Intersect(claimed);
            if (overlap.IsEmpty) continue;
            if ((long)overlap.Width * overlap.Height >= area * ClaimOverlapFraction)
            {
                return true;
            }
        }

        return false;
    }
}
