# TESTING.md — strategia testów automatycznych

Dokument opisuje, co testujemy automatycznie, gdzie te testy leżą i jak je uruchamiać.
Testy wymagające żywego pulpitu Windows (OCR na realnym oknie, nakładka, globalne skróty)
są **wyłącznie ręczne** — patrz [docs/MANUAL_TESTING.md](MANUAL_TESTING.md).

## Podział testów

| Warstwa | Projekt | Co testuje | Zależności |
|---|---|---|---|
| Jednostkowe | `tests/GameTranslatorOverlay.Core.Tests` | czysta logika z `src/GameTranslatorOverlay.Core` | brak (zero I/O, zero Windows) |
| Integracyjne | `tests/GameTranslatorOverlay.Infrastructure.Tests` | `src/GameTranslatorOverlay.Infrastructure`: SQLite, DeepL (na atrapie HTTP), DPAPI | pliki tymczasowe, Windows (DPAPI) — CI działa na `windows-latest`, więc przechodzą |
| Ręczne | — | UI, capture, OCR na żywo, nakładka, skróty | pulpit Windows, osobny dokument |

Framework: **xUnit** w obu projektach testowych.

## Testy jednostkowe (Core.Tests)

### Normalizacja tekstu OCR

Normalizacja czyści wynik OCR (białe znaki, łamania linii, artefakty), ale **nie może
zniekształcać danych liczbowych** — to najczęstsze treści w tooltipach gier. Obowiązkowe
przypadki (każdy jako osobny test lub `[Theory]` z `[InlineData]`):

- `+25%` — znak plusa i procent zachowane,
- `-10%` — minus nie ginie i nie zamienia się w myślnik,
- `10–15` — zakres (en dash) zachowany; warianty `10-15` i `10—15` nie sklejają się w `1015`,
- `1.5 seconds` — kropka dziesiętna nietknięta (nie „1,5", nie „15"),
- `Level 20`, `3/5`, `x2` — liczby przy słowach, ułamki i mnożniki bez zmian,
- wielokrotne spacje/taby → pojedyncza spacja; puste linie na brzegach ucięte,
- normalizacja jest **idempotentna**: `Normalize(Normalize(x)) == Normalize(x)`.

### Łączenie linii w bloki (tooltips)

OCR zwraca pojedyncze linie z pozycjami; grupowanie ma złożyć z nich logiczne bloki
(np. cały tooltip przedmiotu):

- linie blisko siebie w pionie → jeden blok; duża przerwa → nowy blok,
- kolejność linii w bloku zgodna z układem na ekranie (góra→dół, lewo→prawo),
- pojedyncza linia = poprawny blok jednoliniowy,
- pusta lista wejściowa → pusta lista bloków (bez wyjątku).

### Filtr śmieciowych wyników OCR

`Windows.Media.Ocr` nie daje per-słowo confidence, więc filtr działa na tekście:

- odrzuca: pojedyncze znaki interpunkcyjne, losowe zbitki symboli, teksty poniżej progu
  sensownej długości bez żadnej litery/cyfry,
- **nie** odrzuca: krótkich, ale znaczących tekstów (`x2`, `3/5`, `+25%`, `HP`),
- test graniczny na każdą regułę progową (wartość na progu i tuż obok).

### Hash / stabilne ID tekstu

Klucz cache i deduplikacji:

- identyczny tekst → identyczny hash (deterministycznie, między uruchomieniami),
- tekst różniący się tylko białymi znakami przed normalizacją → ten sam hash po normalizacji,
- realnie różne teksty → różne hashe (spot-check, nie dowód matematyczny).

### Glossary (słownik)

Zasady z briefu produktu — testujemy wprost:

- dopasowanie **całych słów/fraz**: termin `Shield` nie łapie się w `Shielded`,
- fraza dłuższa wygrywa z krótszą: przy terminach `Energy Shield` i `Shield` tekst
  `Energy Shield` dostaje tłumaczenie frazy dłuższej,
- `caseSensitive: false` → dopasowanie bez rozróżniania wielkości liter; `true` → z rozróżnianiem,
- konflikt (ten sam `source` → różne `target`): wygrywa wyższy `priority`; wykrycie konfliktu
  jest raportowane (test na samą detekcję),
- pusty słownik → tekst przechodzi nietknięty.

### Pipeline tłumaczenia

Orkiestracja na interfejsach (`ITranslationProvider` = atrapa/`MockTranslationProvider`):

- kolejność źródeł: **glossary → cache → API**; trafienie na wcześniejszym etapie
  nie woła późniejszych (weryfikacja przez licznik wywołań atrapy),
- priorytet wyników: ręczna korekta > wpis profilu gry > cache globalny > API,
- **deduplikacja in-flight**: dwa równoległe żądania o ten sam tekst → jedno wywołanie providera,
- **tryb cache-only**: provider API nie jest wołany nigdy; brak wpisu w cache → wynik
  „brak tłumaczenia", nie wyjątek,
- **limit znaków sesji**: po przekroczeniu limitu pipeline nie wysyła kolejnych zapytań
  do API i sygnalizuje stan limitu,
- **anulowanie**: `CancellationToken` odwołany w trakcie → operacja kończy się
  `OperationCanceledException`, bez zapisu częściowych wyników do cache.

## Testy integracyjne (Infrastructure.Tests)

### SQLite cache (pliki tymczasowe)

Każdy test tworzy bazę w pliku tymczasowym (`Path.GetTempFileName()` / katalog tymczasowy
per test) i sprząta po sobie — testy są niezależne i mogą biec równolegle:

- zapis → odczyt roundtrip,
- **priorytet ręcznych korekt**: korekta nadpisuje wynik z cache/API przy odczycie,
- **czyszczenie cache z zachowaniem korekt**: po operacji „wyczyść cache" ręczne korekty zostają,
- **import/eksport**: eksport do pliku → import do świeżej bazy → identyczna zawartość
  (w tym korekty),
- **migracje**: baza z `PRAGMA user_version` = N-1 otwarta nową wersją kodu → schemat
  zmigrowany, dane zachowane, `user_version` podbite; świeża baza → od razu najnowsza wersja.

### DeepLTranslationProvider (fake `HttpMessageHandler`)

Provider dostaje `HttpClient` z podstawionym handlerem — **żaden test nie dotyka sieci**:

- sukces: poprawny JSON z `/v2/translate` → poprawnie sparsowane tłumaczenia; żądanie
  idzie na `api-free.deepl.com` dla klucza z sufiksem `:fx`, na `api.deepl.com` dla pro,
- **403** → błąd „nieprawidłowy klucz API" (typowany, nie goły `HttpRequestException`),
- **456** → błąd „limit wyczerpany",
- **429** → ograniczony retry (handler liczy próby: retry następuje, ma górny limit,
  po wyczerpaniu zwraca błąd rate-limit),
- **timeout** → czytelny błąd, bez zawieszenia (test z krótkim timeoutem i handlerem,
  który nie odpowiada),
- **chunkowanie batchy**: >50 tekstów → podział na żądania po maks. 50, wyniki złożone
  w oryginalnej kolejności,
- klucz API **nigdy** nie pojawia się w URL-u zapytania (tylko nagłówek `Authorization`).

### DPAPI

- roundtrip: `Protect` → `Unprotect` (CurrentUser) zwraca oryginał,
- dane zaszyfrowane ≠ plaintext (zaszyfrowany blob nie zawiera klucza jawnym tekstem).

Testy DPAPI wymagają Windows — CI używa `windows-latest`, lokalnie działa każdy Windows 10 2004+.

## Uruchamianie

Całość:

```powershell
dotnet test
```

Pojedynczy projekt:

```powershell
dotnet test tests/GameTranslatorOverlay.Core.Tests
dotnet test tests/GameTranslatorOverlay.Infrastructure.Tests
```

Filtry kategorii — testy oznaczamy `[Trait("Category", "...")]` wartościami `Unit`
lub `Integration`:

```powershell
dotnet test --filter "Category=Unit"
dotnet test --filter "Category=Integration"
dotnet test --filter "FullyQualifiedName~Glossary"   # po nazwie
```

## Zasady twarde

1. **CI nie potrzebuje sekretów.** Wszystkie testy automatyczne używają wyłącznie
   `MockTranslationProvider` albo fake `HttpMessageHandler`. Żaden test nie woła
   prawdziwego DeepL i żaden nie czyta klucza API. Pipeline CI (GitHub Actions,
   `windows-latest`: restore → build → test) musi przejść na czystym runnerze.
2. **Syntetyczne obrazy testowe.** Jeżeli test potrzebuje bitmapy (np. przyszłe testy
   ścieżki capture/OCR), generujemy ją w kodzie (tekst rysowany na jednolitym tle) —
   **nigdy** nie commitujemy zrzutów ekranu z gier (prawa autorskie + rozmiar repo).
3. **Zero zależności od pulpitu.** Test, który wymaga okna, fokusu, skrótu globalnego
   albo prawdziwego OCR, nie jest testem automatycznym — trafia do MANUAL_TESTING.md.
4. **Testy integracyjne sprzątają po sobie** (pliki tymczasowe usuwane w `Dispose`/`finally`).
