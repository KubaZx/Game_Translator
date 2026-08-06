# Roadmap — GameTranslatorOverlay

Etapy realizowane sekwencyjnie; każdy etap ma jednoznaczne kryterium ukończenia. Wersjonowanie SemVer, start 0.1.0. Pierwsze MVP = ukończony Etap 6.

Statusy: ✅ zrobione · 🔨 w trakcie · ⬜ planowane

## Etapy

### Etap 0 — Analiza i dokumenty 🔨 (w trakcie)

Specyfikacja produktu, wizja, roadmapa, dokumentacja architektury, bezpieczeństwa i prywatności w `docs/`.

**Kryterium ukończenia:** komplet dokumentów w `docs/` opisuje produkt, ograniczenia, architekturę i plan tak, że można realizować kolejne etapy bez wracania do ustaleń.

### Etap 1 — Szkielet rozwiązania 🔨 (w trakcie)

Struktura projektów: `src/GameTranslatorOverlay.Core` (czysta logika, bez zależności Windows), `src/GameTranslatorOverlay.Infrastructure` (SQLite cache, DeepL, DPAPI, pliki profili/słowników), `src/GameTranslatorOverlay.App` (WPF: UI, capture, OCR, nakładka, skróty globalne), `tests/GameTranslatorOverlay.Core.Tests`, `tests/GameTranslatorOverlay.Infrastructure.Tests` (xUnit). TFM aplikacji `net10.0-windows10.0.19041.0`. DI przez Microsoft.Extensions.Hosting. CI na GitHub Actions (windows-latest): restore → build → test.

**Kryterium ukończenia:** `dotnet build` i `dotnet test` przechodzą lokalnie i w CI; solution ma docelowy układ projektów; CI zielone bez żadnych sekretów.

### Etap 2 — Przechwytywanie obrazu ⬜

Capture przez GDI: lista okien z wyborem okna gry, screenshot okna (`PrintWindow` z PW_RENDERFULLCONTENT + fallback na crop ekranu), zrzut wskazanego regionu ekranu (`CopyFromScreen`/BitBlt).

**Kryterium ukończenia:** aplikacja wyświetla listę okien, użytkownik wybiera okno, aplikacja poprawnie zrzuca obraz okna oraz dowolnego regionu ekranu (okna i borderless fullscreen; exclusive fullscreen udokumentowany jako nieobsługiwany).

### Etap 3 — OCR systemowy ⬜

`Windows.Media.Ocr.OcrEngine` jako `WindowsOcrProvider` za interfejsem `IOcrProvider`. Normalizacja i filtr śmieci na poziomie tekstu (API nie daje per-słowo confidence). Obsługa braku pakietu językowego: czytelny komunikat + instrukcja doinstalowania języka w ustawieniach Windows.

**Kryterium ukończenia:** tekst z przechwyconego obrazu jest rozpoznawany lokalnie; brak pakietu językowego kończy się zrozumiałym komunikatem, nie wyjątkiem.

### Etap 4 — Tłumaczenie przez API ⬜

Interfejs `ITranslationProvider`; implementacje: `DeepLTranslationProvider` (api-free.deepl.com dla kluczy `:fx`, api.deepl.com dla pro; `/v2/translate` z batchem do 50 tekstów; `/v2/usage` do testu połączenia i licznika; obsługa 403 = zły klucz, 456 = limit wyczerpany, 429 = rate limit z ograniczonym retry, timeout, brak sieci) oraz `MockTranslationProvider` (deterministyczny, do testów i pracy bez klucza). Klucz API przez DPAPI (CurrentUser) w `%LOCALAPPDATA%\GameTranslatorOverlay`.

**Kryterium ukończenia:** tekst EN wraca jako PL przez DeepL; każdy scenariusz błędu (zły klucz, limit, rate limit, timeout, brak sieci) daje czytelny komunikat; testy jednostkowe przechodzą na Mocku bez sieci i sekretów.

### Etap 5 — SQLite cache ⬜

Cache przez Microsoft.Data.Sqlite, migracje przez `PRAGMA user_version`. Priorytet wyników: ręczna korekta > wpis profilu gry > cache globalny > API. Deduplikacja zapytań in-flight. Tryb Cache-only.

**Kryterium ukończenia:** ten sam tekst nie idzie drugi raz do API; równoległe zapytania o ten sam tekst wykonują jedno wywołanie; w trybie Cache-only nic nie wychodzi do sieci.

### Etap 6 — Ręczne tłumaczenie regionu (PIERWSZE MVP) ⬜

Globalny skrót `Ctrl+Shift+T` → zaznaczenie regionu → pipeline capture → OCR → słownik → cache → API → panel wyniku. Skrót steruje wyłącznie tłumaczem, nigdy grą.

**Kryterium ukończenia:** pełna ścieżka użytkownika działa end-to-end w realnej grze: skrót w trakcie gry, zaznaczenie regionu, przetłumaczony tekst w panelu wyniku, bez utraty sterowania grą.

### Etap 7 — Nakładka (overlay) ⬜

Okno WPF z `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, Topmost, per-monitor DPI (manifest PerMonitorV2), poprawne działanie multi-monitor.

**Kryterium ukończenia:** wynik tłumaczenia wyświetla się nad grą; kliknięcia przechodzą przez nakładkę do gry; nakładka nigdy nie przejmuje fokusu; pozycjonowanie poprawne przy różnych DPI i na wielu monitorach.

### Etap 8 — Tryb live ⬜

Windows Graphics Capture dla ciągłego przechwytywania. Analiza 3–6 fps, OCR tylko przy wykrytej zmianie obrazu, latest-frame-wins (nieaktualne zadania anulowane przez CancellationToken), debounce niestabilnego tekstu.

**Kryterium ukończenia:** obserwowany obszar tłumaczy się automatycznie po zmianie treści; niezmieniony obraz nie generuje OCR ani wywołań API; UI nie jest nigdy blokowane.

### Etap 9 — Strategie Tooltip / Subtitle / Universal ⬜

Tryby wyświetlania zbudowane na silniku live: Tooltip Mode (tłumaczenie tooltipów w miejscu wyświetlania), Subtitle Mode (stały pas dialogów), Universal Live Mode (dowolny obszar).

**Kryterium ukończenia:** trzy strategie działają na wspólnym silniku i można się między nimi przełączać bez restartu aplikacji.

### Etap 10 — Słowniki, korekty, import/eksport ⬜

Słowniki wg schematu `glossaries/<id>/en-pl.json` (dopasowanie całych słów/fraz, dłuższe frazy przed krótszymi, priorytety, wykrywanie konfliktów ten sam source → różne targety). Ręczne korekty tłumaczeń użytkownika (najwyższy priorytet). Import/eksport słowników i korekt.

**Kryterium ukończenia:** użytkownik dodaje termin do słownika i poprawia tłumaczenie z poziomu UI; korekta wygrywa z każdym innym źródłem; słownik da się wyeksportować i zaimportować bez utraty danych; konflikty są raportowane.

### Etap 11 — Profile gier + profil PoE2 ⬜

Obsługa profili wg schematu `profiles/<id>/profile.json` (wykrywanie gry po nazwie procesu/tytule okna, parametry OCR i detekcji zmian, powiązany słownik, `minAppVersion`). Pierwszy dostarczony profil: Path of Exile 2 wraz ze słownikiem terminów.

**Kryterium ukończenia:** aplikacja wykrywa uruchomione PoE2 i proponuje profil; profil ustawia parametry i słownik; usunięcie profilu nie zmienia działania aplikacji dla innych gier.

### Etap 12 — Wersja produkcyjna ⬜

Release: `dotnet publish` win-x64, aplikacja portable. Instrukcje użytkownika, `MANUAL_TESTING.md` (testy wymagające pulpitu Windows: OCR na żywo, nakładka, skróty — wyłącznie ręczne), licencje zależności, polityka prywatności, disclaimer. Artefakt Release z CI na tag lub manualnie.

**Kryterium ukończenia:** spełnione wszystkie 25 punktów Definition of Done poniżej.

## Definition of Done pierwszej wersji (25 punktów)

Instalacja i konfiguracja:

1. Użytkownik pobiera i uruchamia aplikację bez instalowania Pythona, lokalnych modeli AI/LLM, CUDA ani Dockera.
2. Aplikacja jest portable (unpackaged) i działa na Windows 10 2004+ oraz Windows 11.
3. Użytkownik wpisuje klucz DeepL w ustawieniach; klucz jest zapisywany przez DPAPI i nie pojawia się nigdy w logach, plikach konfiguracyjnych plaintext ani w repo.
4. Przycisk testu połączenia weryfikuje klucz przez `/v2/usage` i pokazuje aktualne zużycie limitu.
5. Przy braku pakietu językowego Windows OCR użytkownik dostaje czytelny komunikat z instrukcją doinstalowania języka.

Podstawowy przepływ (Manual Region Mode):

6. Użytkownik wybiera okno gry z listy okien.
7. Użytkownik wybiera język źródłowy i docelowy.
8. Globalny skrót `Ctrl+Shift+T` działa w trakcie gry (okno / borderless fullscreen) i uruchamia zaznaczanie regionu.
9. Zaznaczony region jest przechwytywany, rozpoznawany systemowym OCR i tłumaczony.
10. Wynik pojawia się w panelu/nakładce; użytkownik nie traci sterowania grą (nakładka click-through, bez fokusu).
11. Nakładka wyświetla się poprawnie przy różnych DPI i na wielu monitorach.
12. UI nigdy nie jest blokowane przez OCR ani sieć (async/await + CancellationToken).

Słownik, korekty, cache:

13. Użytkownik poprawia tłumaczenie ręcznie; korekta ma najwyższy priorytet przy kolejnych wystąpieniach tekstu.
14. Użytkownik dodaje termin do słownika z poziomu UI; słownik dopasowuje całe słowa/frazy z priorytetami.
15. Powtórzony tekst jest serwowany z cache SQLite bez wywołania API; zapytania in-flight są deduplikowane.
16. Tryb Cache-only działa: żadne dane nie wychodzą do sieci.
17. Licznik użycia API i limity (miesięczny, znaków na sesję) działają i ostrzegają przed przekroczeniem.

Prywatność i bezpieczeństwo:

18. Do API idzie wyłącznie rozpoznany tekst — nigdy obraz; domyślnie żaden screenshot nie jest zapisywany na dysk.
19. Tryb prywatny działa: bez historii, bez logowania treści tłumaczeń, cache tylko w pamięci, czyszczony po sesji.
20. Aplikacja nie wykonuje żadnej ingerencji w grę (bez DLL injection, hooków, czytania pamięci, inputu do gry); disclaimer jest widoczny w aplikacji i dokumentacji.

Błędy i profil:

21. Scenariusze błędów (403 zły klucz, 456 limit wyczerpany, 429 rate limit, timeout, brak sieci) dają czytelne komunikaty: co się stało, czy tłumaczenie stoi, co zrobić; stack trace tylko do logu.
22. Profil Path of Exile 2 jest dostarczony, wykrywa grę i podpina słownik terminów PoE2.

Budowanie i testy:

23. `dotnet build` + `dotnet test` przechodzą lokalnie i w CI (windows-latest, Mock provider, zero sekretów).
24. Release buduje się przez `dotnet publish` win-x64 zgodnie z instrukcją w dokumentacji; artefakt powstaje z CI na tag lub manualnie.
25. Testy ręczne (OCR na żywo, nakładka, skróty globalne) są opisane w `MANUAL_TESTING.md` i dają się wykonać wg dokumentacji.

## Priorytety produktu

Przy każdym konflikcie decyzyjnym rozstrzyga niższy numer:

1. Bezpieczeństwo i brak ingerencji w grę.
2. Działający tryb ręczny (Manual Region Mode).
3. Czytelność wyników.
4. Stabilność.
5. Cache i kontrola kosztów API.
6. Obsługa błędów.
7. Nakładka.
8. Tryb live.
9. Profile gier.
10. Wyjaśnianie mechanik (opcjonalny LLM, domyślnie OFF).
