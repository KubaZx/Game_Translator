namespace GameTranslatorOverlay.Core.Translation;

/// <summary>
/// Pamięć ostatnich linii dialogu i ich tłumaczeń (od najstarszej) — tylko w pamięci procesu,
/// nigdy na dysku. Pipeline jest budowany od nowa przy zmianie dostawcy lub ustawień, więc
/// pary trafiają wyłącznie do dostawcy, który już je widział (sam je przetłumaczył).
/// Ograniczona liczbą par i łączną długością: długie opisy przedmiotów nie mogą rozdmuchać
/// każdego kolejnego zapytania (koszt tokenów i czas odpowiedzi modelu).
/// <para>
/// Obok par trzyma osobną kolejkę samych linii źródłowych dla dostawców przyjmujących kontekst
/// tylko w języku źródłowym (DeepL): ich budżet znaków liczy wyłącznie źródła (tłumaczenia nie
/// są im wysyłane, więc nie mogą wypychać kontekstu), a trafiają tam także linie, których wynik
/// nie nadaje się na przykład dla modelu (echo oryginału, „rozgadany” wynik) — angielski
/// oryginał jest wciąż poprawnym kontekstem rozmowy.
/// </para>
/// </summary>
public sealed class DialogMemory(int maxPairs, int maxChars)
{
    private readonly Lock _gate = new();
    private readonly LinkedList<RecentExchange> _pairs = new();
    private readonly LinkedList<string> _sources = new();
    private int _totalChars;
    private int _sourceChars;

    public int Count
    {
        get { lock (_gate) return _pairs.Count; }
    }

    /// <summary>Dopisuje pary na koniec; ta sama linia źródłowa przesuwa się na koniec z nowym tłumaczeniem.</summary>
    public void Remember(IEnumerable<RecentExchange> exchanges) =>
        RememberResults(exchanges.Select(static exchange => (exchange, true)));

    /// <summary>
    /// Jak <see cref="Remember(IEnumerable{RecentExchange})"/>, ale para z IsExample = false trafia
    /// tylko do kontekstu źródłowego (<see cref="SourcesExcluding"/>), a jej dawna para (jeśli
    /// była) znika — najnowszy wynik tej linii nie jest przykładem, którego model ma się trzymać.
    /// </summary>
    public void RememberResults(IEnumerable<(RecentExchange Exchange, bool IsExample)> results)
    {
        if (maxPairs <= 0 || maxChars <= 0) return;
        lock (_gate)
        {
            foreach (var (exchange, isExample) in results)
            {
                RememberSource(exchange.Source);
                if (!isExample)
                {
                    RemovePair(exchange.Source);
                    continue;
                }
                // Para dłuższa niż cały budżet wypchnęłaby wszystkie wcześniejsze linie, a sama
                // i tak by się nie zmieściła — pomijamy ją, zostawiając dotychczasowy kontekst.
                if (Size(exchange) > maxChars) continue;
                RemovePair(exchange.Source);
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
    /// Same linie źródłowe od najstarszej, bez linii bieżącej partii — kontekst dla dostawców,
    /// którzy przyjmują tylko tekst w języku źródłowym.
    /// </summary>
    public IReadOnlyList<string> SourcesExcluding(IReadOnlyList<string> currentSources)
    {
        lock (_gate)
        {
            if (_sources.Count == 0) return [];
            var current = currentSources.ToHashSet(StringComparer.Ordinal);
            return _sources.Where(source => !current.Contains(source)).ToList();
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

    private void RememberSource(string source)
    {
        // Linia dłuższa niż cały budżet wypchnęłaby wszystkie wcześniejsze — pomijamy ją.
        if (source.Length > maxChars) return;
        for (var node = _sources.First; node is not null; node = node.Next)
        {
            if (!node.Value.Equals(source, StringComparison.Ordinal)) continue;
            _sourceChars -= node.Value.Length;
            _sources.Remove(node);
            break;
        }
        _sources.AddLast(source);
        _sourceChars += source.Length;
        while (_sources.Count > maxPairs || _sourceChars > maxChars)
        {
            _sourceChars -= _sources.First!.Value.Length;
            _sources.RemoveFirst();
        }
    }

    private void RemovePair(string source)
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
