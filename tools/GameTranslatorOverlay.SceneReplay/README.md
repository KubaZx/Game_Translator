# SceneReplay

Lokalny, powtarzalny pomiar rzeczywistej `LiveTranslationSession`: przechwytuje wyłącznie własne okno WPF, używa Mock (domyślnie 2000 ms) i świeżego cache w pamięci. Nie czyta ustawień, kluczy ani cache użytkownika; wszystkie żądania HTTP są blokowane (wyjątek: tryb `openai-local` łączy się wyłącznie ze wskazanym serwerem modelu na tym komputerze, bez klucza — opis na końcu). Nie obsługuje okna gry. Nie zapisuje pikseli ani treści OCR.

```
dotnet run --project tools/GameTranslatorOverlay.SceneReplay -c Release -- --output C:\existing-folder\new-report.jsonl --scenario displayed
dotnet run --project tools/GameTranslatorOverlay.SceneReplay -c Release -- --output C:\existing-folder\new-report.jsonl --scenario inflight --ocr windows
```

`displayed` czeka na pokazanie Inspect, zmienia tło i tekst na opis, mierzy usunięcie starego bloku i pojawienie nowego. `inflight` dodatkowo zmienia scenę 250 ms po OCR opisu, podczas symulowanego opóźnienia tłumacza. Śledzi, czy spóźniony opis trafia do callbacków. Każdy wariant obserwuje jeszcze 3 s po docelowym wyniku.

Domyślny `--ocr scripted` wybiera scenę z mediany koloru przechwyconej klatki; zwraca z góry ustalony tekst i prostokąt. Dzięki temu test cyklu życia nie zależy od jakości OCR. `--ocr windows` używa rzeczywistego Windows OCR; wyniki mogą się różnić. Tło ma kolejne jasności 20, 38 i 56 (zmiana 18): wywołuje globalne cięcie według zwykłego progu, bez polegania na progu mocnego ruchu 25. Oba tryby używają prawdziwego capture i nie czytają stanu okna jako odpowiedzi OCR.

JSONL zawiera czas monotoniczny, zdarzenia zmiany sceny, liczby oraz booleany. Punktem odniesienia jest zdarzenie WPF Rendering po zmianie własnej sceny, a końcem pomiaru callback sesji. To nie jest pomiar faktycznej prezentacji nakładki ani opóźnienia w grze. Narzędzie nie otwiera okna nakładki; odtwarza semantykę Clear/Hide/Blocks z MainWindow. Scenariusz inflight oznacza zmianę po OCR, a nie bezpośredni pomiar wejścia do dostawcy.

Plik musi być nowy, a katalog istnieć. Kod wyjścia 0 oznacza ukończony pomiar; regresje są wartościami w summary, nie błędem samego narzędzia. Niezerowy kod oznacza m.in. timeout, brak OCR, nieudane zakończenie lub screen fallback. Screen fallback unieważnia pomiar izolacji. Okno otwiera się bez przejmowania aktywacji i zamyka po próbie; timeouty ograniczają działanie.

Dodatkowy --scenario noisy wymaga --ocr scripted: nowa scena pozostaje wizualnie taka sama jak displayed, ale OCR celowo zwraca zaszumiony opis w tym samym prostokącie co Inspect. Fixture przechodzi JunkFilter, ma ReadingQuality <0.9 i podobieństwo <0.5; wyniki kontroli zapisuje header. Przez 8 s od pierwszego OCR opisu mierzy podtrzymywanie starego bloku mimo cięcia sceny. To sztuczny wynik OCR, nie pomiar jakości Windows OCR. Summary podaje również liczbę ponownie użytych bloków i obecność starego napisu na końcu.


Scenariusze opóźnień (tylko scripted OCR):

- --scenario aba: po OCR Archive zmienia scenę na Garden po 650 ms, a po kolejnych 650 ms wraca do Archive; zapisuje rzeczywisty czas powrotu i liczbę zakończonych zapytań Mock (najwyżej 3 różne teksty łącznie z Inspect). Powrót powinien dołączyć do pracy w toku lub skorzystać z cache.
- --scenario churn: sześć kolejnych opisów zmienia się co 650 ms, ostatni pozostaje stabilny. Raport wskazuje sceny rozpoznane, nieaktualne sceny opublikowane w callbackach, czas świeżego wyniku końcowego i liczbę Mock (górna granica 7 różnych tekstów z Inspect). Brak nieaktualnych callbacków potwierdza brak pokazywania kolejki starych wyników, ale sam nie dowodzi, że dostawca nie dostał zbędnej pracy.

Limit dwóch równoległych operacji jest badany deterministycznie w BoundedTranslationWorkTests, nie wyprowadzany z liczby zakończonych żądań w tej sondzie. JSONL nie zawiera tekstów, a nowe sceny są z góry zdefiniowane; żaden scenariusz nie generuje kolejki bez ograniczenia. Po wyniku końcowym obserwacja trwa jeszcze 3 s. Churn rozszerza barwy scen do 20–128; w tym wariancie stały biały pas chroni PrintWindow przed heurystyką pustej klatki. Pozostałe warianty zachowują wcześniejszy szary pas dla porównywalności baseline.


--scenario stop (tylko scripted): po pokazaniu Inspect i OCR Archive odczekuje 250 ms, zmienia scenę na Garden, czeka na jego OCR i kolejne 100 ms. Warunkiem ważności próby są dwie aktywne rezerwacje znaków dla Archive+Garden i tylko jedno zakończone zapytanie (Inspect). Następnie zatrzymuje prawdziwą sesję i czeka na Completion maksymalnie 10 s. Raport zapisuje shutdownMs, postStopUpdateCallbacks, reservedApiCharactersAfterCompletion oraz liczbę zakończonych Mock. Wymagane: zero rezerwacji, zero callbacków po żądaniu Stop, nadal tylko jedno zakończone Mock. Dodatkowe 100 ms po Completion pozwala zaobserwować późne callbacki. Niespełnienie warunków lub timeout daje niezerowy kod wyjścia. Ten wariant wymaga nowej wersji Core z rezerwacjami; pozostałe warianty nadal mogą działać z zachowanymi starszymi DLL baseline.


Scenariusze małych zmian odczytu w osobnym LocalReadingReplay.cs:

- --scenario local-reading automatycznie używa rzeczywistego Windows OCR. Po pokazaniu The door is locked zmienia wyłącznie lokalny panel (około 4,5% okna) i jego tekst na The door is open. Globalne tło pozostaje bez zmian. Przez 12 s obserwuje domyślne odczyty (heartbeat 4 s); ważny pomiar wymaga przynajmniej 3 odczytów B oraz braku kolejnych sceneCut.
- --scenario reading-jitter automatycznie używa jawnie syntetycznego OCR. Fizyczny napis Level 20 nigdy się nie zmienia. Po pierwszym pokazaniu A, OCR zwraca B,A,B,A, potem stale A (B oznacza Level 21). Narzędziowy heartbeat wynosi 600 ms, obserwacja 12 s. Ten test bada sumowanie niekolejnych wariantów OCR, nie jakość Windows OCR. Geometria syntetycznego odczytu pochodzi z prostokąta białych glifów w przechwyconej bitmapie, identycznie dla pełnego okna i ROI. Treść A/B nadal jest z góry zadanym wynikiem testowym, bez odczytu stanu UI.

Oba warianty używają własnego okna bez aktywacji, Mock 2 s, świeżego prywatnego cache i zablokowanego HTTP. Raport zawiera observedA/B, callbacki A/B, firstBReadyMs, rzeczywiste flagi sceneCut/clear/hide oraz liczniki Mock. Pomiary odnoszą się do callbacków sesji. Dla reading-jitter pojawienie się B jest błędem, a dla local-reading oczekiwanym nowym wynikiem. Kod 0 oznacza poprawnie wykonany pomiar; desiredReadingPublished wskazuje rezultat. Niezerowy kod oznacza m.in. za mało rozpoznań docelowego wariantu, globalne cięcie, fallback lub timeout. JSONL nie zapisuje rozpoznanych tekstów ani obrazów.


--scenario reading-whiff to dodatkowy wariant tego samego statycznego okna i jawnie syntetycznego OCR. Po początkowym A podaje B, pusty wynik, B, A, potem A. Deklarowany heartbeat wynosi 500 ms. Pomiar wymaga co najmniej dwóch B i jednego pustego OCR; pojawienie się B w nakładce oznacza błędne zsumowanie odczytów rozdzielonych pustym wynikiem. Pola observedEmpty, callbacki B i Mock pozwalają sprawdzić reset potwierdzeń w prawdziwej pętli sesji.


Geometria reading-jitter/reading-whiff: FindWhiteBox uwzględnia piksele, których wszystkie kanały RGB wynoszą przynajmniej 200. Szary pas referencyjny i tło są ciemniejsze, więc wyznacza wyłącznie prostokąt fizycznych białych glifów Level 20. Współrzędne są lokalne dla dostarczonej bitmapy; przesunięcie ROI i skalę nakłada istniejąca sesja. Brak glifów jest jawnym błędem fixture. Pełna klatka oraz wycinek używają tej samej metody. Po tej zmianie geometrii porównanie wymaga ponownego uruchomienia baseline jitter/whiff z nową sondą i zachowanymi starszymi DLL aplikacji.


Parametry pomiaru czasu: --provider-delay-ms przyjmuje całkowite 0–5000 (domyślnie 2000) i ustawia rzeczywisty Delay dostawcy Mock. --phase-ms przyjmuje całkowite 0–200 (domyślnie 0); displayed/inflight odczekują ten czas po InitialDisplayed, przed pierwszą zmianą na scenę 2. Header zapisuje oba ustawienia. Summary initialDisplayedToScene2Ms obejmuje także dyspozytor/WPF Rendering i pokazuje rzeczywisty odstęp od callbacku początkowego do sceny 2. Dotychczasowe wywołania bez opcji zachowują ustawienia 2000/0.

Niezerowa faza jest dostępna tylko dla displayed/inflight oraz stale-* (tam przesuwa mierzoną zmianę, opis niżej). Stop odrzuca opóźnienie inne niż 2000 lub fazę inną niż 0, bo ma stałe założenie dwóch oczekujących odpowiedzi. local-reading, reading-jitter i reading-whiff odrzucają samo podanie którejkolwiek nowej opcji, nawet z wartością domyślną. Przy zmianie Delay w ABA/churn/inflight trzeba uwzględnić, czy odpowiedź rzeczywiście nadal trwa podczas zmiany sceny; np. inflight przy Delay <250 ms może już nie przerywać oczekiwania.

Każdy update z diagnostyką zawiera numeric captureMs, ocrMs i translateMs. Warianty displayed/inflight/aba/churn/stop/noisy (`Program.cs`) i stale-* zapisują też `glyphCoverMs` (budowa łatek z wypełnionymi literami w tle, od startu zadania do końca; 0 = łatki z poprzedniego przebiegu) i `glyphCoverWaitMs` (ile przetworzenie czekało na łatki po tłumaczeniu — to, co łatki dokładają do „zmiana → napis”). Sesja w SceneReplay buduje łatki zawsze (`BuildGlyphCovers` domyślnie true), choć narzędzie nie rysuje nakładki. Summary middleFirstOcrCompletedMs mierzy czas od Rendering sceny 2 do pierwszego zakończonego OCR rozpoznającego jej opis Archive; nie jest czasem rozpoczęcia OCR ani faktycznej prezentacji nakładki. middleReadyMs obejmuje także dalsze przetwarzanie/tłumaczenie. Gdy właściwego OCR/wyniku nie ma, odpowiedni pomiar pozostaje null.

Scenariusze pozycji w osobnym MovingTextReplay.cs (wejście Run(output, scenario)):

- moving-text: stały napis na niezmiennym tle i panelu około 4,6% okna przesuwa się 12 razy po 3 fizyczne piksele. Przesunięcie WPF uwzględnia DPI; pozycja odniesienia pochodzi z białych glifów w osobnym przechwyceniu własnego okna, nie z zadanych współrzędnych DIP. Każdy krok jest utrzymany do dwóch callbacków z diagnostyką; pierwszy zostaje odrzucony jako możliwy wynik klatki przechwyconej przed zmianą. Raport mierzy błąd pozycji po zatrzymaniu kroku, a nie opóźnienie podczas ciągłego ruchu.
- position-jitter: fizyczny napis stoi nieruchomo. Po początkowym pokazaniu syntetyczny OCR dodaje do geometrii naprzemiennie (+1,-1) oraz (-1,+1) piksela w osiach X/Y. Zapisuje obie polaryzacje szumu i każdą zmianę pozycji callbacku; oczekiwane positionChangesAtRest=0.

Oba warianty używają prawdziwego capture, lecz jawnie syntetycznej treści OCR. Box OCR jest wyliczany wyłącznie z glifów dostarczonej bitmapy, zarówno dla pełnej klatki, jak i ROI; skalowanie i przesunięcie wycinka stosuje sesja. JSONL podaje sourceBoxPx i callbackBoxWindowRelativePx w fizycznych pikselach, maksymalny błąd, skok, kolejne kroki z przyklejoną pozycją, clear/hide/sceneCut, liczbę kluczy oraz Mock. Brak bloku jest osobną próbką emptySamples, nie zerowym błędem. Mock ma stałe 2 s, heartbeat narzędzia 600 ms, cache jest świeży i prywatny, HTTP zablokowany. Brak renderowania właściwej nakładki. Kod 0 oznacza ważne ukończenie fixture; desiredPositionBehavior podaje wynik (ruch: błąd każdej osi do 2 px; szum: zero zmian pozycji; oba: brak zniknięć i stały klucz). Nie jest to gwarancja zachowania wszystkich gier ani pomiar Windows OCR.

Scenariusz `ocr-timing` w `OcrTimingReplay.cs` mierzy narzut oczekiwania na OCR i kontroli sceny na statycznym własnym oknie o żądanym rozmiarze 1920×1080 px. OCR jest jawnie syntetyczny: znajduje trzy pasy glifów w przechwyconych pikselach i dodaje `--ocr-delay-ms 100..600` (domyślnie 175 ms). Mock ma tutaj **0 ms**. To nie jest pomiar szybkości ani jakości Windows OCR. Scenariusz odrzuca `--provider-delay-ms`, `--phase-ms` oraz `--ocr windows`; `--ocr-delay-ms` jest dostępne wyłącznie tutaj.

Po jednym przebiegu rozgrzewającym zbiera osiem próbek, przy heartbeat 500 ms. `fixtureValid` wymaga kompletu próbek, dodatnich metryk operacji OCR, trzech linii i wyświetlonych bloków oraz braku fallback, niespodziewanego stopu, clear/hide, sceneCut, whiff i częściowego OCR w pomiarze. Niespełnienie warunków daje niezerowy exit. Summary rozdziela `ocrOperationMedianMs`, `ocrWrappedMedianMs`, medianę różnicy tych czasów (`ocrOverheadMedianMs`) oraz liczbę i czas kontroli sceny (`ocrSceneChecksMedian`, `ocrSceneCheckMedianMs`). Są to czasy operacji i callbacków, nie prezentacji nakładki; ten wariant nie ma pola `expectedBehavior`.

```
dotnet run --project tools/GameTranslatorOverlay.SceneReplay -c Release -- --output C:\existing-folder\ocr-175.jsonl --scenario ocr-timing --ocr-delay-ms 175
```

Scenariusze `local-occlusion`, `local-occlusion-hover` i `local-occlusion-inflight` w `LocalOcclusionReplay.cs` automatycznie używają **rzeczywistego Windows OCR** i stałego Mock **2000 ms**. Własne okno zawiera niezmienne etykiety Inventory panel / Journal panel i lokalny panel zajmujący około 6,5% powierzchni. Zmienia się tylko ten panel; globalne tło i menu pozostają stałe. Wszystkie trzy warianty odrzucają jawne `--provider-delay-ms`, `--phase-ms`, `--ocr-delay-ms` i `--ocr scripted`.

- `local-occlusion`: po pokazaniu menu i Inspect panel przykrywa Inspect, a nowy opis pojawia się 84 DIP wyżej, poza jego starym boxem. Oczekiwane: Inspect znika przed gotowym tłumaczeniem opisu i nie wraca; menu pozostaje w każdym wizualnym callbacku.
- `local-occlusion-hover`: zmienia samo tło pod nadal obecnym Inspect. Oczekiwane: Inspect i oba elementy menu pozostają przez cały pomiar.
- `local-occlusion-inflight`: zmienia panel 250 ms po pierwszym OCR Inspect i menu, przed pierwszym wynikiem Mock. `inflightPreconditionMet` wymaga rzeczywistego odstępu od OCR do Rendering wynoszącego co najmniej 250 i mniej niż 2000 ms, zera zakończonych żądań/rozliczonych znaków oraz dodatniej rezerwacji znaków. Nie czeka na początkowy callback. Oczekiwane: `oldInspectCallbacksAfterChange=0`, nowy opis pokazany wraz z menu, które pozostaje od tego wyniku.

Obserwacja trwa 12 s. `fixtureValid` wymaga co najmniej dwóch odczytów docelowego tekstu (Inspect dla hover, opisu dla pozostałych), braku fallback, niespodziewanego stopu i globalnego sceneCut, a dla wariantów z opisem także co najmniej 3 s obserwacji po jego pokazaniu. Inflight dodatkowo wymaga opisanego warunku rozpoczęcia pomiaru. **Exit 0 oznacza ważny ukończony pomiar; `expectedBehavior` określa wynik regresji.** Poza kryteriami wariantu wynik wymaga `clearCallbacks=0`, `hideCallbacks=0` i zachowania menu. Summary zapisuje m.in. `firstInspectRemovedMs`, `firstDescriptionOcrMs`, `firstDescriptionReadyMs`, `inspectReturnUpdates`, `menuLossUpdates` oraz warunek i liczniki inflight. Oba rodzaje sond używają świeżego prywatnego cache, blokują HTTP i zapisują JSONL bez obrazów i treści OCR; zamknięcie sesji nie liczy się jako usunięcie starego napisu.

## Stałe menu podczas ruchu tła

`hud-motion`, `hud-motion-whiff` i `hud-motion-small-whiff` w `StaticHudReplay.cs`
przechwytują wyłącznie własne okno 1200×760 DIP. Dwie etykiety są na stałym,
nieprzezroczystym panelu; napis w świecie przesuwa się, a tło zmienia jasność co
71 ms. Po fazie stabilnego menu następuje zamiana jednej etykiety (2200 ms),
usunięcie obu (1400 ms), zatrzymanie tła i nowy końcowy opis. Początek i koniec
korzystają z rzeczywistego Windows OCR. Mock ma stałe 200 ms, cache jest prywatny
w pamięci, HTTP zablokowane; raport nie zapisuje obrazów ani treści OCR.

- `hud-motion`: rzeczywisty Windows OCR przez cały test; stałe menu obserwowane 4200 ms.
- `hud-motion-whiff`: po początkowym wyniku wymusza puste odczyty w fazach ruchu.
  Pozwala sprawdzić zachowanie menu także przy błędzie OCR i globalnym cięciu.
- `hud-motion-small-whiff`: zmienia tylko pas o szerokości 400 DIP (około 1/3 okna),
  również wymusza puste odczyty. Faza menu trwa 10500 ms i musi zawierać przynajmniej
  trzy ukończone przebiegi bez `SceneCut`. To sprawdza wygaśnięcie po wielu brakach OCR
  przy ruchu zbyt małym dla progu globalnego cięcia.

Narzędzie odrzuca opcje opóźnień oraz `--ocr scripted`. Przykład:

```powershell
dotnet run --project tools/GameTranslatorOverlay.SceneReplay -c Release -- --scenario hud-motion --output C:\measurements\hud.jsonl
```

`fixtureValid` wymaga początku i końca, wystarczającej liczby zmian tła i odczytów,
braku fallback/stopu, co najmniej 30 próbek stanu podczas pierwszej fazy oraz
obserwacji po końcowym opisie przez przynajmniej sekundę. Warianty z pustymi wynikami
muszą rzeczywiście je wykonać; wariant małego ruchu dodatkowo spełnić opisane warunki
trzech przebiegów. **Exit 0 oznacza ważność próby, nie przejście regresji.**

`expectedBehavior` wymaga zachowania niezmienionych etykiet w każdej aktualizacji
sesji i próbce zegara, usunięcia starej zmienionej etykiety oraz całego usuniętego menu
w ciągu 700 ms i braku ich późniejszego powrotu. `inventoryLossUpdates` oraz
`journalLossUpdates` liczą obserwacje stanu przy callbackach, a `missingHudPolls`
obserwacje zegara w fazach 1–2; nie są liczbą osobnych mignięć. `stablePolls` obejmuje
samą fazę 1. `journalRemovedMs` i `inventoryRemovedMs` mierzą pierwszą aktualizację
bez starego napisu od zdarzenia WPF Rendering. Próby nie rysują fizycznej nakładki
ani nie mierzą DeepL. Wynik nie potwierdza jakości na przezroczystym/animowanym HUD,
przy skalowaniu OCR, zapasowym przechwytywaniu ekranu ani w każdej grze.

## Stary napis po lokalnym zniknięciu etykiety

`stale-junk`, `stale-junk-ghost`, `stale-texture`, `stale-newtext`, `stale-busy`, `stale-fade`,
`stale-dim`, `stale-newdirty`, `stale-present-junk` i `stale-blink` w `StaleLabelReplay.cs`
mierzą, jak długo tłumaczenie etykiety „Inspect” zostaje w callbackach sesji po tym, jak
oryginał zniknął **lokalnie** (bez globalnego cięcia sceny), a w wariantach dim i present-junk
— czy tłumaczenie etykiety, która nadal jest na ekranie, nie znika ani nie mruga. Własne okno ma dokładnie
1500×900 fizycznych pikseli (rozmiar DIP liczony z DPI), 1 jednostka płótna = 1 piksel, więc
etykieta ma rozmiar jak w 4K: glify około 259×70 px. Pole etykiety zajmuje 5% okna.
Mock ma domyślnie **1000 ms** (`--provider-delay-ms` zmienia wartość), cache jest prywatny
w pamięci, HTTP zablokowane. `--phase-ms 0..200` dolicza się do 1500 ms oczekiwania przed
mierzoną zmianą: bez niej zmiana wypada zawsze w tej samej fazie zegara przechwytywania
(6 fps, co ok. 167 ms), więc pojedynczy czas usunięcia nie mówi nic o rozrzucie. Odrzucane:
`--ocr-delay-ms` i niezgodny `--ocr`.

- `stale-junk` (scripted OCR): po zniknięciu etykiety OCR w miejscu starego pola zwraca stały
  śmieciowy odczyt. Header zapisuje, że fixture przechodzi `JunkFilter.IsMeaningful`, ma
  `ReadingQuality` < 0,9 i gorszą o więcej niż 0,1 od „Inspect”, a prawdziwy
  `LiveReadingStabilizer` dwukrotnie zwraca dla niego `Keep`. Box śmieci to oczekiwane pole
  glifów przesunięte o 15% szerokości; pokrycie mniejszego pola jest zapisywane przy każdym
  odczycie i musi wynosić co najmniej 0,5 (warunek `FindOverlapping`; w pomiarach rundy
  2026-10-05 wynosiło 0,848–0,891). Test ścieżki „śmieciowy odczyt podtrzymuje stary blok”.
- `stale-junk-ghost` (scripted OCR): to samo, ale w miejscu bez glifów OCR zwraca cyklicznie
  pusty, pusty, pusty, śmieci. Bada wskrzeszanie ducha (10 s) i liczy powroty starego bloku.
- `stale-texture` (Windows OCR): etykieta znika z teksturowanego tła, bez śmieci, bez nowego
  tekstu. Pole nie jest jednolite (`TextPresenceProbe`), więc test jednolitości nie pomaga;
  od rundy 2026-10-05 sesja ma dowód z `KnownTextAbsenceProbe` (w polu nie zostaje ćwierć
  dawnego kontrastu). Okres łaski bez żadnego dowodu w pikselach bada `--bright-spot-px`.
- `stale-newtext` (Windows OCR): w chwili zniknięcia etykiety w lewym dolnym rogu pojawia się
  „The drawer is open”; kwadrat 96×96 px w prawym górnym rogu stoi nieruchomo. Test samej
  publikacji usunięcia razem z wynikiem dostawcy (bez wycinków animacji). W wariancie real
  Windows OCR czyta nowy tekst raz z przekłamaną literą, więc sesja uruchamia też wycinki
  potwierdzające wariant odczytu wokół nowego tekstu — to też ścieżka (3) z ROADMAP: przed
  poprawką `FullScanSchedule` blok poza tymi wycinkami czekał na pełny skan.
- `stale-busy` (Windows OCR): jak `stale-newtext`, ale kwadrat miga co 200 ms od startu sesji
  (stałe wycinki OCR). Test publikacji usunięcia razem z wynikiem dostawcy i braku pełnego
  skanu przy ciągłych wycinkach. Nowy tekst jest klasyfikowany po frazie „is open”, bo Windows
  OCR na prawdziwej teksturze potrafi przekłamać jedną literę słowa „drawer”.
- `stale-fade` (Windows OCR): etykieta wygasa przez `--fade-ms` (domyślnie 300, zakres
  100–1000; animacja przezroczystości WPF 1 → 0), potem znika. Pomiar od pierwszej klatki
  wygaszania; `staleLifetimeAfterFadeEndMs` liczy od końca animacji. Odczyt „ustalony” liczy się
  tu od końca wygaszania.
- `stale-dim` (Windows OCR): etykieta przygasa do `--dim-percent` (domyślnie 40, zakres 10–90)
  dawnej jasności — wszystkie kanały RGB glifów, obrysu i ikony mnożone przez ten ułamek — i
  **zostaje** na ekranie. Pod etykietą od startu sesji co 200 ms zmienia się nieprzezroczysty
  podkład (szarość 0–12 na przemian z 44–56, poziomy z okresem 2,8 s, więc przechwycenie rzadko
  powtarza wcześniejsze piksele): każda zmiana jest istotna dla detektora i obejmuje blok.
  Wymagane: Windows OCR przeczytał przygaszoną etykietę co najmniej raz (inaczej próba jest
  nieważna). `--dim-percent 25` to wariant poniżej ćwierci kontrastu, w którym sonda nieobecności
  z kolorami sprzed przygaszenia mówi „nieobecny”.
- `stale-present-junk` (scripted OCR): etykieta zostaje, podkład pod nią pulsuje jak w
  `stale-dim`, a podczas pomiaru OCR nad etykietą zwraca na przemian `--junk-run` odczytów
  śmieci (domyślnie 1, zakres 1–3) i jeden odczyt „Inspect”. Domyślnie co drugi przebieg to
  śmieć; dopiero `--junk-run 3` daje trzy chybione przebiegi z rzędu, które na kodzie sprzed
  poprawki wyczerpywały okres łaski.
- `stale-newdirty` (scripted OCR): etykieta znika, a 1500 ms później w tym samym miejscu
  pojawia się prawdziwy napis „Loading…” (U+2026, ten sam styl co etykieta). Między zniknięciem
  a nowym napisem OCR nie zwraca linii. „Loading…” ma `ReadingQuality` 0,875 i jest
  „niepowiązanym, brudniejszym” odczytem względem „Inspect” (header zapisuje te fakty).
  `newReadyAfterShownMs` liczy od pokazania nowego napisu.
- `stale-blink` (Windows OCR): etykieta znika i po 1500 ms wraca. Raport odtwarza obsługę
  paska napisów z MainWindow (`ClearOverlay`, `ClearSubtitle`, `SubtitleText`,
  `PreserveSubtitleLifetime`, czas paska 8 s jak domyślne `SubtitleSeconds`) i sprawdza, czy po
  powrocie etykiety jej tłumaczenie wraca także na pasek (`subtitleOldReturnedAfterSecondStepMs`),
  a nie tylko do bloków.

Geometria scripted OCR pochodzi wyłącznie z pikseli: znacznik #900090 8×8 px w środku
starego pola glifów (rysowany we wszystkich wariantach scripted) wyznacza położenie i skalę także
w wycinku powiększonym 2×; obecność etykiety rozpoznaje liczba białych pikseli w tym polu.
Znacznik zostaje po zniknięciu etykiety, więc ma luminancję (ok. 60) poniżej progu „ćwierci
dawnego kontrastu” białej etykiety (ok. 71) — inaczej sam udawałby resztkę tekstu.
Pierwotny #FF00FF (luminancja ok. 105) odbierał sesji dowód w pikselach; pomiar bazowy
junk powtórzono z nowym znacznikiem na kodzie sprzed poprawki (wyniki w ROADMAP, runda 2026-10-05).
Nowy napis w `stale-newdirty` ma osobny znacznik #007000 (luminancja ok. 66, kolor nieobecny
w teksturze visual-01 — niebieski #1000C0 kolidował z 259 jej pikselami); OCR zwraca
„Loading…” tylko wtedy, gdy ten znacznik i białe glify nowego napisu są w kadrze.

Bez opcji scenariusz rysuje syntetyczną etykietę (Segoe UI Bold 76 px, biały z czarnym
obrysem 8 px, rysowana ikona myszy) na deterministycznej teksturze. Prawdziwe zasoby czyta
**wyłącznie lokalnie** i nigdy ich nie kopiuje ani nie zapisuje:

- `--assets KATALOG` — katalog z `inspect_crop.png` (450×150 px, wycinek etykiety z gry).
  Etykieta jest wycinana maską: białe glify i ikona po lewej, poszerzone o 7 px obrysu;
  reszta wycinka jest przezroczysta.
- `--texture OBRAZ` — dowolny obraz co najmniej 1500×900 px; okno pokazuje jego fragment 1:1.
- `--texture-origin X,Y` — lewy górny róg fragmentu (domyślnie środek obrazu).
- `--bright-spot-px N` (4–14) — biały kwadrat N×N px pod etykietą, wewnątrz starego pola
  glifów, który zostaje po jej zniknięciu. Usuwa dowód w pikselach (`KnownTextAbsenceProbe`
  widzi w polu piksele po stronie jasnego tekstu), więc sesja musi polegać na brakach OCR,
  okresie łaski i pełnym skanie. Prawda referencyjna zapisuje `afterKnownTextAbsent`;
  z plamką musi to być `false`, inaczej próba jest nieważna. Header zapisuje `brightSpotPx`.

```powershell
GameTranslatorOverlay.SceneReplay.exe --scenario stale-busy --output C:\pomiary\busy-1.jsonl `
  --assets C:\lokalne\zasoby --texture C:\lokalne\frame-original.png --texture-origin 1750,560
```

Przed startem sesji narzędzie robi trzy przechwycenia własnego okna (etykieta, stan po
zmianie, znów etykieta; w `stale-newdirty` dodatkowo nowy napis) i zapisuje `ground_truth`:
pole glifów, kolory, czy pole po zniknięciu jest „jednolite” według `TextPresenceProbe`
(musi nie być, inaczej to test dowodu w pikselach), czy `KnownTextAbsenceProbe` z kolorami
sprzed zmiany widzi zniknięcie znanego tekstu (`afterKnownTextAbsent`; w `stale-dim` to
właśnie ta pomyłka sondy), najjaśniejszy piksel pola po zmianie, odsetek zmienionych pikseli
i komórek siatki. Po pierwszym callbacku z „Inspect” czeka 1500 ms (+ `--phase-ms`),
wykonuje zmianę i obserwuje 12 s od niej.

Metryka główna `staleLifetimeMs`: od zdarzenia WPF Rendering zmiany do pierwszego
wizualnego callbacku bez starego bloku; `null` z `oldNotRemovedAfterObservationMs`, gdy nie
zniknął w 12 s. Dodatkowo: `oldReturnTransitions` (powroty), `oldRemovalTransitions`
(zniknięcia; w `stale-dim` i `stale-present-junk` każde to mignięcie napisu, który stoi na
ekranie), `oldVisibleMsAfterChange` (łączny czas widoczności po zmianie), `lastOldRemovedMs`,
`newReadyMs` (stale-newtext, stale-busy, stale-newdirty), `newReadyAfterShownMs`
(stale-newdirty), `oldReturnedAfterSecondStepMs` i `subtitleOldReturnedAfterSecondStepMs`
(stale-blink), `subtitleOldRemovedMs`, `fullOcrCallbacksAfterChange`, `partialOcrCallbacksAfterChange`,
`maxFullOcrGapMsIncludingEdges` (najdłuższa przerwa bez pełnego OCR, łącznie z odcinkiem od
zmiany i do końca), `firstRetainedCallbackAfterChangeMs`, `reusedBlocks`, liczniki odczytów
OCR po zmianie i `mockRequestsAfterChange`. Odczyt „ustalony” to OCR rozpoczęty co najmniej
200 ms po zmianie (w `stale-fade` po końcu wygaszania, w `stale-blink` przed powrotem etykiety).

`fixtureValid` wymaga zgodnej prawdy referencyjnej (1500×900, bez fallbacku, glify przed,
brak glifów po — poza dim i present-junk — pole nie jednolite, z `--bright-spot-px` także
`afterKnownTextAbsent=false`), callbacku z Inspect, zero globalnych `SceneCut` po zmianie oraz:
dla junk co najmniej dwóch odczytów śmieci (ghost: jednego) z pokryciem ≥ 0,5 i braku
ustalonego „Inspect”; dla stale-newtext i stale-busy odczytu nowego tekstu, a dla stale-busy
także ciągłej animacji; dla stale-fade braku ustalonego „Inspect” po końcu wygaszania; dla
stale-dim co najmniej jednego ustalonego odczytu przygaszonej etykiety i pracującego podkładu;
dla stale-present-junk co najmniej dwóch śmieci, jednego „Inspect” i pracującego podkładu; dla
stale-newdirty odczytu „Loading…” bez ustalonego „Inspect”; dla stale-blink braku ustalonego
„Inspect” przed powrotem i odczytu po powrocie. **Exit 0 oznacza ważną próbę; `expectedBehavior` jest
wynikiem regresji:** stary napis usunięty, bez powrotu (w stale-newtext, stale-busy i
stale-newdirty także pokazany nowy tekst); w stale-dim i stale-present-junk napis ani razu
nie zniknął; w stale-blink napis zniknął i wrócił — w blokach i na pasku. JSONL nie
zawiera pikseli ani treści OCR. To callbacki sesji, nie fizyczna nakładka ani zachowanie
każdej gry; wariant scripted bada logikę sesji, nie to, czy Windows OCR naprawdę czyta śmieci.

## Kwestia pisana literami

`typing` i `typing-nohold` w `TypingReplay.cs` (scripted OCR): własne okno wpisuje dwie
syntetyczne linie dialogu (35 ms na znak, 450 ms pauzy po `.`, `,`, `!`, `?`), każda zostaje
na ekranie 3 s, potem znika. Sesja ma aktywny profil z syntetycznym korpusem (obie linie i trzy
teksty-zmyłki) w katalogu tymczasowym, Mock 0 ms, tryb prywatny i zablokowane HTTP.
OCR czyta wpisaną część z pikseli: białe glify wyznaczają zasięg tuszu, a tło koduje współrzędną
x w kanałach RGB, więc wycinek OCR jest czytany od właściwej litery (fragment, gdy wycinek nie
obejmuje początku linii). `typing` włącza `HoldTypingPrefixes` (jak aplikacja w trybie
zakrywania), `typing-nohold` je wyłącza dla porównania. Odrzucane: `--provider-delay-ms`,
`--phase-ms`, `--ocr-delay-ms`, `--ocr windows` i opcje stale-*.

Raport na linię: `shownAfterTypingEndMs` (od zdarzenia końca pisania, czyli po pauzie 450 ms
za ostatnią kropką; wartość ujemna = napis pojawił się jeszcze w tej pauzie),
`shownWhileTypingUpdates`, `partialTranslationUpdates`, `textChanges`; w podsumowaniu
`fixtureValid`, `holdTypingPrefixes` i zapytania do Mocka. `fixtureValid` wymaga w każdej linii
końca pisania, co najmniej jednego odczytu niepełnej linii i pokazanego tłumaczenia, zera
wycinków bez odczytanej współrzędnej (`undecodedCrops`) i braku fallbacku. Nieważna próba albo
nieudane zamknięcie sesji lub okna kończy się kodem 4, błąd albo timeout w trakcie próby —
kodem 3. Zdarzenia `ocr` zapisują długości odczytów, nie ich treść; zdarzenia `update` zapisują
pokazane tłumaczenie Mocka syntetycznych linii.

## Szybka panorama z cięciem sceny

`pan-cut` w `PanCutReplay.cs` (scripted OCR, Mock 200 ms, prywatny cache w pamięci, HTTP
zablokowane): własne okno 1200×700 DIP pokazuje mocno teksturowany „świat” (losowe kafle
24 DIP, jasność 24–175) z jasnym napisem na ciemnej tabliczce. Po pokazaniu tłumaczenia i 1,5 s
spokoju cały świat przeskakuje w bok o 40, a potem sześć razy o 110 fizycznych pikseli, z przerwą
450 ms po każdym skoku. Przerwa sprawia, że każdy skok zaczyna się od próbki bez ruchu, więc sesja
widzi go jako globalne cięcie sceny (zmiana ≥ 55% siatki przy jednej próbce ruchu), choć napis
po prostu jedzie z kamerą. OCR zwraca stały tekst z prostokątem białych glifów przechwyconej
bitmapy (pełna klatka albo wycinek), prawda to niezależne przechwycenie własnego okna po każdym
skoku.

Raport: `removalTransitionsDuringPan` (ile razy blok zniknął z callbacków w trakcie panoramy),
`firstRemovalAfterPanStartMs`, `trackingLossUpdates` (aktualizacje „napis zniknął albo odjechał”),
`clearCallbacks`, `jumpsDisplayedAtEnd`/`jumpsMissingAtEnd` (stan na końcu przerwy po skoku) i
`maxPositionErrorAtJumpEndPx`. `fixtureValid` wymaga wszystkich skoków o zadaną odległość (±1 px),
braku fallbacku i stopu; **exit 0 oznacza ważną próbę, a `desiredTrackingBehavior` wynik**: blok ani
razu nie zniknął, jest widoczny po każdym skoku z błędem położenia ≤ 3 px, bez czyszczenia nakładki.
Skoki 110 px przekraczają zwykły promień śledzenia (72 px), więc blok przechodzi przez cięcie tylko
wtedy, gdy zachował prędkość i flagę ruchu z poprzedniego skoku. Nie mierzy fizycznej nakładki ani
ciągłego ruchu w grze.

## Napis jadący chwilowo zgubiony przez śledzenie

`motion-occlusion` i `motion-blur` w `MotionLossReplay.cs` (scripted OCR, Mock 200 ms, prywatny cache w pamięci, HTTP
zablokowane): własne okno 1200×700 DIP z teksturowanym „światem” i tabliczką jak w `pan-cut`, ale panorama jest ciągła —
64 kroki po 12 fizycznych pikseli co 50 ms (ok. 240 px/s, w prawo), więc po pierwszej próbce ruchu sesja widzi trwający
ruch, a nie cięcie sceny. Trzy razy (kroki 18, 34 i 50) przez 150 ms, czyli na 1–2 przechwycenia, napis traci wygląd,
który zna śledzenie: w `motion-occlusion` środek napisu zasłania nieprzezroczysta belka 100 DIP (jak słup albo postać
przed tabliczką), w `motion-blur` napis jest rozmyty (`BlurEffect`, promień 4). OCR zwraca stały tekst z prostokątem
białych glifów (wszystkie kanały ≥ 200) pełnej klatki, wycinka albo wycinka lokalnego odczytu: przy belce glify są po
obu stronach, więc prostokąt obejmuje cały napis; przy rozmyciu białych pikseli może nie być i odczyt nic nie znajduje.
`--lost-read on|off` (domyślnie `on`) ustawia `LiveSessionOptions.ReadLostBlocks` — `off` to zachowanie sprzed lokalnego
odczytu zgubionego bloku i służy za pomiar „przed” na tym samym kodzie.

Raport: `removalTransitionsDuringPan` (zniknięcia bloku z callbacków w trakcie panoramy), `firstRemovalAfterPanStartMs`,
`trackingLossUpdates` (aktualizacje „napis zniknął albo odjechał”), `missingMsTotal` i `longestGapMs` (łączny i najdłuższy
czas bez bloku), `visibleAfterGlitch` i `maxPositionErrorAfterGlitchPx` (stan 250 ms po końcu każdego zakłócenia wobec
niezależnego przechwycenia własnego okna), `finalPositionErrorPx`, `relocationsDuringGlitch`, `ocrDuringGlitch`,
`lostReads` / `lostRecovered` / `lostReadMs` / `lostReadTotalMs` (liczniki sesji z ostatniej diagnostyki; OCR wycinka
i cały odczyt z budową łatki), `mockProviderRequests`. `fixtureValid` wymaga trzech zakłóceń, co najmniej jednego OCR,
braku fallbacku i stopu; **exit 0 oznacza ważną próbę, a `desiredTrackingBehavior` wynik**: blok ani razu nie zniknął,
bez czyszczenia i ukrycia nakładki, widoczny po każdym zakłóceniu z błędem położenia ≤ 24 px (`panTolerancePx`, dwa
kroki panoramy — callback bywa krok za obrazem) i na końcu, po zatrzymaniu, ≤ 3 px. Pierwsze przechwycenie prawdy przed
startem sesji czasem trafia na zapasowy zrzut ekranu — wtedy próba kończy się kodem 3 i trzeba ją powtórzyć. Nie mierzy
fizycznej nakładki ani prawdziwego rozmycia ruchu w grze.

```
dotnet run --project tools/GameTranslatorOverlay.SceneReplay -c Release -- --output C:\folder\occl-off.jsonl --scenario motion-occlusion --lost-read off
dotnet run --project tools/GameTranslatorOverlay.SceneReplay -c Release -- --output C:\folder\occl-on.jsonl --scenario motion-occlusion
```

## Ostrzeżenie kontroli jakości w bloku nakładki

`quality-mark` i `quality-mark-clean` w `QualityMarkReplay.cs` (scripted OCR): własne okno ze stałym
napisem `Unlock level 20 door`, OCR zwraca ten tekst z prostokątem białych glifów, a zamiast Mocka
działa natychmiastowy dostawca testowy (bez sieci, tryb prywatny, świeży cache w pamięci).
W `quality-mark` dostawca gubi liczbę (`Otwórz drzwi poziomu`), w `quality-mark-clean` ją zachowuje.
Po pierwszym pokazaniu bloku narzędzie obserwuje 4 s przy powtórnym OCR co 500 ms. Odrzucane:
`--provider-delay-ms`, `--phase-ms`, `--ocr-delay-ms` i `--ocr windows`.

Podsumowanie: `flaggedBlocks`/`cleanBlocks` (bloki `LiveDisplayBlock` z flagą `QualityIssues` i bez),
`subtitleUpdates`/`flaggedSubtitles` (`LiveUpdate.SubtitleQualityWarning`), `keys` i `boxes` bloków
(porównanie obu wariantów pokazuje, że flaga nie zmienia kluczy ani stabilizacji), `warningsTotal`
z `TranslationOrchestrator.QualityWarnings` (ta sama para liczona raz mimo kolejnych odczytów)
i `providerRequests` (z problemem: pierwsze tłumaczenie + jedno ponowne, potem wpis ostateczny
z cache). `expectedBehavior` to wynik regresji; kod 4 — za mało odczytów albo bloków (`fixtureValid`).

## Strumieniowanie odpowiedzi modelu

`stream`, `stream-refusal`, `stream-change` i `stream-partition` w `StreamReplay.cs` (zawsze Windows OCR): własne okno pokazuje pięć
syntetycznych linii w osobnych wierszach, po 1,5 s od startu sesji naraz. Zamiast Mocka działa
prawdziwy dostawca — `OpenAiCompatibleTranslationProvider` (adres `http://localhost:5799/v1`,
bez klucza) albo `ClaudeTranslationProvider` — ale jego `HttpClient` ma skryptowany handler
(„strumieniowy Mock”): odpowiada wyłącznie na adres tego dostawcy, nie łączy się z siecią
i tłumaczy każdy tekst jako `PL ` + tekst. Element k odpowiedzi kończy się po
`--first-line-ms + k × --line-ms` od zapytania (domyślnie 900 i 350 ms); z `stream: true`
handler wysyła SSE w formacie dostawcy (OpenAI: `data:` + `choices[0].delta.content` + `[DONE]`;
Anthropic: `message_start` / `content_block_*` / `message_delta` / `message_stop`), każdy element
w dwóch kawałkach, a bez strumienia oddaje cały JSON w chwili ostatniego elementu. Koszt
odpowiedzi jest więc ten sam, różni się tylko moment pojawienia się linii.

```
dotnet run --project tools/GameTranslatorOverlay.SceneReplay -c Release -- --output C:\folder\stream-on.jsonl --scenario stream --stream on --stream-provider llm
dotnet run --project tools/GameTranslatorOverlay.SceneReplay -c Release -- --output C:\folder\stream-off.jsonl --scenario stream --stream off --stream-provider claude
dotnet run --project tools/GameTranslatorOverlay.SceneReplay -c Release -- --output C:\folder\refusal.jsonl --scenario stream-refusal --stream-provider claude
```

`--stream on|off` ustawia `AppSettings.StreamLlmResponses` (domyślnie on), `--stream-provider
llm|claude` wybiera dostawcę (domyślnie llm). `stream-refusal` kończy strumień odmową
(`finish_reason: content_filter` albo `stop_reason: refusal`) i wymaga `--stream on`: mierzy, czy
wcześnie pokazane linie zostają zdjęte. `stream-change` podmienia w tych samych miejscach pięć linii
na inne (ze słowem „garden”) `--first-line-ms` + 100 ms po pierwszym zapytaniu, czyli w trakcie odpowiedzi;
działa też z `--stream off` jako porównanie. Mierzy, czy po zmianie pojawiają się jeszcze wczesne
linie starej sceny (zdarzenia `update` z `provisional: true` po `change_started`), jak długo widać
stare bloki (`maxOldShownAfterChange`, `lastOldShownAfterChangeMs`) i kiedy pojawia się nowa scena
(`changedFirstBlockMs`, `changedAllBlocksMs`, `mixedOldAndChangedUpdates`, `oldUpdatesAfterChangedAll`). Opcje Mocka (`--provider-delay-ms`, `--phase-ms`,
`--ocr-delay-ms`, `--ocr scripted`) są tu odrzucane, a opcje strumienia — w pozostałych scenariuszach.

`stream-partition` (wymaga `--stream on`, dostawca `llm` albo `claude`) dzieli odpowiedź na dwie
partie: dostawca dostaje `MaxBatchSize = 3`, więc pięć linii idzie jako partia 3 + partia 2. Dla
partii dwóch linii skryptowany model zwraca tylko jeden element (po `--line-ms` zamyka tablicę), więc
dostawca unieważnia wczesne linie tej partii i tłumaczy ją pojedynczo (dwa zwykłe zapytania).
Mierzy, czy wczesne linie pierwszej partii zostają na ekranie do wyniku końcowego, a wczesna linia
drugiej partii znika. Zdarzenie `partition_summary`: `requestPattern` (oczekiwane `stream:3`,
`stream:2`, `plain:1`, `plain:1`), `firstPartitionEarlyShown`, `firstPartitionFirstShownMs`,
`firstPartitionDropsBeforeFinal` (zniknięcia linii pierwszej partii przed publikacją końcową — ma być 0),
`secondPartitionEarlyShown`, `secondPartitionDropsBeforeFinal`, `finalPublishedMs`,
`allLinesShownAtEnd`, `miscountStatusUpdates` (statusy „model pomylił liczbę linii”), `fixtureValid`
i `expectedBehavior`. Niespełniony `fixtureValid` (inny układ zapytań albo brak wczesnych linii
w którejś partii) daje kod 4.

Podsumowanie: `firstBlockMs` i `allBlocksMs` (od renderu nowych linii do callbacku sesji
z pierwszym / wszystkimi pięcioma blokami `PL `), `firstToAllMs`, `provisionalUpdates`
(publikacje wczesnych linii), `textChangesOfShownBlocks` (zmiana tekstu już pokazanego bloku —
miganie albo podmiana), `dropsAfterAllShown`, `clearedAfterRefusalMs` (od pierwszego bloku do
pustej nakładki po odmowie), zapytania handlera (`providerRequests`, `streamedRequests`, czas
pierwszej linii i całej odpowiedzi od strony handlera) oraz mediany `LatencyMonitor`
(dostawca, pierwsza linia, nowy tekst, zmiana → napis). Kod 0 oznacza ważną próbę; 4 — fallback
przechwytywania, mniej niż dwa rozpoznane bloki albo nieudane zamknięcie; 3 — błąd albo timeout.
To callbacki sesji, nie fizyczna nakładka, a czasy modelu są syntetyczne. JSONL nie zawiera
pikseli ani rozpoznanych tekstów.

## Prawdziwy serwer modelu na tym komputerze (`openai-local`)

`--stream-provider openai-local` mierzy scenę `stream` (te same pięć syntetycznych linii, ta sama
prawdziwa sesja, Windows OCR) na prawdziwym serwerze zgodnym z OpenAI — przez prawdziwy
`OpenAiCompatibleTranslationProvider` (SSE albo zwykły JSON) i prawdziwe gniazdo TCP:

```
GameTranslatorOverlay.SceneReplay.exe --scenario stream --stream-provider openai-local --endpoint http://127.0.0.1:1234/v1 --model NAZWA_Z_LM_STUDIO --output C:\folder\lmstudio-on.jsonl
GameTranslatorOverlay.SceneReplay.exe --scenario stream --stream-provider openai-local --endpoint http://127.0.0.1:1234/v1 --model NAZWA_Z_LM_STUDIO --stream off --output C:\folder\lmstudio-off.jsonl
GameTranslatorOverlay.SceneReplay.exe --scenario stream --stream-provider openai-local --endpoint http://localhost:11434/v1 --model qwen3:8b --output C:\folder\ollama-on.jsonl
GameTranslatorOverlay.SceneReplay.exe --scenario stream --stream-provider openai-local --endpoint http://localhost:11434/v1 --model qwen3:8b --stream off --output C:\folder\ollama-off.jsonl
```

Serwer uruchamia się samodzielnie (LM Studio: zakładka Developer → Start Server, domyślnie port 1234;
Ollama: `ollama serve`, port 11434), z załadowanym modelem. Narzędzie niczego nie uruchamia
i nie pobiera.

Twarda blokada w kodzie (`LocalModelServer.cs`, testy w Infrastructure.Tests):

- `--endpoint` musi wskazywać host `127.0.0.1`, `localhost` albo `[::1]` (dokładnie te trzy nazwy;
  `127.0.0.2`, `0.0.0.0`, adres w sieci lokalnej czy `localhost.` są odrzucane z komunikatem
  „odmowa”), bez loginu, parametrów i fragmentu;
- każde zapytanie przechodzi przez strażnika: inny schemat, host albo port niż w `--endpoint`
  oraz nagłówek `Authorization`, `Proxy-Authorization`, `x-api-key` albo `api-key` kończą przebieg
  wyjątkiem, zanim cokolwiek wyjdzie; dostawca nie dostaje klucza (narzędzie nie czyta DPAPI ani
  ustawień użytkownika), a połączenie TCP powstaje wyłącznie z adresem pętli zwrotnej (nazwa
  rozwiązana na inny adres jest odrzucana), bez proxy systemowego i bez przekierowań;
- najwyżej 12 zapytań na przebieg (rozgrzewka + scena + ewentualne tłumaczenie pojedyncze
  i ponowienie jakości), potem odmowa.

Blokada nie odróżni serwera modelu od lokalnego pośrednika, który sam przekazuje zapytania do
płatnego API ze swoim kluczem (np. LiteLLM na `localhost:4000`) — wskazuj wyłącznie LM Studio,
Ollamę albo inny serwer, który sam liczy model.

Przed sceną idzie jedno zwykłe zapytanie rozgrzewające (to samo, co „Testuj połączenie”
w aplikacji): ładuje model i ten sam prompt systemowy, więc pomiar sceny pokazuje pracę
w trakcie gry, a nie wczytywanie modelu. Zdarzenie `warmup` ma `ok` i `ms`; nieudana rozgrzewka
kończy przebieg kodem 3 (`warmup_failed`). Scena czeka na wszystkie pięć bloków do 180 s (limit
zapytania dostawcy jak w aplikacji: 60 s). Opcje serwera (`reasoning_effort: none` dla Ollamy)
dochodzą tak jak w aplikacji po wyborze gotowego serwera: tylko gdy host i port są dokładnie jak
w presecie (`localhost:11434`), zdarzenie `provider_setup` podaje `serverOptions`. Uwaga: przy
`localhost` Windows najpierw próbuje `[::1]`; serwer słuchający tylko na IPv4 kosztuje wtedy ok. 2 s
na każde nowe połączenie (na atrapie: rozgrzewka 3034–3050 ms pod `localhost` wobec 975–994 ms pod
`127.0.0.1`, kolejne zapytania używały tego samego połączenia) — widać to w `HeadersMs` pierwszego
zapytania.

Zdarzenia `provider_response` (po jednym na odpowiedź, także rozgrzewkę): `Stream`, `Status`,
`MediaType`, `Texts` (linie w zapytaniu), `HeadersMs`, `FirstByteMs`, `FirstContentMs` (pierwszy
niepusty `delta.content`), `FirstLineMs` (pierwszy kompletny element tablicy `translations` według
parsera aplikacji), `TotalMs`, `Events`, `DoneMarker` (`[DONE]`), `Usage`, `FinishReason`,
`ReasoningChars` (`reasoning_content`/`reasoning` w strumieniu), `Returned`, `Empty`,
`QualityFlagged` (z podziałem `NumbersChanged`, `Untranslated`, `Runaway` według
`TranslationQualityGate`), `AppAccepted` (czy aplikacja przyjęła odpowiedź bez tłumaczenia
pojedynczego) i `translations` (odpowiedź modelu dla syntetycznych linii, do 200 znaków na linię).
Zdarzenie `local_summary` sumuje odpowiedzi sceny (bez rozgrzewki): zapytania strumieniowe
i zwykłe (np. po odrzuceniu `stream_options` przez serwer), błędy HTTP, linie zwrócone, puste,
oznaczone przez bramkę jakości, odpowiedzi z inną liczbą linii, czasy pierwszego zapytania oraz
`lineResults` — wynik każdej linii na ekranie (tłumaczenie końcowe, wczesne, czy się różniły,
`QualityIssues`, `SameAsSource`). `summary` podaje jak w scenie skryptowanej `firstBlockMs`,
`allBlocksMs` i mediany `LatencyMonitor`; `--stream off` daje porównanie bez strumienia.
JSONL zawiera tłumaczenia syntetycznych linii z modelu, nie zawiera pikseli ani odczytów OCR.
`--first-line-ms` i `--line-ms` są tu odrzucane, a `openai-local` działa tylko ze scenariuszem `stream`.

## Reakcja na zmianę ustawień i ręczną poprawkę

`settings-change` i `manual-correction` w `ReactionReplay.cs` (zawsze scripted OCR, prywatny cache
w pamięci, HTTP zablokowane, domyślny bezpiecznik pełnego odczytu sesji 4 s). Własne okno ma
500×300 DIP (mieści się także na małym ekranie): ciemne tło, panel z jasną etykietą i szary pas
referencyjny. OCR znajduje glify w przechwyconych pikselach (wszystkie kanały R i G ≥ 240); który
napis jest na ekranie, rozpoznaje po kanale B glifów (biały — „Unlock level 20 door”, żółtawy
#FFFF96 — „Garden clue opens the next door”). Etykieta jest rysowana w skali szarości (bez
ClearType), żeby kolorowe obwódki nie myliły odczytu. Odrzucane: `--provider-delay-ms`,
`--phase-ms`, `--ocr-delay-ms` i `--ocr windows`.

- `settings-change` (Mock 1500 ms): gdy pierwsza klatka czeka już na Mock (`ReservedApiCharacters`
  > 0), po 300 ms woła `TranslationOrchestrator.RebuildPipeline()` — to samo, co zmiana ustawienia
  wpływającego na tłumaczenie (ustawienia się nie zmieniają, liczy się anulowanie epoki). Mierzy
  czas od tego wywołania do startu następnego OCR (`singleRereadStartMs`, czy była to pełna klatka
  — `singleRereadFullFrame`), do jego końca i do pokazania tłumaczenia. Potem, tuż po kolejnym
  pełnym odczycie (żeby bezpiecznik wypadł możliwie daleko), zmienia etykietę na drugi napis,
  czeka na jego zapytanie do Mock i robi serię 8 przebudów co 40 ms (jak przesuwany suwak):
  `ocrDuringBurst` (odczyty w trakcie serii — oczekiwane 0, bez pętli anulowanych klatek),
  `burstRereadStartAfterLastMs` (od ostatniej zmiany do pełnego odczytu) i
  `burstDisplayedAfterLastMs`. Przed poprawką odczyt po anulowanej klatce czekał na bezpiecznik
  (do ok. 4 s, zależnie od fazy); po poprawce rusza po ustaleniu wersji pipeline'u (120 ms) i
  najbliższym cyklu sesji. `expectedBehavior`: oba odczyty < 500 ms i pełne, 0 odczytów w serii,
  oba napisy pokazane, bez czyszczenia i chowania nakładki. `fixtureValid` wymaga oczekującego
  zapytania przy obu zmianach i co najmniej dwóch klatek anulowanych przez zmianę ustawień.
- `manual-correction` (natychmiastowy dostawca testowy gubiący liczbę, więc kontrola jakości
  zapisuje ostrzeżenie): po pokazaniu bloku i 1,5 s narzędzie bierze wpis z
  `TranslationOrchestrator.QualityWarnings` (jak „Popraw” na liście „Ostatnie ostrzeżenia”) i woła
  `SaveManualCorrectionAsync`. Mierzy czas od zapisu do pierwszego callbacku z poprawionym tekstem
  (`correctedShownAfterSaveMs`), liczbę odczytów OCR przed nim (`ocrBeforeCorrectionShown`),
  odstęp od ostatniego pełnego odczytu w chwili poprawki (`msSinceLastFullOcrAtCorrection` — tyle
  mniej więcej czekał stary kod), czy blok zachował klucz, prostokąt i ten sam obiekt łatki
  (`sameKey`, `sameBox`, `coverPreserved`), czy poprawka trafiła na pasek napisów
  (`subtitleCorrectedAfterSaveMs`), czy stare tłumaczenie wróciło w ciągu kolejnych 4,5 s
  (obejmuje następny pełny odczyt) i ile zapytań do dostawcy padło po poprawce (oczekiwane 0).
  `expectedBehavior`: poprawka < 500 ms od zapisu i bez żadnego odczytu OCR przed nią (sesja
  stosuje ją na początku najbliższego cyklu, więc czas to najwyżej jedno przechwycenie), ten sam
  klucz, prostokąt i łatka, bez czyszczenia i powrotu starego tekstu, na pasku napisów i bez zapytań.

Oba scenariusze używają tylko publicznego API, które istnieje także w kodzie bazowym, więc ten
sam plik narzędzia można zbudować na starszej aplikacji do porównania przed/po. Czasy to callbacki
sesji i wywołania OCR, nie fizyczna nakładka. JSONL nie zawiera pikseli ani rozpoznanych tekstów.

## Przestój wątku UI przy starcie live i nowym napisie

`ui-fonts` w `UiFontsReplay.cs` nie przechwytuje obrazu i nie uruchamia sesji live. Tworzy
własny wątek UI (STA z dyspozytorem i małym oknem z tekstem, jak okno aplikacji przed startem
live), na nim `TranslationOrchestrator.Initialize()` z dołączonym profilem `escape-academy`
(krój Lexend Deca, reguła Zilla Slab) i ustawienie „Na oryginale (zakrywa)”. Wymaga
`--font-path`, innych opcji nie przyjmuje:

- `sync` — dawna ścieżka: `OverlayFonts.WarmUpProfile` na wątku UI przy starcie, a przy każdym
  nowym napisie `VoteNativeWeight` (pomiar grubości: rasteryzacja tekstu w każdej grubości kroju)
  i układ `CreateNativeElement` + `LayoutNativeElement` na wątku UI.
- `background` — nowa ścieżka: `OverlayFonts.WarmUpProfileInBackground` (wątek roboczy krojów),
  a przed przekazaniem napisu do wątku UI `OverlayBlockRenderer.PrepareNativeWeights` na wątku
  „sesji” (jak w `MainWindow`), potem ten sam układ na wątku UI.

Sześć nowych napisów (syntetyczne łatki z `GlyphCoverBuilder.Build`) przychodzi 150 ms po
starcie, potem co 300 ms. Sonda z innego wątku co ok. 0,5 ms wysyła wywołanie dyspozytora
o priorytecie Send i zapisuje jego opóźnienie — maksimum przybliża najdłuższy przestój wątku UI.
Podsumowanie: `startCallMs` (czas wywołania przy starcie live na wątku UI), `warmUpDoneMs`,
`firstUpdateUiMs` / `firstUpdateSessionMs`, mediany nowych napisów (`newTextUiMedianMs`,
`newTextUiVoteMedianMs` — sam pomiar grubości, `newTextSessionMedianMs`), `uiThreadBusyTotalMs`,
`maxProbeLatencyStartMs` (do pierwszego napisu) i `maxProbeLatencyWholeMs`, a także grubości
i rozmiary krojów każdego napisu (`weights`, `emSizes`) — w obu ścieżkach muszą być takie same
(pierwsze rysowanie z właściwym krojem). Kroje i JIT są wspólne dla procesu, więc każdy tryb
trzeba uruchamiać w osobnym procesie, kilka razy na przemian:

```powershell
GameTranslatorOverlay.SceneReplay.exe --scenario ui-fonts --font-path sync --output C:\pomiary\ui-sync-1.jsonl
GameTranslatorOverlay.SceneReplay.exe --scenario ui-fonts --font-path background --output C:\pomiary\ui-bg-1.jsonl
```

Wynik zależy od DPI monitora i od tego, co proces zdążył już narysować; nie obejmuje rysowania
przez kompozytor ani łatek zbudowanych z prawdziwej klatki gry.
