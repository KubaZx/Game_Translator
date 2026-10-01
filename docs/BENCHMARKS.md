# Benchmarki wydajności

Projekt `benchmarks/GameTranslatorOverlay.Benchmarks` mierzy powtarzalnie (BenchmarkDotNet)
najgorętsze fragmenty czystej logiki aplikacji: trafienia cache w klatce live, detekcję zmian
klatki, odcisk regionu tekstu i obróbkę tekstu po OCR. Zastępuje jednorazowe pomiary
„na oko” liczbami, które każdy może odtworzyć jednym poleceniem i porównać między commitami.

**Czego tu nie ma:** przechwytywania okna, Windows OCR, WPF, sieci ani prawdziwych dostawców
(DeepL/Azure/Google/LLM/Claude). Te etapy wymagają Windows albo sieci i mierzy je licznik czasu
tłumaczeń w aplikacji oraz narzędzia z `tools/` (zob. [MANUAL_TESTING.md](MANUAL_TESTING.md)).
Benchmarki niczego nie wysyłają do sieci — dostawca w pomiarze cache rzuca wyjątkiem przy
każdym wywołaniu.

## Jak uruchomić

Wymagany .NET SDK 10. Benchmarki zawsze w konfiguracji Release (BenchmarkDotNet odmawia
pomiaru buildu Debug).

Szybko (ok. 2,5 min na maszynie z tabel niżej; 3 iteracje — wyniki orientacyjne):

```bash
cd benchmarks/GameTranslatorOverlay.Benchmarks
dotnet run -c Release -- --filter '*' --job short
```

Pełny przebieg (ok. 7 min tamże; domyślne zadanie BenchmarkDotNet, dokładniejsze):

```bash
cd benchmarks/GameTranslatorOverlay.Benchmarks
dotnet run -c Release -- --filter '*'
```

Przydatne przełączniki:

- `--filter '*CacheHit*'` — tylko jedna klasa (lub `'*KlatkaPelna*'` — jedna metoda),
- `--list flat` — lista wszystkich pomiarów,
- `--exporters json markdown` — wyniki do porównań maszynowych i do wklejenia,
- `--artifacts <katalog>` — gdzie zapisać wyniki (domyślnie `BenchmarkDotNet.Artifacts/`
  w bieżącym katalogu; katalog jest w `.gitignore`).

Na GitHubie: **Actions → Benchmarks → Run workflow** (`.github/workflows/benchmarks.yml`).
Workflow uruchamia się tylko ręcznie, na `ubuntu-latest`, niczego nie blokuje i wrzuca
`BenchmarkDotNet.Artifacts` (JSON + Markdown) jako artefakt przebiegu. Runnery GitHuba są
współdzielone, więc wyniki z różnych przebiegów porównuj ostrożnie.

Pliki tymczasowe (baza SQLite w pomiarze cache) powstają wyłącznie w
`Path.GetTempPath()/gto-bench/<guid>` i są usuwane po pomiarze. Benchmarki nigdy nie dotykają
`%LOCALAPPDATA%` ani prawdziwego cache gracza.

## Co modeluje każdy pomiar

| Klasa / metoda | Co w aplikacji |
|---|---|
| `CacheHitFrameBenchmarks.SqliteRozgrzanaPamiec` | Klatka live, w której wszystkie teksty (20 lub 100) są już w cache SQLite i w pamięci trafień — typowa sytuacja po kilku sekundach gry w tej samej scenie. Cały `TranslationPipeline.TranslateAsync`: normalizacja, słownik, cache. Zbiorczy zapis liczników użycia jest wyzwalany tak jak w aplikacji, ale wykonuje się na innym wątku (`Task.Run`): do `Mean` wchodzi tylko jego zaplanowanie i ewentualna rywalizacja o zasoby, a jego alokacje są liczone przez `MemoryDiagnoser` (który mierzy cały proces). Różnica między tym wariantem a `PamiecTrybPrywatny` **nie** jest więc kosztem zapisu do SQLite. |
| `CacheHitFrameBenchmarks.SqliteZimnaPamiec` | Pierwsza klatka po starcie aplikacji: nowa instancja `SqliteTranslationCache` na tym samym pliku (pusta pamięć trafień), każdy odczyt idzie do bazy. Instancja tworzona w `IterationSetup`, więc jedno wywołanie na iterację. Pula połączeń SQLite i pamięć podręczna plików systemu są już ciepłe — to nie jest „zimny dysk”. |
| `CacheHitFrameBenchmarks.PamiecTrybPrywatny` | To samo na `InMemoryTranslationCache` — cache trybu prywatnego (nic nie trafia na dysk). |
| `ChangeDetectionBenchmarks.SiatkaLuminancji` | `LuminanceGrid.FromBgra32` na pełnej klatce 1920×1080 lub 3840×2160 (siatka 48×27, 9 próbek na komórkę). |
| `ChangeDetectionBenchmarks.AnalizaZmian` | `NoiseAwareChangeDetector.Analyze` dwóch siatek (odsiew szumu, region zmian). |
| `ChangeDetectionBenchmarks.KlatkaPelna` | Oba kroki razem — decyzja „czy klatka się zmieniła” wykonywana dla każdej przechwyconej klatki przed ewentualnym OCR. Klatki syntetyczne: ciemne tło z ziarnem pikseli (poniżej progu komórki — uśrednione, niewidoczne dla detektora), pas „mgły” w górnych 40% ekranu, który co klatkę zmienia luminancję o 14 (ponad próg komórki 10, poniżej progu mocnej zmiany 25 — po rozgrzaniu odsiewany jako szum), oraz jasny prostokąt przesuwający się między klatkami (zmiana mocna). `GlobalSetup` sprawdza, że obie ścieżki detektora pracują. |
| `FingerprintBenchmarks.OdciskRegionu` | `TextRegionFingerprint.FromBitmap` (budowniczy wiersz po wierszu: skrót RGB — od 2026-10 XxHash128, wcześniej SHA-256 — + test kontrastu) dla regionu 64×16 (etykieta), 512×64 (wiersz napisów) i 512×512 (największy dopuszczalny region). |
| `FingerprintBenchmarks.PustyRegion` | `TextPresenceProbe.IsClearlyEmpty` na jednolitym regionie tych samych rozmiarów (tekst zniknął z ekranu) — najgorszy przypadek testu kontrastu: bez wczesnego wyjścia, każdy piksel RGB. Dodany 2026-10. |
| `TextBenchmarks.GrupowanieFiltrNormalizacja` | 40 syntetycznych wierszy OCR (dialog, menu, dziennik zadań, podpowiedź przedmiotu, śmieci HUD): `TextBlockGrouper.Group`, potem dla każdego bloku `TextNormalizer.Normalize` i `JunkFilter.IsMeaningful`. |
| `TextBenchmarks.SklejanieWierszy` | `TextReflow.Unwrap` na 6-wierszowym dialogu (sklejanie miękkich zawinięć przed wysłaniem do dostawcy). |
| `TextBenchmarks.RozkladanieTlumaczenia` | `TextReflow.Rewrap` — rozłożenie polskiego tłumaczenia z powrotem na wiersze oryginału. |
| `GlossaryTermBenchmarks.TerminySlownikaWTekstach` | `GlossaryService.FindTermsIn` dla słownika 50 i 500 terminów na tekstach jednej partii (sklejony dialog + 12 wierszy menu/dziennika) — dobór terminów dla dostawców kontekstowych. Osobna klasa, bo tylko ten pomiar zależy od liczby terminów. |

## Wyniki

**To liczby z kontenera Linux w chmurze (4 rdzenie Xeon 2,1 GHz), nie z Windows z włączonym
antywirusem, na którym gra się naprawdę.** Na Windows dostęp do pliku SQLite przechodzi przez
filtr antywirusa i inny system plików, więc zwłaszcza wariant „zimny” może wyglądać inaczej.
Liczby służą do porównań między commitami na tej samej maszynie, nie jako obietnica czasu
w grze.

Tabele poniżej to stan sprzed optymalizacji z 2026-10; wyniki po nich (z porównaniem) są
w sekcji [Optymalizacje 2026-10](#optymalizacje-2026-10).

Przebieg pełny (domyślne zadanie BenchmarkDotNet), commit z wprowadzeniem benchmarków,
2026-09-30:

```
BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.112
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
```

### Trafienia cache w klatce live

| Method                | InvocationCount | Texts | Mean         | Error      | StdDev       | Allocated |
|---------------------- |---------------- |------ |-------------:|-----------:|-------------:|----------:|
| SqliteRozgrzanaPamiec | Default         | 20    |     48.54 μs |   0.781 μs |     0.731 μs |  57.09 KB |
| PamiecTrybPrywatny    | Default         | 20    |     47.56 μs |   0.761 μs |     0.712 μs |  58.66 KB |
| SqliteZimnaPamiec     | 1               | 20    |  2,589.89 μs |  73.020 μs |   209.507 μs |  133.1 KB |
| SqliteRozgrzanaPamiec | Default         | 100   |    285.98 μs |   3.067 μs |     2.395 μs | 334.12 KB |
| PamiecTrybPrywatny    | Default         | 100   |    258.20 μs |   2.642 μs |     2.471 μs | 294.08 KB |
| SqliteZimnaPamiec     | 1               | 100   | 12,338.36 μs | 658.670 μs | 1,921.372 μs | 702.49 KB |

Uwagi BenchmarkDotNet do wariantu zimnego: czas iteracji (2–13 ms) jest poniżej zalecanych
100 ms, a rozkład wielomodalny (odczyty z bazy + okazjonalny zapis liczników) — traktuj go
jako rząd wielkości, nie dokładną wartość. Kolumny Ratio pominięte (wariant zimny ma inne
ustawienia zadania, więc BenchmarkDotNet nie liczy dla niego proporcji).

### Detekcja zmian klatki

| Method           | Resolution | Mean      | Error     | StdDev    | Allocated |
|----------------- |----------- |----------:|----------:|----------:|----------:|
| SiatkaLuminancji | 1920x1080  | 54.192 μs | 1.0288 μs | 0.9623 μs |    5240 B |
| AnalizaZmian     | 1920x1080  |  4.872 μs | 0.0879 μs | 0.1608 μs |      64 B |
| KlatkaPelna      | 1920x1080  | 59.647 μs | 1.0994 μs | 1.4677 μs |    5304 B |
| SiatkaLuminancji | 3840x2160  | 66.111 μs | 1.0863 μs | 0.9630 μs |    5240 B |
| AnalizaZmian     | 3840x2160  |  4.572 μs | 0.0901 μs | 0.0885 μs |      64 B |
| KlatkaPelna      | 3840x2160  | 71.438 μs | 1.3900 μs | 2.1641 μs |    5304 B |

Tabela z ponownego pełnego przebiegu tej klasy (ta sama maszyna i środowisko co wyżej) po
dodaniu migoczącego pasa „mgły” — w pierwszej wersji klatek szum pikseli nigdy nie przekraczał
progu komórki, więc ścieżka odsiewu szumu praktycznie nie pracowała. Czasy prawie się nie
zmieniły (analiza jest liniowa w liczbie komórek, niezależnie od tego, ile z nich się zmienia).

Siatka próbkuje stałą liczbę punktów (48×27 komórek × 9 próbek), więc 4K kosztuje niewiele
więcej niż 1080p (różnica to najpewniej rozrzut próbek po 4× większym buforze; tego nie
mierzyliśmy osobno). Pomiar nie obejmuje
kopiowania klatki z GPU ani samego przechwytywania.

### Odcisk regionu tekstu

| Method        | Region  | Mean         | Error     | StdDev    | Allocated |
|-------------- |-------- |-------------:|----------:|----------:|----------:|
| OdciskRegionu | 512x512 | 1,039.045 μs | 4.1446 μs | 3.6741 μs |     328 B |
| OdciskRegionu | 512x64  |   129.851 μs | 1.4316 μs | 1.2691 μs |     328 B |
| OdciskRegionu | 64x16   |     5.616 μs | 0.1082 μs | 0.1407 μs |     328 B |

Koszt rośnie liniowo z liczbą pikseli (SHA-256 z każdego piksela RGB); alokacja jest stała.

### Obróbka tekstu

| Method                      | Mean      | Error     | StdDev    | Gen0   | Allocated |
|---------------------------- |----------:|----------:|----------:|-------:|----------:|
| GrupowanieFiltrNormalizacja | 29.352 μs | 0.3899 μs | 0.3647 μs | 0.3662 |  52.13 KB |
| SklejanieWierszy            |  2.222 μs | 0.0284 μs | 0.0252 μs | 0.0458 |    6.1 KB |
| RozkladanieTlumaczenia      |  4.923 μs | 0.0944 μs | 0.1010 μs | 0.0381 |   5.91 KB |

| Method                   | GlossaryTerms | Mean      | Error     | StdDev    | Gen0   | Allocated |
|------------------------- |-------------- |----------:|----------:|----------:|-------:|----------:|
| TerminySlownikaWTekstach | 50            |  8.568 μs | 0.1204 μs | 0.1127 μs | 0.0610 |   9.56 KB |
| TerminySlownikaWTekstach | 500           | 47.179 μs | 0.7985 μs | 0.7469 μs | 0.0610 |  13.08 KB |

### Przebieg szybki dla porównania

Ten sam kod i maszyna, `--job short` (3 iteracje). Średnie zgodne z pełnym przebiegiem
w granicach kilku–kilkunastu procent, poza wariantem zimnym SQLite, który przy 3 iteracjach
ma błąd większy niż sama średnia:

| Pomiar | short | pełny |
|---|---:|---:|
| SqliteRozgrzanaPamiec, 20 tekstów | 48.50 μs | 48.54 μs |
| PamiecTrybPrywatny, 20 tekstów | 48.88 μs | 47.56 μs |
| SqliteZimnaPamiec, 20 tekstów | 3,101.78 μs (±8,564.565 μs) | 2,589.89 μs |
| SqliteRozgrzanaPamiec, 100 tekstów | 304.31 μs | 285.98 μs |
| PamiecTrybPrywatny, 100 tekstów | 232.13 μs | 258.20 μs |
| SqliteZimnaPamiec, 100 tekstów | 13,205.63 μs | 12,338.36 μs |
| KlatkaPelna 1920x1080 ¹ | 61.578 μs | 59.561 μs |
| KlatkaPelna 3840x2160 ¹ | 73.245 μs | 70.554 μs |
| OdciskRegionu 512x512 | 1,042.493 μs | 1,039.045 μs |

¹ Oba przebiegi na pierwszej wersji klatek (bez pasa „mgły”); aktualny pełny wynik w tabeli
„Detekcja zmian klatki” wyżej.

Do decyzji (np. „czy zmiana przyspieszyła cache”) używaj pełnego przebiegu przed i po zmianie
na tej samej maszynie; szybki wystarcza do wyłapania regresji rzędu wielokrotności.

## Optymalizacje 2026-10

Pełny przebieg (domyślne zadanie BenchmarkDotNet) klas `FingerprintBenchmarks`,
`CacheHitFrameBenchmarks` i `ChangeDetectionBenchmarks` przed zmianami i po nich, na tej samej
maszynie, jeden proces pomiarowy naraz (bez równoległych buildów i testów):

```
BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.112
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
```

**To oszczędności procesora rzędu mikrosekund, nie widoczne skrócenie tłumaczenia.** Czas od
zmiany na ekranie do napisu wyznaczają OCR (ok. 100 ms) i dostawca (setki ms); praktyczny zysk
to mniej pracy procesora w każdej klatce trybu live — czyli mniej odebranego grze czasu CPU.

### Odcisk regionu tekstu

| Pomiar | Region | Przed: Mean ± Error | Przed: Allocated | Po: Mean ± Error | Po: Allocated |
|---|---|---:|---:|---:|---:|
| OdciskRegionu | 512x512 | 1,035.458 ± 9.511 μs | 328 B | 121.778 ± 2.381 μs | 744 B |
| OdciskRegionu | 512x64 | 129.423 ± 1.596 μs | 328 B | 14.591 ± 0.288 μs | 744 B |
| OdciskRegionu | 64x16 | 5.489 ± 0.032 μs | 328 B | 0.920 ± 0.011 μs | 744 B |
| PustyRegion ¹ | 512x512 | 674.258 ± 12.503 μs | – | 60.281 ± 0.967 μs | 48 B |
| PustyRegion ¹ | 512x64 | 83.983 ± 0.872 μs | – | 7.295 ± 0.094 μs | 48 B |
| PustyRegion ¹ | 64x16 | 2.664 ± 0.053 μs | – | 0.409 ± 0.004 μs | 48 B |

¹ Pomiar dodany w tej zmianie; „przed” zmierzony osobnym przebiegiem samego `PustyRegion`
z poprzednią wersją `TextPresenceProbe` (BenchmarkDotNet pokazał wtedy „–” zamiast 48 B
za obiekt sondy — kod alokacji się nie zmienił).

- **Skrót XxHash128 zamiast SHA-256** (`System.IO.Hashing`, Microsoft, MIT): odcisk porównuje
  tylko klatki tej samej gry w jednej sesji i nigdzie nie jest zapisywany, więc nie potrzebuje
  odporności kryptograficznej; 128 bitów daje szansę przypadkowej kolizji rzędu 2^-128.
  Skrót obejmuje piksele BGRA z wyzerowaną wektorowo alfą zamiast RGB przepisywanego bajt po
  bajcie — te same bajty kolorów w tej samej kolejności. Większa alokacja (744 B zamiast 328 B)
  to stan obiektu XxHash128; stała na region, bez znaczenia dla GC.
- **Wektorowy test kontrastu** (`TextPresenceProbe`): minimum i maksimum kanałów liczone
  wektorami po 256 pikseli, z tym samym wynikiem co przegląd piksel po pikselu (testy porównują
  obie wersje na losowych danych).

### Trafienia cache w klatce live

| Pomiar | Teksty | Przed: Mean ± Error | Przed: Allocated | Po: Mean ± Error | Po: Allocated |
|---|---:|---:|---:|---:|---:|
| SqliteRozgrzanaPamiec | 20 | 49.78 ± 0.762 μs | 57.8 KB | 11.36 ± 0.047 μs | 7.85 KB |
| PamiecTrybPrywatny | 20 | 49.92 ± 0.981 μs | 59.36 KB | 12.72 ± 0.144 μs | 7.3 KB |
| SqliteZimnaPamiec | 20 | 2,816.61 ± 107.857 μs | 134.1 KB | 928.60 ± 30.139 μs | 54.44 KB |
| SqliteRozgrzanaPamiec | 100 | 295.15 ± 5.888 μs | 338.83 KB | 58.22 ± 0.782 μs | 44.88 KB |
| PamiecTrybPrywatny | 100 | 242.24 ± 4.000 μs | 297.28 KB | 61.85 ± 1.071 μs | 33.43 KB |
| SqliteZimnaPamiec | 100 | 13,147.02 ± 390.159 μs | 708.04 KB | 3,144.04 ± 122.006 μs | 304.89 KB |

Wariant zimny ma nadal rozkład wielomodalny i iteracje poniżej 100 ms (uwagi wyżej) — traktuj
go jako rząd wielkości; różnica przed/po jest jednak wielokrotnie większa niż błąd.

- **Normalizacja bez kopii czystego tekstu:** `TextNormalizer.Normalize` zwraca ten sam napis,
  gdy zachowawczy test pokazuje, że pełna normalizacja nic by nie zmieniła (znaki widocznego
  ASCII, Latin-1 i Latin Extended-A, pojedyncze spacje, bez pustych wierszy i odstępów na
  brzegach); wszystko inne idzie pełną ścieżką. Usuwa podwójną normalizację (pipeline, potem
  słownik). Osobny pomiar z wyłączonym samym tym skrótem: 25.08 μs / 41.29 KB (20 tekstów,
  SQLite rozgrzany) zamiast 11.36 μs / 7.85 KB.
- **Pamięć trafień bez SHA-256:** pamięć trafień SQLite i cache trybu prywatnego są kluczowane
  samym znormalizowanym tekstem (struktura klucza zamiast napisu `hash|język|język|profil`),
  więc trafienie nie liczy skrótu ani nie składa klucza. SHA-256 (format kolumny `text_hash` bez
  zmian) liczony jest tylko przy odczycie z bazy i zapisie. Osobny pomiar z doliczonym
  (jak dawniej) skrótem SHA-256 i napisem klucza przy każdym trafieniu w pamięci: 31.09 μs / 16.99 KB (20 tekstów) i
  157.31 μs / 107.07 KB (100 tekstów).
- **Odczyt partii (`ITranslationCache.LookupManyAsync`):** pipeline (`TranslateAsync` i próba
  lokalna `TranslateLocalAsync`) pyta cache o wszystkie teksty klatki naraz — te same teksty co
  dotąd, w tej samej kolejności, z tą samą obsługą błędu pojedynczego odczytu. SQLite obsługuje
  trafienia w pamięci bez zadania w tle, a pozostałe teksty jednym zadaniem, na jednym połączeniu
  i jednym przygotowanym zapytaniu (zamiast `Task.Run`, połączenia i zapytania na każdy tekst) —
  to jest główny zysk wariantu zimnego. Priorytety wpisów, wersjonowanie pamięci i zbiorczy zapis
  liczników bez zmian. Implementacja domyślna w interfejsie woła `LookupAsync` po kolei.
- Drobne: bez LINQ i domknięć w pętli klatki (`pending.Any` → zbiór), słowniki o znanym rozmiarze,
  kopia `TranslationOutcome` tylko wtedy, gdy tekst źródłowy wystąpienia się różni.

**Wypróbowane i wycofane:** `TextHasher.Sha256Hex` z buforem na stosie zamiast tablicy bajtów
UTF-8 — w wariancie zimnym (jedyny, który jeszcze liczy skrót) 2,992.55 ± 76.820 μs bez tej
zmiany wobec 3,324.68 ± 92.057 μs z nią (100 tekstów), czyli w granicach rozrzutu tego
pomiaru, a alokacja niższa tylko o ok. 3 KB na 100 tekstów. Nie warto komplikować kodu.

### Detekcja zmian klatki

| Pomiar | Rozdzielczość | Przed: Mean ± Error | Przed: Allocated | Po: Mean ± Error | Po: Allocated |
|---|---|---:|---:|---:|---:|
| SiatkaLuminancji | 1920x1080 | 53.616 ± 0.761 μs | 5240 B | 34.036 ± 0.579 μs | 5240 B |
| KlatkaPelna | 1920x1080 | 58.840 ± 0.765 μs | 5304 B | 39.542 ± 0.786 μs | 5304 B |
| SiatkaLuminancji | 3840x2160 | 65.460 ± 1.290 μs | 5240 B | 50.081 ± 1.000 μs | 5240 B |
| KlatkaPelna | 3840x2160 | 70.704 ± 0.906 μs | 5304 B | 55.353 ± 1.095 μs | 5304 B |
| AnalizaZmian | 1920x1080 | 4.691 ± 0.093 μs | 64 B | 4.587 ± 0.025 μs | 64 B |
| AnalizaZmian | 3840x2160 | 4.633 ± 0.062 μs | 64 B | 4.598 ± 0.031 μs | 64 B |

- **Siatka luminancji wierszami pikseli:** `LuminanceGrid.FromBgra32` przechodzi po wierszu
  próbek przez wszystkie komórki wiersza siatki naraz (przesunięcia kolumn liczone raz). Każda
  komórka dodaje próbki w tej samej kolejności, więc wynik jest co do bitu taki sam (test
  porównuje bity z wersją komórka po komórce), ale sumy sąsiednich komórek są niezależne —
  procesor nie czeka na każde dodawanie — a odczyty idą po pamięci kolejno.
  `AnalizaZmian` bez zmian w kodzie (różnica w granicach błędu).
