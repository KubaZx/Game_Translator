# Testy i pomiary — GameTranslatorOverlay

Stan sprawdzony 6 października 2026 (wydanie 0.5.0): **1697 testów xUnit** — **1251 Core**,
**280 Infrastructure** i **166 CorpusTool** — oraz kompilacja całego rozwiązania (z aplikacją
WPF, narzędziami i benchmarkami) bez ostrzeżeń. Ta runda była weryfikowana na Windows
(.NET 10 SDK): testy DPAPI (`[WindowsFact]`) wykonane, 0 pominiętych; smoke test Windows OCR
z Mockiem zakończony wynikiem OK. Projekty Infrastructure i CorpusTool uruchamiają klasy testów
po kolei (`tests/*/AssemblyInfo.cs`, `CollectionBehavior(DisableTestParallelization = true)`):
testy bazy w `Dispose` czyszczą globalnie pule połączeń SQLite (`SqliteConnection.ClearAllPools`),
a przy równoległych klasach zamykało to połączenie innego testu (`ObjectDisposedException`
w `SqliteTranslationCache.OpenConnection`). Pod pomiarem pokrycia padały tak 2 z 6 pełnych
przebiegów Infrastructure (m.in. `SqliteBatchLookupTests`, `SqliteTranslationCacheTests`,
wcześniej `SqliteUsageFlushFailureTests`); po zmianie 8 z 8 zielonych, czas Infrastructure
ok. 1 → 2 s.
Poprzednio (1 października 2026, wydanie 0.4.0): 1114 testów (880 Core, 234 Infrastructure)
na Linuksie, 3 testy DPAPI pominięte; pełny przebieg na Windows z 15 września 2026: 371 testów
i smoke test z Mockiem.

Rozdzielamy testy logiki, lokalne sondy z rzeczywistym capture/OCR i ocenę
fizycznej nakładki przez użytkownika. Wynik jednej grupy nie zastępuje pozostałych.

## Podział

| Grupa | Miejsce | Zależności i zakres |
|---|---|---|
| Logika i regresje | `tests/GameTranslatorOverlay.Core.Tests` | xUnit, atrapy i dane w pamięci; bez aktywnej gry, WPF i prawdziwego OCR |
| Integracje | `tests/GameTranslatorOverlay.Infrastructure.Tests` | SQLite w plikach tymczasowych, atrapa HTTP, DPAPI na Windows |
| Narzędzie korpusu | `tests/GameTranslatorOverlay.CorpusTool.Tests` | `CorpusTool` (net10.0, bez WPF): syntetyczne kontenery Unity w katalogu tymczasowym, parsery, zabezpieczenia ADR-014, `translate` przez atrapę HTTP i SQLite w katalogu tymczasowym |
| Smoke test | `tools/GameTranslatorOverlay.SmokeTest` | rzeczywisty Windows OCR na syntetycznym obrazie i lokalny pipeline |
| Sondy sesji | `tools/GameTranslatorOverlay.LiveDiag`, `tools/GameTranslatorOverlay.SceneReplay` | lokalny pulpit Windows, własne okno lub wskazana gra, Mock |
| Pomiary korpusu | `tools/GameTranslatorOverlay.CorpusEval` | Windows OCR, lokalny korpus i kopia cache; do katalogu wyników same liczby |
| Podgląd nakładki | `tools/GameTranslatorOverlay.OverlayPreview` | Windows OCR i WPF, prawdziwa nakładka na zapisanych klatkach gry → PNG (poza repozytorium) |
| Ocena wizualna | [MANUAL_TESTING.md](MANUAL_TESTING.md) | użytkownik, konkretna gra, DPI, monitory, układ i skróty |

CI uruchamia wszystkie trzy projekty xUnit (`dotnet test` na całym rozwiązaniu) na
`windows-latest` oraz szybki przebieg na `ubuntu-latest` (kompilacja
z `-p:EnableWindowsTargeting=true`, pokrycie kodu z progiem i smoke test ProviderEval
z Mockiem). Smoke test OCR, sondy pulpitu, CorpusEval i OverlayPreview wykonuje się osobno;
nie są częścią standardowego `dotnet test`. Pomiary wydajności są w osobnym projekcie
`benchmarks/` (BenchmarkDotNet, poza `dotnet test`) — [BENCHMARKS.md](BENCHMARKS.md).

Nowy push do tego samego PR-a anuluje starszy przebieg CI. Push na `main`, tagi `v*`
i ręczne uruchomienie nie są anulowane ani kolejkowane — każdy commit na `main` i każde
wydanie ma własny, dokończony przebieg. Joby testów mają token GitHub tylko do odczytu;
zapis ma wyłącznie job publikacji.

## Co obejmuje Core.Tests

- Normalizację OCR z zachowaniem liczb, znaków i zakresów; grupowanie linii,
  filtr śmieci, podobieństwo odczytów, hashe i geometrię.
- Dopasowanie słownika, konflikty, priorytet ręcznych poprawek i lokalnych wyników;
  priorytet między terminami z rozróżnianiem wielkości liter i bez, normalizację kluczy
  oraz wyszukiwanie terminów wewnątrz zdań (całe słowa, dłuższe frazy najpierw).
- Przekazywanie nazwy gry i pasujących terminów dostawcom kontekstowym; cały słownik
  (dla glosariusza DeepL) tylko przy partii zawierającej termin.
- Licznik czasu: mediana, p90, maksimum, okno próbek, format czasu, pomiar tylko udanych
  zapytań do dostawcy.
- Skalowanie OCR: jawne wyłączenie auto-powiększenia w profilu, skalowanie krawędzi
  prostokątów bez dryfu oraz porównanie `minAppVersion` profilu z wersją aplikacji.
- Deduplikację tłumaczeń, Cache-only, rezerwacje znaków dla równoległych operacji,
  ponowny odczyt cache oraz anulowanie zapisu po zmianie konfiguracji pipeline'u.
- Ograniczenie pracy do zadanej liczby zadań, obserwowanie błędów i domykanie sesji.
- Zmiany sceny, termin przetwarzania przy ciągłym i przerywanym ruchu, wybudzanie
  przy stabilizacji i harmonogram kontroli podczas OCR.
- Kolejne potwierdzenia podobnej nowej treści. Powrót poprzedniego tekstu lub
  pusty odczyt w badanym obszarze przerywa serię kandydata.
- Ostrożny dowód pustego, jednolitego pola: kontrast, całe ROI, cienkie znaki,
  stride/padding, alpha i nieprawidłowe dane.
- Dokładny skrót RGB niezmienionego tekstu: pojedynczy zmieniony piksel, pomijanie
  alpha/paddingu, pełne pole, geometria, granice kosztu i błędne dane (25 przypadków).
- Niezależną stabilizację pozycji i rozmiaru oraz usuwanie źródeł paska napisów.
- Parser opcji LiveDiag, dołączony do testów bez zależności od aplikacji WPF.
- Kontrolę jakości (`TranslationQualityGate`): normalizację liczb, brak tłumaczenia, „rozgadany”
  wynik, ponowienie dla dostawców LLM, znaczniki `qa=`/`qa-final` i limit zapytań.
- Pamięć dialogu (`DialogMemory`), płeć gracza i ponowne tłumaczenie po jej zmianie (`pg=`),
  także w połączeniu z `qa-final`.
- Odporność na błędy cache (traktowanie jak brak wpisu, pamięć awaryjna), pętlę wpisów profilu
  i Mock oraz liczniki użycia bez podwójnego odczytu.
- Szybką ścieżkę znanych klatek live i pomiar „Zmiana → napis” (`ChangeToTextTracker`).
- Komunikaty nakładki (`OverlayNoticePolicy`, `NoticeTexts`, filtr echa komunikatu w OCR,
  komunikaty ręcznego tłumaczenia przy schowanej nakładce).
- Wybór okna dla skrótu live (`LiveTargetResolver`) i jego komunikaty.
- Słownik: liczba mnoga i dopełniacz, łamanie wierszy, dwukropek, zakres `label`,
  `GlossaryPrecedence`, `PersistableTerms` bez terminów prywatnych.
- Ewaluację (`Evaluation*Tests`): chrF zgodny z sacreBLEU, korpus, kontrole i ich fałszywe
  alarmy, raport i CSV bezpieczny dla Excela.
- Znikanie starego napisu (0.5.0): `KnownTextAbsenceProbe` (ćwierć dawnego kontrastu, zmiana
  barwy przy najechaniu, przygaszenie, tolerancja promila, niepełne pole i błędne dane),
  `KnownTextReference` (piksele rdzenia glifów, wycinek powiększony ×2, jasna plamka po
  zniknięciu), `FullScanSchedule` (wycinki nie odsuwają pełnego skanu),
  `LiveBlockSurvival` (region bloków z brakami, `PartialOcrSeed`), ocenę odczytów
  (`LiveReadingEvidenceTests`: śmieć a wariant, wiarygodny nowy napis z wielokropkiem lub cyfrą),
  podmianę napisu po dwóch identycznych odczytach w `LiveReadingStabilizer` oraz powrót tekstu
  na pasek napisów (`LiveSubtitleContent.Restore`).
- Korpus gry (`GameTranslatorOverlay.Core.Corpus`): klucze dopasowania (`CorpusText`), zapis
  i odczyt JSONL, `CorpusSnapper` (dokładne, przybliżone, fragmenty, składanie wiersza, prefiks
  mówcy, odstęp do drugiego kandydata), ochronę liczb, znaków, walut i procentów
  (`CorpusSnapperNumberTests`), odległość ważoną pomyłkami OCR, etykiety z pomyłkami, początek
  kwestii dialogu, odrzucanie szumu, wspólny klucz nakładki dla różnych odczytów
  (`CorpusOcrMatchingTests`), plan jednostek (`TranslationUnitPlanner`), pipeline z korpusem
  (korekty, zapas starego wpisu przy błędzie dostawcy, Cache-only, brak aktywnego profilu)
  i znacznik `src=corpus` w cache (`CorpusCacheMarkerTests`).
- Profile: recepta `corpus` (stary profil bez sekcji, walidacja, dostarczony profil
  `escape-academy`) i pole `overlay.fontFamily`.
- Łatkę „Na oryginale” (`GlyphCoverTests`): kolor, kontur i cień tekstu, przezroczystość poza
  maską liter, odtworzenie gradientu tła, linia bazowa i wysokość liter, wyrównanie do środka,
  ikony klawiszy przed i za tekstem, liczenie dużych napisów w połowie rozdzielczości, podpis
  pola, kotwica i profil tuszu (`InkProfile`).
- Komunikat o pełnym ekranie (`NoticeTextsTests`: okno zakrywające cały monitor, ostrzeżenie
  bez treści z gry).

Czyste helpery nie dowodzą poprawnego rysowania przez WPF ani jakości Windows OCR.
Ich powiązanie z sesją sprawdzają osobne sondy i obserwacje użytkownika. Rysowanie napisu
(`GameTextElement`, `OverlayFonts`, `OverlayBlockRenderer`) i wstrzymywanie niedokończonej
kwestii w `LiveTranslationSession` należą do aplikacji WPF i nie mają testów xUnit — sprawdza
je OverlayPreview, SceneReplay (`typing`, `typing-nohold`) i [MANUAL_TESTING.md](MANUAL_TESTING.md).

## Co obejmuje Infrastructure.Tests

- SQLite: odczyt/zapis, migracje, import/eksport oraz zachowanie ręcznych poprawek.
- DeepL na fałszywym `HttpMessageHandler`: format żądań i odpowiedzi, wybór endpointu,
  batchowanie, błędy 403/456/429, retry, timeout i uwierzytelnianie w nagłówku.
- Glosariusze DeepL: tworzenie raz na zawartość słownika, ponowne użycie istniejącego,
  usuwanie starych wersji aplikacji (bez cudzych), czyszczenie wpisów, przerwa po błędzie,
  ponowienie tłumaczenia bez glosariusza i wyłączenie glosariusza.
- Azure i Google: nagłówki z kluczem i regionem (klucz nigdy w adresie), parametry
  języków, mapowanie błędów, brak ponawiania wyczerpanego limitu, odpowiedź proxy.
- Modele językowe: prompt (języki, gra, terminy, „dane, nie instrukcje”), tolerancyjny
  parser JSON, walidacja adresu (HTTPS poza localhost), tłumaczenie pojedyncze przy złej
  liczbie wyników, odmowa modelu, brak modelu, niedziałający serwer lokalny.
- Claude przez oficjalne SDK: model domyślny, fallback przy odmowie z nagłówkiem beta,
  schemat JSON, `effort` tylko dla obsługujących go modeli, błędy API i test przez Models API.
- Katalog dostawców i zapis nowych ustawień (także odczyt starego `settings.json` bez nowych
  pól: `showOverlayNotices`, `playerGender`, `liveToggleHotkey`, `lastGameProcess`/`lastGameTitle`)
  oraz `PipelineSnapshot` pomijający pola samego wyglądu.
- Glosariusz DeepL w tle: wspólny termin oczekiwania, limit czasu zapytań, brak ponawiania.
- Prompt LLM: pary `previous`, reguła płci gracza, instrukcja odmiany terminów.
- SQLite: błąd zapisu liczników nie przerywa odczytu, eksport/import pola `context`.
- Zgodność dostarczonych profili z bieżącą wersją aplikacji (`minAppVersion`).
- DPAPI: szyfrowanie i odszyfrowanie danych dla bieżącego użytkownika Windows.
- Korpus w aplikacji (`CorpusCatalogTests`): plik w katalogu danych, brak pliku bez ostrzeżenia,
  ponowne wczytanie po zmianie pliku, uszkodzony korpus z ostrzeżeniem bez tekstów gry, wpis
  z wyprzedzeniem trafiany przez przyciągnięty odczyt, `paragraphCacheKeys` domyślnie wyłączone.
- Baza dla `CorpusTool translate` (`CorpusCacheSupportTests`): `PeekManyAsync` bez liczników
  użycia i bez tworzenia pliku, `StoreManyAsync` bez nadpisywania korekt i wpisów zatwierdzonych,
  `EnvironmentTranslationProviders` (zmienne środowiskowe podawane przez test, nie z systemu).
- Opcje serwera LLM (`LlmServerOptionsTests`): zapytanie bez opcji takie jak dotąd, opcje tylko
  dla serwera, do którego je przypisano, presety DeepSeek i Ollama, `usage` w logu bez treści,
  scena i notatki partii w prompcie, kontekst DeepL.
- Migracja kroju (`OverlayFontSettingsTests`): dawny domyślny Segoe UI przechodzi raz na krój
  z profilu (`auto`), świadomy wybór kroju zostaje.

Testy nie wywołują prawdziwych usług tłumaczeniowych ani nie czytają klucza użytkownika.
Pliki tymczasowe są odizolowane od danych aplikacji.

## Co obejmuje CorpusTool.Tests

Projekt testuje narzędzie offline `tools/GameTranslatorOverlay.CorpusTool` (ADR-014) bez
prawdziwej gry: kontenery UnityFS i pliki serializowane Unity są budowane syntetycznie
w katalogu tymczasowym (`SyntheticUnity`).

- Czytnik Unity: dekoder LZ4, odczyt przez granice bloków, wersje plików serializowanych,
  odmowa przy nieznanych flagach i znaczniku szyfrowania UnityCN, blok LZMA jako
  nieobsługiwany.
- Parsery: CSV (cudzysłowy, nowe wiersze, tekst nie-UTF-8 czytany jak Windows-1252), SRT,
  wzorce nazw, recepta z mówcą, węzłem i czasem napisu.
- Zabezpieczenia: uruchomiona gra (sprawdzana przed odczytem nagłówków), anti-cheat także
  w katalogu głównym gry, lista wykluczeń (Path of Exile), profil `online`, zaszyfrowane
  i podpisane kontenery, wynik i statystyki poza folderem gry i bibliotekami gier, pliki gry
  otwierane tylko do odczytu; biblioteka narzędzia nie odwołuje się do bibliotek sieciowych
  .NET, a kod `extract` — także do dostawców tłumaczeń.
- `extract` od początku do końca: korpus zapisany poza grą, gra niezmieniona, opcje wiersza
  poleceń.
- `translate`: partie (węzły dialogów, napisy, UI), notatki i scena, ochrona znaczników
  `{0}` / `[X]` / `%s`, kontrola jakości jak w pipeline, podział partii po odmowie lub złej liczbie
  wyników, przerwanie po błędzie klucza, tryb prywatny i nieczytelne ustawienia = odmowa,
  przebieg próbny bez zmian w bazie, szacunek kosztu DeepL i LLM; dostawcy przez atrapę HTTP.

## Testy tylko dla Windows (`[WindowsFact]`)

Testy, które mają sens tylko na Windows (DPAPI, w przyszłości Windows OCR), oznaczamy
`[WindowsFact]` lub `[WindowsTheory]` zamiast `if (!OperatingSystem.IsWindows()) return;`.
Na Linuksie xUnit pokazuje je jako **Skipped** z polskim powodem, a nie jako Passed.
W pełni wykonuje je job `build-test` na `windows-latest`.

## Pokrycie kodu

Pokrycie zbiera `coverlet.collector` z ustawieniami w `tests/coverage.runsettings`: liczymy
tylko kod produkcyjny, bez projektów testowych, kodu generowanego i `obj/`. W CI job
`test-linux` wypisuje podsumowanie w zakładce Summary przebiegu, publikuje raport HTML jako
artefakt `coverage-report` i sprawdza progi pokrycia linii skryptem `tools/check-coverage.py`:
**Core ≥ 91%**, **Infrastructure ≥ 80%** (przy wprowadzeniu zmierzono 95,1% i 84,9%). Spadek
poniżej progu przerywa job. Kod `GameTranslatorOverlay.CorpusTool` jest w scalonym raporcie
(filtr `[GameTranslatorOverlay.*]*`), ale nie ma progu.

Lokalnie przed każdym pomiarem usuń stare wyniki: katalog `TestResults` nie jest czyszczony
automatycznie, a ReportGenerator scala **wszystkie** pliki `coverage.cobertura.xml`, które
w nim znajdzie. Z resztkami poprzednich przebiegów pokrycie lokalne wychodzi zawyżone względem
CI, które zawsze startuje od czystego checkoutu.

```bash
rm -rf TestResults            # PowerShell: Remove-Item -Recurse -Force TestResults
dotnet build GameTranslatorOverlay.slnx -c Release -p:EnableWindowsTargeting=true
dotnet test GameTranslatorOverlay.slnx -c Release --no-build -p:EnableWindowsTargeting=true --collect:"XPlat Code Coverage" --settings tests/coverage.runsettings --results-directory TestResults
dotnet tool restore
dotnet tool run reportgenerator "-reports:TestResults/**/coverage.cobertura.xml" "-targetdir:TestResults/coverage-report" "-reporttypes:HtmlInline;MarkdownSummaryGithub;Cobertura"
python3 tools/check-coverage.py TestResults/coverage-report/Cobertura.xml GameTranslatorOverlay.Core=91 GameTranslatorOverlay.Infrastructure=80
```

Raport HTML: `TestResults/coverage-report/index.html`. ReportGenerator jest przypięty jako
lokalne narzędzie w `.config/dotnet-tools.json` (5.5.11).

## Ewaluacja dostawców (ProviderEval)

[ProviderEval](../tools/GameTranslatorOverlay.ProviderEval/README.md) porównuje dostawców
i warianty promptu na korpusie EN→PL (chrF, czasy, kontrole jakości). To nie jest test
przechodzi/nie przechodzi: wynik ocenia człowiek. CI uruchamia go tylko jako smoke test
z Mockiem (bez kluczy i sieci) — sprawdza, że korpus, pipeline i raport działają:

```bash
dotnet run --project tools/GameTranslatorOverlay.ProviderEval -c Release --no-build -- --corpus eval/en-pl.sample.jsonl --providers mock --out eval/out
```

Opcje `--llm-thinking`, `--llm-effort`, `--llm-max-tokens`, `--llm-json` i `--llm-no-preset`
ustawiają pola zapytania serwera LLM (ADR-013, dopisek 2026-10-06); po przebiegu dostawcy
`llm` narzędzie podaje aktywne opcje i sumę tokenów z `usage`. Ewaluacji jakości (chrF)
z prawdziwymi dostawcami nie wykonano. Czasy i koszty DeepL i DeepSeek z badania 2026-10-05/06 (bez porównania jakości) są
w [API_PROVIDERS.md → Pomiary: DeepL i DeepSeek](API_PROVIDERS.md#pomiary-deepl-i-deepseek).

## Uruchamianie

Windows i .NET 10 SDK, katalog główny repozytorium:

```powershell
dotnet build GameTranslatorOverlay.slnx -c Release
dotnet build src/GameTranslatorOverlay.App -c Release --no-restore
dotnet test GameTranslatorOverlay.slnx -c Release --no-build --no-restore
```

App budujemy jawnie: samo uruchomienie testów nie jest potwierdzeniem kompilacji
aplikacji WPF. Przykładowe zawężenie testów:

```powershell
dotnet test tests/GameTranslatorOverlay.Core.Tests -c Release --filter "FullyQualifiedName~LiveReadingStabilizer"
dotnet test tests/GameTranslatorOverlay.Core.Tests -c Release --filter "FullyQualifiedName~Corpus"
dotnet test tests/GameTranslatorOverlay.Infrastructure.Tests -c Release
dotnet test tests/GameTranslatorOverlay.CorpusTool.Tests -c Release
```

Osobno Windows OCR na syntetycznym tekście:

```powershell
dotnet run --project tools/GameTranslatorOverlay.SmokeTest -c Release --no-build --no-restore
```

Smoke test wymaga pakietu językowego OCR. Sprawdza rozpoznane linie, pełny pipeline
i ponowne użycie cache. Nie otwiera ani nie steruje grą.

## Powtarzalne sondy

[LiveDiag](../tools/GameTranslatorOverlay.LiveDiag/README.md) mierzy capture,
OCR, oczekiwanie na tłumaczenie Mock i moment przygotowania aktualizacji sesji.
Przy wyborze gry należy podać jej tytuł oraz jawny profil; każde uruchomienie
ma świeży cache w pamięci. Domyślny raport nie zawiera rozpoznanego tekstu ani obrazów.

[SceneReplay](../tools/GameTranslatorOverlay.SceneReplay/README.md) obsługuje
własne okna i powtarzalne scenariusze:

| Obszar | Scenariusze |
|---|---|
| Zmiana widoku i spóźniona odpowiedź | `displayed`, `inflight`, `noisy` |
| Powrót, szybkie zmiany i zatrzymanie | `aba`, `churn`, `stop` |
| Zmiana treści i pomyłki OCR | `local-reading`, `reading-jitter`, `reading-whiff` |
| Przykrycie lokalne i zachowanie menu | `local-occlusion`, `local-occlusion-hover`, `local-occlusion-inflight` |
| Pozycja i szum geometrii | `moving-text`, `position-jitter` |
| Koszt kontroli podczas OCR | `ocr-timing` |
| Stałe menu i puste OCR przy ruchu | `hud-motion`, `hud-motion-whiff`, `hud-motion-small-whiff` |
| Stary napis po lokalnym zniknięciu etykiety (także nowy napis w jej miejscu) | `stale-junk`, `stale-junk-ghost`, `stale-texture`, `stale-newtext`, `stale-busy`, `stale-newdirty` |
| Napis, który wygasa, przygasa, zostaje albo wraca | `stale-fade`, `stale-dim`, `stale-present-junk`, `stale-blink` |
| Kwestia pisana literami (korpus syntetyczny) | `typing`, `typing-nohold` |

Przykład (katalog docelowy musi istnieć, plik raportu musi być nowy):

```powershell
dotnet run --project tools/GameTranslatorOverlay.SceneReplay -c Release -- --scenario local-occlusion --output C:\measurements\occlusion.jsonl
dotnet run --project tools/GameTranslatorOverlay.SceneReplay -c Release -- --scenario stale-texture --phase-ms 80 --output C:\measurements\stale-texture.jsonl
dotnet run --project tools/GameTranslatorOverlay.SceneReplay -c Release -- --scenario typing --output C:\measurements\typing.jsonl
```

Scenariusze `stale-*` mają własne okno 1500×900 fizycznych pikseli i Mock 1000 ms;
`--phase-ms 0..200` przesuwa zmianę względem zegara przechwytywania, a `--bright-spot-px`
odbiera sesji dowód w pikselach (ścieżka bez sondy nieobecności). Bez opcji rysują
syntetyczną etykietę i teksturę; `--assets`, `--texture` i `--texture-origin` czytają prawdziwy
wycinek etykiety i klatkę gry wyłącznie lokalnie, bez kopiowania i bez zapisu pikseli.
`typing` i `typing-nohold` mają skryptowy OCR, Mock 0 ms i korpus syntetyczny w katalogu
tymczasowym; `typing-nohold` wyłącza `HoldTypingPrefixes` dla porównania. Raporty
`displayed`, `inflight`, `aba`, `churn`, `stop`, `noisy` i `stale-*` zawierają `glyphCoverMs`
i `glyphCoverWaitMs` (budowa łatek z wypełnionymi literami w tle i czekanie na nie po
tłumaczeniu; sesja w SceneReplay buduje łatki zawsze, choć narzędzie nie rysuje nakładki).
Opis wszystkich opcji i metryk: [README SceneReplay](../tools/GameTranslatorOverlay.SceneReplay/README.md).

LiveDiag i SceneReplay używają lokalnego Mocka, blokują HTTP i nie otwierają ustawień ani
cache aplikacji użytkownika. Nie uruchamiamy równolegle sond ani ciężkiej kompilacji
podczas porównania czasów. Testy pozycji mierzą callback po zatrzymaniu kroku;
nie sprawdzają ciągłego śledzenia obrazu.

### Korpus gry (CorpusEval)

[CorpusEval](../tools/GameTranslatorOverlay.CorpusEval/README.md) (Windows) mierzy
przyciąganie odczytów OCR do korpusu z `CorpusTool extract`. Polecenia: `render` (prawda
syntetyczna — teksty korpusu na klatkach przez Windows OCR), `evaluate` (przegląd progów,
prawdziwy cache i kontrola negatywna), `bench` (czas `SnapBlock` p50/p95/p99), `replay`
(bloki z kopii cache przez prawdziwy `TranslationPipeline` z Mockiem), `session` (odczyty
jednej sesji gracza przez bramkę live), `prefixes` (progi dialogu pisanego literami)
i `typing` (dialog dopisywany co 3 znaki albo w przerwach po interpunkcji). `--features off`
(evaluate, bench, replay, typing) odtwarza dopasowanie sprzed rundy 2026-10-06 (4) do porównań
przed/po. Narzędzie nie wysyła niczego do sieci i nie używa kluczy.

`bench` ma własne domyślne progi (podobieństwo 0,85, margines 0,05, min. 12 znaków), inne niż
aplikacja (0,80 / 0,08 / 16 w `CorpusSnapOptions`) — do pomiaru stanu aplikacji podaj je jawnie;
`--ocr` przyjmuje kilka plików rozdzielonych `;`:

```powershell
dotnet run --project tools/GameTranslatorOverlay.CorpusEval -c Release -- bench --corpus PRYWATNE\gra.corpus.jsonl --ocr "PRYWATNE\ocr-a.jsonl;PRYWATNE\ocr-b.jsonl" --cache KOPIA\cache.db --out LICZBY --fuzzy 0.80 --margin 0.08 --min-length 16
```

### Działanie w ruchu (MotionLab)

[MotionLab](../tools/GameTranslatorOverlay.MotionLab/README.md) (Windows) nagrywa okno gry
(`record`), liczy „prawdę” offline OCR każdej klatki (`truth`) i odtwarza nagranie w czasie
rzeczywistym przez prawdziwą `LiveTranslationSession` i prawdziwe `OverlayWindow` z Mockiem
i kopią bazy (`replay`), a potem liczy metryki (`analyze`): pokrycie (w ruchu i w spoczynku,
HUD i tekst ruchomy), opóźnienie, nieaktualne tłumaczenia, położenie, miganie, zniknięcia HUD,
świeżość łatek i koszt, ze stykówkami najgorszych momentów. Nagrania, prawda i raporty zawierają
obrazy i teksty gry — trzymaj je poza repozytorium. Wyniki jednego nagrania różnią się między
przebiegami (czas OCR, tempo odtwarzania) o kilka punktów procentowych; porównuj kilka przebiegów.

```powershell
dotnet run --project tools/GameTranslatorOverlay.MotionLab -c Release -- replay pokoj-ruch2 --out C:\measurementsuch\pokoj-ruch2
```

### Wygląd nakładki (OverlayPreview)

[OverlayPreview](../tools/GameTranslatorOverlay.OverlayPreview/README.md) (Windows) składa
prawdziwą nakładkę trybu „Na oryginale (zakrywa)” (wspólny `OverlayBlockRenderer`, łatka
`GlyphCoverBuilder`, krój z profilu) na zapisanej klatce gry i zapisuje PNG, porównanie
i powiększenia bloków. Odtwarza pierwszy pełny przebieg OCR — nie symuluje wycinków OCR,
podtrzymywania bloków między klatkami, ponownego użycia łatki ani wstrzymywania kwestii pisanej
literami (niedokończona kwestia jest tłumaczona od razu); miękką łatkę, którą sesja live
wybiera przy ruchu tła, pokazuje się ręcznie przez `--cover soft`.
Baza tłumaczeń jest czytana przez kopię roboczą, HTTP jest zablokowane, a tekst spoza bazy
dostaje Mock `[PL] …`. Opcje `--cover crisp|soft|off`, `--font-family auto` i `--placement
cover|below` pozwalają porównać warianty na tej samej klatce.

## Jak interpretować wyniki

- Kod wyjścia 0 oznacza ukończoną sondę. Sprawdź także `fixtureValid` i wynik
  scenariusza (`expectedBehavior`, `desiredPositionBehavior` lub odpowiednie
  metryki danego wariantu), a nie sam kod wyjścia.
- Capture → update kończy się przed fizycznym rysowaniem nakładki i nie wyznacza
  całego opóźnienia od pojawienia się tekstu w grze.
- Mock z opóźnieniem 2000 ms symuluje wolnego dostawcę. Wynik nie jest pomiarem DeepL.
- Czasy operacji OCR i kontroli obrazu nakładają się. Nie sumujemy ani nie
  odejmujemy ich jako niezależnych etapów.
- Liczby zapytań i znaków Mock porównujemy przy tej samej treści i scenariuszu.
  Różny ruch kamery może zmienić liczbę odczytów i nie dowodzi oszczędności API.
- Podejrzenie whiffa jest sygnałem algorytmu, nie potwierdzonym błędem rozpoznania.
- Zapasowe przechwycenie ekranu może zniekształcić próbę izolowanego własnego okna.
- Czas usunięcia starego napisu w `stale-*` zależy od tego, kiedy zmiana wypada względem
  zegara przechwytywania (6 fps). Pojedyncza próba ze stałą fazą nie mówi nic o rozrzucie —
  porównuj serie z kilkoma wartościami `--phase-ms`.
- `stale-junk*`, `stale-present-junk`, `stale-newdirty` i `typing*` mają skryptowy OCR:
  badają logikę sesji, nie to, czy Windows OCR w grze rzeczywiście daje takie odczyty.
- `glyphCoverWaitMs` to czas, który budowa łatek dokłada po tłumaczeniu; `glyphCoverMs`
  biegnie równolegle z tłumaczeniem i nie sumuje się z nim.
- Dotychczasowe pomiary CorpusEval i OverlayPreview pochodzą z jednej gry (Escape Academy,
  korpus i klatki 4K; sesja PoE2 tylko jako kontrola negatywna) i z lokalnych kopii danych
  gracza; progi przyciągania wybrano na tych samych danych, na których je mierzono. To nie jest
  dowód zachowania w innych grach.

## Zasady danych i zakres dowodu

Do repozytorium trafiają kod, syntetyczne scenariusze i opisy. Nie dodajemy
zrzutów gier, kluczy, ustawień, cache użytkownika ani paczek lokalnej instalacji.
Raporty porównawcze i kopie binariów zachowujemy oddzielnie.

Testy xUnit używają wyłącznie danych syntetycznych: tekstów, obrazów i kontenerów Unity
tworzonych w teście (także łatka, profil tuszu i ikony w `GlyphCoverTests`), atrapy HTTP
i plików w katalogu tymczasowym. Klucze DeepL i LLM w testach `CorpusTool` i
`EnvironmentTranslationProviders` podaje sam test (Mock kluczy nie potrzebuje); ze zmiennych
środowiskowych systemu nie są czytane. Z repozytorium testy czytają wyłącznie jego własne
pliki (dostarczone profile, słownik globalny, przykładowy korpus `eval/en-pl.sample.jsonl`, kod
źródłowy `CorpusTool` w teście braku odwołań sieciowych) — nigdy danych gracza ani plików gry.

Korpus gry (`CorpusTool extract`) i tłumaczenia z wyprzedzeniem zostają lokalnie, poza
repozytorium (narzędzie odmawia zapisu w repozytorium poza `eval/private/`, które jest
w `.gitignore`). Narzędzia pomiarowe rozdzielają wyniki: CorpusEval zapisuje do `--out`
same liczby, a szczegóły z tekstami gry do `--private` / `--work` (wyjątek: `render`, którego
`--out` to plik próbek z tekstami gry); OverlayPreview zapisuje galerię z obrazami i tekstami
gry do `--out`. Że te katalogi leżą poza repozytorium, pilnuje uruchamiający — CorpusEval
i OverlayPreview tego nie sprawdzają. SceneReplay czyta `--assets` i `--texture` tylko
lokalnie i nie zapisuje pikseli ani treści OCR. Do dokumentów trafiają liczby i pojedyncze
etykiety interfejsu, nie teksty ani obrazy z gier.

Testy xUnit nie zależą od aktywnego pulpitu. Sondy z prawdziwym przechwytywaniem
są uruchamiane jawnie na stanowisku lokalnym; fizyczną prezentację, skróty,
ręczne ukrywanie i zachowanie przy zmianie monitorów sprawdzamy zgodnie z
[MANUAL_TESTING.md](MANUAL_TESTING.md).
