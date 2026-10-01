using System.Text;
using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Core.Usage;

/// <summary>
/// Filtr anty-sprzężeniowy dla komunikatów nakładki: gdy wykluczenie nakładki
/// z przechwytywania nie działa, OCR widzi nasz własny komunikat („⚠ Brak klucza DeepL”)
/// i wysłałby go do płatnego dostawcy, a tłumaczenie pokazałby jako napis. Pamiętamy
/// ostatnie pokazane komunikaty i rozpoznajemy ich odczyty mimo zgubionej ikony
/// (⚠ → „A”) czy drobnych błędów OCR. Bezpieczny wątkowo: komunikaty dopisuje wątek UI,
/// a sprawdza pętla live.
/// </summary>
public sealed class OverlayNoticeEcho
{
    private const int Capacity = 32;
    private const double SimilarityThreshold = 0.8;

    // Krótszych kluczy nie porównujemy przybliżenie — „OK” z gry nie może zniknąć
    // tylko dlatego, że przypomina fragment naszego komunikatu.
    private const int MinFuzzyLength = 8;

    private readonly Lock _gate = new();
    private readonly LinkedList<string> _keys = new();

    /// <summary>Zapamiętuje tekst pokazanego (albo właśnie pokazywanego) komunikatu.</summary>
    public void Remember(string noticeText)
    {
        var key = Key(noticeText);
        if (key.Length == 0) return;
        lock (_gate)
        {
            _keys.Remove(key);
            _keys.AddLast(key);
            while (_keys.Count > Capacity) _keys.RemoveFirst();
        }
    }

    /// <summary>Czy odczyt OCR to nasz własny komunikat (a nie tekst gry).</summary>
    public bool IsEcho(string ocrText)
    {
        var key = Key(ocrText);
        if (key.Length == 0) return false;
        lock (_gate)
        {
            foreach (var known in _keys)
            {
                if (known == key) return true;
                if (key.Length >= MinFuzzyLength && known.Length >= MinFuzzyLength
                    && TextSimilarity.Ratio(known, key) >= SimilarityThreshold)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Same litery i cyfry, małymi literami: ikony (⚠, ⏳, ▶), myślniki i odstępy OCR
    /// czyta różnie albo wcale, a o tożsamości komunikatu decydują słowa.
    /// Ikona zgubiona albo przeczytana jako jedna litera na początku nie zmienia wyniku.
    /// </summary>
    private static string Key(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
