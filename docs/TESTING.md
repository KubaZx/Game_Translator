# Testy i pomiary — GameTranslatorOverlay

Stan sprawdzony 29 września 2026: **482 testy xUnit** — **361 Core** i
**121 Infrastructure** — oraz kompilacja całego rozwiązania (z aplikacją WPF) bez ostrzeżeń.
Ta runda była weryfikowana na Linuksie (.NET 10 SDK, `-p:EnableWindowsTargeting=true`):
testy DPAPI i smoke test Windows OCR wymagają Windows i nie były w niej uruchamiane.
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

CI uruchamia projekty xUnit na `windows-latest`. Smoke test i sondy pulpitu
wykonuje się osobno; nie są częścią standardowego `dotnet test`.

## Co obejmuje Core.Tests

- Normalizację OCR z zachowaniem liczb, znaków i zakresów; grupowanie linii,
  filtr śmieci, podobieństwo odczytów, hashe i geometrię.
- Dopasowanie słownika, konflikty, priorytet ręcznych poprawek i lokalnych wyników;
  priorytet między terminami z rozróżnianiem wielkości liter i bez, normalizację kluczy
  oraz wyszukiwanie terminów wewnątrz zdań (całe słowa, dłuższe frazy najpierw).
- Przekazywanie nazwy gry i pasujących terminów dostawcom kontekstowym.
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

Czyste helpery nie dowodzą poprawnego rysowania przez WPF ani jakości Windows OCR.
Ich powiązanie z sesją sprawdzają osobne sondy i obserwacje użytkownika.

## Co obejmuje Infrastructure.Tests

- SQLite: odczyt/zapis, migracje, import/eksport oraz zachowanie ręcznych poprawek.
- DeepL na fałszywym `HttpMessageHandler`: format żądań i odpowiedzi, wybór endpointu,
  batchowanie, błędy 403/456/429, retry, timeout i uwierzytelnianie w nagłówku.
- Azure i Google: nagłówki z kluczem i regionem (klucz nigdy w adresie), parametry
  języków, mapowanie błędów, brak ponawiania wyczerpanego limitu, odpowiedź proxy.
- Modele językowe: prompt (języki, gra, terminy, „dane, nie instrukcje”), tolerancyjny
  parser JSON, walidacja adresu (HTTPS poza localhost), tłumaczenie pojedyncze przy złej
  liczbie wyników, odmowa modelu, brak modelu, niedziałający serwer lokalny.
- Claude przez oficjalne SDK: model domyślny, fallback przy odmowie z nagłówkiem beta,
  schemat JSON, `effort` tylko dla obsługujących go modeli, błędy API i test przez Models API.
- Katalog dostawców i zapis nowych ustawień (także odczyt starego `settings.json`).
- Zgodność dostarczonych profili z bieżącą wersją aplikacji (`minAppVersion`).
- DPAPI: szyfrowanie i odszyfrowanie danych dla bieżącego użytkownika Windows.

Testy nie wywołują prawdziwych usług tłumaczeniowych ani nie czytają klucza użytkownika.
Pliki tymczasowe są odizolowane od danych aplikacji.

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
