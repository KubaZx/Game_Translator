# SceneReplay

Lokalny, powtarzalny pomiar rzeczywistej `LiveTranslationSession`: przechwytuje wyłącznie własne okno WPF, używa Mock (domyślnie 2000 ms) i świeżego cache w pamięci. Nie czyta ustawień, kluczy ani cache użytkownika; wszystkie żądania HTTP są blokowane. Nie obsługuje okna gry. Nie zapisuje pikseli ani treści OCR.

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

Niezerowa faza jest dostępna tylko dla displayed/inflight. Stop odrzuca opóźnienie inne niż 2000 lub fazę inną niż 0, bo ma stałe założenie dwóch oczekujących odpowiedzi. local-reading, reading-jitter i reading-whiff odrzucają samo podanie którejkolwiek nowej opcji, nawet z wartością domyślną. Przy zmianie Delay w ABA/churn/inflight trzeba uwzględnić, czy odpowiedź rzeczywiście nadal trwa podczas zmiany sceny; np. inflight przy Delay <250 ms może już nie przerywać oczekiwania.

Każdy update z diagnostyką zawiera numeric captureMs, ocrMs i translateMs. Summary middleFirstOcrCompletedMs mierzy czas od Rendering sceny 2 do pierwszego zakończonego OCR rozpoznającego jej opis Archive; nie jest czasem rozpoczęcia OCR ani faktycznej prezentacji nakładki. middleReadyMs obejmuje także dalsze przetwarzanie/tłumaczenie. Gdy właściwego OCR/wyniku nie ma, odpowiedni pomiar pozostaje null.

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
