# ProviderEval — porównanie dostawców tłumaczeń

Konsolowe narzędzie (net10.0, bez WPF — działa także na Linuksie), które przepuszcza korpus
EN→PL przez tych samych dostawców i ten sam `TranslationPipeline`, co aplikacja, a potem
liczy **chrF** i **kontrole jakości**. Służy do obiektywnego porównania: DeepL vs Claude,
stary prompt vs nowy, model A vs model B.

Nie używa OCR, okna gry ani ustawień aplikacji. Cache tłumaczeń jest tylko w pamięci —
narzędzie niczego nie zapisuje w bazie tłumaczeń gracza.

## Uruchomienie

```bash
# test dymny bez internetu i bez kluczy (Mock dopisuje „[PL] ” — chrF będzie niski i to jest OK)
dotnet run --project tools/GameTranslatorOverlay.ProviderEval -c Release -- --providers mock

# prawdziwi dostawcy — klucze WYŁĄCZNIE ze zmiennych środowiskowych
export GTO_DEEPL_KEY=...            # DeepL
export GTO_AZURE_KEY=...            # Azure AI Translator
export GTO_AZURE_REGION=westeurope  # opcjonalnie (zasób globalny działa bez regionu)
export GTO_GOOGLE_KEY=...           # Google Cloud Translation
export ANTHROPIC_API_KEY=...        # Claude
export GTO_CLAUDE_MODEL=claude-sonnet-5-5   # opcjonalnie, domyślnie jak w aplikacji
export GTO_LLM_ENDPOINT=http://localhost:11434/v1  # serwer zgodny z OpenAI (Ollama, LM Studio, OpenAI)
export GTO_LLM_MODEL=qwen3:14b
export GTO_LLM_KEY=...              # opcjonalnie; wysyłany tylko na adres z GTO_LLM_ENDPOINT

dotnet run --project tools/GameTranslatorOverlay.ProviderEval -c Release -- \
  --providers deepl,claude,llm --variant "prompt-v2" --out eval/out/prompt-v2
```

W PowerShellu: `$env:GTO_DEEPL_KEY = "..."`.

| Opcja | Znaczenie |
|---|---|
| `--corpus PLIK` | Korpus JSONL (domyślnie `eval/en-pl.sample.jsonl`; format w [eval/README.md](../../eval/README.md)). |
| `--providers LISTA` | `mock`, `deepl`, `azure`, `google`, `claude`, `llm` po przecinku (domyślnie `mock`). |
| `--out KATALOG` | Gdzie zapisać `report.md` i `results.csv` (domyślnie `eval/out`, ignorowany przez git). |
| `--limit N` | Tylko pierwsze N linii w kolejności odtwarzania — tani test przed pełnym przebiegiem. |
| `--variant ETYKIETA` | Opis wariantu w raporcie i CSV (np. nazwa gałęzi albo wersji promptu). |
| `--glossary PLIK` | Dodatkowy słownik JSON w formacie `glossaries/*.json`, obok pól `terms` z korpusu. |
| `--game NAZWA` | Nazwa gry przekazywana modelom językowym, jak nazwa z profilu w aplikacji. |
| `--no-deepl-glossary` | DeepL bez glosariusza — nic nie powstaje na koncie DeepL. |

Dostawca bez klucza jest **pomijany** z komunikatem, która zmienna jest pusta. Klucze nigdy
nie są wypisywane ani zapisywane w raporcie. Kod wyjścia: 0 — sukces, 1 — błąd korpusu,
słownika lub zapisu, 2 — złe opcje, 3 — żaden dostawca nie został uruchomiony.

## Jak działa przebieg

- Linie są odtwarzane **scenami** (kolejność pierwszego wystąpienia sceny, w scenie kolejność
  z pliku). Każda linia to osobne zapytanie, jak kolejne napisy w grze, więc kontekst
  ostatnich kwestii dialogu (DeepL `context`, prompt LLM) działa tak jak w aplikacji.
- Terminy z pól `terms` (i z `--glossary`) trafiają do słownika pipeline'u: linia równa
  terminowi jest tłumaczona lokalnie, a modele językowe i glosariusz DeepL dostają terminy
  jak w aplikacji.
- **Czas** to czas jednego wywołania pipeline'u dla linii, liczony tylko wtedy, gdy odpowiedział
  dostawca (bez trafień w słownik i cache). Raport podaje medianę i p90 (`LatencyMonitor`).
  Pierwsze zapytanie zawiera zestawienie połączenia (DNS, TLS) — przy małym korpusie widać
  to w p90.

## Raport

- `report.md` — tabela zbiorcza (linie przetłumaczone, chrF korpusu, mediana i p90 czasu,
  błędy dostawcy, liczba linii z uwagami i liczniki każdej kontroli), pominięci dostawcy,
  a potem 10 najgorszych linii każdego dostawcy (najpierw nieprzetłumaczone, potem
  najniższy chrF) z oryginałem, referencją, tłumaczeniem i uwagami.
- `results.csv` — wszystkie linie wszystkich dostawców (UTF-8 z BOM, kropka dziesiętna),
  do własnych analiz w arkuszu.

## chrF — co mierzy i czego nie

chrF (Popović 2015) porównuje **n-gramy znakowe** (1–6 znaków, bez spacji) tłumaczenia
i referencji: precyzja (ile z tłumaczenia jest w referencji) i pełność (ile z referencji jest
w tłumaczeniu), uśrednione po długościach n-gramów, złożone w F-beta z beta = 2 (pełność
ważniejsza — zgubiony fragment boli bardziej niż dodatkowe słowo). Implementacja odpowiada
domyślnemu `chrF` z sacreBLEU; wynik korpusu sumuje statystyki wszystkich linii
(`corpus_chrf`), a nie uśrednia wyników linii. Linia bez tłumaczenia liczy się jak pusta.

Znaki zamiast słów, bo polski się odmienia: „Tarczy Energii” zamiast „Tarcza Energii”
dostaje częściowe punkty, a nie zero.

Ograniczenia — czytaj wyniki ostrożnie:

- **Jedna referencja karze poprawne parafrazy.** „Wczytaj grę” i „Wczytaj zapis” są równie
  dobre, a chrF da drugiej niski wynik. Wynik bezwzględny (np. 55) niewiele mówi.
- Używaj go **do porównań względnych** na tym samym korpusie: dostawca A vs B, prompt
  przed i po zmianie.
- **Mały korpus = szum.** Przy ~40 liniach różnica 1–2 punktów może być przypadkiem;
  patrz na najgorsze linie i uwagi kontroli, nie tylko na liczbę. Dla decyzji zbierz
  większy korpus własnych linii w `eval/private/`.
- Mock zwraca angielski tekst z prefiksem „[PL] ”, więc jego chrF jest bliski zera —
  to tylko test, że narzędzie działa.

## Kontrole

Heurystyki, które zgłaszają problem tylko przy dowodzie:

| Kontrola | Co zgłasza |
|---|---|
| Liczby | Liczba ze źródła zgubiona albo zmieniona („+15%” → „+51%”). „1.5” = „1,5”, „1,250” = „1250” = „1 250”. |
| Pan/Pani | „Pan”, „Pani”, „Państwo” (i odmiany) zamiast „ty”. Pomija tytuły przed nazwą („Pan Ciemności”), zdania ze „sir/lady/lord…” w oryginale i formy obecne w referencji. |
| Rodzaj | Dla `expect_gender`: formy przeciwnego rodzaju (-łam/-łaś/gotowa vs -łem/-łeś/gotowy) przy braku oczekiwanych. Zdanie bez form rodzajowych nie jest błędem. |
| Słownik | Termin z oryginału, którego polski rdzeń (pierwsze min(5, długość−2) liter każdego słowa ≥ 4 liter) nie występuje w tłumaczeniu. |
| Wiersze | Inna liczba niepustych wierszy niż w oryginale (nakładka musi pasować do okienka gry). |
| Długość | Tłumaczenie krótsze niż 0,5× albo dłuższe niż 2× oryginału (bez spacji). |

## Prywatność

- Do wybranych dostawców trafiają **wyłącznie linie tekstu z korpusu** (oraz, jak
  w aplikacji, terminy słownika i poprzednie linie sceny jako kontekst). **Nigdy zrzuty
  ekranu** — narzędzie nie przechwytuje ekranu ani nie uruchamia OCR.
- Mock niczego nie wysyła. Lokalny serwer LLM (Ollama, LM Studio) na `localhost` nie
  wysyła tekstu poza komputer.
- DeepL z glosariuszem tworzy glosariusz na Twoim koncie DeepL (tak jak aplikacja; zastępuje
  on poprzedni glosariusz aplikacji dla tej pary języków — aplikacja odtworzy swój przy
  następnym tłumaczeniu). `--no-deepl-glossary` wyłącza to.
- Raport i CSV zawierają teksty korpusu. Linie przepisane z gier trzymaj w `eval/private/`,
  a wyniki w `eval/out/` — oba katalogi są w `.gitignore`.
