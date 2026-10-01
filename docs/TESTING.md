# Testy i pomiary — GameTranslatorOverlay

Stan sprawdzony 1 października 2026 (wydanie 0.4.0): **1114 testów xUnit** — **880 Core**
i **234 Infrastructure** — oraz kompilacja całego rozwiązania (z aplikacją WPF, narzędziami
i benchmarkami) bez ostrzeżeń. Ta runda była weryfikowana na Linuksie (.NET 10 SDK,
`-p:EnableWindowsTargeting=true`): 1111 testów zaliczonych, 3 testy DPAPI pominięte (Skipped).
Testy DPAPI i smoke test Windows OCR wymagają Windows i nie były w tej rundzie uruchamiane
lokalnie (job `build-test` w CI wykonuje je na `windows-latest`).
Poprzedni pełny przebieg na Windows (15 września 2026): 371 testów i smoke test z Mockiem.

Rozdzielamy testy logiki, lokalne sondy z rzeczywistym capture/OCR i ocenę
fizycznej nakładki przez użytkownika. Wynik jednej grupy nie zastępuje pozostałych.

## Podział

| Grupa | Miejsce | Zależności i zakres |
|---|---|---|
| Logika i regresje | `tests/GameTranslatorOverlay.Core.Tests` | xUnit, atrapy i dane w pamięci; bez aktywnej gry, WPF i prawdziwego OCR |
| Integracje | `tests/GameTranslatorOverlay.Infrastructure.Tests` | SQLite w plikach tymczasowych, atrapa HTTP, DPAPI na Windows |
| Smoke test | `tools/GameTranslatorOverlay.SmokeTest` | rzeczywisty Windows OCR na syntetycznym obrazie i lokalny pipeline |
| Sondy sesji | `tools/GameTranslatorOverlay.LiveDiag`, `tools/GameTranslatorOverlay.SceneReplay` | lokalny pulpit Windows, własne okno lub wskazana gra, Mock |
| Ocena wizualna | [MANUAL_TESTING.md](MANUAL_TESTING.md) | użytkownik, konkretna gra, DPI, monitory, układ i skróty |

CI uruchamia projekty xUnit na `windows-latest` oraz szybki przebieg na `ubuntu-latest`
(kompilacja z `-p:EnableWindowsTargeting=true`, pokrycie kodu z progiem i smoke test
ProviderEval z Mockiem). Smoke test OCR i sondy pulpitu wykonuje się osobno; nie są częścią
standardowego `dotnet test`. Pomiary wydajności są w osobnym projekcie `benchmarks/`
(BenchmarkDotNet, poza `dotnet test`) — [BENCHMARKS.md](BENCHMARKS.md).

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

Czyste helpery nie dowodzą poprawnego rysowania przez WPF ani jakości Windows OCR.
Ich powiązanie z sesją sprawdzają osobne sondy i obserwacje użytkownika.

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

Testy nie wywołują prawdziwych usług tłumaczeniowych ani nie czytają klucza użytkownika.
Pliki tymczasowe są odizolowane od danych aplikacji.

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
poniżej progu przerywa job.

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

Pomiary z prawdziwymi dostawcami nie były jeszcze wykonane (kontener bez dostępu do ich API).

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
dotnet test tests/GameTranslatorOverlay.Infrastructure.Tests -c Release
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

Przykład (katalog docelowy musi istnieć, plik raportu musi być nowy):

```powershell
dotnet run --project tools/GameTranslatorOverlay.SceneReplay -c Release -- --scenario local-occlusion --output C:\measurements\occlusion.jsonl
```

Obie sondy używają lokalnego Mocka, blokują HTTP i nie otwierają ustawień ani
cache aplikacji użytkownika. Nie uruchamiamy równolegle sond ani ciężkiej kompilacji
podczas porównania czasów. Testy pozycji mierzą callback po zatrzymaniu kroku;
nie sprawdzają ciągłego śledzenia obrazu.

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

## Zasady danych i zakres dowodu

Do repozytorium trafiają kod, syntetyczne scenariusze i opisy. Nie dodajemy
zrzutów gier, kluczy, ustawień, cache użytkownika ani paczek lokalnej instalacji.
Raporty porównawcze i kopie binariów zachowujemy oddzielnie.

Testy xUnit nie zależą od aktywnego pulpitu. Sondy z prawdziwym przechwytywaniem
są uruchamiane jawnie na stanowisku lokalnym; fizyczną prezentację, skróty,
ręczne ukrywanie i zachowanie przy zmianie monitorów sprawdzamy zgodnie z
[MANUAL_TESTING.md](MANUAL_TESTING.md).
