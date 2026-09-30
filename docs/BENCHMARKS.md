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
| `FingerprintBenchmarks.OdciskRegionu` | `TextRegionFingerprint.FromBitmap` (budowniczy wiersz po wierszu: SHA-256 z RGB + test kontrastu) dla regionu 64×16 (etykieta), 512×64 (wiersz napisów) i 512×512 (największy dopuszczalny region). |
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
