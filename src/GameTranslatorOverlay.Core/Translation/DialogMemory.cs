namespace GameTranslatorOverlay.Core.Translation;

/// <summary>
/// Pamięć ostatnich linii dialogu i ich tłumaczeń (od najstarszej) — tylko w pamięci procesu,
/// nigdy na dysku. Pipeline jest budowany od nowa przy zmianie dostawcy lub ustawień, więc
/// pary trafiają wyłącznie do dostawcy, który już je widział (sam je przetłumaczył).
/// Ograniczona liczbą par i łączną długością: długie opisy przedmiotów nie mogą rozdmuchać
/// każdego kolejnego zapytania (koszt tokenów i czas odpowiedzi modelu).
/// </summary>
public sealed class DialogMemory(int maxPairs, int maxChars)
{
    private readonly Lock _gate = new();
    private readonly LinkedList<RecentExchange> _pairs = new();
    private int _totalChars;

    public int Count
    {
        get { lock (_gate) return _pairs.Count; }
    }

    /// <summary>Dopisuje pary na koniec; ta sama linia źródłowa przesuwa się na koniec z nowym tłumaczeniem.</summary>
    public void Remember(IEnumerable<RecentExchange> exchanges)
    {
        if (maxPairs <= 0 || maxChars <= 0) return;
        lock (_gate)
        {
            foreach (var exchange in exchanges)
            {
                // Para dłuższa niż cały budżet wypchnęłaby wszystkie wcześniejsze linie, a sama
                // i tak by się nie zmieściła — pomijamy ją, zostawiając dotychczasowy kontekst.
                if (Size(exchange) > maxChars) continue;
                RemoveSource(exchange.Source);
                _pairs.AddLast(exchange);
                _totalChars += Size(exchange);
            }
            while (_pairs.Count > maxPairs || _totalChars > maxChars) RemoveFirst();
        }
    }

    /// <summary>
    /// Pary od najstarszej, bez linii tłumaczonych w bieżącej partii — model nie może dostać
    /// gotowej odpowiedzi (starego tłumaczenia) dla tekstu, który ma właśnie przetłumaczyć.
    /// </summary>
    public IReadOnlyList<RecentExchange> Excluding(IReadOnlyList<string> currentSources)
    {
        lock (_gate)
        {
            if (_pairs.Count == 0) return [];
            var current = currentSources.ToHashSet(StringComparer.Ordinal);
            return _pairs.Where(pair => !current.Contains(pair.Source)).ToList();
        }
    }

    /// <summary>
    /// Podmienia tłumaczenie zapamiętanej linii (ręczna korekta gracza) bez zmiany jej miejsca
    /// w kolejności. Zwraca false, gdy linii nie ma w pamięci — korekta niczego wtedy nie dodaje,
    /// bo pamięć ma odtwarzać przebieg rozmowy, a nie zbierać dowolne teksty.
    /// </summary>
    public bool ReplaceTranslation(string source, string translation)
    {
        if (string.IsNullOrWhiteSpace(translation)) return false;
        lock (_gate)
        {
            for (var node = _pairs.First; node is not null; node = node.Next)
            {
                if (!node.Value.Source.Equals(source, StringComparison.Ordinal)) continue;
                var replaced = node.Value with { Translation = translation };
                _totalChars += Size(replaced) - Size(node.Value);
                node.Value = replaced;
                // Dłuższa korekta może przekroczyć budżet — wtedy odpadają najstarsze linie
                // (także ta, jeśli jest najstarsza), tak jak przy zwykłym dopisywaniu.
                while (_totalChars > maxChars && _pairs.Count > 0) RemoveFirst();
                return true;
            }
            return false;
        }
    }

    private void RemoveSource(string source)
    {
        for (var node = _pairs.First; node is not null; node = node.Next)
        {
            if (!node.Value.Source.Equals(source, StringComparison.Ordinal)) continue;
            _totalChars -= Size(node.Value);
            _pairs.Remove(node);
            return;
        }
    }

    private void RemoveFirst()
    {
        _totalChars -= Size(_pairs.First!.Value);
        _pairs.RemoveFirst();
    }

    private static int Size(RecentExchange exchange) => exchange.Source.Length + exchange.Translation.Length;
}
