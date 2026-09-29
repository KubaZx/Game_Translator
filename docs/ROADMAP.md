# Roadmap — GameTranslatorOverlay

## Obecny stan — 29 września 2026

Ostatnie opublikowane wydanie to **0.3.0** (29 września 2026). Obejmuje poprawki
live wprowadzone po 0.2.2: aktualność sceny i lokalnych opisów, nadzorowane tłumaczenia w toku,
stabilizacja kolejnych odczytów oraz położenia. Kolejna lokalna poprawka zachowuje
napisy o identycznym obrazie źródła podczas ruchu tła. Runda 29 września dodaje
kolejnych dostawców tłumaczeń (Azure, Google, modele językowe z kontekstem gry i słownika)
i zamyka pozycje backlogu audytu #3. Przeszło **509 testów** i kompilacja całego
rozwiązania; próby wizualne nowych pól okna czekają na Windows (M23–M26).

Produkt jest rozwijany dla różnych gier. Escape Academy służy do pomiarów;
PoE2 jest jednym z obsługiwanych przypadków z dodatkowym profilem. Aktualny priorytet
to stabilność i czytelność w rozgrywce. Etapy poniżej zachowują historię powstawania
produktu; status implementacji nie zastępuje testów wizualnych na kolejnych grach.

## Kierunki dalszych prac

1. Rozszerzenie zachowywania stałych napisów: obecny dowód wymaga identycznych RGB; skalowanie, fallback i animowane tło pozostają otwarte.
2. Śledzenie położenia napisu między kolejnymi odczytami OCR.
3. Lepsza czytelność i zakrywanie na wzorzystym oraz animowanym tle.
4. Dostosowywanie tempa pracy do menu, dialogu i ruchu.

Przed implementacją każdego kierunku potrzebny jest pomiar wykonalności i kosztu.
Silny ruch nadal może czyścić napisy bez pewnego dowodu ich niezmienności; obecna
stabilizacja pozycji działa przy kolejnych odczytach OCR. Automatyczna detekcja obszaru tooltipu pozostaje otwarta.
History Mode i wyjaśnianie tekstu przez LLM nie są funkcjami obecnej aplikacji.

## Historia etapów

Statusy: ✅ zrobione · 🔨 w trakcie · ⬜ planowane

### Etap 0 — Analiza i dokumenty ✅

Specyfikacja produktu, wizja, roadmapa, dokumentacja architektury, bezpieczeństwa i prywatności w `docs/`.

**Kryterium ukończenia:** komplet dokumentów w `docs/` opisuje produkt, ograniczenia, architekturę i plan tak, że można realizować kolejne etapy bez wracania do ustaleń.

### Etap 1 — Szkielet rozwiązania ✅

Struktura projektów: `src/GameTranslatorOverlay.Core` (czysta logika, bez zależności Windows), `src/GameTranslatorOverlay.Infrastructure` (SQLite cache, DeepL, DPAPI, pliki profili/słowników), `src/GameTranslatorOverlay.App` (WPF: UI, capture, OCR, nakładka, skróty globalne), `tests/GameTranslatorOverlay.Core.Tests`, `tests/GameTranslatorOverlay.Infrastructure.Tests` (xUnit). TFM aplikacji `net10.0-windows10.0.19041.0`. DI przez Microsoft.Extensions.Hosting. CI na GitHub Actions (windows-latest): restore → build → test.

**Kryterium ukończenia:** `dotnet build` i `dotnet test` przechodzą lokalnie i w CI; solution ma docelowy układ projektów; CI zielone bez żadnych sekretów.

### Etap 2 — Przechwytywanie obrazu ✅

Capture przez GDI: lista okien z wyborem okna gry, screenshot okna (`PrintWindow` z PW_RENDERFULLCONTENT + fallback na crop ekranu), zrzut wskazanego regionu ekranu (`CopyFromScreen`/BitBlt).

**Kryterium ukończenia:** aplikacja wyświetla listę okien, użytkownik wybiera okno, aplikacja poprawnie zrzuca obraz okna oraz dowolnego regionu ekranu (okna i borderless fullscreen; exclusive fullscreen udokumentowany jako nieobsługiwany).

### Etap 3 — OCR systemowy ✅

`Windows.Media.Ocr.OcrEngine` jako `WindowsOcrProvider` za interfejsem `IOcrProvider`. Normalizacja i filtr śmieci na poziomie tekstu (API nie daje per-słowo confidence). Obsługa braku pakietu językowego: czytelny komunikat + instrukcja doinstalowania języka w ustawieniach Windows.

**Kryterium ukończenia:** tekst z przechwyconego obrazu jest rozpoznawany lokalnie; brak pakietu językowego kończy się zrozumiałym komunikatem, nie wyjątkiem.

### Etap 4 — Tłumaczenie przez API ✅

Interfejs `ITranslationProvider`; implementacje: `DeepLTranslationProvider` (api-free.deepl.com dla kluczy `:fx`, api.deepl.com dla pro; `/v2/translate` z batchem do 50 tekstów; `/v2/usage` do testu połączenia i licznika; obsługa 403 = zły klucz, 456 = limit wyczerpany, 429 = rate limit z ograniczonym retry, timeout, brak sieci) oraz `MockTranslationProvider` (deterministyczny, do testów i pracy bez klucza). Klucz API przez DPAPI (CurrentUser) w `%LOCALAPPDATA%\GameTranslatorOverlay`.

**Kryterium ukończenia:** tekst EN wraca jako PL przez DeepL; każdy scenariusz błędu (zły klucz, limit, rate limit, timeout, brak sieci) daje czytelny komunikat; testy jednostkowe przechodzą na Mocku bez sieci i sekretów.

### Etap 5 — SQLite cache ✅

Cache przez Microsoft.Data.Sqlite, migracje przez `PRAGMA user_version`. Priorytet wyników: ręczna korekta > wpis profilu gry > cache globalny > API. Deduplikacja zapytań in-flight. Tryb Cache-only.

**Kryterium ukończenia:** ten sam tekst nie idzie drugi raz do API; równoległe zapytania o ten sam tekst wykonują jedno wywołanie; w trybie Cache-only nic nie wychodzi do sieci.

### Etap 6 — Ręczne tłumaczenie regionu (PIERWSZE MVP) ✅ (do potwierdzenia testem ręcznym w realnej grze)

Globalny skrót `Ctrl+Shift+T` → zaznaczenie regionu → pipeline capture → OCR → słownik → cache → API → panel wyniku. Skrót steruje wyłącznie tłumaczem, nigdy grą.

**Kryterium ukończenia:** pełna ścieżka użytkownika działa end-to-end w realnej grze: skrót w trakcie gry, zaznaczenie regionu, przetłumaczony tekst w panelu wyniku, bez utraty sterowania grą.

### Etap 7 — Nakładka (overlay) ✅ (wersja podstawowa; testy ręczne DPI/multi-monitor w toku)

Okno WPF z `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, Topmost, per-monitor DPI (manifest PerMonitorV2), poprawne działanie multi-monitor.

**Kryterium ukończenia:** wynik tłumaczenia wyświetla się nad grą; kliknięcia przechodzą przez nakładkę do gry; nakładka nigdy nie przejmuje fokusu; pozycjonowanie poprawne przy różnych DPI i na wielu monitorach.

### Etap 8 — Tryb live ✅ (wykrywanie zmian + stabilizacja; jakość w ruchu w trakcie poprawy)

Obecnie GDI/PrintWindow z zapasowym przechwyceniem ekranu. Jedna pętla obsługuje capture, OCR i stan sceny, z wykrywaniem zmian, stabilizacją i okresowym OCR. Po wykrytej zmianie widoku nowe tłumaczenie może ruszyć obok starego; sesja utrzymuje najwyżej dwa zadania, bez kolejki nieaktualnych klatek. Podczas dłuższego oczekiwania dodatkowe przechwycenia sprawdzają scenę. Wyniki wykrytej poprzedniej sceny są odrzucane; wysłane tłumaczenie może dokończyć zapis do cache. WGC pozostaje możliwym ulepszeniem.

**Kryterium:** automatyczne tłumaczenie zmian bez blokowania UI; cache ogranicza powtórne wywołania API. Statyczna scena może uruchamiać OCR w ramach ponownego skanu lub powtórki po podejrzeniu pustego odczytu. Sprawność prezentacji w dynamicznej rozgrywce wymaga dalszych testów.

### Etap 9 — Strategie Tooltip / Subtitle / Universal 🔨 (Manual/Universal-live/Subtitle działają; automatyczna detekcja tooltipów — planowana)

Tryby wyświetlania zbudowane na silniku live: Tooltip Mode (tłumaczenie tooltipów w miejscu wyświetlania), Subtitle Mode (stały pas dialogów), Universal Live Mode (dowolny obszar).

**Kryterium ukończenia:** trzy strategie działają na wspólnym silniku i można się między nimi przełączać bez restartu aplikacji.

### Etap 10 — Słowniki, korekty, import/eksport ✅ (edytor słownika, import/eksport JSON, konflikty, priorytety)

Słowniki wg schematu `glossaries/<id>/en-pl.json` (dopasowanie całych słów/fraz, dłuższe frazy przed krótszymi, priorytety, wykrywanie konfliktów ten sam source → różne targety). Ręczne korekty tłumaczeń użytkownika (najwyższy priorytet). Import/eksport słowników i korekt.

**Kryterium ukończenia:** użytkownik dodaje termin do słownika i poprawia tłumaczenie z poziomu UI; korekta wygrywa z każdym innym źródłem; słownik da się wyeksportować i zaimportować bez utraty danych; konflikty są raportowane.

### Etap 11 — Opcjonalne profile gier ✅ (autodetekcja po procesie gry; walidacja minAppVersion od 2026-09-29)

Obsługa profili wg schematu `profiles/<id>/profile.json` (wykrywanie gry po nazwie procesu/tytule okna, parametry OCR i detekcji zmian, powiązany słownik, `minAppVersion`). Pierwszy dostarczony profil: Path of Exile 2 wraz ze słownikiem terminów.

**Kryterium ukończenia:** aplikacja wykrywa uruchomione PoE2 i proponuje profil; profil ustawia parametry i słownik; usunięcie profilu nie zmienia działania aplikacji dla innych gier.

### Etap 12 — Dystrybucja portable (wydanie 0.3.0 opublikowane; pełna ocena ręczna według checklisty nadal osobna)

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

## Historyczne priorytety pierwszej wersji

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
10. Dalsze funkcje po potwierdzeniu jakości i kosztu; integracji LLM obecnie nie ma.



## Backlog po audycie #3 (2026-08-06, znaleziska LOW odłożone świadomie)

Zamknięte 2026-09-29 (szczegóły w rundzie poniżej):

- ✅ `OcrScaling.ComputeUpscale`: profil może jawnie wyłączyć auto-powiększenie (`upscale: 1.0`);
  brak pola oznacza ustawienia aplikacji.
- ✅ `GlossaryService`: priorytet działa między terminami z rozróżnianiem wielkości liter i bez.
- ✅ `GlossaryService`: klucze terminów są normalizowane jak tekst z OCR.
- ✅ `RectPx.Scale`: skalowanie krawędzi zamiast X i Width osobno — bez dryfu 1 px.
- ✅ Ctrl+Shift+T przy otwartym selektorze regionu zamyka go (jak Esc).

Otwarte:

- Windows Graphics Capture jako alternatywna ścieżka capture — wg pomiarów na PoE2 (audyt #3
  i diagnoza live) GDI/PrintWindow działa dobrze (25–48 ms, zero fallbacku), więc WGC to
  ulepszenie „nice to have", nie naprawa.

## Weryfikacja przejęcia projektu — 2026-09-12

Priorytetem jest jakość live w różnych grach. Escape Academy jest pierwszym
przypadkiem pomiarowym; Path of Exile 2 pozostaje jednym z kolejnych przypadków.

- Punkt wyjścia v0.2.2: build całego rozwiązania bez ostrzeżeń, 174/174 testy,
  smoke test z Windows OCR i Mockiem zakończony kodem 0.
- Opisy „latest-frame-wins” w Etapie 8 i starszych dokumentach są zbyt szerokie.
  W chwili audytu pętla live sekwencyjnie czekała na OCR i tłumaczenie, zanim pobrała nowy
  obraz. Zmiana obrazu nie unieważniała wyniku będącego w locie (poprawione w rundzie sceny poniżej). Anulowanie przy
  zatrzymaniu sesji i zmianie ustawień to odrębne mechanizmy.
- Capture live używa PrintWindow/GDI. Windows Graphics Capture w opisie Etapu 8
  jest planem, nie obecną implementacją. Niezmieniony obraz ma też pełny przebieg
  bezpieczeństwa co 4 s, więc nie oznacza dosłownie zera OCR.
- Przygotowano jawny dobór profilu i pomiary w LiveDiag
  (instrukcja: ../tools/GameTranslatorOverlay.LiveDiag/README.md). Raport sondy
  opisuje przetwarzanie i aktualizacje sesji; sam nie dowodzi opóźnienia widocznej
  nakładki, jakości nowych tłumaczeń DeepL ani zachowania innych gier.
- Następny krok: porównywalne próby menu i dynamicznej sceny; dopiero po danych
  jedna hipoteza i jedna poprawka z pomiarem przed/po. Wyniki historyczne z gier
  nie zostały ponownie potwierdzone samym buildem, testami ani smoke testem.

### Runda 2026-09-13 — maksymalne oczekiwanie podczas ruchu

Naprawiono zerowanie terminu OCR przez krótką spokojną próbkę między ruchami kamery. Logika wspólna dla wszystkich gier; nie zmieniono progu ruchu 0,12 ani limitu 2500 ms. Test sekwencji ruch/ruch/spokój odtwarzał 10 s bez odczytu, po poprawce decyzje zapadają w 2505/5010/7515 ms przy próbkach co 167 ms. To dowód działania harmonogramu, nie pomiar opóźnienia nakładki. Testy: 204 (167 Core + 37 Infrastructure). Dalsze problemy: stare wyniki po zmianie sceny, częste ukrywanie podczas ruchu, wygląd i pełne skany wypierane przez częściowy OCR.

### Runda 2026-09-13 — aktualność napisów po zmianie widoku

Wczesne czyszczenie wykrytej zmiany sceny przed OCR i dopasowaniem bloków. Pule
przywracania poprzednich napisów są czyszczone razem z widokiem. Podczas dłuższego
OCR/tłumaczenia wykonywane są dodatkowe próbki obrazu, a wynik starej generacji
jest odrzucany. Nie uruchamiają one dodatkowego równoległego OCR ani tłumaczenia.
Automatyczne czyszczenie zachowuje ręczne ukrycie i obejmuje pasek napisów.

SceneReplay odtworzył dwie regresje: usunięcie poprzedniego napisu czekało 2,36 s
na Mocka; zaszumiony nowy odczyt przywracał stary napis mimo cięcia i trzymał go
przez całe okno 8 s. Test odpowiedzi w locie wykazał publikację starej sceny po
przejściu do następnej. Raporty i próby końcowe pozostają poza repozytorium.
Testy czystej logiki obejmują także ciągłą panoramę, powrót do wcześniejszego widoku
oraz reset okna. Nowe tłumaczenie nadal może czekać na odpowiedź dostawcy; ulepszenie
jego czasu oraz małe zmiany lokalne pozostają otwarte. Fizyczną prezentację w grze
potwierdza użytkownik, oddzielnie od wyników callbacków sondy.
### Runda 2026-09-13 — oczekiwanie na poprzednie tłumaczenie

Po wykrytej zmianie sceny kończy się oczekiwanie tej klatki, a wysłane tłumaczenie
pozostaje pod opieką sesji i może uzupełnić cache. Nowy widok dostaje drugie miejsce;
przy dwóch zajętych miejscach pętla śledzi najnowszy obraz, bez kolejki opisów.
Zatrzymanie anuluje i domyka także stare zadania. Rezerwacja znaków przed API
chroni wspólny limit sesji przy równoległości, a ponowny lookup cache domyka wyścig
deduplikacji. Osobny token ustawień blokuje zapis do nieaktualnego cache.

Pomiar kontrolny z Mockiem opóźnionym o 2 s porównuje gotowość nowego opisu,
liczbę wywołań i odrzucenie starej sceny. Nie wyznacza czasu odpowiedzi DeepL
ani faktycznej prezentacji w grze. Małe zmiany lokalne, jakość OCR i wygląd
nakładki pozostają oddzielnymi tematami kolejnych pomiarów.

### Runda 2026-09-13 — stabilizacja kolejnych odczytów

Pomiar odtworzył blokowanie podobnego krótszego opisu: Windows OCR trzy razy czytał
„The door is open”, ale sesja przez 12 s pokazywała „The door is locked”. Czysty
nowy tekst może teraz zastąpić podobny stary niezależnie od długości, po dwóch
kolejnych wiarygodnych obserwacjach. Celowo zachowano ochronę oczywistych fragmentów
całych słów z początku/końca odczytu oraz odczytów wyraźnie gorszej jakości.

Odtworzono też sumowanie niekolejnych pomyłek: B,A,B i B,pusty,B prowadziły do
błędnego napisu mimo stałego obrazu A. Seria jest teraz resetowana przez powrót A,
brak bloku w zeskanowanym obszarze i zmianę kandydata. Częściowy skan poza blokiem
nie stanowi obserwacji tego bloku. Pierwszy wiarygodny nowy wariant zleca szybką
powtórkę OCR obszaru; API dostaje dopiero przyjęty odczyt.

To stabilizacja wyników, nie nowy silnik rozpoznawania. Dwa identyczne, wiarygodnie
wyglądające błędne odczyty nadal mogą zostać przyjęte. Dopasowanie niewidocznych
duchów, znikanie nad ruchomym tłem i fizyczna prezentacja wymagają kolejnych danych
z gry. Kontrolne próby nie obiecują całkowitego braku migania w każdej scenie.

### Runda 2026-09-13 — próbkowanie przy terminie stabilności

Dla domyślnych 6 FPS termin 250 ms wypada między zwykłymi próbkami. Pętla może
skrócić najbliższy sen do tego przyszłego terminu, po czym wykonuje świeży capture
oraz zwykłe sprawdzenie zmian. Nie skraca 250 ms stabilności ani dwóch kolejnych
potwierdzeń podobnej treści. Przeterminowany termin zachowuje zwykłe próbkowanie,
żeby błędy capture nie powodowały zapętlenia. Przy pełnym interwale próbkowania
co najmniej równym stabilizacji (domyślnie <=4 FPS) zachowuje ustawiony rytm.

SceneReplay, Windows OCR, cztery fazy 0/50/100/150 ms: przy Mock 0 ms gotowość
opisu spadła z 388–510 do 307–451 ms (zysk w parach 59–81 ms); przy Mock 2000 ms
zysk wyniósł 33–66 ms. Przełączenie podczas wolnej odpowiedzi: 2512,6 → 2440,7 ms;
podobny krótszy opis: 2712,8 → 2594 ms. To małe serie lokalnych callbacków, nie
pomiar DeepL ani fizycznego wyświetlania w grze. Liczba zapytań/znaków Mock w
porównywanych parach nie wzrosła; ochrona przed niekolejnymi pomyłkami i starymi
odpowiedziami przeszła kontrolę. Koszt w innych scenach zależy od znalezionej treści.
Dodano 19 przypadków testowych harmonogramu; całość 280 (243 Core + 37 Infrastructure).

### Runda 2026-09-13 — koszt kontroli, lokalne przykrycie i pozycja

Trzy zatwierdzone usprawnienia dotyczą wspólnego silnika wszystkich gier:

- Pierwsza okresowa kontrola podczas OCR następuje po 1,5 interwału. Odczyt trwający
  co najmniej jeden interwał nadal wymaga świeżej kontroli po zakończeniu. Przy 6 FPS
  oznacza to do około 83 ms późniejszą pierwszą kontrolę trwającego odczytu. Kontrolne
  OCR 175/225 ms potrzebowało medianowo 1 zamiast 2 dodatkowych przechwyceń. Zysk czasu
  był mały i nie wystąpił dla każdej długości odczytu; czasy OCR i kontroli nakładają się.
- Potwierdzona nowa treść albo całe stare pole o znanym kontraście zastąpione
  jednolitym obrazem pozwala usunąć stary blok przed odpowiedzią dostawcy. Sprawdzane
  są też oczekujące źródła; odrzucona klatka ponawia wszystkie swoje obszary. Menu
  niezależne od znikającej etykiety oraz nowsze źródła paska napisów są zachowane.
  Częściowe usunięcie paska nie przedłuża jego czasu. Tekstura i niepełny obszar nadal
  nie stanowią dowodu braku tekstu.
- Stabilizacja pozycji ma stałą tolerancję 2 fizycznych pikseli na oś, oddzielnie
  od rozmiaru. Globalne czyszczenie przy silnym ruchu i maksymalna pauza OCR 2,5 s
  pozostają bez zmian.

Powtarzalne własne okno, rzeczywisty Windows OCR i Mock 2 s: lokalne przykrycie
usuwało Inspect po 2864 ms, obecnie po 289 ms. Nowy opis był gotowy po około
2311 ms; obie pozycje menu pozostały. Przy przykryciu przed pierwszą odpowiedzią
nowy opis: 4113 → 2498 ms, stare callbacki Inspect: 3 → 0. Obie pary zachowały
2 zapytania / 50 znaków Mock. Hover oraz warianty pomyłek OCR nie powodowały
błędnego znikania lub zmiany treści w sprawdzonych scenariuszach.

Syntetyczny odczyt geometrii z rzeczywistego capture, 12 kroków po 3 px: maksymalny
błąd położenia 18 → 2 px, maksymalny skok około 22 → 6 px. Próba szumu +/-1 px:
zero zmian pozycji, bez zniknięć. To pozycja callbacku po zatrzymaniu kroku,
nie pomiar ciągłego śledzenia ani fizycznej prezentacji nakładki.

Menu Escape Academy przed i po pierwszej poprawce: po 15 pełnych OCR i 8 bloków,
bez whiff/clear/hide/fallback. Czasy zależały także od rozgrzania systemowego OCR.
Dwie minutowe próby pokoju różniły się przebiegiem ruchu i proporcją wycinków;
nie służą do wyliczania procentowej poprawy jakości lub kosztu. Ocena wyglądu
nowej instalacji przez użytkownika pozostaje otwarta. Całość: 346 testów
(309 Core + 37 Infrastructure), jawny build App i smoke test OCR przeszły.
Szczegółowe raporty oraz zachowane binaria porównawcze pozostają poza repozytorium.

### Runda 2026-09-15 — stałe napisy podczas ruchu tła

Wprowadzono dokładny dowód niezmienności RGB dla już wyświetlonych źródeł. Ruch
świata nie usuwa takiego napisu, lecz nadal unieważnia generację starych wyników
w toku. Zmiana lub zasłonięcie jego pola usuwa ochronę. Dowód jest sprawdzany także
przy pustym OCR: zarówno po dużej zmianie sceny, jak i po kolejnych brakach odczytu
przy ruchu poniżej progu globalnego cięcia. Pozostałe progi i terminy nie zmieniły się.

Porównanie z kodem e1ab7c3, własne okno i prawdziwy capture, Mock 200 ms:

| Sonda | Obserwacje zegara bez wymaganego menu przed → po | Mock przed = po |
|---|---:|---:|
| Windows OCR, duży ruch tła | 77 → 0 | 3 zapytania / 79 znaków |
| Windows OCR na początku/końcu, wymuszone puste odczyty podczas ruchu | 79 → 0 | 2 zapytania / 65 znaków |

Liczniki opisują stan sesji w próbkach, nie osobne mignięcia. Obie próby po poprawce
zachowały niezmienne menu i nie przywróciły usuniętych napisów. W końcowej powtórce
zmienione etykiety znikały w około 16–95 ms. Wcześniejsze powtórki dochodziły do
156 ms; nie jest to gwarancja czasu w grze ani przy innym rytmie próbkowania.

Dodatkowa sonda małego ruchu ujawniła błąd pierwszej wersji poprawki: po kilku pustych
odczytach nadal wygasał niezmieniony blok. Przed dopięciem tej gałęzi były 62 próbki
bez menu, po nim 0, przy pięciu wymuszonych pustych odczytach i czterech przebiegach
w fazie stabilnego menu bez SceneCut. To porównanie dwóch lokalnych kandydatów,
nie osobny wynik względem wydania 0.2.2.

Weryfikacja: 371 testów (334 Core + 37 Infrastructure), jawna kompilacja App bez
ostrzeżeń, smoke test Windows OCR. Przeszło 15 dotychczasowych scenariuszy sesji;
po dopięciu zachowania przy pustym OCR powtórzono bez regresji jego próbę oraz
trzy warianty lokalnego przykrycia i dwie sondy dużego ruchu. Mały ruch również
przeszedł. Sprawdzone callbacki i logika nie oznaczają zaliczenia wizualnego M17–M22.

Ograniczenia: referencja wyłącznie z natywnej, nieskalowanej klatki bez fallback,
stały rozmiar okna, pełne pole z marginesem 3 px, znany kontrast i najwyżej 262144
pikseli na blok. Animowane lub przezroczyste tło nie daje takiego dowodu. Pierwsze
jeszcze niewyświetlone tłumaczenie menu nadal może zostać odrzucone przy ruchu.
Cała runda odbyła się bez gry i bez DeepL. Próby wyglądu, innych DPI oraz rozszerzenie
na pozostałe sposoby przechwytywania pozostają do wykonania.

### Runda 2026-09-29 — dostawcy tłumaczeń, słownik w kontekście i backlog audytu #3

Runda wspólna dla wszystkich gier; nie zmienia progów ani logiki sesji live.

- **Dostawcy:** Azure AI Translator (2 mln znaków miesięcznie w planie F0), Google Cloud
  Translation, model językowy przez API zgodne z OpenAI (OpenAI, OpenRouter, Groq, lokalne
  Ollama/LM Studio) i Claude na oficjalnym SDK Anthropic. DeepL pozostaje domyślny;
  każdy dostawca ma osobny klucz DPAPI. Decyzja i jej stosunek do DoD pkt 1: ADR-013.
- **Wspólny rdzeń HTTP** (`ProviderHttp`): timeout, ograniczony retry, mapowanie błędów;
  DeepL przeniesiony bez zmiany zachowania (dotychczasowe testy DeepL bez zmian).
- **Słownik w kontekście:** modele językowe dostają nazwę gry z profilu i terminy słownika
  występujące w tłumaczonych zdaniach (`FindTermsIn`, całe słowa, dłuższe frazy najpierw).
  Odpowiedź z inną liczbą tłumaczeń nie jest przypisywana blokom — małe partie są wtedy
  tłumaczone pojedynczo.
- **Backlog audytu #3:** pięć z sześciu pozycji zamknięte (wyżej); `minAppVersion` profili
  jest sprawdzany przy wczytywaniu.
- **Okno aplikacji:** przejście między polami bez zmiany wartości nie zapisuje ustawień
  i nie przebudowuje pipeline'u, więc nie anuluje tłumaczeń live w locie.

Weryfikacja: 509 testów (363 Core + 146 Infrastructure) i kompilacja całego rozwiązania
z aplikacją WPF bez ostrzeżeń — na Linuksie z `-p:EnableWindowsTargeting=true`. Nie było
w tej rundzie: uruchomienia okna, testów DPAPI, smoke testu Windows OCR, SceneReplay ani
wywołań prawdziwych usług tłumaczeniowych. Jakość tłumaczeń poszczególnych dostawców
i modeli nie została zmierzona. Do wykonania na Windows: M23–M26 z MANUAL_TESTING.md
oraz powtórka smoke testu i scenariuszy SceneReplay (zmiana `RectPx.Scale` może przesunąć
pozycję bloku o 1 px względem wcześniejszych pomiarów).

### Runda 2026-09-29 (2) — szybkość cache, połączeń i kontekst dialogu

- **Cache:** trafienia w RAM, zbiorcze liczniki użycia, `synchronous=NORMAL` w WAL. Pomiar
  lokalny (Linux, Mock, 50 klatek × 20 bloków z cache): ~10–13 ms → ~0,25 ms na klatkę.
  Czas na Windowsie z antywirusem i innym dyskiem nie był mierzony.
- **Połączenia:** pula HTTP na 10 minut i rozgrzewka `HEAD` (bez klucza i treści) przy
  rozpoczęciu zaznaczania regionu i starcie live; nigdy w Cache-only. Zysku nie zmierzono —
  proxy środowiska blokowało połączenia z serwerami dostawców.
- **Kontekst dialogu:** do 6 ostatnich linii wysłanych wcześniej do tego samego dostawcy
  (DeepL `context`, `previous_lines` dla modeli). Wpływ na jakość tłumaczeń nie był
  mierzony na prawdziwych dialogach — do sprawdzenia w grze (np. rodzaj gramatyczny kwestii).
- **CI:** job testów na `ubuntu-latest` i cache NuGet.

Weryfikacja: 509 testów (363 Core + 146 Infrastructure), kompilacja całego rozwiązania.
