# Roadmap — GameTranslatorOverlay

## Obecny stan — 6 października 2026

Ostatnie wydanie to **0.6.0** (6 października 2026, runda 2026-10-06 (7)): działanie w ruchu
kamery — śledzenie napisów między odczytami OCR, odświeżanie łatek z Windows Graphics Capture
(w ruchu i zamiast zrzutu ekranu, gdy gra nie wspiera PrintWindow), ponowny OCR pustego odczytu
w pasach, stabilny krój; zmierzone na nagraniach Escape Academy w narzędziu MotionLab, w grze na
żywo jeszcze nie oglądane (scenariusze M41–M42 w [MANUAL_TESTING.md](MANUAL_TESTING.md)).

Wcześniej **0.5.0** (6 października 2026): stary napis znika razem z oryginałem (sonda
pikseli `KnownTextAbsenceProbe` także na teksturze, pełny skan co 4 s niezależnie od wycinków,
śmieciowy odczyt nie podtrzymuje ani nie przywraca napisu, a tłumaczenie napisu, który wróci
w ciągu 10 s, wraca też na pasek napisów); korpus tekstów gry według ADR-014 — osobne narzędzie
offline `CorpusTool` (`extract`, `translate`) czyta teksty z plików wyłączonej gry według recepty
w profilu (pierwsza: Escape Academy) i tłumaczy je z wyprzedzeniem do cache, a aplikacja wczytuje
tylko jego wynik (`<folder danych>\corpus\<profil>.corpus.jsonl`) i przyciąga do niego odczyty
OCR, także krótkie etykiety z pomyłkami OCR i początki kwestii dialogu, a śmieci OCR odrzuca;
opcje serwera LLM przypisane do adresu (gotowy DeepSeek bez myślenia, Ollama
z `reasoning_effort: none`); tryb „Na oryginale (zakrywa)” wygląda jak napis gry (wypełnione
litery oryginału zamiast prostokąta, kolor, kontur, cień i linia bazowa z pikseli gry, krój
z profilu — dołączony Lexend Deca, ustawienie „Jak w grze (krój z profilu)”), a kwestia
z korpusu pisana literami czeka w nim na koniec pisania. Dla deweloperów: OverlayPreview
(prawdziwa nakładka na zapisanych klatkach gry), CorpusEval, scenariusze SceneReplay `stale-*`
i `typing`, projekt testów CorpusTool. Linia 0.4 dała szybszy tryb live, komunikaty w nakładce,
pamięć dialogu i glosariusze DeepL.

Przeszło **1732 testy** (1286 Core + 280 Infrastructure + 166 CorpusTool) na Windows; build
całego rozwiązania bez ostrzeżeń. Zachowanie sesji live zmierzono w SceneReplay (callbacki sesji,
nie fizyczna nakładka), wygląd — na klatkach 4K z Escape Academy przez OverlayPreview, dopasowanie
do korpusu — powtórkami bloków z kopii cache przez pipeline z Mockiem (CorpusEval).
**W grze na żywo** była tylko krótka sesja Escape Academy rano 6 października na wersji
z korpusem, przed rundami 2026-10-06 (4)–(6). Nie oglądano jeszcze w grze dopasowania etykiet
i początków kwestii, odrzucania śmieci, nowego wyglądu napisów ani wstrzymywania kwestii pisanej
literami; w innych grach nic z linii 0.5 nie było sprawdzane na żywo. Scenariusze ręczne
wydania 0.5.0 to M36–M40 z [MANUAL_TESTING.md](MANUAL_TESTING.md); przejście scenariuszy
M23–M35 z linii 0.3–0.4 nie jest nigdzie odnotowane.

Produkt jest rozwijany dla różnych gier. Escape Academy (profil z receptą korpusu i krojem) służy
do pomiarów; PoE2 jest jednym z obsługiwanych przypadków z dodatkowym profilem, bez korpusu
(Path of Exile 1/2 są na liście wykluczeń ADR-014). Aktualny priorytet: sprawdzić 0.5.0 w grze
na żywo — wygląd w trybie zakrywania, kwestie pisane literami, etykiety i odrzucanie śmieci przy
korpusie — i dopiero na tej podstawie wybierać kolejne kierunki; dalej stabilność i czytelność
w rozgrywce.
Etapy poniżej zachowują historię powstawania produktu; status implementacji nie zastępuje testów
wizualnych na kolejnych grach.

## Kierunki dalszych prac

1. Rozszerzenie zachowywania stałych napisów: obecny dowód wymaga identycznych RGB; skalowanie, fallback i animowane tło pozostają otwarte.
2. ✅ Śledzenie położenia napisu między kolejnymi odczytami OCR — runda 2026-10-06 (7) (tylko
   napisy z łatką, tryb zakrywania; w innych trybach nadal aktualizacja przy odczycie).
3. Lepsza czytelność i zakrywanie na wzorzystym oraz animowanym tle.
4. Dostosowywanie tempa pracy do menu, dialogu i ruchu.
5. Pokazanie ostrzeżenia kontroli jakości (`TranslationOutcome.QualityWarning`,
   `UsageTracker.QualityIssues`) w oknie lub nakładce — dziś jest tylko w cache i licznikach.
6. Przerywanie przygotowania glosariusza DeepL w tle po przełączeniu na Cache-only albo tryb
   prywatny (dziś kończy się samo po najwyżej 5 s).
7. Klatka live anulowana przez zmianę ustawień czeka dziś na ponowne sprawdzenie co 4 s —
   powinna być odczytana od razu po przebudowie pipeline'u.
8. Wpisy nieaktualne po zmianie formatu (`reflow`), dla których dostawca zwraca pusty wynik,
   są ponawiane przy każdym wystąpieniu — potrzebny znacznik ostateczności jak `qa-final`.
9. Edytor skrótów w oknie (dziś tylko `settings.json`, m.in. `liveToggleHotkey`).
10. Publikacja dwufazowa: najpierw bloki znane lokalnie, potem tłumaczenia dostawcy w tej
    samej klatce.
11. HTTP/2 i metryki połączeń (czas zestawienia, ponowne użycie) dla dostawców.
12. Strumieniowanie odpowiedzi modeli językowych, żeby pierwsze linie pojawiały się wcześniej.
13. Pilotaż `paragraphCacheKeys` u prawdziwego dostawcy (najpierw PoE2: 21% znaków
    powtórzonych w innych blokach) i decyzja o włączeniu domyślnie — runda 2026-10-06 (3).
14. Tryb prywatny: czytanie (bez zapisu) tłumaczeń korpusu z wyprzedzeniem z bazy na dysku.
15. Odświeżenie korpusu po nowym `extract`/`translate` bez restartu aplikacji (dziś plik korpusu
    jest czytany ponownie dopiero przy przebudowie pipeline'u po zmianie ustawień tłumaczenia albo
    profilu, a tłumaczenia już odczytane z bazy zostają w pamięci trafień do restartu).
16. Szerokość okna dialogu dla jednowierszowego początku kwestii (geometria bloku z nakładki),
    żeby pełne tłumaczenie od pierwszego odczytu miało docelową liczbę wierszy w trybach z tekstem
    obok oryginału (w trybie zakrywania kwestia czeka na koniec pisania) — rundy 2026-10-06 (4), (6).
17. Etykieta korpusu z liczbą albo datą obok („…: <data>”) jako tekst korpusu + okruch dosłowny,
    bez zapytania do dostawcy — runda 2026-10-06 (4).
18. ✅ Łatka „Na oryginale” na ruchomym tle odświeżana z bieżącej klatki między odczytami OCR —
    w ruchu kamery z WGC do ~30×/s, bez ruchu kamery z klatki PrintWindow przy każdym przechwyceniu
    (ok. 6×/s), gdy zmieni się podpis pola; łatka zbudowana jako miękka zostaje miękka do
    następnego odczytu — runda 2026-10-06 (7).
19. Wierniejszy krój: kursywa, szerokość (np. Lexend zamiast Lexend Deca), osobny krój dla
    rodzajów tekstu w profilu (etykiety szeryfowe), kerning — runda 2026-10-06 (5).
20. Rama przycisku jako granica dopasowania jednoliniowego napisu (dziś wolne miejsce liczone
    tylko do następnego bloku albo krawędzi monitora) — runda 2026-10-06 (5).
21. Smugi po wypełnianiu liter: ciemne kleksy w miejscu ogonków („p”, „y”) i rozmyta granica dwóch
    płaskich teł pod napisem (widoczne w powiększeniu 1:1) — runda 2026-10-06 (5).
22. Pamięć budowy łatki dialogu w 4K: ok. 21 MB przy liniach ≥ 90 px (liczony w połowie
    rozdzielczości), 72–76 MB przy liniach 36–89 px (pełna rozdzielczość; w rundzie (5) podano
    21–58 MB), pełne odśmiecania przy kolejnych budowach — bufory wielokrotnego użytku albo niższy
    próg zmniejszania — runda 2026-10-06 (5), [BENCHMARKS.md](BENCHMARKS.md).
23. Praca na wątku UI: rozgrzanie krojów (ok. 212 ms przy starcie live) i pomiar grubości nowego
    tekstu (4 rendery) poza wątkiem UI — runda 2026-10-06 (5).
24. Odróżnienie wyłącznego pełnego ekranu od okna bez ramki w komunikacie o pełnym ekranie
    (dziś liczy się tylko zajęcie całego monitora przy braku PrintWindow) — runda 2026-10-06 (5).
25. Napis na ścianie przy podchodzeniu (rośnie, obraca się w perspektywie): śledzenie ze skalą
    albo szybki lokalny OCR zgubionego bloku zamiast zdjęcia; tekst ruchomy przy ruchu kamery ma
    dziś ok. 16% pokrycia (pokój) — runda 2026-10-06 (7).
26. Windows Graphics Capture w grach na żywo: wpływ otwartej sesji na opóźnienie obrazu gry
    (składanie przez DWM), HDR, wyłączny pełny ekran, Windows 10 bez ukrywania ramki — runda (7).
27. Koszt WPF przy odświeżaniu łatek do 30×/s przy wielu blokach (dziś zmierzony tylko czas
    śledzenia: ok. 2,5 ms na odświeżenie) — runda 2026-10-06 (7).
28. MotionLab: fałszywe „miganie”/„zgubione”, gdy offline OCR (prawda) skleja dwa wiersze HUD
    w jeden blok albo czyta ikonę klawisza raz z napisem, raz osobno — runda 2026-10-06 (7).
29. Śledzenie napisów także poza trybem zakrywania (pod oryginałem, obok): wzorzec liter bez
    łatki, żeby tłumaczenie jechało za napisem w ruchu we wszystkich układach — runda (7).
30. Łatka na animowanym tle bez ruchu kamery (woda, ogień, migające światło pod napisem):
    odświeżanie częstsze niż przechwytywanie PrintWindow (dziś ok. 6×/s), np. z wycinków WGC,
    i ostra łatka zamiast miękkiej — runda (7).

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

### Etap 12 — Dystrybucja portable (wydanie 0.6.0; pełna ocena ręczna według checklisty nadal osobna)

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

Weryfikacja: 531 testów (383 Core + 148 Infrastructure) i kompilacja całego rozwiązania
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

Weryfikacja: 531 testów (383 Core + 148 Infrastructure), kompilacja całego rozwiązania.

### Runda 2026-09-29 (3) — licznik czasu i glosariusze DeepL

- **Licznik czasu:** `LatencyMonitor` (Core, w `UsageTracker`) zbiera w pamięci ostatnie
  200 próbek na etap: przechwycenie, OCR, udane zapytanie do dostawcy oraz klatka → gotowy
  napis osobno dla tekstu nowego i znanego. Panel „Szybkość” pokazuje medianę i p90,
  „Kopiuj raport” — mediana, p90, maksimum i liczba pomiarów. Czas rysowania nakładki
  przez WPF nie jest mierzony. Cel: prawdziwe czasy DeepL z gry zamiast pomiarów z Mocka.
- **Glosariusze DeepL:** `DeepLGlossaryManager` buduje glosariusz z całego słownika, gdy
  partia zawiera termin; jeden glosariusz na zawartość słownika (ponowne użycie po
  restarcie), sprzątanie starych wersji w tle, 10 minut przerwy po błędzie, ponowienie
  tłumaczenia bez glosariusza. Wyłączone w trybie prywatnym. Nie sprawdzono jeszcze na
  prawdziwym koncie DeepL (proxy środowiska blokuje DeepL) — do potwierdzenia w M27.

Weryfikacja: 562 testy (402 Core + 160 Infrastructure), kompilacja całego rozwiązania.

### Runda 2026-10-01 — wydanie 0.4.0

Dwie fale równoległych pakietów, każdy z przeglądem, potem przegląd połączenia i wydanie.

- **Fala 1:** cache i kontrola jakości (koniec pętli płatnych zapytań dla wpisów profilu
  i Mock, odporność na błędy bazy z pamięcią awaryjną, `TranslationQualityGate` z jednym
  ponowieniem dla LLM i znacznikami `qa=`/`qa-final`); słownik (terminy prywatne poza
  glosariuszem DeepL, glosariusz w tle z budżetem 300 ms, `GlossaryPrecedence`, liczba mnoga,
  łamanie wierszy, dwukropek, zakres `label`); ewaluacja (ProviderEval, chrF, korpus 42 linii);
  benchmarki BenchmarkDotNet ([BENCHMARKS.md](BENCHMARKS.md)); CI (pokrycie z progiem,
  `[WindowsFact]`, concurrency, Dependabot).
- **Fala 2:** szybkość live (stabilność od zauważonej zmiany, szybka ścieżka znanych klatek,
  pomiar „Zmiana → napis”), komunikaty w nakładce (`OverlayNoticePolicy`, filtr echa
  komunikatu w OCR), pamięć dialogu i płeć gracza dla LLM (`DialogMemory`, `pg=f/m`), skrót
  live `Ctrl+Shift+L` (`LiveTargetResolver`, zasobnik, pamięć ostatniej gry).
- **Przegląd połączenia fal:** ręczne tłumaczenie zawsze daje odpowiedź przy schowanej
  nakładce; pamięć awaryjna także przy bazie, która czyta, ale nie zapisuje; stary wynik dla
  wszystkich równoległych tłumaczeń tego samego tekstu przy braku sieci; zmiana płci nie
  wznawia ponowień `qa-final`; komunikat „zatrzymane” przy zminimalizowanej grze w ostatnim
  położeniu gry; zmiana samego wyglądu nie przebudowuje pipeline'u (`PipelineSnapshot`);
  token CI tylko do odczytu poza publikacją; smoke ProviderEval (Mock) w CI.

Weryfikacja: 1114 testów (880 Core + 234 Infrastructure), na Linuksie 1111 zaliczonych
i 3 pominięte (DPAPI), kompilacja całego rozwiązania z aplikacją WPF bez ostrzeżeń
(`-p:EnableWindowsTargeting=true`). Pokrycie przy wprowadzeniu progu: Core 95,1%,
Infrastructure 84,9%. Nie wykonano: uruchomienia okna na Windows, testów DPAPI, smoke testu
OCR, SceneReplay, wywołań prawdziwych dostawców (kontener nie ma dostępu do DeepL, Anthropic
i innych) ani pomiaru ProviderEval na prawdziwych dostawcach. Zyski szybkości live wynikają
z logiki harmonogramu i testów, nie z pomiaru w grze. Do wykonania na Windows: M23–M35
z [MANUAL_TESTING.md](MANUAL_TESTING.md).

### Runda 2026-10-05 — stary napis na ekranie

Zgłoszenie z wersji 0.2.2: tłumaczenie etykiety „Inspect/Zbadaj” w Escape Academy zostaje
po zniknięciu oryginału. Krok 1 rundy: powtarzalne scenariusze i pomiar stanu obecnego
(HEAD 3fa49aa), bez zmian kodu produktu. Nowe scenariusze SceneReplay `stale-junk`,
`stale-junk-ghost`, `stale-texture`, `stale-newtext`, `stale-busy` opisuje
[README SceneReplay](../tools/GameTranslatorOverlay.SceneReplay/README.md). Okno 1500×900
fizycznych pikseli, etykieta znika lokalnie (pole 5% okna, zmienione 2–3,6% komórek siatki,
zero globalnych `SceneCut`), Mock 1000 ms, obserwacja 12 s. Wariant „real” używa lokalnie
prawdziwego wycinka etykiety (glify 259×70 px) i tekstury z klatki 4K gry; zasoby nie trafiają
do repozytorium. Pole po zniknięciu etykiety nie jest jednolite (wariant real: zakres
kanałów 33–50), więc usunięcie z dowodem w pikselach nie ma zastosowania — to stan sprzed
sondy `KnownTextAbsenceProbe` (krok 2); wtedy jedynym dowodem w pikselach był test jednolitości.

**Pomiar** (callbacki sesji od WPF Rendering; 5 prób „real” + 1 syntetyczna; percentyle
nearest-rank):

| Scenariusz | OCR | Stary napis usunięty | Czas do usunięcia p50 / p90 / max | Powroty | Mock po zmianie |
|---|---|---:|---:|---:|---:|
| `stale-junk` | scripted | 0/5 (synt. 0/1) | > 12 s we wszystkich | 0 | 0 |
| `stale-junk-ghost` | scripted | 5/5 (synt. 1/1) | 875 / 882 / 882 ms, potem powrót | 5 (po 1 na próbę) | 0 |
| `stale-texture` | Windows | 5/5 (synt. 1/1) | 915 / 937 / 937 ms (synt. 896) | 0 | 0 |
| `stale-newtext` | Windows | 5/5 (synt. 1/1) | 6329 / 6355 / 6355 ms (synt. 1902) | 0 | 2 (synt. 1) |
| `stale-busy` | Windows | 0/5 (synt. 0/1, kontrolna 0/1) | > 12 s we wszystkich | 0 | 1 |

- `stale-junk`: śmieciowy odczyt w każdym przebiegu nad starym polem (wycinek po ok. 300 ms,
  potem pełny skan co ok. 4,1 s) daje `reusedBlocks=1` i stary blok z `Misses=0`.
- `stale-junk-ghost`: trzy puste odczyty usuwają blok po ok. 0,87 s; pierwszy śmieciowy odczyt
  przy pełnym skanie po ok. 4,95 s wskrzesza ducha, ponowne usunięcie ok. 9,57 s. Łączny czas
  widoczności po zmianie p50 5502 ms, max 5521 ms.
- `stale-texture`: Windows OCR zwrócił 0 linii we wszystkich przebiegach po zmianie (25/25).
  Usunięcie po trzecim pustym odczycie: wycinek ok. 0,31 s, dwie powtórki whiff ok. 0,6
  i 0,9 s.
- `stale-newtext`: nowy tekst gotowy p50 1345 / p90 1353 / max 1353 ms. Pierwszy dowód
  nieobecności starego bloku (`Misses=1`) trafia do callbacku dopiero razem z wynikiem Mock
  (OCR po ok. 0,32 s, callback po ok. 1,34 s). W wariancie real Windows OCR czyta nowy tekst
  raz poprawnie, raz z przekłamaną literą; potwierdzenie wariantu daje wycinki wokół nowego
  tekstu, więc trzecie pudło starego bloku czeka na pełny skan po ok. 6,3 s. W 5/5 próbach real
  nowy tekst znika na 1017–1032 ms (od ok. 2,2 s) przy podmianie wariantu odczytu.
- `stale-busy`: jeden pełny OCR po zmianie (z nowym tekstem, publikacja z wynikiem Mock po
  1,1–1,6 s), potem 15–17 wycinków samej animacji (kontrolna: 10). Najdłuższa przerwa bez
  pełnego OCR 10,5–10,9 s, czyli do końca obserwacji. Nowy tekst gotowy p50 1502 / p90 1532 /
  max 1532 ms.

Logi LiveDiag z gry (2026-09-12/13, 20 plików, `staticRescanIntervalMs` 4000): 282 przerwy
między pełnymi OCR, p50 4,13 s, p90 4,19 s, max 17,81 s; ponad 4,5 s: 21, ponad 8 s: 4,
ponad 15 s: 2 (17,81 s z 13 wycinkami w środku, 15,36 s z 10). `menu-none` (v0.2.2): po
jedynym pełnym OCR (0,78 s) 36 wycinków 256×256 bez linii i brak pełnego OCR przez kolejne
59,43 s. W tych trzech najdłuższych odcinkach odstęp między kolejnymi przebiegami nie
przekraczał 1,71–3,50 s, więc pełny skan co 4 s nie nadszedł; w dwóch krótszych (8,6 i 9,9 s)
pełny skan przyszedł dopiero 4,2 s po ostatnim wycinku.

**Hipotezy (do sprawdzenia w kolejnych krokach, nie wynik pomiaru):**

1. Śmieciowy odczyt nad teksturą trzyma stary blok bez końca (`LiveReadingStabilizer` → `Keep`,
   `Misses=0`, pominięty test pustego pola), a przerywany śmieć wskrzesza ducha. Logika jest
   odtworzona; to, czy Windows OCR w grze rzeczywiście czyta takie śmieci, nie zostało pokazane
   (na badanej teksturze zwracał 0 linii).
2. Usunięcie bez dowodu w pikselach czeka na wynik dostawcy nowego tekstu w tej samej klatce
   (ok. +1 s przy Mock 1000 ms).
3. Pełny skan liczony od dowolnego przetworzenia nie nadchodzi przy ciągłych wycinkach
   (animacja, potwierdzanie odczytów), a skan z oczekującym regionem jest wycinkiem; stary blok
   poza wycinkami nie dostaje kolejnych pudeł. Zgodne z przerwami w logach z gry.

Wyniki, skrypty i JSONL: `GTO Diagnostics\20261005-natywne-spolszczenie\krok1` (poza
repozytorium). Weryfikacja: build całego rozwiązania bez ostrzeżeń, 911 testów Core
i 242 Infrastructure zaliczone.

#### Krok 2 — poprawka i pomiar po (2026-10-06)

**Co trzymało napis w SceneReplay (z pomiaru bazowego, nie hipoteza).** Interpretacja, nie
osobny pomiar: wspólną przyczyną było to, że na teksturowanym tle pole po etykiecie nie jest
jednolite, więc sesja nie miała żadnego dowodu w pikselach i w najlepszym razie czekała na
trzy przebiegi OCR bez etykiety (ok. 0,9 s). Na to nakładały się trzy błędy logiki:

- (1) potwierdzone w SceneReplay: brudniejszy, niepowiązany odczyt (`lRrgIé@ue` nad „Inspect”)
  dawał `Keep` i `Misses=0`, więc blok nie znikał wcale (5/5), a ten sam odczyt nad duchem
  wskrzeszał usunięty napis (5/5 powrotów po ok. 4,95 s). Logika sesji jest potwierdzona przy
  skryptowym OCR; nie wykazano, że Windows OCR w grze daje taki odczyt (na badanej teksturze
  zwracał 0 linii w 25/25 przebiegów po zmianie);
- (3) potwierdzone: wycinki OCR zerowały zegar pełnego skanu, a blok spoza wycinka nie dostawał
  kolejnych pudeł — `stale-busy` bez usunięcia (5/5, przerwa bez pełnego OCR 10,5–10,9 s),
  `stale-newtext` ok. 6,3 s; ten sam wzorzec jest w logach z gry (do 59 s bez pełnego OCR);
- (2) częściowo: pierwsze pudło trafia do callbacku dopiero z wynikiem dostawcy, ale samo
  usunięcie bez dowodu (trzecie pudło) w żadnej próbie (120 po poprawce i 42 kontrolne na
  HEAD) nie wypadło w klatce z zapytaniem do dostawcy, więc ta ścieżka nie trzymała napisu.

**Zmiana w kodzie produktu.**

1. `KnownTextAbsenceProbe` (Core): dowód zniknięcia znanego tekstu także na teksturze. W całym
   polu bloku nie ma piksela, który zachował choćby ćwierć dawnego kontrastu luminancji między
   tekstem a tłem (próg 25% od tła w stronę tekstu; kolory znane, kontrast luminancji ≥ 48;
   tolerancja 0,1% pikseli; pole kompletne, bez przycinania). Zmiana barwy przy najechaniu
   (biały → żółty) i przygaszenie do ok. 30% nie są zniknięciem. Próg 50% dałby dowód w 74–77%
   pól 259×70 na trzech klatkach 4K z Escape Academy zamiast 31–46%, ale uznałby za zniknięty
   napis przygaszony do ok. 30% (pulsujące „Press any key” migałoby). Wybrano ostrożniej.
   Analiza: klatki `20260912-escape-menu\visual-01..03\frame-original.png` (3840×2160), pole
   259×70 przesuwane siatką co 8 px (117 376 pozycji na klatkę), kolory etykiety tekst FAFAFA /
   tło 0C0B0D, tolerancja 18 pikseli (1‰); dowód przy progu 25%: 31,2 / 45,7 / 35,7%, przy 50%:
   73,6 / 77,6 / 76,3%. Skrypt `krok1\skrypty\pozycje_pola_4k.py`, wynik (same liczby, bez
   pikseli) `krok1\pozycje-pola-4k-wynik.json`.
2. Sonda działa w trzech miejscach: (a) przy każdym przechwyceniu z istotną zmianą, która
   obejmuje wyświetlany blok (bez cięcia sceny i bez zapasowego zrzutu ekranu; blok o niezmienionym
   odcisku pola jest pomijany) — blok znika od razu i zostaje duchem, który wskrzesi tylko
   podobny odczyt; (b) po OCR dla bloku, którego OCR nie zobaczył (obok testu jednolitości);
   (c) w kontroli oczekujących pól podczas tłumaczenia. Gdy zniknięcie wykryje kontrola w trakcie
   OCR lub tłumaczenia klatki obejmującej ten blok, klatka jest porzucana i ponawiana (jak przy
   jednolitym przykryciu), żeby jej starszy odczyt nie przywrócił napisu.
3. Ścieżka (1): `LiveReadingStabilizer.IsUnrelatedDirtierReading` — odczyt o podobieństwie
   < 0,5, jakości gorszej o > 0,1 i niebędący fragmentem całych słów — nie zeruje braków bloku,
   chyba że odcisk pola w natywnej klatce jest niezmieniony; nie wskrzesza ducha (jest
   odrzucany: bez nowego bloku i bez zapytania). Podobny brudny odczyt („Lasi Played”) działa
   jak dotąd.
4. Ścieżka (3): `FullScanSchedule` liczy `StaticRescanInterval` od ostatniego **pełnego** OCR,
   a należny skan jest pełną klatką także przy oczekującym regionie. Wycinek zawsze obejmuje
   bloki z brakami (`LiveBlockSurvival.UnconfirmedRegion`), więc kolejne pudła nie czekają na
   pełny skan.
5. Ścieżka (2) — wcześniejsza publikacja usunięć bez dowodu — **nie zmieniona**. Pomiar
   wariantu bez dowodu (niżej): trzecie pudło zawsze wypadało w klatce bez zapytania do
   dostawcy, a czas wyznacza to, że pętla sesji nie robi kolejnego OCR, dopóki czeka na
   dostawcę. Wcześniejsza publikacja nic by tu nie dała, a osobna ścieżka usuwania bez dowodu
   zmieniałaby obsługę napisów dolnych (trybu subtitle).

Narzędzie: opcja `--bright-spot-px N` (jasna plamka w polu etykiety odbiera dowód w pikselach),
pole `afterKnownTextAbsent` w prawdzie referencyjnej, znacznik junk zmieniony z #FF00FF na
#900090 (jasny znacznik sam udawał resztkę tekstu: z nim `stale-junk` PO dawał 848–885 ms
zamiast ok. 35 ms). Baseline junk powtórzony na kodzie HEAD z nowym znacznikiem daje to samo
co w kroku 1 (junk 0/5 usuniętych, ghost 5/5 powrotów).

**Pomiar po** — ten sam wariant co baseline (prawdziwy wycinek etykiety, tekstura visual-01 od
1750,560, Mock 1000 ms, 5 prób, nearest-rank; czas od WPF Rendering zniknięcia do pierwszego
callbacku bez starego bloku):

| Scenariusz | Przed: p50 / p90 / max | Po: p50 / p90 / max | Powroty przed → po | Mock po zmianie przed → po |
|---|---:|---:|---:|---:|
| `stale-junk` | nie usunięty w 12 s (5/5) | 35 / 48 / 48 ms | 0 → 0 | 0 → 0 |
| `stale-junk-ghost` | 875 / 882 / 882 ms, potem powrót | 45 / 62 / 62 ms | 5 → 0 | 0 → 0 |
| `stale-texture` | 915 / 937 / 937 ms | 47 / 62 / 62 ms | 0 → 0 | 0 → 0 |
| `stale-newtext` | 6329 / 6355 / 6355 ms | 45 / 49 / 49 ms | 0 → 0 | 2 → 2 |
| `stale-busy` | nie usunięty w 12 s (5/5) | 47 / 49 / 49 ms | 0 → 0 | 1 → 1 |

Próby syntetyczne (1 na scenariusz): 47–60 ms. Nowy tekst bez zmian (`stale-newtext` p50/p90
1345/1353 → 1357/1367 ms, `stale-busy` 1502/1532 → 1519/1525 ms). Najdłuższa przerwa bez
pełnego OCR w `stale-busy` 10,5–10,9 s → 4,02–4,17 s. Czasy rzędu 0,05 s wynikają ze stałej
fazy fixture (zmiana zawsze 1500 ms po callbacku, więc zawsze w tym samym miejscu zegara
przechwytywania 6 fps); rozkład po przesunięciu fazy i zmiana tego zachowania — krok 1b.

**Wariant bez dowodu w pikselach** (`--bright-spot-px 12`, to samo HEAD vs po poprawce, 5 prób):

| Scenariusz | HEAD: p50 / p90 / max | Po: p50 / p90 / max | Powroty HEAD → po |
|---|---:|---:|---:|
| `stale-junk` | nie usunięty w 12 s (5/5) | 862 / 869 / 869 ms | 0 → 0 |
| `stale-junk-ghost` | 860 / 876 / 876 ms, potem powrót | 855 / 888 / 888 ms | 5 → 0 |
| `stale-texture` | 916 / 937 / 937 ms | 916 / 931 / 931 ms | 0 → 0 |
| `stale-newtext` | 6074 / 6096 / 6096 ms | 1832 / 1868 / 1868 ms | 0 → 0 |
| `stale-busy` | nie usunięty w 12 s (5/5) | 2956 / 3151 / 3151 ms | 0 → 0 |

Tu cel ≤ 300 ms jest nieosiągalny bez zmiany okresu łaski: bez dowodu w pikselach blok znika
po trzech przebiegach OCR, który go nie widzi (ochrona przed czknięciami Windows OCR), a
w `stale-busy` przebiegi wyznacza wymuszony cykl 600 ms i oczekiwanie na dostawcę. Zapytania
Mock bez zmian.

**Regresje** — wszystkie istniejące scenariusze SceneReplay (displayed i inflight w obu
trybach OCR, noisy, aba, churn, stop, local-reading, reading-jitter, reading-whiff, ocr-timing,
local-occlusion ×3, moving-text, position-jitter, hud-motion ×3), po 3 próby na kodzie HEAD i po
poprawce: wszystkie `expectedBehavior`, `desired*` i `fixtureValid` takie same albo lepsze
(moving-text: na HEAD 1 nieważna próba z 3, po poprawce 3/3 — to niestabilność fixture, nie
poprawa), liczby zapytań i znaków Mock
identyczne. `local-occlusion` usuwa Inspect po 16 ms zamiast 295 ms. Drobne różnice:
local-reading i local-occlusion-hover mają o jeden callback z tekstem więcej (częstszy pełny
skan), czasy usunięcia w hud-motion mieszczą się w rozrzucie (65–172 ms, limit 700 ms).

**Ograniczenia i ryzyka.** To callbacki sesji, nie fizyczna nakładka; gry nie uruchamiano.
Dowód w pikselach działa tylko przy przechwytywaniu okna (nie przy zapasowym zrzucie ekranu)
i tylko gdy pod napisem nie zostaje nic jasnego jak on (na klatkach z gry: 31–46% pozycji).
Tekst, który pulsuje poniżej ćwierci kontrastu albo przy najechaniu zmienia kolor na równie
ciemny jak tło, zniknie z nakładki i wróci przy następnym OCR (bez zapytania do dostawcy) —
w recenzji okazało się, że miga przy każdej istotnej zmianie w polu (M1, zmienione w kroku 1b).
Niezależnie od poprawki w `stale-newtext` (wariant real) nowy tekst nadal znika na ok. 1,02 s
w 5/5 prób przed i po: Windows OCR czyta go raz z przekłamaną literą, potwierdzona podmiana
wariantu zdejmuje blok przed tłumaczeniem i wysyła nowe zapytanie — osobny temat (usunięte
w kroku 1b).
Weryfikacja: build bez ostrzeżeń, Core 947/947 (36 nowych testów), Infrastructure 242/242.

#### Krok 1b — poprawki po recenzji (2026-10-06)

Dwóch recenzentów kroku 2 dało werdykt ok-with-fixes: M1–M3 (medium), L1–L2 (low) i uwagi do
dokumentów. Dla każdego znaleziska najpierw scenariusz odtwarzający na HEAD c99947a, potem
poprawka, potem pomiar. „Przed” = kod produktu c99947a z nowym narzędziem SceneReplay
(osobny build `krok1\after2\bin-before` ze źródeł `git archive c99947a`), „po” = poprawki
(`after2\bin-after`). Wszędzie prawdziwy wycinek etykiety i tekstura visual-01 od 1750,560,
Mock 1000 ms, obserwacja 12 s, percentyle nearest-rank, czas od WPF Rendering zmiany do
pierwszego callbacku. Próby przed/po przeplatane w każdej turze, z jednym wyjątkiem: nowe
scenariusze „po” (5 × 7 wariantów) poszły po wszystkich „przed”, bo pierwsza wersja poprawki
została odrzucona w trakcie serii (niżej, M3).

**Odtworzenie na HEAD (pomiar).**

- M1: `stale-dim --dim-percent 25` — 12–16 zniknięć tłumaczenia na próbę (5/5), każde wracało
  po 0,34–2,4 s. Prawda referencyjna potwierdza mechanizm: sonda z kolorami pierwszego odczytu
  (F9F9F9 / 010102) mówi „nieobecny” (`afterKnownTextAbsent=true`, najjaśniejszy piksel pola 63
  przy progu ok. 71), a OCR czyta przygaszoną etykietę i duch wraca ze starymi kolorami.
  `stale-dim` z domyślnymi 40% (najjaśniejszy piksel 102) i `stale-fade` (300 ms) na HEAD
  przechodzą 5/5 — tych dwóch M1 nie odtwarza. W jednym próbnym przebiegu `stale-dim` 40% na
  HEAD (poza serią) tłumaczenie zniknęło raz na ok. 1 s: potwierdzona podmiana wariantu odczytu
  zdejmowała stary blok przed tłumaczeniem — ten sam mechanizm co luka ok. 1,02 s nowego tekstu
  w `stale-newtext` real z kroku 2.
- M2: `stale-blink` — po powrocie etykiety blok wraca po 78–434 ms, pasek napisów nie wraca
  wcale (0/5).
- M3: `stale-newdirty` — „Loading…” w miejscu zniknętej etykiety nie pojawia się przez 10,5 s
  od pokazania (0/5); każdy z 3–4 odczytów nad duchem był odrzucany.
- L1: `stale-present-junk --junk-run 3` — 5 zniknięć i powrotów na próbę (5/5), pierwsze po
  1,48–1,59 s, każde na 0,26–0,78 s. Wariant domyślny (śmieć co drugi przebieg) na HEAD przechodzi
  5/5: poprawny odczyt między śmieciami zeruje braki, więc „co drugi przebieg” L1 nie odtwarza.
  Pierwsza wersja podkładu (dwa stałe odcienie) też nie odtwarzała: co któreś przechwycenie
  powtarzało piksele odcisku i zerowało braki; podkład ma teraz poziomy z okresem 2,8 s.
- L2: w żadnym istniejącym scenariuszu bloki z brakami nie leżą daleko od siebie; liczba pełnych
  OCR (niżej) nie pokazuje problemu ani zmiany.

**Zmiana w kodzie produktu.**

1. M1 — `KnownTextReference` w `LiveOverlayBlock.Probe`: kolory dla sond i liczba pikseli
   „rdzenia” glifów (po stronie tekstu połowy kontrastu) z ostatniego odczytu OCR, który
   potwierdził blok (ten sam klucz, podobny odczyt, wskrzeszony duch), trzymane osobno od kolorów
   rysowania z histerezą. Dowód nieobecności na teksturze w chwili przechwycenia nie zdejmuje już
   bloku: blok staje się podejrzany, jego pole wchodzi do najbliższego OCR, a trwający przebieg,
   który go obejmuje, jest porzucany i ponawiany. Blok znika dopiero, gdy OCR go nie widzi, a sonda
   z aktualnym wzorcem mówi „nieobecny”. Jednolite przykrycie (`TextPresenceProbe`) nadal zdejmuje
   blok od razu. Koszt: usunięcie z dowodem w pikselach przychodzi z najbliższym OCR, a nie
   z najbliższą klatką (pomiar niżej). Wymóg dwóch potwierdzeń sondy w osobnych przechwyceniach
   odrzucono: przy przygaszeniu bez dalszych zmian drugie przechwycenie przychodzi przed OCR
   (ok. 167 ms wobec ok. 300 ms) i też zdjęłoby napis.
   Przy okazji: potwierdzona podmiana **wariantu** odczytu (podobieństwo ≥ 0,5) nie zdejmuje
   starego bloku przed tłumaczeniem — zamiana następuje w aktualizacji z nowym tłumaczeniem.
   Zmiana treści (podobieństwo < 0,5) nadal zdejmuje stary blok od razu.
2. M2 — duch pamięta, czy jego tekst był na pasku i zniknął z niego razem z blokiem; wskrzeszenie
   go przywraca (`LiveSubtitleContent.Restore`). Pusty pasek pokazuje się od nowa (pełny czas
   paska), pasek z innymi liniami dostaje tekst bez odnawiania czasu — zgodnie z M21 i punktem 4
   regresji M20/M21/M22 („usunięcie części treści nie odnawia czasu”). Duchy po okresie łaski nie
   usuwały treści paska, więc niczego nie przywracają.
3. M3 — nad duchem odrzucany jest tylko odczyt niewiarygodny (`IsImplausibleReading`:
   niepowiązany i brudniejszy, a do tego `ReadingQuality` < 0,75 albo litery < 50% znaków
   niebiałych; śmieć fixture ma 0,667, „Loading…” 0,875, „Lv5 Key” 0,857, „Price: €5” 0,889).
   Wiarygodny niepowiązany odczyt nad duchem usuniętym **z dowodem w pikselach** to nowy napis:
   duch wygasa, nowy blok dziedziczy styl. Nad duchem po samych brakach OCR (napis mógł zostać)
   brudniejszy niepowiązany odczyt jest dalej odrzucany. Nad wyświetlanym blokiem
   `LiveReadingStabilizer` przyjmuje wiarygodny niepowiązany odczyt gorszej jakości po dwóch
   kolejnych identycznych odczytach (wcześniej nigdy); różne przekłamania się nie sumują.
   Pierwsza wersja (wiarygodny odczyt nad blokiem, który i tak wypadłby w tym przebiegu, od razu
   zastępował napis) została odrzucona w trakcie serii: w 1 z 5 prób `stale-dim` 25% przekłamany
   odczyt przygaszonej, wciąż widocznej etykiety zastąpił ją po 10,5 s nowym tłumaczeniem
   (+1 zapytanie Mock). Wyniki tej wersji: `after2\odrzucone-po-v1`.
4. L1 — niewiarygodny niepowiązany odczyt nad blokiem nie jest pudłem i nie zeruje braków, gdy
   przechwycenie pokazuje w polu tyle pikseli rdzenia co przy ostatnim potwierdzeniu (0,5–2×).
   Liczone na natywnej bitmapie przed skalowaniem, więc także dla wycinków powiększanych ×2,
   dla których nie powstaje odcisk pola. Bez tego dowodu (jasna plamka po zniknięciu: 144 px
   wobec ok. 4 tys. pikseli rdzenia) odczyt liczy się jak pudło, jak w kroku 2.
5. L2 — `LiveBlockSurvival.PartialOcrSeed`: bloki z brakami dołączają do wycinka tylko wtedy,
   gdy unia z regionem zmian (z zapasem 24 px) nie przekracza połowy klatki; inaczej wycinek
   obejmuje sam region zmian, a pełny skan zostaje na zegarze `FullScanSchedule`.

**Nowe scenariusze, przed → po** (5 prób; „zniknięcia” = przejścia widoczny → niewidoczny):

| Scenariusz | Wynik przed | Wynik po | Przed: szczegóły | Po: szczegóły |
|---|---:|---:|---|---|
| `stale-fade` (300 ms) | 5/5 | 5/5 | usunięty 372–394 ms od startu wygaszania (91–108 ms po końcu) | usunięty 643–671 ms (356–387 ms po końcu) |
| `stale-dim` 40% | 5/5 | 5/5 | 0 zniknięć | 0 zniknięć |
| `stale-dim` 25% | 0/5 | 2/5 | 12–16 zniknięć na próbę | 0, 1, 1, 0, 1 zniknięcie, każde ok. 0,61 s |
| `stale-present-junk` (co drugi) | 5/5 | 5/5 | 0 zniknięć | 0 zniknięć |
| `stale-present-junk --junk-run 3` | 0/5 | 5/5 | 5 zniknięć na próbę (0,26–0,78 s) | 0 zniknięć |
| `stale-newdirty` | 0/5 | 5/5 | nowy napis nie pokazany w 10,5 s | nowy napis 1059–1477 ms po pokazaniu |
| `stale-blink` | 0/5 | 5/5 | pasek nie wraca | blok i pasek wracają razem 71–324 ms po powrocie etykiety |

Zapytania Mock po zmianie: wszędzie 0 przed i po, poza `stale-newdirty` po (1 — tłumaczenie
nowego napisu). Stary napis w `stale-newdirty` i `stale-blink` znika po 328–349 ms (przed
44–72 ms). Jedyne pozostałe mignięcia w `stale-dim` 25%: pierwszy OCR po przygaszeniu nie
zwrócił linii, a wzorzec sondy był jeszcze sprzed przygaszenia; następny OCR przeczytał etykietę
i tłumaczenie wróciło (blok i pasek).

**Dotychczasowe `stale-*`, przed → po** (3 próby; p50 / max, ms; powroty 0 i Mock bez zmian
we wszystkich 60 próbach, `expectedBehavior` 30/30 przed i 30/30 po):

| Scenariusz | Z dowodem w pikselach: przed | po | Bez dowodu (`--bright-spot-px 12`): przed | po |
|---|---:|---:|---:|---:|
| `stale-junk` | 63 / 65 | 328 / 345 | 890 / 891 | 894 / 912 |
| `stale-junk-ghost` | 58 / 59 | 328 / 341 | 863 / 898 | 871 / 891 |
| `stale-texture` | 55 / 66 | 343 / 344 | 930 / 940 | 941 / 950 |
| `stale-newtext` | 49 / 60 | 354 / 365 | 1713 / 1855 | 1723 / 1870 |
| `stale-busy` | 65 / 108 | 535 / 552 | 3105 / 3223 | 3044 / 3208 |

Nowy tekst w `stale-newtext` (real) znikał przed na 1028–1042 ms w 3/3 próbach (podmiana wariantu
odczytu), po — w 0/3; czas pojawienia bez zmian (1374–1395 → 1378–1400 ms). Usunięcie z dowodem
w pikselach jest wolniejsze o ok. 0,3 s — to świadomy koszt poprawki M1: decyzja wymaga OCR.

**Faza** (`--phase-ms` 0/40/80/120/160, po 3 próby, 15 na scenariusz): czasy ok. 0,05 s z kroku 2
wynikały ze stałej fazy fixture. Przed (c99947a): `stale-junk` 12–158 ms (p50 68, p90 143),
`stale-texture` 14–160 ms (p50 66, p90 156) — czyli w ciągu jednej klatki przechwycenia (do ok.
0,17 s przy 6 fps). Po: `stale-junk` 295–460 ms (p50 367, p90 457), `stale-texture` 294–443 ms
(p50 346, p90 421) — najbliższy OCR po zmianie. `expectedBehavior` 60/60.

**Regresje** — 20 starszych scenariuszy (displayed i inflight w obu trybach OCR, noisy, aba,
churn, stop, local-reading, reading-jitter, reading-whiff, ocr-timing, local-occlusion ×3,
moving-text, position-jitter, hud-motion ×3), po 3 próby przed i po, przeplatane: wszystkie
`expectedBehavior`, `desired*`, `fixtureValid` i flagi poprawności takie same, liczby zapytań
i znaków Mock identyczne, porównanie automatyczne bez regresji (`after2\porownanie-regresji.txt`).
`local-occlusion` nadal 15–18 ms (jednolite przykrycie zdejmuje od razu). L2: pełne OCR bez
zmian — `stale-busy` 3/3/3 → 3/3/3, `hud-motion` 2 → 2, `hud-motion-whiff` 3 → 3,
`hud-motion-small-whiff` 6 → 6; poprawka L2 jest zapobiegawcza, żaden scenariusz nie układa
bloków z brakami daleko od siebie.

**Ograniczenia i ryzyka.** Callbacki sesji, nie fizyczna nakładka; gry nie uruchamiano;
`stale-junk*`, `stale-present-junk` i `stale-newdirty` mają skryptowy OCR. Podkład w `stale-dim`
i `stale-present-junk` jest płaski i zasłania teksturę w polu etykiety. Usunięcie z dowodem
w pikselach trwa teraz 0,29–0,55 s zamiast do ok. 0,16 s. Napis przygaszony poniżej ćwierci
kontrastu mignie raz (ok. 0,6 s), jeśli pierwszy OCR po przygaszeniu go nie zobaczy — usunięcie
tego wymagałoby drugiego OCR przed każdym usunięciem (ok. +0,3 s). Kierunek M3 ma swój koszt:
wiarygodne, ale błędne przekłamanie OCR nad duchem z dowodem w pikselach staje się nowym blokiem
i zapytaniem do dostawcy. Dowód obecności L1 to liczba pikseli rdzenia w paśmie 0,5–2× — jasna
tekstura o podobnej liczbie jasnych pikseli mogłaby przy samych śmieciach OCR podtrzymać napis,
którego już nie ma. Nowy napis z brudniejszym odczytem w miejscu zniknięcia bez dowodu w pikselach
nadal czeka do 10 s. Weryfikacja: build bez ostrzeżeń, Core 982/982 (+35 testów), Infrastructure
242/242. Analiza pozycji pola: `krok1\skrypty\pozycje_pola_4k.py`, wynik
`krok1\pozycje-pola-4k-wynik.json`; JSONL i agregaty: `krok1\after2`.

### Runda 2026-10-06 — korpus gry

Krok 2 decyzji ADR-014: narzędzie offline czytające teksty z plików gry, przyciąganie odczytów
OCR do korpusu i eksperyment precyzji. Integracja z pipeline'em tłumaczeń (klucz cache po
kanonicznym tekście, wpisy z wyprzedzeniem, tryb prywatny) **nie jest częścią tej rundy**.

**Co powstało.** `tools/GameTranslatorOverlay.CorpusTool` (`extract`; czytnik UnityFS z własnym
dekoderem LZ4 → plik serializowany Unity → TextAsset, parsery CSV i SRT, zabezpieczenia
ADR-014 jako kod z testami — [README](../tools/GameTranslatorOverlay.CorpusTool/README.md)),
`GameTranslatorOverlay.Core.Corpus` (`CorpusEntry`, `CorpusIndex` — trigramy po tekście bez
wielkości liter, `CorpusSnapper`), opcjonalne sekcje `corpus` i `online` w `GameProfile`,
profil `profiles/escape-academy` z receptą oraz narzędzie dev `tools/GameTranslatorOverlay.CorpusEval`
(prawda syntetyczna przez Windows OCR, przegląd progów, czasy).

**Korpus Escape Academy (pomiar).** `data.unity3d` (UnityFS v8, Unity 2020.3.40f1) →
`resources.assets` (plik serializowany v22, 12 422 obiekty, 5 456 TextAssetów). Wpisy: 8 341
(UI 6 358 z 4 tabel GameplayStrings, dialogi Yarn en-US 482 z 55 tabel, napisy 1 501 z 1 316
plików SRT); unikalne teksty bez wielkości liter: 7 601 (UI 5 975, dialogi 467, napisy 1 168),
248 791 znaków klucza dopasowania; mówcy: 19 w dialogach, 42 w napisach. Pominięte: 11 wierszy UI
bez liter i cyfr, 8 napisów z samymi nutami. Jedna tabela nie jest czystym UTF-8 (2 bajty
odczytane jak Windows-1252). Czas (3 przebiegi): odczyt 62–92 ms, parsowanie 45–47 ms, razem
z zabezpieczeniami 178–219 ms (prototyp w Pythonie: 1,4 s). Korpus leży poza repozytorium.

**Wybrane progi (domyślne w `CorpusSnapOptions`).** Przybliżone i fragmenty: podobieństwo
Levenshteina ≥ 0,80, odstęp do drugiego kandydata ≥ 0,08, co najmniej 16 znaków i 2 słowa
(fragment 3 słowa i wpis ≥ 1,3× dłuższy); liczby samodzielne (niesklejone z literą) 1:1;
krótsze teksty tylko dokładnie, bez wielkości liter i znaków na brzegach, od 2 liter lub cyfr;
krótka etykieta nie jest przyciągana w środku zawiniętego zdania; okruchy przy etykiecie tylko
jednoznakowe lub nazwy klawiszy na początku i bez liter na końcu. Wariant ścisły: to samo
i dokładne dopiero od 8 znaków (`MinExactLength = 8`). Progi wybrano z 448 konfiguracji
(T 0,75–0,95 × margines 0,03–0,12 × min. długość 8–16 × min. długość dokładnego 0–12).

**Prawda syntetyczna.** 880 próbek: 2 × 440 (cała klatka 4K jak pełny skan live i wycinek
co najmniej 800×400 jak OCR regionu), warstwy: krótkie etykiety UI 158, dłuższe UI 246,
dialogi 238, napisy 238; biały tekst z czarnym obrysem 32–72 px, 9 czcionek systemowych, trzy
klatki z gry z ominięciem miejsc z tekstem gry; prawdziwy Windows OCR i `TextBlockGrouper` jak
w live.

| | Wybrane progi | Wariant ścisły |
|---|---:|---:|
| Surowy OCR identyczny z prawdą (z wielkością liter) | 417 (47,4%) | 417 |
| Poprawnie przyciągnięte | 697 (79,2%) | 623 (70,8%) |
| Błędnie przyciągnięte (z przyciągniętych) | 1 (0,14%) | 0 (0%) |
| Tylko fragment poprawnego wpisu | 10 | 10 |
| Bez dopasowania | 172 | 247 |

Jedyny błąd to odczyt samego „Only” z etykiety z ceną (OCR zgubił liczbę) — przyciągnięcie
nie zmieniło tekstu. Z 172 próbek bez dopasowania w 109 OCR nie zwrócił żadnego tekstu (cała
klatka 91/440, wycinek 18/440).

**Prawdziwy cache** (kopia bazy z `-wal`, 518 wpisów DeepL; dni EA 2026-09-04/12/13/15: 242 bloki,
3 437 liter i cyfr; znaki liczone bez spacji i interpunkcji):

| | Dziś (klucz z wielkością liter) | Wybrane progi | Wariant ścisły |
|---|---:|---:|---:|
| Znaki obsłużone lokalnie | 583 (17,0%) | 2 043 (59,4%) | 1 644 (47,8%) |
| — dokładne / przybliżone | | 1 364 / 679 | 925 / 719 |
| — w tym części wierszy (etykieta obok glifu, opis + etykieta) | | 171 | 0 |
| + fragmenty (bez złożenia nieobsługiwane) | | 67,7% | 56,1% |
| Bloki w pełni obsłużone lokalnie | 48 (19,8%) | 118 (48,8%) | 62 (25,6%) |
| Bez dnia 09-04 (217 bloków): znaki / bloki | | 63,2% / 51,2% | 51,1% / 27,2% |

Dzień 09-04 osobno (25 bloków): 18,0% znaków. Ręczny przegląd przyciągnięć przybliżonych:
w całym cache jest ich tylko 39 różnych nawet przy najluźniejszych progach, więc przejrzano
wszystkie (zamiast 50) — przy wybranych progach 0 błędnych z 24 (5 uzupełnia ucięty odczyt,
2 odczyty z etykietą mówcy, 4 wpisy ze znacznikiem przycisku `[x]`), przy luźnych (T 0,70,
min. 8 znaków) 2 błędne z 15 dodatkowych (dwie krótkie etykiety w jednym wierszu).

**Kontrola negatywna.** Dzień 2026-08-06 nie jest czystym PoE2: po 12:24 UTC w cache jest 12
bloków samouczka Escape Academy (wcześniejsze 7,7% fałszywych trafień liczono razem z nimi).
Kontrola = sesja PoE2 09:06–09:58 UTC, 247 bloków, 7 139 liter i cyfr. Wybrane progi: 8 bloków
z trafieniem (3,24%) — wszystkie dokładne, identyczne ogólne etykiety menu — i 0 przyciągnięć
zmieniających odczytany tekst. Wariant ścisły: 2 bloki (0,81%), 0 zmian.

**Czas dopasowania** (`SnapBlock`, 32 wątki CPU, .NET 10.0.12): korpus EA (7 601 tekstów,
indeks 33 ms) na 1 278 zapytaniach (bloki cache EA i PoE2 + odczyty syntetyczne) — p50 0,004 ms,
p95 0,90 ms, p99 1,95 ms, max 5,1 ms. Korpus syntetyczny 150 tys. tekstów (141 005 unikalnych,
indeks 430 ms, ok. 75 MB) — p50 1,01 ms, p95 4,77 ms, p99 6,74 ms na 2 000 zapytaniach;
zapytania EA na tym korpusie p95 4,89 ms.

**Kryteria rundy.** ≥40% znaków lokalnie — spełnione (59,4%; ścisły 47,8%). ≤1% błędnych
przyciągnięć syntetycznych — spełnione (0,14%; ścisły 0%). ≤1% fałszywych trafień PoE2 —
wariant ścisły spełnia (0,81%); wybrane progi tylko przy liczeniu przyciągnięć zmieniających
tekst (0%), a przy liczeniu każdego trafienia nie (3,24%, identyczne etykiety). Wariant ścisły
spełnia więc wszystkie trzy, ale obsługuje o połowę mniej bloków w całości. Po zawężeniu korpusu
do aktywnego profilu gry trafienia w identyczne etykiety innej gry nie mogą wystąpić.

**Ograniczenia i hipotezy.** Czcionki systemowe zamiast czcionki gry, bez animacji pisania
i bez ruchu; progi wybrane na tych samych danych, na których je mierzono (ryzyko dopasowania do
próby); cache zawiera tylko bloki wysłane do dostawcy, nie wszystkie odczyty; jedna gra
w kontroli negatywnej. Tekst z tekstur i lokalizowanych obrazków nie trafi do korpusu.
Wpisy ze znacznikiem przycisku (`[x]`) wymagają obsługi przy wyświetlaniu.

Liczby: `GTO Diagnostics\20261005-natywne-spolszczenie\krok2\eksperyment` (`sweep.json`,
`wybrane-progi.json`, `wyniki-progi.md`, `czasy-dopasowania.json`, `extract-stats.json`,
`przeglad-reczny.json`); teksty gry tylko w `krok2\private` (lokalnie). Weryfikacja: build
całego rozwiązania bez ostrzeżeń, Core 1052/1052 (+70 testów), Infrastructure 242/242,
CorpusTool 85/85 (nowy projekt testów).

### Runda 2026-10-06 (2) — opcje serwera LLM i tłumaczenie korpusu z wyprzedzeniem

**Opcje serwera LLM** (ADR-013, dopisek). `OpenAiCompatibleTranslationProvider` wysyła
opcjonalne `thinking`, `reasoning_effort`, `max_tokens` i `response_format` z ustawienia
`llmServerOptions` przypisanego do hosta; bez niego zapytanie jest bajt w bajt takie jak dotąd
(test kontraktu). Gotowe serwery: DeepSeek (`deepseek-flash`, `thinking: disabled`) i Ollama
(`reasoning_effort: none`). Odpowiedź: `usage` do logu i do sum tokenów. ProviderEval:
`--llm-thinking`, `--llm-effort`, `--llm-max-tokens`, `--llm-json`, `--llm-no-preset`.

**`CorpusTool translate`.** Partie: dialogi węzłami w kolejności linii (dłuższy węzeł dostaje
poprzednie linie z tłumaczeniami), napisy po pliku, UI w obrębie tabeli; opis sceny i notatka do
każdego tekstu (mówca, plik napisu, klucz, kolumna kontekstu bez flag lokalizacji zaczynających się od `%`). Kontrola jakości
jak w pipeline plus zachowanie znaczników `{0}`, `[X]`, `%s`; partia z nieczytelną odpowiedzią,
odmową filtra albo zbyt długim tekstem jest dzielona na połowy (do 3 poziomów). Zapis: profil gry, prawdziwa nazwa
dostawcy, `context` = `reflow-1[;qa…][;pg…];src=corpus`. Pomijane: słownik, korekty, wpisy
zatwierdzone, aktualne wpisy profilu. Tryb prywatny i nieczytelne ustawienia = odmowa; baza
poza repozytorium; Mock tylko do jawnie wskazanej bazy; klucze tylko ze zmiennych środowiskowych.

**Pomiar na Mocku** (2026-10-06, korpus Escape Academy, kopia `cache.db` użytkownika w
`krok2\private\cache-mock` z dodanymi: korektą globalną, korektą profilu, wpisem zatwierdzonym
i terminem prywatnego słownika). Korpus: 8 341 wpisów → 7 639 unikalnych kluczy (z wielkością
liter; 702 powtórzenia). Pominięte: słownik 8, korekty 2, zatwierdzony 1; do tłumaczenia 7 628
tekstów, 248 781 znaków, 199 partii (dialogi 466, napisy 1 165, UI 5 997); 51 przesłania wpis
globalny DeepL, 18 ma znaczniki, 763 zwraca się do gracza. Przebieg: 7 628 zapisanych, 0 błędów,
322 ms (proces 0,64 s). Pipeline z aktywnym profilem `escape-academy` i Mockiem: lokalnie
7 639/7 639 (słownik 8, cache 7 631: 7 628 wpisów korpusu + 3 wpisy chronione); korekty,
wpis zatwierdzony i termin słownika wygrywają; bez profilu wynik jak przed przebiegiem (60
z 7 639); z prawdziwym dostawcą atrapy Mocka nie są pokazywane. Drugi przebieg: „już w profilu
7 628”, nic do tłumaczenia. Nowa pusta baza (3 przebiegi): 7 632 zapisane, 324–325 ms
(proces 0,56 s), plik 3,9 MB. Przebieg próbny na kopii nie zmienił plików bazy (SHA-256
`cache.db`, `-wal`, `-shm` przed i po).

**Szacunek przed prawdziwym przebiegiem** (pełny korpus, kopia bez dodatków): DeepSeek
`deepseek-flash` bez myślenia — 343 zapytania, ok. 333 tys. tokenów wejścia i 114 tys. wyjścia,
0,12–0,24 USD (poza szczytem – w szczycie, bez trafień cache; hipoteza ±30%); DeepL — 200 zapytań,
248 979 znaków = 49,8% miesięcznego limitu API Free. Prawdziwego przebiegu w tej rundzie nie
było (zero zapytań do płatnych API).

Liczby: `krok2\tlumaczenie\*.json` (same liczby); teksty i bazy tylko w `krok2\private`.
Weryfikacja: build całego rozwiązania i App bez ostrzeżeń, Core 1067/1067, Infrastructure
270/270, CorpusTool 147/147; pokrycie linii (scalone): Core 97,3%, Infrastructure 93,4%,
CorpusTool 94,0%.

### Runda 2026-10-06 (3) — dopasowanie do korpusu w trybie live

**Co działa.** Aplikacja wczytuje korpus aktywnego profilu jako dane
(`<folder danych>\corpus\<id>.corpus.jsonl`, `CorpusCatalog`; nigdy pliki gry) i przekazuje
`CorpusSnapper` (progi z rundy 2026-10-06: T 0,80, margines 0,08, min. 16 znaków) do pipeline'u.
`TranslationUnitPlanner` dzieli znormalizowany blok OCR na jednostki: tekst kanoniczny korpusu
(cały blok, akapit, wiersz albo część wiersza; klucz cache = `CorpusTranslationKey`, ten sam co
w `CorpusTool translate`), akapity nieznanej reszty (klucz = wiersze akapitu z OCR) i tekst
dosłowny (prefiks mówcy z korpusu, okruchy typu klawisz, śmieciowy akapit obok czegoś do
tłumaczenia). Słownik, cache, deduplikacja in-flight, kontrola jakości, pamięć dialogu i dostawca
pracują na jednostkach; wynik bloku jest składany z powrotem: tekst kanoniczny rozkładany na
wiersze odczytu (`TextReflow.ToParagraphs` + `Rewrap`/`WrapBalanced`), wielkie litery odczytu
przechodzą na tłumaczenie. Kolejność: ręczna korekta całego odczytu > słownik na całym odczycie >
jednostki (każda: korekta > słownik > cache) > dawny automatyczny wpis całego odczytu (zapas, gdy
jednostki nie są lokalne) > dostawca dla brakujących jednostek. Próba lokalna (`TranslateLocalAsync`)
oddaje wyniki jednostek do `TranslateAsync`, więc znana jednostka nie jest czytana z bazy drugi raz.
Przyciąganie nie używa tekstów z podstawieniem (`{…}`, `%s/%d/%i/%f`) ani ze znacznikiem przycisku
`[X]`, którego nie ma w odczycie. Krótki tekst, który odrzuca `JunkFilter`, przechodzi tylko przy
dokładnym dopasowaniu do korpusu (min. 2 litery). Ręczna korekta bloku będącego jednym tekstem
korpusu zapisuje się pod kluczem kanonicznym (`TranslationOutcome.CacheKey`; od kroku 2b tylko
przy dopasowaniu dokładnym). Tryb prywatny
i Cache-only: przyciąganie w pamięci, reszta jak dotąd (w trybie prywatnym baza z dysku nie jest
czytana, więc wpisy z wyprzedzeniem nie działają). Bez korpusu i bez `paragraphCacheKeys` plan
jest pusty i pipeline idzie dokładnie dawną ścieżką kodu.

**Klucz po akapitach (pytanie z badania: 82 linie płacone wielokrotnie).** Pomiar na kopii cache:
w dniach EA 22 linie występowały w więcej niż jednym bloku (34 dodatkowe wystąpienia, 560 znaków),
w sesji PoE2 61 linii (145 wystąpień, 2 231 znaków). Naprawa jest tania i jest zrobiona: przy
aktywnym korpusie nieznana reszta bloku idzie akapitami (klucz = wiersze akapitu, więc akapit
samodzielny i ten sam akapit w większym bloku mają jeden wpis), a dawny wpis całego bloku jest
zapasem — istniejące tłumaczenia nie są płacone drugi raz. Bez korpusu to samo włącza
`"paragraphCacheKeys": true` w `settings.json`; **domyślnie wyłączone**, bo (1) wymaganie tej
rundy: brak korpusu = zachowanie jak dziś, (2) zmienia to, co widzi dostawca (akapity jednego
bloku jako osobne teksty tej samej partii — jakości u DeepL i modeli nie mierzono, Mock niczego tu
nie mówi), (3) liczniki trafień cache liczą części, nie bloki. Klucza po pojedynczych wierszach
akapitu nie wprowadzono: wiersz zawiniętego zdania to fragment zdania, a jego tłumaczenie osobno
psuje gramatykę (powód istnienia `TextReflow`). Rekomendacja: włączyć domyślnie po pilotażu
u prawdziwego dostawcy na PoE2 (zysk tam największy).

**Pomiar — powtórka bloków z kopii cache** (2026-10-06, `CorpusEval replay`, prawdziwy
`TranslationPipeline` + `SqliteTranslationCache` na świeżych bazach, Mock z licznikiem, słownik
globalny; każdy blok raz w kolejności `created_at`, ścieżką live: `TranslateLocalAsync`, potem
`TranslateAsync` z wynikiem próby). Korpus 8 341 wpisów / 7 599 tekstów. Baza B wypełniona przez
`CorpusTool translate --provider mock` (7 632 wpisy w profilu `escape-academy`).

| Bloki | Wariant | Bloki lokalnie | Znaki lokalnie | Zapytania | Teksty / znaki do dostawcy |
|---|---|---|---|---|---|
| EA 242 (09-04/12/13/15) | A — jak dotąd, bez korpusu, pusty cache | 1 (0,4%) | 10 / 3 437 (0,3%) | 241 | 241 / 4 214 |
| EA 242 | A2 — bez korpusu, klucze po akapitach | 7 (2,9%) | 157 (4,6%) | 235 | 245 / 4 019 |
| EA 242 | B0 — korpus, pusty cache | 43 (17,8%) | 755 (22,0%) | 199 | 206 / 3 313 |
| EA 242 | **B — korpus, cache z `translate`** | **120 (49,6%)** | **2 075 (60,4%)** | **122** | **128 / 1 678** |
| EA 217 (09-12/13/15) | A | 1 (0,5%) | 10 / 3 154 (0,3%) | 216 | 216 / 3 873 |
| EA 217 | **B** | **113 (52,1%)** | **2 024 (64,2%)** | **104** | **110 / 1 393** |
| PoE2 247 (08-06 do 12:00) | A | 0 | 0 / 7 139 | 247 | 247 / 8 843 |
| PoE2 247 | A2 — klucze po akapitach | 29 (11,7%) | 1 521 (21,3%) | 218 | 273 / 6 892 |
| PoE2 247 | C — jak B (korpus EA przy innej grze) | 36 (14,6%) | 1 569 (22,0%) | 211 | 265 / 6 843 |

Wystąpienia ważone `use_count` (ile razy blok faktycznie pojawił się w sesji): EA 242 — 80,3% (A)
→ 90,0% (B) obsłużonych lokalnie; EA 217 — 70,8% → 85,9%. Wyświetlane tłumaczenie ma tyle
wierszy co odczyt we wszystkich blokach wszystkich wariantów. Próba lokalna z korpusem: p50
0,10 ms, p95 0,66 ms (EA); 0,32/1,92 ms (PoE2 — dłuższe bloki). Kontrola C: 6 bloków PoE2
przyciągniętych do korpusu EA — 4 różnią się tylko wielkością liter (ogólne etykiety menu), 1
kropką na końcu, 1 to krótka etykieta w bloku śmieci; zero zmian znaczenia (przegląd ręczny).
W praktyce przyciąganie działa wyłącznie z korpusem aktywnego profilu, a PoE2 korpusu mieć nie
może (ADR-014). Przegląd ręczny przyciągnięć EA (79 bloków): bez błędnych podmian; pokazał trzy
usterki naprawione w tej rundzie — literalne `\n` w 30 tekstach UI (sprostowanie w kroku 2b: wcześniej
podano 102 — tyle tekstów ma prawdziwy nowy wiersz; teraz nowy wiersz, także
w kluczu `translate`), znacznik mówcy w środku wiersza (teraz dosłowny) i liczba jako osobny
akapit wysyłana do dostawcy (teraz dosłowna). Odczyty z animacji pisania (ucięte zdanie)
dostają tłumaczenie całego zdania — świadomie (jak w rundzie 2026-10-06).

**Regresje.** SceneReplay bez korpusu (Mock w pamięci), przed i po zmianie, ten sam komputer:
`displayed`, `local-reading`, `reading-jitter`, `hud-motion`, `stale-junk` — wszystkie exit 0,
te same `fixtureValid`/`expectedBehavior`/`desiredReadingPublished`, te same liczniki zapytań
i znaków Mock oraz trafień cache; czasy w granicach szumu (np. `stale-junk` 330 → 325 ms,
`displayed` `middleReadyMs` 2 318 → 2 326 ms, `hud-motion` usunięcie 101/178 → 94/177 ms).

Liczby: `GTO Diagnostics\20261005-natywne-spolszczenie\krok2\powtorka` (`powtorka.json`,
`powtorka.md`, `corpustool-mock-b.json`, `sceny\przed-*.jsonl`, `sceny\po-*.jsonl`,
`sceny\porownanie-przed-po.json`); bazy i przegląd z tekstami gry tylko w `krok2\private\powtorka`.
Weryfikacja: build całego rozwiązania i App bez ostrzeżeń; Core 1097/1097 (+30), Infrastructure
276/276 (+6), CorpusTool 147/147; pokrycie linii (scalone): Core 96,9%, Infrastructure 93,5%.

**Otwarte.** Nie sprawdzone w oknie gry (tylko powtórka przez pipeline i SceneReplay bez korpusu);
jakość tłumaczeń składanych z jednostek u DeepL/LLM na żywo to hipoteza. Tłumaczenie korpusu
u prawdziwego dostawcy wykonano 2026-10-06 (DeepSeek V4.1 Flash bez rozumowania, 7 581 tekstów,
337 partii bez błędu, 8 z uwagą kontroli jakości, ok. 0,12–0,24 USD, 5 min 23 s).
Tryb prywatny nie czyta wpisów z wyprzedzeniem. Korpus jest wczytywany przy przebudowie
pipeline'u — po nowym `extract`/`translate` trzeba uruchomić aplikację ponownie. Ręczna korekta
bloku złożonego z kilku części działa tylko dla tego samego odczytu.

#### Krok 2b — poprawki po recenzji (2026-10-06)

Recenzja kroku 2 (rundy 2026-10-06 (1)–(3)): ok-with-fixes, 2 × medium, 5 × low. Każda poprawka
ma najpierw test odtwarzający na HEAD `f37ef8f` (na HEAD nie przechodziło 31 nowych testów Core
i 13 CorpusTool; testy nowych funkcji — `BoundedGuarded`, `ResolveGameRoot`, `CheckOutsideGame` —
powstały razem ze zmianą), potem zmianę. L1 odtworzono pomiarem (3 próbki z kluczem prawdy
różnym od bieżącego `MatchKey`).

- **M1 — znak, waluta i procent przy liczbie.** `LooseKey` obcinał na brzegach wszystko poza
  literami i cyframi, więc „-10%” przyciągało się dokładnie (1,0) do „+10%”, „€25” do „$25”,
  a samo „10” do „+10%”. Teraz `CorpusText.IsNumberSign` zostawia przy liczbie `+ - − #`
  (bezpośrednio przed cyfrą; `+` także po), `%` (po liczbie, także po spacji) i `$ € £ ¥`
  (przed albo po, także ze spacją), a te znaki muszą się zgadzać również w przybliżeniach,
  fragmentach i częściach wiersza (`EditDistance.BoundedGuarded`). Fragment nie może zaczynać się
  ani kończyć w środku liczby ze znakiem.
- **M2 — liczby sklejone z literami.** „5kg”, „10am”, „1st”, „x3” nie wchodziły do sygnatury
  cyfr, więc „6kg” zamiast „5kg” przechodziło jako literówka. `BoundedGuarded` liczy odległość
  edycyjną, w której cyfry i znaki liczb muszą stać naprzeciw identycznych znaków; dopuszczalna
  jest tylko cyfra odczytu naprzeciw litery mylonej przez OCR w korpusie (1/l/I, 0/O, 5/S, 8/B)
  i — rozszerzenie po pomiarze — `1` naprzeciw apostrofu (OCR czyta „I'm” jako „11m”: bez tego
  reguła odrzucała 6 próbek syntetycznych i 1 blok prawdziwej sesji EA). Wstawiona albo
  zgubiona cyfra, inna cyfra i litera odczytu naprzeciw cyfry korpusu = brak dopasowania.
  Dotychczasowa reguła „liczby samodzielne 1:1” zostaje.
- **L1 — klucz prawdy w ewaluatorze.** `CorpusEval evaluate` brał `truthKey` zapisany przy renderze
  (stary `MatchKey` sprzed zamiany literalnego `\n`); teraz liczy go przy ocenie z `truth`.
  Tekstów korpusu z literalnym `\n` jest **30** (UI), nie 102 — 102 to teksty z prawdziwym nowym
  wierszem (poprawione w rundzie (3) i w CHANGELOG).
- **L2 — nieaktualny wpis całego bloku przy błędzie dostawcy.** Przy aktywnym korpusie wpis całego
  odczytu w starym formacie (bez `reflow-1`), z `qa=…` albo z inną płcią nie był zapasem: próba
  całego bloku szła bez słownika wpisów zastępowanych. Teraz `ProbeBlocksAsync` go zbiera
  (bez liczenia trafień), a gdy złożenie jednostek kończy się brakiem (błąd dostawcy, limit),
  wynikiem jest stary wpis — jak bez korpusu. Gdy dostawca odpowie, wygrywa nowe tłumaczenie.
- **L3 — korekta bloku przyciągniętego przybliżeniem.** `TranslationOutcome.CacheKey` (klucz
  ręcznej korekty w orkiestratorze) jest kanoniczny tylko przy dopasowaniu dokładnym
  (`UnitPlan.CorrectionKey`, `TranslationUnit.ExactMatch`); przy przybliżeniu jest pusty, więc
  `TranslationOrchestrator.SaveManualCorrectionAsync` zapisuje korektę pod kluczem odczytu OCR —
  błędne przybliżenie nie przenosi korekty na inne odczyty tego zdania.
- **L4 — straże ADR-014.** `extract` sprawdza proces gry przed jakimkolwiek odczytem w folderze
  gry (wcześniej po skanie nagłówków `.pak`/`.utoc`), także `*.exe` z katalogu głównego gry.
  Katalog główny (`GameFolderGuard.ResolveGameRoot`): `<…>\steamapps\common\<gra>` (też
  `Epic Games\<gra>`, `GOG Galaxy\Games\<gra>`) albo najwyższy folder nad `--game-dir` z plikiem
  z `processNames` profilu; gdy `--game-dir` to podfolder, anti-cheat, `.sig` i zaszyfrowane
  kontenery są szukane w całym katalogu głównym (anti-cheat innej gry w bibliotece nie blokuje).
  `--out`/`--stats` (`extract`) i `--cache`/`--stats` (`translate`) nie mogą leżeć w folderze gry,
  w katalogu głównym, pod `steamapps\common` (ani w bibliotekach Epic/GOG) ani w folderze z plikiem
  gry z profilu (`OutputLocationGuard.CheckOutsideGame`).
- **L5 — prywatność.** `PRIVACY.md` i dopisek (3) do ADR-014 nie obiecują już, że do dostawcy idzie
  tylko tekst widoczny na ekranie: przy przyciągnięciu może to być pełne zdanie z korpusu, którego
  część dopiero się wyświetla. Usunięte sprzeczne „korpusu nie wysyła nigdzie” (`CorpusTool
  translate` wysyła teksty korpusu na polecenie gracza).

**Eksperyment przeliczony** (2026-10-06, te same 880 próbek OCR bez nowego renderu, świeża kopia
cache; pełny przegląd 448 konfiguracji w każdym wariancie — zawsze wybiera T 0,80 / m 0,08 / L 16):

| | HEAD, klucz z renderu | HEAD + L1 | Po poprawkach (stan końcowy) |
|---|---:|---:|---:|
| Poprawnie przyciągnięte | 695 (78,98%) | 697 (79,2%) | 690 (78,4%) |
| Błędnie (z przyciągniętych) | 3 (0,43%) | 1 (0,14%) | 1 (0,14%) |
| Tylko fragment / bez dopasowania | 10 / 172 | 10 / 172 | 12 / 177 |
| Wariant ścisły: poprawne / błędne | 621 / 2 | 623 / 0 | 616 / 0 |
| Cache EA 242: znaki / bloki lokalnie | 59,44% / 48,76% | 59,44% / 48,76% | 59,44% / 48,76% |
| PoE2: bloki z trafieniem / ze zmianą tekstu | 3,24% / 0 | 3,24% / 0 | 3,24% / 0 |

HEAD z kluczem z renderu liczył 2 poprawne przyciągnięcia jako błędne (3 próbki z literalnym `\n`).
Stan końcowy traci 7 poprawnych z 697: odczyty, w których OCR wstawił cyfrę w słowo albo naprzeciw
znaku, który nie jest mylną literą — koszt reguły M2 (5 → bez dopasowania, 2 → tylko fragment).
Ten sam jedyny błąd co dotąd (OCR zgubił cenę, została sama etykieta). Wariant z regułą M2 bez
apostrofu: 684 poprawne, 1 błędne, cache EA 58,89% znaków / 48,35% bloków. Prawdziwy cache EA
i PoE2: liczby, przegląd przyciągnięć przybliżonych i trafienia PoE2 identyczne jak na HEAD.
Czas `SnapBlock` (2 przebiegi): EA p95 0,86–0,88 → 0,91–0,94 ms, p99 1,8–1,9 → 2,3 ms; korpus
150 tys. p95 4,7 → 4,8 ms.

**Powtórka przeliczona** (`CorpusEval replay`, Mock, świeża kopia cache i świeżo wypełniona baza B —
`corpustool-mock-b.json` identyczny z poprzednim): wszystkie liczniki identyczne z poprzednią
powtórką i z HEAD na tych samych kopiach — EA 217 B: 2 024 / 3 154 znaków (64,2%), 113 / 217
bloków (52,1%), 104 zapytania, 110 tekstów / 1 393 znaki do dostawcy; EA 242 B: 60,4% / 49,6%,
122 zapytania; kontrola PoE2 (korpus EA przy innej grze): 36 / 247 bloków (14,6%), 1 569 / 7 139
znaków (22,0%), 211 zapytań, 6 przyciągniętych (1 zmiana treści, 4 wielkość liter). Przegląd
przyciągnięć (85 bloków z wyświetlanym tekstem) identyczny. Próba lokalna p50/p95 w granicach
szumu (EA 217 B 0,110/0,701 → 0,100/0,692 ms). Wniosek z pomiaru: w tych sesjach nie ma
przyciągnięcia zależnego od znaku przy liczbie ani od liczby sklejonej z literą; spadek błędnych
podmian na tekstach z liczbami (np. statystyki przedmiotów) to hipoteza bez pomiaru.

Stan kodu pomiaru „po”: `f37ef8f` + niezatwierdzone zmiany kroku 2b (`git diff HEAD --
'src/*.cs' 'tools/*.cs'`: 11 plików, sha256 `4028e8b0d3b36144…`). Liczby: `GTO Diagnostics\20261005-natywne-spolszczenie\krok2`
— `eksperyment-po-poprawkach` (`podsumowanie.md`, `r0-…`–`r3-…`, `czasy-*`) i
`powtorka-po-poprawkach` (`porownanie.md`, `head\`, `po\`, `corpustool-mock-b.json`); teksty gry
tylko w `krok2\private\po-poprawkach` i `krok2\private\powtorka-po-poprawkach`. Weryfikacja:
build całego rozwiązania (z App) bez ostrzeżeń; Core 1155/1155 (+58), Infrastructure 276/276,
CorpusTool 166/166 (+19). SceneReplay nie był powtarzany: bez korpusu pipeline idzie dawną ścieżką
(zmiany dotyczą tylko planu jednostek), testy tej ścieżki bez zmian.

**Otwarte po kroku 2b.** Reguła M2 odrzuca odczyty z cyfrą wstawioną przez OCR w słowo (7 z 880
próbek) — świadomie, precyzja przed zasięgiem. Lista znaków liczb i mylnych liter jest stała (bez
np. `2/Z`, `6/G`, `|`, `!`). Katalog główny gry poza bibliotekami Steam/Epic/GOG jest rozpoznawany
tylko po pliku z `processNames` profilu.

### Runda 2026-10-06 (4) — etykiety, dialog, śmieci

**Punkt wyjścia (pomiar).** Pierwsza sesja gracza na wersji z korpusem (kopia bazy po sesji,
`sesja-2026-10-06\ANALIZA.md`): 36 tekstów poszło do DeepL, choć ich pełne wersje są w korpusie —
ok. 12 krótkich etykiet źle odczytanych przez OCR (dopasowanie krótkich tekstów tylko dokładne),
ok. 9 niedokończonych linii dialogu wpisywanego litera po literze, ok. 12 śmieci z ikon i tekstur,
2 teksty dynamiczne (data ostatniej gry). Trzy poprawki zaakceptowane przez gracza:

- **Etykiety (`CorpusSnapper.FindLabel`).** Odczyt o luźnym kluczu 3–15 znaków bez dopasowania
  dokładnego jest porównywany z krótkimi tekstami korpusu (≤ 24 znaki, kubełki po długości,
  wstępny filtr: maska i worek znaków w postaci kanonicznej) odległością ważoną pomyłkami OCR
  (`OcrEditDistance`, jednostka 100): kreski I/l/|/!/' między sobą 25, cyfra odczytu za mylną literę
  korpusu (1/l/I/', 0/O, 5/S, 8/B) 25, h↔n 40, c↔e 50, rn↔m / cl↔d / vv↔w 30, spacja
  i interpunkcja 30, kreska dopisana albo zgubiona na brzegu 30, każda inna zmiana 100. Strażnik
  cyfr jak w `BoundedGuarded` (cyfra korpusu musi stać naprzeciw identycznej). Budżet: do 4 znaków
  50, do 6 znaków 80, dalej min(175, 15·L+30) — czyli najwyżej jedna „zwykła” zmiana i tylko od
  7 znaków. Warunki: cel jest etykietą (`ui`) aktywnego korpusu; drugi kandydat (dowolnego rodzaju)
  co najmniej 50 dalej; zgodna sygnatura liczb; na brzegach tylko tanie zmiany (zwykła litera
  dopisana albo ucięta na brzegu = obcięty tekst, nie pomyłka OCR); odczyt nie ma więcej słów niż
  etykieta; odczyt złożony wyłącznie ze słów słownika korpusu musi mieć te same litery co cel
  (prawdziwe inne słowo nie jest pomyłką); odczyt nie jest dosłownym początkiem innego, dłuższego
  tekstu korpusu. Wynik: `CorpusMatch.IsLabel`, rodzaj `Fuzzy`, tłumaczenie z klucza kanonicznego.
- **Dialog pisany literami (`CorpusSnapper.FindPrefix`).** Odczyt od 12 liter i 3 słów (próg
  z przeglądu niżej), który jest początkiem linii korpusu z dopasowaniem z wolnym końcem
  (`OcrEditDistance.Prefix`, te same wagi, budżet 10% długości odczytu), przy czym cel jest
  linią `dialog`/`subtitle`, drugi kandydat (dowolnego rodzaju, także pełna krótsza linia) co
  najmniej 1,0 zmiany dalej, cięcie nie wypada w środku liczby, a za nim zostaje jeszcze tekst.
  Tylko dla bloku, akapitu albo wiersza kończącego blok (wiersz w środku bloku nie jest prefiksem).
  Jednostka planu dostaje `Prefix`: gdy odczyt ma co najmniej 2 wiersze, tłumaczenie jest
  od razu łamane na przewidywaną liczbę wierszy całej kwestii (długość klucza / szerokość pełnych
  wierszy odczytu), więc pełny odczyt daje ten sam układ (`ToPrefixLayout` = `ToScreenLayout`
  przy trafionej liczbie wierszy). Klucz tłumaczenia = pełna linia, więc wszystkie dłuższe
  odczyty trafiają w ten sam wpis.
- **Ten sam klucz nakładki.** `TranslationPipeline.CorpusIdentity` daje tożsamość bloku w pełni
  obsłużonego korpusem (klucze kanoniczne + luźne klucze dosłownych okruchów); `LiveBlockKeyer`
  (opcjonalnie) liczy klucz live z tej tożsamości zamiast z tekstu odczytu. Kolejne odczyty
  tej samej kwestii i drżenie OCR etykiety („…ect”/„…cct”) to ten sam blok: aktualizacja
  w miejscu (pozycja, rozmiar, tekst), bez stabilizatora podmian, bez ponownego fade-in i bez
  nowego wpisu na pasku napisów. Bez korpusu tożsamość jest pusta — klucze jak dotąd.
- **Śmieci (`CorpusSnapper.LooksLikeNoise`, `TranslationPipeline.ShouldTranslateLive`).** Bramka
  live: dokładny tekst korpusu albo (`JunkFilter` i nie szum). Szum przy aktywnym korpusie =
  odczyt niskiej jakości (brak liter; do 16 liter: mniej niż połowa liter w słowach znanych ze
  słownika korpusu albo `ReadingQuality` < 0,75; dłuższy: oba warunki naraz), bez żadnego
  dopasowania `SnapBlock` (także fragmentu) i bez podobieństwa do tekstu korpusu (krótki klucz:
  odległość ważona najwyżej 1 zmiana na 4 litery + 60 i ≤ 30% długości, także bez cyfr; długi:
  ≥ 70% trygramów wspólnych z jednym tekstem) i nie będący terminem słownika. Szum nie idzie do
  dostawcy ani na nakładkę; wiersz-szum w bloku z prawdziwym tekstem jest dosłowny. Wynik
  zapamiętany na pipeline (jak plany). Tłumaczenie regionu (ręczne) nadal używa dawnego filtra.
  Opcje: `AllowLabels`, `AllowPrefixes`, `RejectNoise` i progi w `CorpusSnapOptions`.

**Pomiar (a) — odczyty z sesji gracza** (`CorpusEval session`: 36 wpisów cache od 06:28Z jako
wejście OCR, bramka live + `TranslationPipeline` z Mockiem, cache = kopia bazy gracza bez tych
wpisów, profil `escape-academy`, korpus 8 341 wpisów):

| | Odrzucone (szum) | Lokalnie | Do dostawcy | Znaki do dostawcy |
|---|---:|---:|---:|---:|
| Przed (dopasowanie jak w `008f39c`) | 0 | 0 | 36 | 571 |
| **Po** | **11** | **15** (11 etykiet, 4 kwestie) | **10** | **171** |

Przegląd ręczny: 15 z 15 lokalnych przyciągnięć poprawnych (1 etykieta z kreską po słowie oceniona
jako „bardzo prawdopodobnie poprawna”), 0 błędnych. Z 11 odrzuconych 9 to śmieci bez tekstu,
2 to tak zniekształcone odczyty prawdziwych napisów, że DeepL zwrócił je bez zmian — strata
żadna. Do dostawcy dalej: 2 teksty dynamiczne (etykieta + data), 2 kwestie o wspólnym początku
z inną linią (dwie wersje kwestii różnią się dopiero dalej — świadomie czekamy), 2 kawałki
jednego napisu interfejsu (nie dialogu, więc bez prefiksu; jeden to środek linii), 4 krótkie
odczyty (2 ze słowami słownika korpusu, 2 podobne do tekstu korpusu). 4 grupy różnych
odczytów tej samej etykiety mają wspólny klucz nakładki.

**Pomiar (b) — precyzja** (`CorpusEval evaluate`, te same 880 próbek OCR i kopia cache co
w rundzie 2026-10-06; progi wydania T 0,80 / m 0,08 / L 16):

| | Przed (HEAD `008f39c`) | Po |
|---|---:|---:|
| Syntetyczne: poprawne / błędne (z przyciągniętych) / brak | 690 / 1 (0,14%) / 177 | 698 / 1 (0,14%) / 170 |
| Wariant ścisły (dokładne od 8 znaków): poprawne / błędne | 616 / 0 | 624 / 0 |
| Cache EA 242: znaki / bloki obsłużone lokalnie | 59,44% / 48,76% | 74,63% / 60,33% |
| Cache EA 217 (bez 09-04): znaki / bloki | 63,16% / 51,15% | 79,42% / 63,59% |
| PoE2 247 (kontrola): bloki z trafieniem / ze zmianą tekstu | 8 / 0 (0%) | 8 / 0 (0%) |

Błędne przyciągnięcie to wciąż to samo (OCR zgubił cenę przy etykiecie). Pełny przegląd 448
konfiguracji po zmianie wybiera T 0,85 / L 12 (EA 75,15%, syntetyczne 687 / 2 = 0,29%) — progi
wydania zostają. `--features off` na nowym kodzie odtwarza HEAD co do liczby (690/1/177, 59,44%).
Przegląd ręczny nowych przyciągnięć w cache EA (18 etykiet, 12 początków dialogu): 0 błędnych.

**Próg dialogu** (`CorpusEval prefixes`: 3 793 ucięte odczyty OCR linii dialogu, 12 919 uciętych
linii korpusu bez błędów, 1 593 bloki PoE2 w całości i ucięte; cięcie po k literach). Błędne
przyciągnięcia **przez prefiks: 0** we wszystkich 24 konfiguracjach. PoE2 przez prefiks: 3 przy
L 8, 0 od L 10 / W 2 — wybrano L 12 / W 3 / 10% / margines 1,0 (zapas): poprawne 57,5% uciętych
odczytów OCR (2 016 przez prefiks), 61,2% uciętych linii czystych. Pozostałe „błędne” w tych
sondach (37 OCR, 111 czystych) to dokładne i przybliżone dopasowania uciętego tekstu do innego
istniejącego tekstu (np. pierwsze słowa kwestii = osobna etykieta) — zachowanie sprzed rundy;
przez nową etykietę: 1 (OCR) i 2 (ucięte PoE2).

**Dialog pisany literami przez pipeline** (`CorpusEval typing`: 1 633 linie korpusu i 435
odczytów OCR linii dialogu, dopisywane co 3 znaki albo tylko w przerwach po interpunkcji, Mock,
pusty cache na linię):

| | Przed: trafione przed końcem / średnio wpisane | Po | Teksty do dostawcy przed → po |
|---|---:|---:|---:|
| Korpus, co 3 znaki | 1 316 / 83% | 1 365 / 41% | 15 945 → 6 689 |
| OCR, co 3 znaki | 330 / 85% | 364 / 40% | 5 188 → 2 214 |
| Korpus, w przerwach | 107 / 88% | 592 / 51% | 1 574 → 1 261 |
| OCR, w przerwach | 31 / 90% | 177 / 48% | 504 → 385 |

Po pierwszym trafieniu: **0 zmian klucza nakładki** w każdym zbiorze, 0 nowych zapytań przy
cięciu w przerwach; zmiany wyświetlanego tekstu to wyłącznie zmiana podziału wierszy (np. 128 / 0
poza układem dla korpusu w przerwach; przy cięciu co 3 znaki 749 / 1). Przy cięciu co 3 znaki
w 28 z 364 linii OCR któryś późniejszy odczyt (mocno zniekształcony) wypada z tolerancji —
wtedy jest jak dotąd (osobny klucz, zapytanie).

**Pomiar (c) — powtórka sesji EA** (`CorpusEval replay`, wariant B: korpus + baza wypełniona
`CorpusTool translate --provider mock`; HEAD i po na tych samych kopiach):

| Bloki | | Znaki lokalnie | Bloki lokalnie | Zapytania | Teksty / znaki do dostawcy | Odrzucone jako szum (bloki / znaki) |
|---|---|---:|---:|---:|---:|---:|
| EA 242 | przed | 2 075 / 3 437 (60,4%) | 120 (49,6%) | 122 | 128 / 1 678 | — |
| EA 242 | **po** | **2 613 (76,0%)** | **150 (62,0%)** | **57** | **58 / 737** | 35 / 218 |
| EA 217 | przed | 2 024 / 3 154 (64,2%) | 113 (52,1%) | 104 | 110 / 1 393 | — |
| EA 217 | **po** | **2 553 (80,9%)** | **142 (65,4%)** | **42** | **43 / 475** | 33 / 210 |
| PoE2 247 (korpus EA przy innej grze) | przed | 1 569 / 7 139 (22,0%) | 36 | 211 | 265 / 6 843 | — |
| PoE2 247 | po | 1 760 (24,7%) | 41 | 155 | 190 / 6 181 | 51 / 364 |

Przyciągnięcia PoE2 do korpusu EA bez zmian (6, w tym 1 zmiana treści — ta sama co dotąd).
Wyświetlane tłumaczenie ma tyle wierszy co odczyt w 205 z 207 bloków EA (2 to kwestie
z prefiksu, łamane na przewidywaną liczbę wierszy całej kwestii — celowo).

**Czas** (`CorpusEval bench`, 2 przebiegi): `SnapBlock` EA p50 0,004 → 0,12 ms, p95 0,90–0,96 →
1,09–1,21 ms, p99 2,2 → 2,4–2,6 ms (wyszukiwanie etykiet dla krótkich odczytów); budowa indeksu
35 → 55 ms. Korpus syntetyczny 150 tys. tekstów: p95 4,1–5,0 → 5,5–5,8 ms, zapytania EA na nim
p95 3,1–5,4 → 9,1–9,4 ms. Próba lokalna w powtórce (z regułą szumu): EA p95 0,66 → 1,8 ms.

**Weryfikacja.** Build całego rozwiązania i App bez ostrzeżeń; Core 1 226/1 226 (+71: odległość
OCR, etykiety, prefiksy, szum, tożsamość, klucz nakładki, układ wierszy — wyłącznie dane
syntetyczne), Infrastructure 276/276, CorpusTool 166/166. SceneReplay nie był powtarzany: bez
korpusu bramka = `JunkFilter`, tożsamość pusta, plan bez zmian. Liczby:
`GTO Diagnostics\20261005-natywne-spolszczenie\krok-dopasowanie` (`sesja`, `ewaluacja`,
`progi-prefiksu`, `pisanie`, `powtorka`, `czasy`, `PODSUMOWANIE.md`); teksty gry tylko
w `krok-dopasowanie\private`.

**Otwarte / ryzyka.** (1) Reguła szumu zależy od słownika korpusu: czysty napis z tekstury, którego
słów nie ma w plikach gry, zostanie bez tłumaczenia (oryginał widoczny); przy korpusie innej gry
odrzuciłaby 51 z 247 bloków PoE2 (21%), w tym prawdziwe nazwy — działa tylko z korpusem
aktywnego profilu. (2) Prefiks dla jednowierszowego początku nie zna szerokości okna dialogu:
pełne tłumaczenie stoi w jednym wierszu (może wyjść poza okno), dopóki gra nie zacznie drugiego
wiersza. (3) Dwie wersje kwestii o wspólnym początku czekają, aż odczyt je rozróżni. (4) Krótki
odczyt równy innemu tekstowi korpusu (pierwsze słowa kwestii = etykieta) nadal przyciąga się
dokładnie, jak przed rundą. (5) Nie sprawdzone w oknie gry — tylko powtórki przez pipeline
i testy jednostkowe; zachowanie nakładki (aktualizacja w miejscu przy rosnącym bloku) wynika
z kodu `OverlayWindow.UpdateLiveBlocks`, bez pomiaru SceneReplay z korpusem.

### Runda 2026-10-06 (5) — wygląd napisów w trybie „Na oryginale (zakrywa)”

**Punkt wyjścia (pomiar).** Gracz: napisy „wyglądają mocno średnio” i „czasem się bugują”.
Galeria stanu po rundzie (4) (`OverlayPreview` na 4 klatkach 4K z EA, DPI 144, kopia bazy gracza,
`krok-wyglad\przed`) dała 13 wad: biały kontur pod białym tekstem dialogu (próbnik wziął
kolor szuflady #2F3940 za kolor tekstu), tłumaczenie 0,54–0,58× wysokości oryginału, cienki Segoe UI
zamiast grubego zaokrąglonego kroju gry, łatka ciemniejsza od tła o 11–19 poziomów i pusta smuga
przy krótszym tekście, prześwitujący cień oryginału (6 px za boxem łatka kryła w 0,67), ikony
klawiszy zjadane przez łatkę, kontur w kolorze półtonu, schodki konturu z 8 kopii tekstu,
kolorowe plamy w teksturze łatki, zakrywane tłumaczenia identyczne z oryginałem, zły kolor
dialogu, przesunięcie w pionie i przepełnienia.

**Zmiany.**

- **Wypełnienie liter zamiast prostokąta (`Core.Vision.GlyphCoverBuilder`).** Dla każdego bloku
  na przechwyconej klatce: tło szacowane z pierścienia wokół boxu (push-pull w zmniejszonej
  siatce), polaryzacja tekstu z odchylenia od tego tła (przy remisie: która grupa jest otoczona
  przez drugą — wypełnienie vs kontur), maska liter = odchylenie we właściwą stronę ∧ białe/czarne
  top-hat (cienkie struktury, nie duże obiekty tła), doprecyzowana progiem 50% kontrastu do
  lokalnego tła; obwódka (kontur, cień, antyaliasing) = piksele w zasięgu 0,2 wysokości linii,
  które różnią się od tła bardziej niż 4× szum tła. Maska poszerzona o 1–2 px, piksele pod nią
  wypełniane z otoczenia (push-pull, interpolacja dwuliniowa). Łatka to obraz RGBA: krycie 1 tylko
  na pikselach liter (brzeg 0,5), **reszta przezroczysta** — tło gry zostaje żywe i nietknięte.
  Duże napisy (linia ≥ 90 px) liczone w połowie rozdzielczości, ≥ 150 px w 1/3.
- **Pomiar stylu z tych samych pikseli:** kolor tekstu (rdzeń liter), kontur (kolor, grubość
  z mediany odległości od krawędzi z pominięciem pasma antyaliasingu, kontur uznany przy ≥ 2 px
  albo ≥ 1 px i różnicy jasności ≥ 30 od otoczenia), cień (różnica zasięgu w prawo/dół i w lewo/górę),
  profil tuszu każdej linii (`InkProfile`: linia bazowa = ostatni wiersz ≥ 30% maksimum, góra = ciągły
  tusz nad linią bazową — kropki i akcenty odcięte, gęstość tuszu), rozstaw linii, wyrównanie
  (lewo/środek/prawo z co najmniej 2 linii), ikony: krótki token klawisza przed tekstem z odstępem
  ≥ 0,45 wysokości linii (np. „X Hint”) i 1–2-znakowy token na końcu z odstępem ≥ 0,55 wysokości
  słów albo ≥ 0,3 przy wyższym o 10% polu (np. ✓ czytany jako „to”) — ich piksele nie są ruszane,
  a token znika z tłumaczenia, jeśli w nim jest.
- **Liczone w tle przy OCR, pamiętane po kluczu i podpisie pola.** `LiveTranslationSession`
  uruchamia budowę łatek (`Parallel.For`, do 4 wątków) równolegle z tłumaczeniem; po tłumaczeniu
  czeka na wynik. Podpis pola (siatka 12×4 jasności) bez zmian i ten sam box (±2 px) = łatka
  z poprzedniego przebiegu (bez obliczeń). Podpis zmieniony przy tym samym boxie (tło się rusza,
  najechany wiersz) = **miękka łatka** (całe pole z wypełnieniem, wygaszony brzeg); gdy obraz stanie,
  następny przebieg wraca do ostrej. Nieudana budowa zostawia łatkę poprzedniego przebiegu; bez
  łatki działa dawna ścieżka (rozmyta tekstura). Opcja `LiveSessionOptions.BuildGlyphCovers`
  (aplikacja: tylko „Na oryginale” i tryb przy oryginale).
- **Tekst z geometrii (`App.Ui.GameTextElement`).** `FormattedText.BuildGeometry`, kontur piórem
  2t z zaokrąglonymi łączeniami pod wypełnieniem, cień = przesunięta kopia konturu; jedna geometria
  na tekst/krój/rozmiar, przesunięcie kotwicy i linii bazowej tylko transformacją. Łatka rysowana
  jako obraz 1:1 z pikselami ekranu (najbliższy sąsiad bez skalowania).
- **Krój z profilu (`overlay.fontFamily`) i dołączony Lexend Deca (OFL).** Napisy EA to rodzina
  Lexend (porównanie glifów na klatkach). `App/Fonts`: Lexend Deca Regular/Medium/SemiBold/Bold
  jako zasoby, licencja w `licenses/LexendDeca-OFL.txt`, `THIRD-PARTY-NOTICES`. Ustawienie kroju
  „Jak w grze (krój z profilu)” (`auto`, domyślne; dawny domyślny Segoe UI przechodzi raz na `auto`,
  `overlayFontRevision`), bez profilu Segoe UI. Grubość dobierana do gęstości tuszu oryginału
  (ten sam tekst EN zrasteryzowany w każdej grubości, ten sam `InkProfile`).
- **Rozmiar i położenie z linii bazowej.** em = wysokość tuszu oryginału nad linią bazową /
  ta sama miara tekstu oryginału w wybranym kroju; pierwsza linia polskiego tekstu na linii bazowej
  oryginału, lewa krawędź tuszu na lewej krawędzi tuszu oryginału (albo środek/prawa krawędź).
  Dopasowanie: wieloliniowe — do 1,08× szerokości, potem mniejsza czcionka do 85%, potem zawijanie
  w szerokości oryginału z jego rozstawem linii; jednoliniowe — do 1,25× szerokości, a gdy po prawej
  jest wolne miejsce (do następnego bloku w tym pasie albo krawędzi monitora) do 3×, potem do 85%;
  przed ikoną na końcu — do szerokości przed ikoną, najwyżej do 70%. Tekst nie wychodzi za krawędź
  monitora.
- **Usterki.** (1) Tłumaczenie identyczne z oryginałem (nazwy, „OK”, logo „ESCAPE”, fałszywe
  „in”→„In”) nie jest rysowane w trybie zakrywania — widać grę. (2) Taki blok był też odrzucany
  przez filtr anty-sprzężeniowy jako „nasze własne tłumaczenie” (`displayedTranslations`), więc przy
  zmiennym tle wypadał po okresie łaski i wracał (miganie co kilka przebiegów) — filtr pomija teraz
  bloki o tłumaczeniu równym oryginałowi. (3) Zmiana sposobu rysowania bloku (łatka pojawia się
  lub znika) podmienia element w miejscu w kolejności warstw, bez ponownego fade-in. (4) Komunikat
  w nakładce „⚠ Pełny ekran utrudnia nakładkę — przełącz na okno bez ramki” (8 s, raz na sesję),
  gdy okno gry nie wspiera PrintWindow i zakrywa cały monitor; w oknie aplikacji pełne zdanie.
  (5) Rozgrzanie krojów i budowy łatek przy starcie live (zimny pierwszy blok kosztował ok. 200 ms
  WPF + ok. 30 ms JIT).

**Pomiar wyglądu (OverlayPreview, te same klatki i ustawienia gracza co PRZED, `krok-wyglad\po`,
pary `krok-wyglad\przed-po`).** Wysokość tuszu nad linią bazową polskiego tekstu względem
oryginału 0,96–1,10× (PRZED 0,54–0,58× na tekstach identycznych, „Zbadaj” 38 px wobec 52 px),
linia bazowa w 0–1 px od oryginału. Kolor dialogu #8C8D8E (gra ok. #8F9293; PRZED biały na białym
konturze). Kontur czarny o grubości 5–6 px z cieniem w prawo/dół jak w grze (PRZED półton
#616161–#985C52). Poza pikselami liter łatka jest przezroczysta (test jednostkowy), więc nie ma
ciemnej plamy ani smugi; ikony „X” i ✓ zostają. Krój Lexend Deca, grubość Normal–Bold zależnie
od napisu.

**Koszt (ms, 4K, 32 wątki).** Budowa łatki na blok: etykiety menu 1,7–4,6, „Click to wishlist…”
12, dialog 3126×199 px 22–24. Cała klatka z samymi nowymi napisami równolegle: menu (7 bloków)
15, pokój 2,3, dialog 16; ponowne użycie (podpis pola) 0,03–0,11. Łatki liczą się w trakcie
tłumaczenia; przy tłumaczeniu z dostawcy czekanie po tłumaczeniu 0 ms (SceneReplay
`glyphCoverWaitMs` mediana 0, najwyżej 23,6 ms — pierwsza miękka łatka z JIT), przy tłumaczeniu
lokalnym dochodzi czas budowy (≤ 16 ms dla klatki 4K). Wątek UI: układ WPF nowych napisów 5–21 ms
na klatkę (pierwszy pomiar grubości nowego tekstu), rozgrzanie krojów raz 212 ms przy starcie live.

**Regresje (SceneReplay przed → po, ten sam build bazowy skopiowany przed zmianami).** displayed:
usunięcie starego 12,1 → 11,5 ms, gotowy opis 2 312 → 2 311 ms, 0 powrotów; local-occlusion:
expectedBehavior true → true, menu bez strat, usunięcie Inspect 13,6 → 14,5 ms; hud-motion:
true → true, 0 brakujących pomiarów menu, usunięcie 59/157 → 61/128 ms; stale-junk: true → true,
czas życia starego 331 → 333 ms; stale-dim: true → true, 0 zniknięć; reading-jitter: B nigdy
nie pokazany (0 → 0); ocr-timing: capture→update mediana 227–233 → 228–241 ms w trzech
przebiegach naprzemiennych (szum ±7 ms, kontrola sceny 17–23 ms obu wersji). Testy: Core
1 250 (+24: łatka, profil tuszu, ikony, kotwica, podpis, komunikat pełnego ekranu, `overlay.fontFamily`),
Infrastructure 280 (+4: migracja kroju), CorpusTool 166 — zielone. Raporty:
`GTO Diagnostics\20261005-natywne-spolszczenie\krok-wyglad\scenereplay`.

**Otwarte / ryzyka.** (1) Napis na ruchomym tle: łatka liczona z klatki OCR, więc między przebiegami
(ok. 0,6 s) wypełnione litery mogą odstawać od przesuwającego się tła; po zmianie podpisu łatka
staje się miękka, ale nadal jest nieruchoma. (2) Tekst na gęstej teksturze, podobnej do koloru
liter: maska może objąć elementy tła (wygładzone pod napisem) albo nie powstać (wtedy dawna
łatka). (3) Kursywa, kerning i szerokość kroju gry nie są odwzorowane („The Headmaster” prosto;
Lexend Deca jest węższy niż Lexend gry, polski wiersz dialogu bywa dłuższy). (4) Szeryfowy napis
„Click to wishlist” dostaje Lexend (jeden krój na profil). (5) Pojedyncza linia bez wolnego
miejsca: tekst dłuższy niż 1,25× schodzi do 85% i dalej wystaje; przy przyciskach z ramką może
wyjść poza ramkę. (6) Grubość z gęstości: obrys gry wchodzący w lico liter daje cieńszy wariant
(„Back” → Normal). (7) Nie sprawdzone w oknie gry na żywo ani na innych grach — tylko klatki
EA i SceneReplay (bez prawdziwej nakładki).

### Runda 2026-10-06 (6) — kwestia pisana literami, grubość, wyśrodkowanie

**Punkt wyjścia (recenzja rund 4–5).** Połączenie przyciągania początku kwestii (runda 4) z łatką
z wypełnionymi literami (runda 5) dawało przy dialogu pisanym literami polski tekst na
dopisywanych angielskich literach: łatka zakrywa tylko litery z chwili odczytu, a pełne
tłumaczenie stoi od razu w całej szerokości (OverlayPreview z blokiem obejmującym wpisaną część
linii: polski tekst 2,95× szerokości łatki na widocznym angielskim). Do tego różna grubość liter
w jednym menu (Graj Normal, Twórcy Medium, Ustawienia i Wyjdź z gry SemiBold), wyśrodkowany
baner „Click to wishlist…” rosnący w prawo (+268 px) i wyłączony filtr anty-sprzężeniowy dla
tłumaczeń identycznych z oryginałem także w trybach, które je rysują.

**Zmiany.**
- `LiveTranslationSession`: w trybie zakrywania (`LiveSessionOptions.HoldTypingPrefixes`) nowy
  blok, którego odczyt jest niedokończoną kwestią korpusu, nie trafia na nakładkę, dopóki tekst
  rośnie. Sesja prosi o kolejny odczyt pola (jak przy potwierdzeniu odczytu) i pokazuje pełne
  tłumaczenie, gdy odczyt jest całą linią albo liczba liter nie rośnie przez 0,9 s
  (`TypingPrefixSettleTime`); bezpiecznik 8 s (`TypingPrefixHoldLimit`). Blok już wyświetlany pod
  tym samym kluczem nie jest wstrzymywany.
- `TranslationPipeline.IsCorpusPrefix`: jednostka z przyciągnięcia początku linii, przybliżenie
  całej linii z brakiem co najmniej 3 liter i 7% liter korpusu (długi początek kwestii mieści się
  w progu przybliżenia 0,80) oraz krótki początek kwestii poniżej progów przyciągania
  (`CorpusSnapper.StartsSpokenLine`: co najmniej 4 litery, początek dłuższej o 3 litery linii
  dialogu lub napisów, sam nie jest tekstem korpusu) — przy wstrzymywaniu (tryb zakrywania bez
  paska napisów) ten ostatni nie idzie już do dostawcy; `IsCorpusPrefix` jest używane tylko tam.
- `OverlayFonts.ChooseStyleWeight`: grubość wybierana z głosów bloków w tym samym stylu (krój,
  wysokość liter ±20%, kolor tekstu, obecność obrysu; górna mediana), głosy wszystkich bloków
  aktualizacji zbierane przed układem.
- `OverlayBlockRenderer`: pojedyncza linia o nieznanym wyrównaniu, której środek tuszu leży
  w 1% szerokości monitora od jego środka, jest wyśrodkowana (limit szerokości 1,6×).
- `LiveSessionOptions.IdentityEchoSafe`: tłumaczenie identyczne z oryginałem jest wyłączone
  z filtra anty-sprzężeniowego tylko wtedy, gdy nakładka go nie rysuje albo działa wykluczenie
  nakładki z przechwytywania.
- SceneReplay `typing` / `typing-nohold`: okno wpisujące dwie syntetyczne linie po 35 ms na znak
  z pauzą 450 ms po interpunkcji, korpus syntetyczny, OCR ze skryptu odczytujący wpisaną część
  z pikseli (tło koduje współrzędną x, więc wycinki OCR są czytane jak fragmenty).

**Pomiar (SceneReplay typing, 2 linie).** Bez wstrzymywania: pełne tłumaczenie od ok. 4,2 s
przed końcem pisania, 6 aktualizacji z polskim tekstem w trakcie pisania na linię, 2 mignięcia
niepełnego tłumaczenia początku na linię, 4 zapytania do dostawcy. Ze wstrzymywaniem: 0
aktualizacji z polskim tekstem w trakcie pisania, 0 niepełnych tłumaczeń, 0 podmian tekstu,
napis ok. 0,1–0,3 s po ostatniej literze, 2 zapytania (pełne linie; w aplikacji z tłumaczeniem
korpusu z wyprzedzeniem 0). Sprostowanie: wcześniej podano 0,1–0,4 s — to wartości
`shownAfterTypingEndMs` (−131 i −389 ms) bez znaku; zdarzenie końca pisania przychodzi 450 ms
po ostatniej kropce, więc napis pojawił się jeszcze w tej pauzie, ok. 0,06–0,33 s po ostatniej
literze. Czasy „przed końcem pisania” wyżej też liczą się od tego zdarzenia. OverlayPreview
(`krok-wyglad\po2`): menu jednolicie Lexend Deca Medium, dialog Normal, baner wyśrodkowany pod
środkiem ekranu.

**Regresje (SceneReplay po zmianach).** displayed: stary usunięty po 13 ms; local-occlusion:
expected true, 0 strat menu; reading-jitter: B nie pokazany; hud-motion: expected true, 0 strat;
ocr-timing: mediana capture→update 228,2 ms; stale-junk: usunięcie po 342 ms, 0 powrotów;
stale-dim: 0 zniknięć. Testy: Core 1 251 (+1), Infrastructure 280, CorpusTool 166 — zielone.
Raporty: `GTO Diagnostics\20261005-natywne-spolszczenie\krok-wyglad\typing` i
`scenereplay-koncowe`.

**Otwarte / ryzyka.** (1) Gra z bardzo długą pauzą w środku kwestii (ponad 0,9 s) pokaże pełne
tłumaczenie w tej pauzie, a dopisywane potem litery wyjdą spod łatki do następnego odczytu.
(2) Tekst spoza korpusu, który jest początkiem kwestii korpusu, czeka 0,9 s. (3) Wstrzymanie
działa tylko w trybie zakrywania; w trybach z tekstem obok oryginału pełne tłumaczenie nadal
pojawia się od początku kwestii. (4) Nie sprawdzone w oknie gry na żywo.

### Runda 2026-10-06 (7) — działanie w ruchu

**Cel.** Na statycznym ekranie tryb zakrywania działał dobrze, ale przy ruchu kamery tłumaczenia
znikały, wisiały w starym miejscu albo migały. Runda: tłumaczenie ma trzymać się napisu w ruchu.

**Metoda.** Nowe narzędzie [MotionLab](../tools/GameTranslatorOverlay.MotionLab/README.md):
trzy nagrania Escape Academy (4K, 10 kl./s, 2026-10-06) — `pokoj-ruch2` (75 s: panoramy, szybki
obrót, chodzenie, drgania, wolna panorama), `prolog-intro` (90 s: dialog, HUD, krótki ruch),
`prolog-ruch` (100 s, mało ruchu kamery). Prawda: offline Windows OCR każdej klatki z poprawką
kąta. Odtwarzanie w czasie rzeczywistym przez prawdziwą `LiveTranslationSession`
i `OverlayWindow`, Mock 500 ms, kopia bazy, tryb zakrywania przy oryginale. „Przed” = kod wydania
0.5.0; „po” = zakres z dwóch przebiegów końcowego kodu (`po24`, `po25`; `prolog-ruch` tylko
`po25`; pomiary jednego nagrania różnią się między przebiegami). Metryki ponownie przeliczone po poprawce analizatora (porównanie tekstów
bez ikony klawisza — wcześniej poprawnie pokazany „Tab Items” liczył się jako zgubiony).

| Nagranie / metryka | 0.5.0 | po |
|---|---:|---:|
| pokój: pokrycie przy ruchu kamery | 3,5% | 76,4–77,6% |
| pokój: pokrycie HUD przy ruchu kamery | 3,6% | 80,5–81,8% |
| pokój: tekst ruchomy (na ścianie) przy ruchu kamery | 1,7% | 15,6% |
| pokój: dziury w tłumaczeniu obecnego napisu | 61 s | 3,5–6,8 s |
| pokój: nieaktualne tłumaczenie | 19,7 s | 9,1–9,7 s |
| pokój: mediana opóźnienia pojawienia | 3,1 s | 0,26–0,31 s |
| pokój: największy błąd położenia | 236 px | 37,7 px |
| pokój: błąd brzegu łatki w ruchu (mediana / p90) | 20,1 / 43,6 | 8,0–8,2 / 39–41 |
| prolog-intro: pokrycie przy ruchu kamery | 8,2% | 45,9–47,1% |
| prolog-intro: pokrycie HUD przy ruchu kamery | 14,4% | 80,6–82,6% |
| prolog-intro: błąd brzegu łatki w ruchu (mediana / p90) | 27,8 / 92,5 | 10,0–10,9 / 40–44 |
| prolog-ruch: pokrycie | 51,5% | 90,3% |
| prolog-ruch: nieaktualne tłumaczenie | 104,5 s | 19,6 s |
| prolog-ruch: p90 błędu położenia | 131,9 px | 3,5 px |

**Zmiany.**

- `GlyphTrack`/`GlyphTracker`: wzorzec punktów liter i obrysu z łatki (krok zgrubny zależny od
  grubości kreski, `Stroke = 2·pole/obwód`), koszt SAD z przewidywaniem ruchu i trzema
  kandydatami, odrzucenie dopasowań wieloznacznych, kontrola kontrastu liter wobec obrysu albo
  pierścienia tła, dowód zniknięcia liter (`LettersGone`). `GlyphCoverBuilder.Refill` odświeża
  łatkę w nowym miejscu.
- Sesja: śledzenie na każdej klatce (także kontrolnej w trakcie OCR, równolegle na blokach);
  trwający ruch przy śledzeniu nie unieważnia sceny; OCR w ruchu co 0,9 s; nowy blok z klatki
  w ruchu szukany na świeżej klatce i pokazywany w nowym miejscu; migawka ramek z chwili
  przechwycenia (blok przesunięty w trakcie OCR nie wraca na stare miejsce).
- `GraphicsCaptureSource`: Windows Graphics Capture w trwającym ruchu (sesja zamykana po
  2 s spokoju) albo na stałe, gdy okno gry nie wspiera PrintWindow (zamiast zrzutu ekranu, przy
  którym śledzenie było wyłączone — tak było w porannej sesji gry 2026-10-06 na pełnym ekranie;
  MotionLab `--capture wgc`: przechwycenie 4K ok. 18 ms, metryki ruchu jak przy PrintWindow), pełna klatka i wycinki przez teksturę staging; szybkie śledzenie na wycinkach
  budzone nową klatką WGC, do 30×/s, tylko odświeżające. Koszt: pełna klatka 4K ok. 18–22 ms (PrintWindow
  ok. 51 ms), szybkie śledzenie ok. 2,5 ms.
- `OcrGeometry.Unrotate`: ramki słów przy `TextAngle` obracane wokół środka obrazu (bez tego
  przesunięte nawet o ponad 100 px). `OcrBands`: pusty wynik Windows OCR obrazu od ok. 1,4 Mpx
  (w próbce 178 klatek co 8.–9. klatka pokoju) ponawiany w 2, potem 4 pasach (po nieudanym
  ponowieniu następne najwcześniej po 2 s); w próbce odzyskał tekst we
  wszystkich pustych klatkach pokoju.
- `TextBlockSplitter`: blok zlepiający napisy obecne na swoich miejscach dzielony z powrotem.
  `CorpusIdentity` bez ikony klawisza. Nowy tekst spoza korpusu po dwóch zgodnych odczytach.
  `live.ignoreRegions` w profilu (Escape Academy: zegar poziomu).
- Krój: rozmiar i grubość trzymane per element (`GameTextElement.StyleAscent`, zmiana dopiero
  przy wysokości liter ±8%, innym kolorze albo obrysie), grubość wspólna dla klasy stylu
  (`OverlayFonts.ClassWeight`); w pokoju zmian kroju/rozmiaru elementów ok. 45 → 4.

**Regresje.** 1732 testy (1286 Core + 280 Infrastructure + 166 CorpusTool), build bez ostrzeżeń.
SceneReplay na końcowym kodzie: displayed/inflight/noisy/aba/churn/stop — stary usunięty po
34 ms; local-occlusion* — 0 strat menu; moving-text/position-jitter — błąd 0 px; hud-motion* — 0
strat HUD; ocr-timing — mediana 246 ms; typing/typing-nohold — 2 zapytania; stale-* (syntetyczne
i z grafiką gry) — usunięcie po 204–280 ms (stale-fade 554–558 ms), stale-dim i stale-present-junk
bez zniknięcia, jak oczekiwano. A/B: z WGC otwartym przez całą sesję zmiana statycznej sceny
była w 2 z 3 przebiegów widziana o 17–45 ms później (stop 33/52/80 ms vs 35/35/35 ms bez WGC;
churn 34/75/68 vs 33/34/34 ms), a z pełnymi klatkami WGC także na stojącym obrazie o ok.
170–200 ms później — stąd WGC tylko w trwającym ruchu;
po tej zmianie A/B bez różnicy (stop 76/34/34 vs 67/34/34 ms — szum testu).
Raporty: `GTO Diagnostics\20261006-ruch` (`baza`, `po1`–`po25`, `regresje-*`).

**Otwarte / ryzyka.** (1) Nikt jeszcze nie oglądał tej rundy w grze na żywo; nagrania mają
10 kl./s, więc korzyść z odświeżania do 30×/s w prawdziwej grze nie jest zmierzona. (2) WGC
w grach: wpływ otwartej sesji na opóźnienie obrazu gry, HDR, wyłączny pełny ekran, Windows 10 —
kierunek 26. (3) Tekst ruchomy na ścianie przy podchodzeniu (skala, perspektywa) nadal ginie —
kierunek 25. (4) Śledzenie działa tylko dla bloków z łatką (tryb zakrywania). (5) Fałszywe
„miganie” w analizatorze przy prawdzie sklejającej wiersze HUD — kierunek 28.
