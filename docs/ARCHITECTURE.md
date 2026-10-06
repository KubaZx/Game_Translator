# Architektura

Dokument opisuje architekturę aplikacji **GameTranslatorOverlay** — desktopowego tłumacza
tekstu z gier (EN→PL) działającego jako zewnętrzna, przezroczysta nakładka, bez jakiejkolwiek
ingerencji w grę. Decyzje technologiczne (i ich uzasadnienia) są w
[TECHNOLOGY_DECISIONS.md](TECHNOLOGY_DECISIONS.md).

## 1. Podział na projekty

Rozwiązanie (`GameTranslatorOverlay.slnx`) składa się z trzech projektów produkcyjnych
i trzech testowych; osobne projekty w `tools/` służą do lokalnej diagnostyki, ewaluacji
i pracy offline na korpusie gry (ADR-014), a `benchmarks/` do pomiarów wydajności. Narzędzia
nie wchodzą do paczki aplikacji:

```
src/
  GameTranslatorOverlay.Core            (biblioteka, bez zależności Windows)
  GameTranslatorOverlay.Infrastructure  (biblioteka, integracje: SQLite, DeepL, DPAPI, pliki)
  GameTranslatorOverlay.App             (WPF, net10.0-windows10.0.19041.0)
tests/
  GameTranslatorOverlay.Core.Tests            (xUnit)
  GameTranslatorOverlay.Infrastructure.Tests  (xUnit)
  GameTranslatorOverlay.CorpusTool.Tests      (xUnit, dane syntetyczne)
tools/
  LiveDiag, SceneReplay, OcrLab, SmokeTest    (diagnostyka na Windows)
  GameTranslatorOverlay.ProviderEval          (konsola net10.0: porównanie dostawców na korpusie EN→PL)
  GameTranslatorOverlay.CorpusTool            (konsola net10.0, ADR-014: extract — korpus z plików gry;
                                               translate — tłumaczenie korpusu do cache z profilem)
  GameTranslatorOverlay.CorpusEval            (Windows: pomiary przyciągania OCR do korpusu — render, evaluate,
                                               replay, bench, session, prefixes, typing)
  GameTranslatorOverlay.OverlayPreview        (Windows: render nakładki na zapisanych klatkach — galeria wyglądu)
benchmarks/
  GameTranslatorOverlay.Benchmarks            (BenchmarkDotNet, poza dotnet test; docs/BENCHMARKS.md)
```

Zależności płyną w jedną stronę: **App → Infrastructure → Core**. Core nie zna nikogo.

### GameTranslatorOverlay.Core — czysta logika

Zero zależności od Windows, WPF i sieci. Wszystko tutaj jest testowalne zwykłym xUnitem
na dowolnym runnerze.

Zawartość:

- **Interfejsy (kontrakty)**: `IOcrProvider`, `ITranslationProvider`, `ITranslationCache`,
  `IGlossaryService` oraz modele współdzielone z usługami aplikacji (rozdz. 5).
- **Modele domenowe**: wynik OCR (tekst + prostokąty), zapytanie/wynik tłumaczenia,
  profil gry, słownik i jego terminy, ustawienia.
- **Logika przetwarzania tekstu**: normalizacja tekstu z OCR (sklejanie linii, białe znaki,
  myślniki przenoszenia), filtr śmieci (odsiew nie-tekstu — API OCR nie daje per-słowo
  confidence, więc filtrujemy po treści), segmentacja na jednostki tłumaczenia.
- **Logika słownika**: dopasowanie całych słów/fraz (nigdy fragmentów słów), dłuższe frazy
  przed krótszymi, rozstrzyganie konfliktów priorytetem, wykrywanie konfliktów
  (ten sam `source` → różne `target`).
- **Priorytet źródeł tłumaczenia**: ręczna korekta > słownik > cache > API.
- **Kontrola kosztów**: deduplikacja zapytań in-flight, debounce niestabilnego tekstu,
  limity (miesięczny, znaków na sesję), tryb Cache-only.
- **Jakość i kontekst tłumaczeń** (`Translation/`): `TranslationQualityGate` (pusty wynik,
  liczby, brak tłumaczenia, „rozgadany” wynik), `TranslationCacheContext` (znacznik wpisu cache:
  `reflow-1`, `qa=…`, `qa-final`, `pg=f/m`, `src=corpus`), `DialogMemory` (ostatnie linie i pary
  źródło → tłumaczenie, tylko w RAM), `PlayerGender` z `IGenderAwareTranslationProvider`
  oraz `IRetryableTranslationProvider`. Szczegóły: [API_PROVIDERS.md](API_PROVIDERS.md).
- **Jednostki tłumaczenia i korpus** (`Translation/TranslationUnits.cs`, `Corpus/`): gdy pipeline
  ma `CorpusSnapper` aktywnego profilu albo `SplitParagraphs`, `TranslationUnitPlanner` dzieli blok
  OCR na jednostki — tekst kanoniczny korpusu (klucz cache `CorpusTranslationKey`), akapit odczytu
  albo tekst dosłowny (prefiks mówcy, klawisz, śmieciowy akapit). Słownik, cache i dostawca działają
  na jednostkach; blok jest składany z powrotem w układzie wierszy z ekranu (`TextReflow`).
  Ręczna korekta, słownik i stary wpis całego odczytu są sprawdzane przed/po jednostkach
  (korekta > słownik > jednostki z cache > stary wpis całego odczytu > dostawca; nieaktualny wpis
  całego odczytu — stary format, `qa=…`, inna płeć — jest zapasem, gdy dostawca zawiedzie).
  Ręczna korekta bloku idzie pod klucz kanoniczny tylko przy dokładnym dopasowaniu do korpusu,
  przy przybliżeniu — pod klucz odczytu. Dopasowanie chroni liczby: znaki `+ - − # $ € £ ¥ %`
  przy liczbie muszą się zgadzać, a cyfra odczytu może różnić się od korpusu tylko naprzeciw
  litery mylonej przez OCR (`EditDistance.BoundedGuarded`). Bez korpusu
  i bez `SplitParagraphs` pipeline działa jak wcześniej (ta sama ścieżka kodu).
  Dla sesji live pipeline odpowiada też na pytania o pojedynczy odczyt: `ShouldTranslateLive`
  (bramka: dokładny tekst korpusu albo `JunkFilter` i nie szum), `CorpusIdentity` (tożsamość bloku
  w pełni obsłużonego korpusem — wspólny klucz nakładki dla kolejnych odczytów tej samej kwestii
  i drżenia OCR etykiety) i `IsCorpusPrefix` (odczyt jest niedokończoną kwestią korpusu).
- **Korpus gry** (`Corpus/`): `CorpusEntry` i `CorpusJsonl` (wpis JSONL z `CorpusTool extract`:
  klucz, tekst EN, kontekst, rodzaj `ui`/`dialog`/`subtitle`, mówca, węzeł dialogu, kolejność,
  czas napisu), `CorpusIndex` (trigramy po tekście bez wielkości liter, słowa i mówcy korpusu)
  oraz `CorpusSnapper`, który przyciąga odczyt OCR do tekstu korpusu: cały blok, akapit, wiersz
  albo część wiersza (dokładnie, przybliżenie, fragment), krótką etykietę z typowymi pomyłkami OCR
  (`OcrEditDistance` — odległość ważona pomyłkami, jednostka 100), początek linii dialogu albo
  napisów (`OcrEditDistance.Prefix`, klucz tłumaczenia = cała linia) i rozpoznaje szum
  (`LooksLikeNoise`). Progi i przełączniki są w `CorpusSnapOptions`. `CorpusTranslationKey` liczy
  klucz cache wpisu korpusu — ten sam w aplikacji i w `CorpusTool translate`.
- **Obraz i tryb live** (`Vision/`): bez WPF i API Windows; piksele przychodzą jako bufor BGRA
  w pamięci (`OcrBitmap`) albo siatka jasności (`LuminanceGrid`) —
  detekcja zmian (`FrameChangeDetector`, `NoiseAwareChangeDetector`, `ChangeStabilizer`), próbki
  kolorów (`BlockColorSampler`), stan bloków live (`LiveSceneValidity`, `LiveReadingStabilizer`,
  `LiveBlockSurvival`, `LiveBlockGeometry`, `LiveSubtitleContent`, `LiveBlockKeyer`), dowody
  w pikselach (`TextRegionFingerprint`, `TextPresenceProbe`, `KnownTextAbsenceProbe`
  z wzorcem `KnownTextReference`), zegar pełnego skanu (`FullScanSchedule`) i łatka
  z wypełnionymi literami (`GlyphCoverBuilder`, `GlyphCover`, `InkProfile`; rozdz. 8).
- **Profile gier** (`Profiles/`): `GameProfile` z opcjonalnymi sekcjami `ocr`, `changeDetection`,
  `overlay` (krój napisów) i `corpus` (recepta dla `CorpusTool`, `CorpusRecipe`) oraz polem `online`;
  `ProfileValidator` sprawdza też receptę (`CorpusRecipeValidator`), ale aplikacja jej nie wykonuje.
- **Słownik** (`Glossary/`): `GlossaryPrecedence` — jedna reguła pierwszeństwa dla tłumaczenia
  lokalnego i glosariusza DeepL; `PersistableTerms` — terminy, które mogą trafić do trwałego
  glosariusza (bez terminów prywatnych i `scope: label`).
- **Użycie i komunikaty** (`Usage/`): `LatencyMonitor` i `ChangeToTextTracker` (pomiar
  „Zmiana → napis”), `OverlayNoticePolicy` (deduplikacja 30 s, pierwszeństwo błędów, komunikaty
  krytyczne przy schowanej nakładce, licznik braków Cache-only), `NoticeTexts` i
  `OverlayNoticeEcho` (filtr OCR własnego komunikatu).
- **Wybór okna** (`Windows/`): `LiveTargetResolver` — dla skrótu live wybiera aktywne okno,
  zapamiętaną grę albo grę z profilem; okna powłoki Windows i sam tłumacz są pomijane.
- **Ewaluacja** (`Evaluation/`): chrF, korpus JSONL, kontrole EN→PL i raport — używane tylko
  przez ProviderEval i testy, nie przez aplikację.

### GameTranslatorOverlay.Infrastructure — integracje bez UI

Implementacje kontraktów z Core, które wymagają świata zewnętrznego, ale nie pulpitu:

- **Cache**: SQLite przez `Microsoft.Data.Sqlite`, migracje przez `PRAGMA user_version`.
- **Tłumaczenie**: `DeepLTranslationProvider` (HTTP, `/v2/translate` batch do 50 tekstów,
  `/v2/usage` do testu połączenia i licznika; `api-free.deepl.com` dla kluczy `:fx`,
  `api.deepl.com` dla pro; obsługa 403/456/429/timeout/braku sieci),
  `AzureTranslatorProvider`, `GoogleTranslateProvider`, dostawcy modeli językowych
  (`OpenAiCompatibleTranslationProvider` dla OpenAI/Ollamy/LM Studio oraz
  `ClaudeTranslationProvider` na oficjalnym SDK Anthropic) i `MockTranslationProvider`
  (deterministyczny — testy i praca bez klucza). Wspólną pętlę HTTP (timeout, ograniczony
  retry, mapowanie błędów) zapewnia `ProviderHttp`; opis dla UI — `TranslationProviderCatalog`.
  Szczegóły: [API_PROVIDERS.md](API_PROVIDERS.md).
- **Klucze API**: Windows DPAPI (`ProtectedData`, zakres CurrentUser), osobny sekret na
  dostawcę, zapis w `%LOCALAPPDATA%\GameTranslatorOverlay`.
- **Korpus gry**: `CorpusCatalog` czyta `<folder danych>\corpus\<id profilu>.corpus.jsonl`
  (`AppPaths.CorpusDirectory`; dane z narzędzia ADR-014, nigdy pliki gry) przy przebudowie
  pipeline'u (start i zmiana ustawień) i buduje z niego `CorpusIndex` i `CorpusSnapper`. Ostatnio
  wczytany korpus zostaje w pamięci, dopóki nie zmieni się profil albo rozmiar lub czas zapisu
  pliku; plik zmieniony w trakcie pracy jest czytany dopiero przy kolejnej przebudowie.
  Identyfikator profilu ze znakami ścieżki nie wskazuje żadnego pliku; brak pliku = pipeline bez
  korpusu, plik pusty albo nieczytelny = ostrzeżenie treści i pipeline bez korpusu.
- **Pliki**: odczyt/zapis profili gier i słowników (JSON, schematy w rozdz. 7),
  `settings.json`. `AppSettings.PipelineSnapshot()` to ustawienia bez pól samego wyglądu
  (czcionka, tło, tryby wyświetlania, skróty, komunikaty, ostatnia gra) — orchestrator
  przebudowuje pipeline (i czyści pamięć dialogu) tylko, gdy zmieni się ta migawka.
- **Glosariusz DeepL**: `DeepLGlossaryManager` przygotowuje glosariusz w tle; nowy glosariusz
  opóźnia najwyżej jedną partię o najwyżej `GlossaryWaitBudget` (300 ms).
- **Logowanie**: konfiguracja Serilog (plik rolling; bez treści tłumaczeń w trybie
  prywatnym, nigdy kluczy API).

Celowo **nie ma** osobnego assembly `Providers.DeepL` — DeepL siedzi w Infrastructure
(mniej assembly = prościej; szczegóły w TECHNOLOGY_DECISIONS.md).

### GameTranslatorOverlay.App — WPF i wszystko, co wymaga pulpitu

- UI (okno główne, panel wyniku, ustawienia), DI przez `Microsoft.Extensions.Hosting`.
- **Capture**: tryb live w ruchu kamery czyta okno gry przez Windows Graphics Capture
  (`Capture/GraphicsCaptureSource`, Direct3D 11 przez COM, bez żółtej ramki); na stojącym obrazie,
  zapasowo i w trybie ręcznym GDI (`CopyFromScreen`/BitBlt dla regionu ekranu, `PrintWindow` z `PW_RENDERFULLCONTENT`
  dla okna + fallback na crop ekranu).
- **OCR**: `WindowsOcrProvider` — adapter `Windows.Media.Ocr.OcrEngine` za interfejsem
  `IOcrProvider` (WinRT wymaga TFM windowsowego, więc siedzi w App, nie w Infrastructure).
- **Nakładka**: osobne okno WPF z `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE |
  WS_EX_TOOLWINDOW`, Topmost, click-through, bez fokusu, wykluczone z przechwytywania ekranu
  (`WDA_EXCLUDEFROMCAPTURE`); na wypadek, gdyby wykluczenie zawiodło, sesja live odfiltrowuje
  też odczyty własnych tłumaczeń i komunikatów (filtr anty-sprzężeniowy).
- **Wygląd napisów** (`Ui/`): `OverlayBlockRenderer` buduje i układa element bloku dla
  `OverlayWindow` i narzędzia OverlayPreview — w trybie „Na oryginale (zakrywa)” z łatką
  `GlyphCover` jako `GameTextElement` (tekst z geometrii, kontur piórem, cień, łatka jako obraz),
  bez łatki dawną ścieżką (`CoverPatchHost` z rozmytą kopią tła i `OutlinedTextBlock`).
  `OverlayFonts` wybiera krój (ustawienie albo — przy „Jak w grze (krój z profilu)”, wartość
  `auto` — `overlay.fontFamily` profilu, bez profilu Segoe UI) i jego grubość. Dołączony krój
  Lexend Deca (Regular, Medium, SemiBold, Bold; SIL OFL 1.1) to zasoby WPF z `App/Fonts`,
  bez instalacji w systemie; licencja trafia obok programu jako `licenses/LexendDeca-OFL.txt`
  (`THIRD-PARTY-NOTICES.md`, ADR-016).
- **Skróty globalne** (Ctrl+Shift+T, Ctrl+Shift+H, Ctrl+Shift+L dla start/stop live) — sterują
  wyłącznie tłumaczem, nigdy grą. Okno dla Ctrl+Shift+L wybiera `LiveTargetResolver` (Core).
- **Komunikaty w nakładce**: krótki pasek przy górnej krawędzi okna gry, sterowany przez
  `OverlayNoticePolicy`; tekst komunikatu nie zawiera treści z ekranu.
- **Sesja live** (`Services/LiveTranslationSession`): pętla capture/OCR/tłumaczenie z rozdz. 8;
  w trybie zakrywania liczy w tle łatki z wypełnionymi literami i wstrzymuje kwestię pisaną
  literami (oba przełączniki w `LiveSessionOptions` ustawia okno główne).

Reguła podziału w jednym zdaniu: **Core = co i dlaczego, Infrastructure = skąd i dokąd
(dysk/sieć), App = ekran, piksele i klawiatura.** Analiza pikseli, która nie potrzebuje API
Windows (detekcja zmian, sondy obecności tekstu, łatka z wypełnionymi literami), mieszka
w `Core/Vision` na buforach w pamięci i jest testowana bez pulpitu; App przechwytuje obraz,
wywołuje ją i rysuje wynik.

## 2. Przepływ danych

1. **Capture:** wybrane okno lub region; live w ruchu przez Windows Graphics Capture,
   poza tym PrintWindow/GDI; bitmapa lokalna.
2. **OCR:** Windows.Media.Ocr zwraca linie i prostokąty.
3. **Tekst:** normalizacja, grupowanie i filtr śmieci; w live także stabilizacja
   odczytów i sprawdzenie aktualności sceny. Z korpusem aktywnego profilu bramka live
   przepuszcza dokładny tekst korpusu i odrzuca szum (rozdz. 1, `ShouldTranslateLive`).
4. **Korpus (opcjonalnie):** gdy aktywny profil ma lokalny korpus, odczyt jest przyciągany
   do znanego tekstu gry, a blok dzielony na jednostki z kluczem kanonicznym korpusu.
5. **Wyniki lokalne:** odczyt ręcznej poprawki, dokładne dopasowanie słownika,
   następnie zwykły cache (z korpusem — na jednostkach, więc trafiają też wpisy
   przetłumaczone z wyprzedzeniem przez `CorpusTool translate`). Trafienie kończy
   wyszukiwanie wyniku bez API.
6. **Dostawca:** brakujące teksty po deduplikacji i rezerwacji znaków, batchowanie,
   wynik zachowywany w cache, o ile nadal pozwala na to token konfiguracji.
7. **Prezentacja:** panel lub nakładka. Sesja live odrzuca wynik wykrytej
   nieaktualnej sceny przed jego publikacją. W trybie „Na oryginale (zakrywa)” łatka
   z wypełnionymi literami powstaje z tej samej klatki równolegle z krokami 5–6 (rozdz. 8).

Do dostawcy trafia wyłącznie tekst; bitmapy pozostają lokalnie. Przy przyciągnięciu do
korpusu może to być pełny tekst wpisu korpusu, którego część dopiero pojawia się na ekranie
(ADR-014, dopisek (3)). Próbki obrazu służą również do stylu nakładki, łatki z wypełnionymi
literami i sprawdzania obecności tekstu. Cache-only wyłącza krok dostawcy. OCR, oczekiwanie
na sieć i budowa łatek nie blokują wątku interfejsu; na nim zostaje układ napisów WPF
(w rundzie 2026-10-06 (5): 5–21 ms na klatkę z nowymi napisami w 4K) i jednorazowe rozgrzanie
krojów przy starcie live (ok. 212 ms) — [ROADMAP.md](ROADMAP.md).

Aplikacja działa pasywnie: bez ingerencji w pamięć lub pliki gry, wstrzykiwania
kodu i wysyłania sterowania do gry. Plików gry aplikacja nie czyta — korpus wytwarza
osobne narzędzie offline uruchamiane przez gracza (ADR-014). Aktualność live opisuje
rozdział 8.

## 3. Kluczowe interfejsy

Kontrakty mieszkają w Core; implementacje w Infrastructure (sieć/dysk) lub App (pulpit).

### IOcrProvider

Rozpoznaje tekst na bitmapie. Obecna implementacja: `WindowsOcrProvider`
(`Windows.Media.Ocr.OcrEngine`, w App).

- Wejście: bitmapa + język źródłowy.
- Wyjście: linie tekstu z prostokątami (współrzędne względem bitmapy).
- Brak per-słowo confidence w API systemowym — filtr śmieci działa na tekście (w Core).
- Brak pakietu językowego Windows = czytelny komunikat + instrukcja doinstalowania języka
  w ustawieniach Windows (nie wyjątek w twarz użytkownika).

### ITranslationProvider

Tłumaczy partię tekstów. Implementacje: `DeepLTranslationProvider`, `AzureTranslatorProvider`,
`GoogleTranslateProvider`, `OpenAiCompatibleTranslationProvider`, `ClaudeTranslationProvider`
(Infrastructure) oraz `MockTranslationProvider` (Core). Dostawcy modeli językowych
implementują też `IContextualTranslationProvider` — pipeline przekazuje im
`TranslationContext` (nazwa gry z profilu + terminy słownika z tłumaczonej partii).

- Wejście: lista tekstów + para językowa; wyjście: lista tłumaczeń w tej samej kolejności.
- Batch (DeepL: do 50 tekstów na zapytanie).
- Test połączenia + licznik zużycia (DeepL: `/v2/usage`).
- Mapowanie błędów na czytelne stany: zły klucz (403), wyczerpany limit (456),
  rate limit z ograniczonym retry (429), timeout, brak sieci.

### ITranslationCache

Trwały cache tłumaczeń (SQLite w Infrastructure; w trybie prywatnym — tylko w pamięci,
czyszczony po sesji).

- Klucz: znormalizowany tekst źródłowy + para językowa (+ kontekst profilu dla wpisów
  profilowych i korekt). Przy przyciągnięciu do korpusu tekstem źródłowym jest tekst wpisu
  korpusu (`CorpusTranslationKey`), wspólny dla różnych odczytów tego samego zdania.
- Realizuje priorytet: **ręczna korekta > wpis profilu gry > cache globalny**; dopiero
  pełny miss idzie do `ITranslationProvider`.
- Zapis ręcznych korekt użytkownika (nadpisują wszystko inne).
- Migracje schematu przez `PRAGMA user_version`.

### IGlossaryService

Lokalny słownik terminów — działa PRZED tłumaczeniem maszynowym i bez sieci.

- Ładuje słowniki JSON (`glossaries/<id>/en-pl.json`, schemat w rozdz. 7).
- Dopasowanie: całe słowa/frazy (nigdy fragment słowa), dłuższe frazy przed krótszymi,
  angielska liczba mnoga i dopełniacz („Waystones”, „Waystone's”), fraza złamana do nowej
  linii; konflikt rozstrzyga `GlossaryPrecedence` (wyższy `priority` → termin z rozróżnianiem
  wielkości liter → wczytany później). Klucze są normalizowane jak tekst z OCR.
- Terminy `scope: label` działają tylko jako cały tekst (etykieta, przycisk); nie są
  podpowiadane w zdaniach ani wysyłane w glosariuszu DeepL.
- `TryTranslateExact` tłumaczy lokalnie tekst będący w całości terminem (także etykietę
  z dwukropkiem: „Rarity:” → „Rzadkość:”);
  `FindTermsIn` wskazuje terminy wewnątrz zdań dla dostawców kontekstowych.
- Wykrywa i raportuje konflikty (ten sam `source` → różne `target`).
- Etap 10: edycja terminów z UI, import/eksport.

## 4. Przepływ trybu Manual Region (MVP)

Pierwszy działający tryb (Etap 6 roadmapy) — punkt odniesienia dla całej architektury:

1. Użytkownik gra; wciska globalny skrót **Ctrl+Shift+T** (skrót rejestruje App;
   gra nie dostaje żadnego inputu od nas).
2. App pokazuje półprzezroczystą warstwę wyboru regionu; użytkownik zaznacza prostokąt
   myszą (współrzędne ekranowe → przeliczenie DPI, rozdz. 6).
3. Capture regionu przez GDI (`CopyFromScreen`).
4. Bitmapa przechodzi pion z rozdz. 2: OCR → normalizacja → (korpus aktywnego profilu,
   jeśli jest) → glossary → cache → (miss) API. Bloki regionu przechodzą dawny filtr śmieci
   (`JunkFilter`, z wyjątkiem dokładnego tekstu korpusu); szum korpusu nie odrzuca tu całych
   bloków jak bramka live.
5. Wynik ląduje w panelu wyniku / nakładce; użytkownik nie traci sterowania grą
   (nakładka jest click-through i nie kradnie fokusu).
6. Użytkownik może poprawić tłumaczenie (korekta → cache z najwyższym priorytetem)
   lub dodać termin do słownika.
7. Kolejne wciśnięcie skrótu = nowe zaznaczenie; skrót zamykający chowa panel.

Każdy krok pionu jest anulowalny — jeśli użytkownik zdąży poprosić o nowy region, stare
zadanie dostaje `CancellationToken.Cancel()` i jego wynik nigdzie nie trafia.

## 5. Usługi aplikacji

| Element | Odpowiedzialność | Projekt |
|---|---|---|
| `ScreenCapture` | przechwycenie okna/regionu, geometria i lokalne próbki pikseli | App |
| `WindowsOcrProvider` | systemowy OCR i jego adapter | App |
| `LiveTranslationSession` | pętla capture/OCR, aktualność sceny, stan bloków i publikacja aktualizacji; w trybie zakrywania budowa łatek w tle i wstrzymanie kwestii pisanej literami | App |
| `TranslationOrchestrator` | składanie pipeline'u (z korpusem aktywnego profilu z `CorpusCatalog`), konfiguracja i jej token życia | App |
| `CorpusCatalog` | wczytanie korpusu aktywnego profilu z folderu danych (plik JSONL z `CorpusTool extract`) | Infrastructure |
| `CorpusIndex`, `CorpusSnapper`, `OcrEditDistance` | indeks korpusu i przyciąganie odczytu: blok, akapit, wiersz, etykieta z pomyłkami OCR, początek kwestii; rozpoznawanie szumu | Core |
| `TranslationPipeline`, `UsageTracker` | wyniki lokalne, deduplikacja, dostawca i rezerwacje znaków, kontrola jakości, pamięć dialogu, pamięć awaryjna przy zepsutym cache | Core |
| `TranslationQualityGate`, `TranslationCacheContext`, `DialogMemory` | ocena wyniku, znacznik wpisu cache, historia dialogu dla dostawców kontekstowych | Core |
| `ChangeToTextTracker`, `LatencyMonitor` | pomiar „Zmiana → napis” i pozostałych etapów (tylko liczby, w pamięci) | Core |
| `OverlayNoticePolicy`, `OverlayNoticeEcho` | które komunikaty pokazać i odfiltrowanie ich echa z OCR | Core |
| `LiveTargetResolver` | wybór okna dla skrótu live | Core |
| `BoundedTranslationWork` | ograniczona liczba nadzorowanych zadań i ich domknięcie | Core |
| `LiveSceneValidity`, `LiveReadingStabilizer` | generacja sceny i kolejne potwierdzenia tekstu | Core |
| `LiveBlockGeometry`, `LiveSubtitleContent` | niezależna stabilizacja położenia/rozmiaru i źródła paska napisów (także powrót linii ze wskrzeszonym blokiem) | Core |
| `LiveBlockSurvival`, `FullScanSchedule` | okres łaski bloków, wycinek OCR obejmujący bloki z brakami; zegar pełnego OCR liczony od ostatniego pełnego skanu | Core |
| `LiveBlockKeyer` | klucz bloku nakładki z treści odczytu albo z tożsamości korpusu | Core |
| `TextPresenceProbe` | konserwatywna ocena całego jednolitego pola starego tekstu | Core |
| `KnownTextAbsenceProbe`, `KnownTextReference` | dowód zniknięcia znanego tekstu także na teksturze; wzorzec kolorów i liczba pikseli rdzenia liter z ostatniego potwierdzającego odczytu | Core |
| `OverlayWindow` | prezentacja bloków/paska, click-through, DPI i ręczne ukrywanie | App |
| `OverlayBlockRenderer` | wygląd i pozycja bloku (łatka, kontur, dopasowanie czcionki, wyrównanie, pominięcie tłumaczeń identycznych z oryginałem w trybie zakrywania) — wspólny dla `OverlayWindow` i OverlayPreview | App |
| `GlyphTracker` / `GlyphTrack` | śledzenie napisu z łatką między odczytami OCR: wzorzec punktów liter i obrysu, koszt SAD z przewidywaniem ruchu, kontrola kontrastu liter wobec obrysu/tła, odrzucanie dopasowań wieloznacznych, dowód zniknięcia liter | Core (`Vision/`) |
| `GraphicsCaptureSource` | sesja Windows Graphics Capture okna gry: ostatnia klatka na GPU, pełna klatka albo wycinek do pamięci, sygnał nowej klatki | App (`Capture/`) |
| `OcrBands` | ponowny odczyt pustego wyniku Windows OCR dużego obrazu w 2, a potem 4 poziomych pasach z zakładką | Core (`Ocr/`) |
| `TextBlockSplitter` | podział zlepionego bloku według napisów wyświetlanych i obecnych na swoich miejscach | Core (`Text/`) |
| `GlyphCoverBuilder` / `GlyphCover` / `InkProfile` | łatka „natywna”: maska liter (odchylenie od tła z pierścienia + top-hat), wypełnienie pikseli liter z otoczenia (push-pull), kolor tekstu, kontur, cień, linia bazowa, wysokość, wyrównanie, ikony klawiszy; liczona w `LiveTranslationSession` równolegle z tłumaczeniem | Core (`Vision/`) |
| `GameTextElement` / `OverlayFonts` | tekst z geometrii (kontur piórem, cień), łatka jako obraz; krój z ustawień albo profilu (dołączony Lexend Deca), rozmiar z wysokości liter nad linią bazową, grubość z gęstości tuszu oryginału — jedna dla bloków w tym samym stylu | App |

Tryb ręczny, live w blokach i pasek napisów są zaimplementowane. Automatyczne
wydzielanie tooltipów, History Mode i wyjaśnianie przez LLM pozostają poza obecną
implementacją. Capture live używa Windows Graphics Capture w ruchu kamery i GDI/PrintWindow
na stojącym obrazie (rozdz. 8, „Ruch kamery”).

## 6. DPI i multi-monitor

- Aplikacja deklaruje w manifeście **PerMonitorV2** — każde okno (główne, warstwa wyboru
  regionu, nakładka) dostaje realne DPI monitora, na którym stoi.
- Współrzędne trzymamy w **pikselach fizycznych ekranu** (tak pracują GDI i Win32);
  na piksele WPF (DIP) przeliczamy dopiero przy rysowaniu, mnożnikiem DPI konkretnego
  monitora.
- Warstwa zaznaczania regionu działa na monitorze pod kursorem. Geometrię wyniku
  przeliczamy na fizyczne współrzędne pulpitu.
- Nakładka pozycjonuje się względem prostokąta okna gry (fizyczne piksele), więc
  przeniesienie gry na inny monitor = przeliczenie od nowa, bez „rozjechanych" ramek.
- Ograniczenie (udokumentowane): **exclusive fullscreen nie jest obsługiwany** — GDI ani
  nakładka nie widzą takiego trybu. Działa okno i borderless fullscreen. Gdy okno gry nie
  wspiera PrintWindow i zajmuje cały monitor, sesja live pokazuje raz na sesję komunikat
  „⚠ Pełny ekran utrudnia nakładkę — przełącz na okno bez ramki”
  (`OverlayNotices.ExclusiveFullscreen`, 8 s). Warunek nie odróżnia wyłącznego pełnego ekranu
  od okna bez ramki bez obsługi PrintWindow.

## 7. Schematy JSON

Specyfika konkretnej gry mieszka WYŁĄCZNIE w opcjonalnych profilach i słownikach —
rdzeń aplikacji jest uniwersalny. Konwencja pól: camelCase.

### Profil gry — `profiles/<id>/profile.json`

```json
{
  "id": "path-of-exile-2",
  "name": "Path of Exile 2",
  "profileVersion": 1,
  "author": "GameTranslatorOverlay",
  "description": "...",
  "processNames": ["PathOfExile.exe", "PathOfExileSteam.exe"],
  "windowTitles": ["Path of Exile 2"],
  "sourceLanguage": "en",
  "recommendedMode": "manual-region",
  "glossary": "path-of-exile-2",
  "ocr": { "upscale": 2.0, "minTextHeight": 10 },
  "changeDetection": { "threshold": 0.02, "fps": 4 },
  "minAppVersion": "0.1.0"
}
```

- `ocr.upscale`: brak pola = ustawienia aplikacji (automatyczne 2× dla małych regionów),
  `1.0` = bez powiększania, `1.0`–`4.0` = stały współczynnik.
- `overlay.fontFamily` (opcjonalne, np. `"Lexend Deca"`): krój napisów nakładki, gdy w ustawieniach
  wybrano „Jak w grze”. Najpierw szukany wśród krojów dołączonych do aplikacji (`App/Fonts`, licencja
  OFL), potem wśród czcionek systemowych; krój nieznaleziony (bez żadnej grubości 300–800) zastępuje
  Segoe UI. Grubość (dla Lexend Deca: Regular/Medium/SemiBold/Bold) dobiera nakładka. Nazwa kroju,
  nie ścieżka (do 64 znaków). Starsze wersje aplikacji pole pomijają.
- `live.ignoreRegions` (opcjonalne): lista prostokątów `{ "x", "y", "width", "height" }` w ułamkach
  okna gry (0–1); linie OCR ze środkiem w takim obszarze są pomijane w trybie live (np. zegar
  poziomu Escape Academy, który co sekundę dawał nowy „tekst”). Walidator odrzuca obszar poza
  oknem albo o niedodatnich wymiarach.
- `online` (opcjonalne): `true` oznacza grę online — `CorpusTool` odmawia dla niej pracy (ADR-014).
- `corpus` (opcjonalne): recepta korpusu dla narzędzia `CorpusTool` — rodzina formatów
  (`format`, np. `unity-textasset`), kontener i plik w folderze gry oraz źródła z wzorcami nazw,
  parserem (`csv`, `srt`) i rodzajem tekstu (`ui`, `dialog`, `subtitle`). Opis pól:
  [README narzędzia](../tools/GameTranslatorOverlay.CorpusTool/README.md). Aplikacja receptę tylko
  waliduje; plików gry nie otwiera.
- `minAppVersion` (SemVer, np. `0.2.2`; przyrostek `-beta` jest pomijany): profil wymagający
  nowszej aplikacji nie jest wczytywany, a problem trafia do logu.

### Słownik — `glossaries/<id>/en-pl.json`

```json
{
  "name": "path-of-exile-2",
  "sourceLanguage": "en",
  "targetLanguage": "pl",
  "version": 1,
  "description": "...",
  "terms": [
    { "source": "Energy Shield", "target": "Tarcza energetyczna", "caseSensitive": false, "priority": 10, "note": "opcjonalna uwaga" },
    { "source": "Save", "target": "Zapis", "scope": "label" }
  ]
}
```

`scope` jest opcjonalne: brak albo `any` — termin działa wszędzie; `label` — tylko jako cały
tekst etykiety. Inną wartość odrzuca walidator („dozwolone: any, label”).

Zasady słownika: dopasowanie **całych słów/fraz** (nigdy fragmentów słów), dłuższe frazy
przed krótszymi, priorytety rozstrzygają konflikty; konflikty (ten sam `source` → różne
`target`) są wykrywane i raportowane.

## 8. Aktualność i praca trybu live

Jedna pętla sesji obsługuje capture, OCR i stan nakładki. Domyślnie próbkuje obraz
przy 6 FPS, wymaga 250 ms stabilności, może wymusić przetwarzanie po 600 ms,
a przy ruchu ma maksymalną pauzę OCR 2,5 s (0,9 s, gdy działa śledzenie napisów). Pełny OCR całej klatki jest należny
4 s (`StaticRescanInterval`) po poprzednim **pełnym** OCR (`FullScanSchedule`): wycinki go nie
odsuwają, a należny skan obejmuje całą klatkę także przy oczekującym regionie zmian. Powtórki
mogą wynikać też z potwierdzania odczytu, podejrzenia whiffa, podejrzenia zniknięcia napisu
i wstrzymanej kwestii. Wycinek OCR obejmuje bloki z brakami
(`LiveBlockSurvival.UnconfirmedRegion`), o ile ich suma z regionem zmian (z zapasem 24 px)
nie przekracza połowy klatki (`PartialOcrSeed`). Parametry profilu mogą zmieniać część tego
zachowania.

### Generacja sceny i praca w toku

- Wykryte globalne cięcie albo potwierdzony silny ruch unieważnia generację sceny.
  Sesja czyści pamięć odtwarzania i usuwa nieaktualne bloki. Wyjątkiem są już
  wyświetlone źródła, których pełne piksele nadal pasują do zapamiętanego skrótu RGB.
  Pasek napisów usuwa treść powiązaną ze znikającymi blokami, bez odnowienia czasu.
  Bez zachowanych bloków czyszczone są oba widoki. Automatyczne czyszczenie
  nie odwołuje decyzji użytkownika o ręcznym ukryciu nakładki.
- Podczas dłuższego OCR i tłumaczenia sesja przechwytuje dodatkowe klatki kontrolne.
  Pierwsza okresowa kontrola OCR czeka 1,5 interwału; kolejne wracają do zwykłego
  rytmu. OCR trwający co najmniej jeden interwał wymaga także świeżej kontroli
  po zakończeniu. Przy 6 FPS pierwsza kontrola trwającego OCR może nastąpić o 83 ms później.
- Po unieważnieniu kończy się oczekiwanie nieaktualnej klatki na tłumaczenie.
  Już wysłane zadanie może dokończyć się i uzupełnić cache. Nowa klatka korzysta
  z drugiego miejsca; przy obu zajętych sesja sprawdza scenę bez kolejki starych opisów.
- `BoundedTranslationWork` nadzoruje najwyżej dwa zadania tłumaczeń. Stop anuluje
  i domyka także pracę pozostawioną przez poprzednie widoki (także porzucone lokalne
  sprawdzenia cache).
- Klatka, której cały tekst jest znany lokalnie (korekty, słownik, cache), idzie szybką
  ścieżką bez miejsca w kolejce tłumaczeń — nie czeka za wolnym zapytaniem starszej klatki
  i nie rezerwuje znaków. Klatka z choć jednym nowym tekstem działa jak dotąd.
- Okno stabilności 250 ms liczy się od pierwszej próbki, która zauważyła zmianę, a nie od
  końca poprzedniego OCR/tłumaczenia. Obraz, który dalej się zmienia, czeka na stabilność
  albo najwyżej 600 ms ciągłych zmian liczonych od końca oczekiwania. Ten sam moment zmiany
  jest początkiem pomiaru „Zmiana → napis” (`ChangeToTextTracker`).
- Zmiana konfiguracji ma osobny token, który blokuje późny zapis do poprzedniego
  cache. Zmiana sceny i zmiana konfiguracji mają różne skutki dla tego zapisu.

### Tekst lokalny i prezentacja

Podobny nowy odczyt wymaga kolejnych wiarygodnych potwierdzeń. Powrót starego tekstu
albo brak obserwacji w badanym obszarze przerywa serię. Potwierdzona zmiana treści
(podobieństwo odczytów < 0,5) usuwa stary blok przed oczekiwaniem na dostawcę; potwierdzony
wariant tego samego napisu (≥ 0,5) zostaje do aktualizacji z nowym tłumaczeniem, więc napis
nie znika na czas tłumaczenia. Brudniejszy, niepowiązany i niewiarygodny odczyt
(`LiveReadingStabilizer.IsImplausibleReading`) nie potwierdza bloku i liczy się jak brak,
chyba że odcisk pola się nie zmienił (wtedy potwierdza) albo przechwycenie pokazuje w polu
0,5–2× tyle pikseli rdzenia liter co ostatnie potwierdzenie (`KnownTextReference.IsPresent`;
blok zostaje bez nowego braku). Wiarygodny, ale brudniejszy niepowiązany odczyt zastępuje
wyświetlany blok dopiero po dwóch identycznych odczytach z rzędu; czysty nowy napis (jakość
odczytu co najmniej 0,9) — od razu.

Brak OCR nad całym dawnym polem, które stało się jednolite i miało znany kontrast, pozwala
na lokalne usunięcie (`TextPresenceProbe`), także od razu przy przechwyceniu. Na teksturze
dowodem jest `KnownTextAbsenceProbe`: w całym, nieuciętym polu bloku żaden piksel (poza 0,1%)
nie zachował ćwierci dawnego kontrastu luminancji między tekstem a tłem (kontrast kolorów
wzorca co najmniej 48). Wzorzec (`KnownTextReference`) pochodzi z ostatniego odczytu, który
potwierdził blok. Gdy sonda mówi „nieobecny” już przy przechwyceniu z istotną zmianą nachodzącą
na blok (bez cięcia sceny i bez zapasowego zrzutu ekranu), blok jest tylko podejrzany: jego pole
wchodzi do najbliższego OCR, a trwający przebieg, który go obejmuje, jest porzucany i ponawiany.
Blok znika, gdy OCR obejmujący całe jego pole go nie widzi, a sonda na tej klatce mówi
„nieobecny”. Przygaszenie i zmiana barwy przy najechaniu nie są
zniknięciem, dopóki zostaje ćwierć kontrastu. Zapasowy zrzut ekranu, ucięte pole i niepewne
kolory nie dają dowodu — wtedy blok znika po okresie łaski (trzeci przebieg OCR bez niego).

Blok zdjęty bez cięcia sceny po okresie łaski albo z dowodem zniknięcia w pikselach (sonda
tekstury, jednolite przykrycie wykryte przy przechwyceniu) zostaje na 10 s duchem. Podobny
odczyt (≥ 0,5) go wskrzesza; jeśli jego linia zniknęła z paska napisów razem z blokiem, wraca
na pasek (`LiveSubtitleContent.Restore`; pasek z innymi liniami nie odnawia czasu).
Niewiarygodny odczyt (`IsImplausibleReading`) nad duchem jest odrzucany; wiarygodny niepowiązany
odczyt nad duchem usuniętym z dowodem w pikselach jest nowym napisem i dziedziczy styl, a nad
duchem po samych brakach OCR brudniejszy niepowiązany odczyt nadal jest odrzucany.

Podczas oczekiwania kontrolowane są też pola zaakceptowanych źródeł. Jeśli źródło
zostało jednoznacznie przykryte, klatka nie jest publikowana, a wszystkie jej
obszary trafiają do ponownego odczytu. Pozwala to zachować również pierwsze menu,
które jeszcze nie dostało wyniku. Lokalne usunięcie aktualizuje tylko powiązane
źródła paska napisów i nie odnawia jego czasu wygasania.

Położenie ma tolerancję 2 fizycznych pikseli na każdej osi, oddzielną od stabilizacji
rozmiaru. To aktualizacja przy odczycie OCR; między odczytami napisy z łatką śledzi
`GlyphTracker` (niżej, „Ruch kamery”).
`TextRegionFingerprint` porównuje wszystkie RGB źródła z marginesem 3 px, ignorując
alpha i padding. W pamięci pozostają tylko skrót XxHash128 (niekryptograficzny), geometria
pola i rozmiar całej klatki. Referencja powstaje z natywnej klatki OCR; każda kontrola
wykorzystuje już przechwycony obraz i bufor jednego wiersza. Pole musi być pełne, mieć
4–262144 pikseli, niejednolity obraz i znany kontrast tekstu. Skalowany odczyt, fallback, zmiana rozmiaru
lub brak dowodu zachowują wcześniejsze reguły usuwania. Dowód może też zachować
pominięty przez OCR blok, jeśli żaden nowy zaakceptowany blok nie zajmuje jego miejsca.

Jest to ochrona identycznego obrazu, bez semantycznego rozpoznawania HUD-u. Animowane
lub przezroczyste tło może ją wyłączyć. Generacja sceny nadal odrzuca wszystkie stare
wyniki w toku, także pierwsze tłumaczenie jeszcze niewyświetlonego menu.

### Korpus w sesji live

Z korpusem aktywnego profilu bloki po OCR przechodzą bramkę `ShouldTranslateLive` (szum nie
idzie do dostawcy ani na nakładkę), a `LiveBlockKeyer` liczy klucz bloku z `CorpusIdentity`:
kolejne, dłuższe odczyty tej samej kwestii i drżenie OCR etykiety są tym samym blokiem
nakładki — aktualizacja w miejscu, bez stabilizatora podmian i bez nowego wpisu na pasku
napisów. Bez korpusu bramka to `JunkFilter`, a klucz liczy się z tekstu odczytu jak dotąd.
Filtr anty-sprzężeniowy (odczyt równy wyświetlanemu tłumaczeniu nie wraca do tłumaczenia)
pomija bloki o tłumaczeniu identycznym z oryginałem tylko przy umiejscowieniu „Na oryginale
(zakrywa)”, w którym nakładka takich bloków nie rysuje, albo gdy działa wykluczenie nakładki
z przechwytywania (`LiveSessionOptions.IdentityEchoSafe`).

### Tryb „Na oryginale (zakrywa)”: łatka i kwestia pisana literami

Oba mechanizmy włącza okno główne tylko przy umiejscowieniu „Na oryginale (zakrywa)” poza
trybem paska napisów (`LiveSessionOptions.BuildGlyphCovers`, `HoldTypingPrefixes`).

- **Łatka liczona równolegle z tłumaczeniem.** Po OCR i próbkach kolorów sesja uruchamia
  w tle budowę łatek dla bloków klatki (`GlyphCoverBuilder.BuildForBlock`, `Parallel.For`
  na najwyżej 4 wątkach) i równocześnie próbę lokalną oraz tłumaczenie; po tłumaczeniu czeka
  na łatki (`LiveFrameDiagnostics.GlyphCoverMs`, `GlyphCoverWaitMs`). Łatka powstaje z tej samej
  przechwyconej klatki co OCR i zostaje w pamięci. Gdy podpis pola (siatka 12×4 jasności)
  różni się mniej niż `GlyphCoverBuilder.StaticSignatureTolerance` przy tym samym boxie
  (±2 px), ostra łatka poprzedniego przebiegu jest używana ponownie bez obliczeń; podpis
  zmieniony umiarkowanie (tło się rusza, najechany wiersz) przy tym samym boxie daje łatkę
  miękką (całe pole, wygaszony brzeg), a gdy obraz stanie, następny przebieg wraca do ostrej.
  Nieudana budowa zostawia łatkę poprzedniego przebiegu, a bez łatki blok jest rysowany
  dawną ścieżką (rozmyta kopia tła). Koszt i zachowanie: ADR-015 i
  [ROADMAP.md → Runda 2026-10-06 (5)](ROADMAP.md).
- **Prezentacja.** `OverlayWindow` najpierw zbiera głosy grubości wszystkich bloków aktualizacji,
  potem `OverlayFonts.ChooseStyleWeight` daje jedną grubość blokom w tym samym stylu (krój,
  wysokość liter, kolor tekstu, obrys), a
  `OverlayBlockRenderer.LayoutNativeElement` ustawia polski tekst na linii bazowej oryginału,
  z rozmiarem z wysokości liter nad linią bazową, wyrównaniem i dopasowaniem szerokości;
  jednowierszowy napis bez ikon i o nieznanym wyrównaniu, którego środek tuszu leży w 1%
  szerokości monitora (co najmniej 8 px) od środka monitora, jest wyśrodkowany.
  Tłumaczenie identyczne z oryginałem nie jest rysowane (widać grę), a ikona klawisza przed
  napisem i ikonka za nim zostają nietknięte.
- **Wstrzymanie kwestii pisanej literami.** Nowy blok, którego odczyt jest niedokończoną kwestią
  korpusu (`TranslationPipeline.IsCorpusPrefix`), nie trafia ani do tłumaczenia, ani na
  nakładkę, dopóki tekst rośnie: sesja prosi o kolejny odczyt jego pola. Pełne tłumaczenie
  idzie, gdy odczyt przestaje być początkiem kwestii (np. jest całą linią), gdy liczba liter nie
  rośnie przez `TypingPrefixSettleTime` (0,9 s) albo po `TypingPrefixHoldLimit` (8 s). Blok już
  wyświetlany pod tym samym kluczem nie jest wstrzymywany. Decyzja: ADR-017.

### Ruch kamery: śledzenie, szybkie odświeżanie i Windows Graphics Capture

Działa przy łatkach (umiejscowienie „Na oryginale (zakrywa)” poza paskiem napisów) i bez
zapasowego zrzutu ekranu.

- **Przechwytywanie.** `GraphicsCaptureSource` trzyma sesję Windows Graphics Capture okna gry
  (bez kursora; ramka wyłączana przez `IGraphicsCaptureSession3`, a gdy system na to nie
  pozwala — WGC nie jest już próbowane w tej sesji live). Sesja WGC startuje, gdy analiza
  klatki pokaże trwający ruch (co najmniej dwie próbki z mocną zmianą przy działającym
  śledzeniu), i jest zamykana po 2 s bez ruchu; każda nowa klatka jest kopiowana na GPU do
  własnej tekstury, a pełna klatka (cykl sesji) i wycinki (śledzenie) są czytane przez teksturę
  staging. Pełna klatka z WGC jest brana tylko w trwającym ruchu i tylko nowa (inna niż przy
  poprzednim przechwyceniu). Na stojącym obrazie nie ma sesji WGC: WGC oddaje zmianę dopiero po
  złożeniu ekranu (w SceneReplay zmiana sceny była widziana o jeden cykl później), a sama otwarta
  sesja opóźniała w SceneReplay zauważenie zmiany o 20–45 ms. Klatka o rozmiarze innym niż widoczna ramka
  okna, czarna albo starsza niż 1,5 s oznacza `PrintWindow` dla tego przechwycenia.
  `liveGraphicsCapture: false` wyłącza WGC.
- **Okno bez PrintWindow.** Gdy `PrintWindow` da pusty obraz i sesja musiałaby użyć zrzutu ekranu
  (gra na pełnym ekranie), WGC staje się głównym źródłem na stałe dla tej sesji live, także na
  stojącym obrazie: klatka WGC to obraz samego okna, więc nie jest „zapasowym zrzutem ekranu”
  i śledzenie działa. Pierwsze klatki przed startem WGC idą przez zrzut ekranu; ostrzeżenie
  o zrzucie ekranu (i komunikat o pełnym ekranie) sesja pokazuje dopiero po 6 kolejnych zrzutach
  albo gdy WGC jest niedostępne. `LiveSessionOptions.PreferGraphicsCapture` wymusza ten tryb
  (MotionLab `--capture wgc`).
- **Śledzenie.** Łatka bloku niesie `GlyphTrack`: punkty wnętrza liter i obrysu (albo pierścienia
  tła) w układzie roboczym łatki. Na każdej przechwyconej klatce (także kontrolnej w trakcie OCR)
  `GlyphTracker.Locate` szuka przesunięcia: najpierw wokół przewidywanego z poprzedniego ruchu,
  potem w promieniu do 72 px; dopasowanie musi mieć mały koszt, zachować kontrast liter wobec
  obrysu/tła i nie mieć równie dobrego kandydata daleko. Udane dopasowanie przesuwa blok
  i odświeża łatkę z bieżącej klatki (`GlyphCoverBuilder.Refill`); śledzony blok jest dowodem
  obecności dla pozostałych reguł. Zgubiony w ruchu blok znika od razu, poza ruchem — po dwóch
  klatkach bez liter.
- **Ruch nie unieważnia sceny**, gdy działa śledzenie (`LiveSceneValidity.Observe` z
  `tolerateMotion`): generacja zmienia się tylko przy cięciu widoku, a w ruchu OCR rusza najwyżej
  co 0,9 s (`TrackedMotionPause`). Nowy blok odczytany z klatki w ruchu jest szukany na świeżej
  klatce (zasięg 2,5× promienia śledzenia) i pokazywany w nowym miejscu; bez pewnego dopasowania
  czeka na kolejny odczyt.
- **Szybkie śledzenie.** Gdy ostatnia pełna klatka pokazała ruch, a WGC działa, sesja między
  cyklami (i w trakcie OCR) czeka na nową klatkę WGC i śledzi bloki na samych wycinkach,
  najwyżej `FastTrackFps` (30) razy na sekundę. Takie śledzenie tylko odświeża — o zdjęciu bloku
  decyduje pełny cykl albo dwa dowody braku liter.
- **Stabilność odczytów.** Ramki słów przy kącie tekstu są obracane wokół środka obrazu
  (`OcrGeometry.Unrotate`). Pusty wynik dużego obrazu jest ponawiany w pasach (`OcrBands`).
  Blok zlepiający napisy, które przy przechwyceniu były na swoich miejscach, dzieli
  `TextBlockSplitter`. Krój i rozmiar tekstu są trzymane dla elementu nakładki
  (`GameTextElement.StyleAscent`), a grubość — wspólna dla klasy stylu (`OverlayFonts.ClassWeight`).

### Diagnostyka i ograniczenia

`LiveFrameDiagnostics` opisuje czas przygotowania aktualizacji, operację OCR,
liczbę i koszt kontroli obrazu oraz czas budowy łatek i czekania na nie po tłumaczeniu
(`GlyphCoverMs`, `GlyphCoverWaitMs`), użycie WGC i liczbę oraz łączny czas szybkich śledzeń
(`GraphicsCapture`, `FastTracks`, `FastTrackMs`). Etapy czasowo nakładają się, a odrzucona klatka
nie ma diagnostyki ukończonego przebiegu. Te dane nie mierzą faktycznego rysowania.

[LiveDiag](../tools/GameTranslatorOverlay.LiveDiag/README.md) i
[SceneReplay](../tools/GameTranslatorOverlay.SceneReplay/README.md) używają Mocka,
prywatnego cache i zablokowanego HTTP. SceneReplay ma scenariusze starego napisu (`stale-*`)
i kwestii pisanej literami (`typing`, `typing-nohold`).
[OverlayPreview](../tools/GameTranslatorOverlay.OverlayPreview/README.md) składa prawdziwą
nakładkę (`OverlayBlockRenderer`) na zapisanych klatkach gry przez Windows OCR i kopię roboczą
bazy (HTTP zablokowane, brak w bazie = Mock), a
[CorpusEval](../tools/GameTranslatorOverlay.CorpusEval/README.md) mierzy przyciąganie do korpusu
(m.in. `replay` przez prawdziwy `TranslationPipeline` z Mockiem).
[MotionLab](../tools/GameTranslatorOverlay.MotionLab/README.md) nagrywa okno gry i odtwarza
nagranie w czasie rzeczywistym przez prawdziwą sesję live i prawdziwe `OverlayWindow` (Mock,
kopia bazy), porównując nakładkę z offline OCR każdej klatki (metryki ruchu). Żadne z tych
narzędzi nie pokazuje fizycznej nakładki nad działającą grą. Wyniki porównawcze oraz ich ograniczenia
opisuje [ROADMAP.md](ROADMAP.md); testy integracji z rzeczywistym pulpitem należy
odróżniać od czystej logiki [TESTING.md](TESTING.md).
