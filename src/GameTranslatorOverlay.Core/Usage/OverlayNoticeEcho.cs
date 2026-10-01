using System.Text;
using GameTranslatorOverlay.Core.Ocr;
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
                if (key.Length < MinFuzzyLength || known.Length < MinFuzzyLength) continue;
                // Ratio = 1 − odległość/dłuższy, a odległość ≥ różnica długości — przy różnicy
                // ponad 20% próg jest nieosiągalny. Odcinamy to przed pełnym Levenshteinem,
                // bo ta pętla biegnie dla każdego bloku w każdej klatce live.
                var longer = Math.Max(known.Length, key.Length);
                if (Math.Abs(known.Length - key.Length) > (1 - SimilarityThreshold) * longer) continue;
                if (TextSimilarity.Ratio(known, key) >= SimilarityThreshold) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Usuwa linie OCR będące naszym komunikatem, zanim grupowanie sklei je z tekstem gry:
    /// komunikat leży tuż nad tytułem/HUD-em, a blok „⚠ Brak klucza DeepL Chapter 3 …”
    /// nie przypomina już komunikatu i poszedłby do dostawcy. Bez echa zwraca tę samą listę.
    /// </summary>
    public IReadOnlyList<OcrLine> RemoveEchoLines(IReadOnlyList<OcrLine> lines)
    {
        lock (_gate)
        {
            if (_keys.Count == 0) return lines;
        }
        List<OcrLine>? kept = null;
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsEcho(lines[i].Text))
            {
                kept ??= new List<OcrLine>(lines.Take(i));
                continue;
            }
            kept?.Add(lines[i]);
        }
        return kept ?? lines;
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
