# MotionLab

Stanowisko pomiarowe trybu live **w ruchu** (narzędzie dev). Nagrywa okno gry, odtwarza nagranie
w czasie rzeczywistym w oknie WPF, uruchamia na nim **prawdziwą** `LiveTranslationSession`
i **prawdziwe** `OverlayWindow`, a potem porównuje to, co pokazała nakładka, z niezależną
„prawdą” z offline OCR każdej klatki. Działa wyłącznie lokalnie: HTTP jest zablokowane, dostawca
to Mock, baza tłumaczeń i korpus są zawsze **kopiowane** do katalogu roboczego (oryginałów nie
otwiera). Nagrania, prawda, kompozyty, łatki i raporty zawierają obrazy i teksty z gry — trzymaj je
poza repo (np. `GTO Diagnostics\...`).

## Polecenia

```
MotionLab record "fragment tytułu okna" --out KATALOG [--seconds 30] [--fps 10] [--quality 92]
MotionLab truth NAGRANIE [--every 1] [--workers 4] [--force] [--plain] [--corpus PLIK] [--profile escape-academy]
MotionLab replay NAGRANIE --out KATALOG [--speed 1] [--provider-delay-ms 500] [--cache KOPIA_cache.db|none]
         [--corpus PLIK] [--profile escape-academy] [--placement cover] [--live at-source] [--render-every 1]
         [--composite-scale 0.5] [--opacity 0.4] [--font-size 0] [--font-family auto] [--tail-ms 2000]
         [--max-frames 0] [--lookahead 10] [--show-overlay] [--no-analyze] [--keep-work] [--no-angle-probe]
         [--capture auto|wgc|gdi] [--phase-ms 0] [--no-lost-read]
MotionLab analyze KATALOG_REPLAY [--truth PLIK] [--plain-truth] [--worst 15]
MotionLab series NAGRANIE --out KATALOG [--runs 5] [--phases auto|0,120,240] [--period-ms 600] [--reanalyze]
         [--truth PLIK] [--plain-truth] [--timeout-min 0] [pozostałe opcje replay]
MotionLab compare SERIA_A SERIA_B [--out PLIK.md] [--alpha 0.05] [--all]
MotionLab lost NAGRANIE --out KATALOG [--placement cover|below] [--ocr-every 9] [--max-frames 0] [--periodic-every 3]
         [--corpus PLIK] [--profile escape-academy]
```

`NAGRANIE` to katalog z `f00000.jpg…` i `frames.jsonl` albo sama nazwa w katalogu nagrań
`<GTO Diagnostics>\20261006-ruch\nagrania`. `<GTO Diagnostics>` to folder ze zmiennej środowiskowej
`GTO_DIAGNOSTICS`, a bez niej — folder `GTO Diagnostics` obok katalogu repozytorium (`LabDefaults`).
Z niego biorą się też domyślne `--cache` i `--corpus` (lokalne kopie z rundy 2026-10-05); gdy ich
nie ma, podaj je jawnie (`--cache none` = pusta baza w pamięci).
Ustawienia nakładki domyślnie jak u gracza: `cover`, `at-source`, krycie 0,4, czcionka auto (z profilu).

Typowy pomiar zmiany kodu — seria na kodzie bazowym i seria na zmienionym, potem porównanie
(to samo dla każdego nagrania; nigdy kilka replay naraz):

```powershell
$exe = "tools\GameTranslatorOverlay.MotionLab\bin\Release\net10.0-windows10.0.19041.0\GameTranslatorOverlay.MotionLab.exe"
& $exe truth pokoj-ruch2 --workers 6
& $exe series pokoj-ruch2 --out "C:\...\GTO Diagnostics\RUNDA\baza\pokoj-ruch2" --runs 5
# … przebudowa z nowym kodem …
& $exe series pokoj-ruch2 --out "C:\...\GTO Diagnostics\RUNDA\nowy\pokoj-ruch2" --runs 5
& $exe compare "C:\...\RUNDA\baza\pokoj-ruch2" "C:\...\RUNDA\nowy\pokoj-ruch2"
```

Pojedynczy przebieg: `replay pokoj-ruch2 --out KATALOG`. `replay` na końcu sam uruchamia `analyze`
(chyba że `--no-analyze`); `analyze` można powtarzać na gotowym katalogu (np. po zmianie metryk) bez
ponownego odtwarzania. Odtwarzanie wymaga aktywnej sesji Windows z pulpitem: przy sesji rozłączonej
(np. po zamknięciu pulpitu zdalnego, `quser` pokazuje `Disc`) PrintWindow i zrzut ekranu nie
działają — `replay` kończy się wtedy od razu kodem 6, a `series` przerywa serię (ponowne uruchomienie
dokończy brakujące przebiegi).

## Pomiar seriami: faza, `series`, `compare`

Jeden przebieg nagrania nie wystarcza do oceny zmiany: w rundach 9–10 te same metryki tego samego
kodu różniły się między przebiegami o kilka–kilkanaście punktów (pokój: pokrycie tekstu ruchomego
w ruchu kamery 15,5–31,8%, dziury 3,5–9,8 s, mediana opóźnienia 259–911 ms). Wynik zależy od tego,
które klatki trafią do OCR, czyli od fazy harmonogramu sesji (sprawdzanie zmian co ~167 ms, wymuszony
OCR co 600 ms, czas OCR) względem klatek nagrania (co 100 ms), a ta faza w zwykłym `replay` jest
przypadkowa.

- **Faza** (`replay --phase-ms N`): okno odtwarzania pokazuje klatki w tych samych chwilach co zawsze,
  a sesja startuje N ms po pierwszej klatce (wątek taktujący wywołuje `Start()` dokładnie w tej
  chwili; kod aplikacji bez zmian). `0` = dotychczasowe zachowanie. Faza trafia do `replay.json`
  (`header.phaseMs`, faktyczna chwila startu: `sessionStartMs`). Analiza liczy od startu sesji: klatki
  wyświetlone i zakończone przed startem nie wchodzą do pokrycia, „obecne od startu sesji” to
  epizody sprzed startu + 1,5 s, a ich opóźnienie liczy się od startu. Dla fazy 0 metryki są
  identyczne jak wcześniej (sprawdzone na zapisanych przebiegach rundy 10). Ustalona faza nie czyni
  przebiegu deterministycznym — czas OCR i tłumaczenia waha się, więc harmonogram i tak się rozjeżdża;
  faza tylko rozkłada przebiegi serii równomiernie zamiast zostawiać to przypadkowi.
- **Seria** (`series`): N przebiegów `replay` **kolejno**, każdy w osobnym procesie (te same warunki
  startu co pojedynczy replay, bez rozgrzanych pamięci podręcznych z poprzedniego przebiegu),
  w podkatalogach `runNN-fazaXXXms` (zwykłe katalogi replay — `analyze` działa na nich jak dotąd),
  wyjście procesu w `runNN-fazaXXXms.log`. Fazy `auto`: ciąg złotego podziału w okresie `--period-ms`
  (domyślnie 600 ms = interwał wymuszonego OCR): 0, 371, 142, 512, 283, 54, 425, 196… — rozłożone
  równomiernie także względem klatek (reszty z 100 ms) i sprawdzania zmian, a seria dłuższa zaczyna
  się od tych samych faz co krótsza. Gotowe przebiegi (z `metrics.json` i tą samą fazą) są używane
  ponownie, więc przerwaną serię dokańcza ponowne uruchomienie, a `--runs 8` dokłada przebiegi do
  serii 5; przerwany przebieg jest przenoszony do `…niepelny-DATA`. `--reanalyze` liczy metryki
  gotowych przebiegów od nowa (np. po zmianie analizy), `--timeout-min` ogranicza przebieg (domyślnie
  3× długość nagrania + 10 min; przekroczenie kończy tylko ten proces). Pozostałe opcje idą do
  `replay`; seria z innymi opcjami niż zapisane w `series.json` wymaga nowego katalogu.
  Wynik po każdym przebiegu: `series.json` (dla każdej liczbowej metryki z `metrics.json`: n,
  mediana, Q1, Q3, IQR, min, max, średnia i wartości kolejnych przebiegów; `null` = brak) i `series.md`
  (kluczowe metryki, skupiska opóźnień, opóźnienie każdego epizodu w kolejnych przebiegach, czasy).
- **Skupiska opóźnień**: zdarzenia „wszystkie” (jak `latency.all`), „start w ruchu” i „start
  w spoczynku” (jak `latency.startedInMotion` / `startedAtRest`) ze wszystkich przebiegów serii:
  liczba pokazanych (razem, na przebieg i w każdym przebiegu), mediana zbiorcza z Q1–Q3, mediana
  median przebiegów, liczba niepokazanych. Mediana jednego przebiegu z ~15 zdarzeń w dwóch skupiskach
  (~0–100 ms i ~2,5 s) przeskakuje między nimi od jednego zdarzenia — liczba zdarzeń w skupiskach jest
  stabilniejsza niż ta mediana. Definicje metryk `metrics.json` są bez zmian.
- **Porównanie** (`compare A B`): dla każdej metryki obecnej w obu seriach mediany A i B z Q1–Q3,
  Δ = mediana B − mediana A, przesunięcie Hodgesa–Lehmanna (mediana różnic wszystkich par B−A),
  δ Cliffa (−1…1) i **dokładny dwustronny test Manna–Whitneya** na wartościach z przebiegów: rozkład
  sumy rang grupy B przy wszystkich podziałach zbioru wartości (rangi średnie przy remisach, liczony
  programowaniem dynamicznym, bez przybliżenia normalnego). Werdykt: „lepiej”/„gorzej” (kierunek z
  rodzaju metryki: więcej pokrycia lepiej; więcej dziur, nieaktualnych, strat HUD, opóźnienia,
  błędu położenia gorzej), „zmiana” (istotna różnica metryki bez kierunku, np. liczby OCR), „w
  rozrzucie”, „bez zmian (stała)”, „nierozstrzygalne” (żaden podział nie da p ≤ α — za mało przebiegów
  albo same remisy). Do tego skupiska opóźnień A/B i **epizody sparowane**: mediana opóźnienia
  każdego epizodu (ta sama klatka startu i tekst prawdy) z przebiegów A i z przebiegów B, liczba
  szybszych/wolniejszych w B i dokładny test znakowanych rang Wilcoxona na różnicach. Wynik:
  Markdown (domyślnie `B\compare-<A>.md`, kluczowe metryki, skupiska, epizody, pozostałe istotne
  różnice; `--all` dokłada tabelę wszystkich metryk) i JSON obok. Ostrzeżenia: różne nagrania,
  opcje replay, fazy, plik lub tryb prawdy, liczba wierszy prawdy, mniej niż 5 przebiegów.
- **Ograniczenia testu.** Przy 5 + 5 przebiegach najmniejsze osiągalne p to 2/252 ≈ 0,008, a p ≤ 0,05
  wymaga, by co najmniej 23 z 25 par A–B szło w jedną stronę (|δ| ≥ 0,84) — wykrywa tylko zmiany
  wyraźnie większe niż rozrzut przebiegów; 3 + 3 nie osiąga nigdy p < 0,1. Brak poprawki na wielokrotne
  porównania: rzeczywisty poziom testu dyskretnego (`size` w JSON, przy 5 + 5 bez remisów 0,032 na
  metrykę) jest sumowany jako „spodziewane przypadkiem” — przy 20 kluczowych metrykach ≈ 0,6
  fałszywego alarmu na porównanie, przy ~300 wszystkich ≈ kilka. Pojedyncza istotna metryka bez
  wsparcia w pokrewnych (np. sama `stale.events` bez `stale.seconds`) to raczej przypadek. Przebiegi
  serii nie są w pełni niezależne od otoczenia: obciążenie komputera innymi procesami zmienia czasy
  OCR, więc serie do porównania uruchamiaj możliwie blisko w czasie (albo naprzemiennie przez
  dokładanie przebiegów: `--runs 2`, `--runs 3`… na zmianę w obu katalogach). Metryki tego samego
  przebiegu są ze sobą skorelowane (pokrycie całości i HUD to w pokoju prawie to samo).
- **Sprawdzenie metody (runda 11).** Dwie serie po 5 na tym samym kodzie (pokój ~7,2 min na serię,
  prolog ~8,5 min): `compare` — 0 z 20 kluczowych metryk istotnych w obu nagraniach (spodziewane
  przypadkiem 0,5–0,7), wszystkich 1 z 292 i 4 z 299 (spodziewane ~7). W pokoju mediany serii różniły
  się o 0,1–0,3 pkt pokrycia przy rozstępie pojedynczych przebiegów 2–4,5 pkt, nieaktualne o 0,01 s
  przy rozstępie 0,9 s, a przebiegi z tą samą fazą zgadzały się kilka razy lepiej niż z inną (tylko
  w pokoju — w prologu ta sama faza nie zgadzała się lepiej). Seria 5 przebiegów NIE stabilizuje:
  w prologu `coverage.staticCameraMotion` (mediany 70,9 i 68,8 przy typowej różnicy dwóch przebiegów
  2,2 pkt) i `hud.losses` (5 i 3), w obu nagraniach `coverage.movingCameraMotion` — to metryki
  o małym mianowniku (`.seconds` obok `.percent`: ruch kamery w prologu 16 s tekst·czasu, HUD 9 s,
  tekst ruchomy w pokoju 6 s), a pokrycie przyjmuje tam kilka dyskretnych poziomów. Dla nich użyj
  n ≥ 8 albo opieraj ocenę na metrykach `.seconds`/`.count`. Serie walidacji liczyły się częściowo
  równolegle z pracą innego agenta (buildy i SceneReplay z Windows OCR; czasy OCR p90 w A1 wyższe
  niż w A2), więc jako punkt odniesienia dla nowego kodu liczy się serię bazową na bezczynnym
  komputerze, blisko w czasie.

## Jak działa `replay`

- Okno bezramkowe (Topmost, bez aktywacji) na własnym wątku STA, ustawione na **dokładnie** rozmiar
  klatki w pikselach fizycznych (DIP = px / DPI, jak SceneReplay/OcrTimingReplay); obraz 1:1
  (`NearestNeighbor`, `Stretch=Fill` w rozmiarze okna). Na starcie narzędzie robi PrintWindow okna
  i porównuje z JPEG — `fidelity` w `replay.json` (oczekiwane: ten sam rozmiar, średnia różnica ≈ 0).
- Klatki dekodują 3 wątki z wyprzedzeniem (`--lookahead`), a wątek taktujący (timeBeginPeriod 1 ms)
  podmienia źródło `Image` w chwili `tMs` z `frames.jsonl` (skalowane `--speed`). Dla każdej klatki
  zapisywane są: termin, chwila podmiany na wątku okna, pierwsze `CompositionTarget.Rendering` po niej
  i ewentualne czekanie na dekoder (`shown.jsonl`, podsumowanie w `replay.json`).
- Sesja: `LiveTranslationSession` z `WindowsOcrProvider` (opakowanym tylko logowaniem czasu do
  `ocr.jsonl`), opcje jak `MainWindow.StartLive` (Fps/Threshold z profilu, upscale, `NoticeEcho`,
  `RenderGlyphCovers`/`HoldTypingPrefixes` dla cover, `IdentityEchoSafe`) + `EnableDiagnostics`.
  `TranslationOrchestrator` z kopią bazy, korpusem w `<out>\app-data\corpus`, profilem z `profiles\`
  obok exe i Mock z opóźnieniem `--provider-delay-ms` (tekst spoza bazy = `[PL] …`). Przed startem:
  rozgrzanie krojów, łatek i silnika OCR (jak długo działająca aplikacja).
- Każda `LiveUpdate` trafia (jak w `MainWindow`, przez dyspozytor UI) do prawdziwego `OverlayWindow`
  — `OverlayHost.Apply` to kopia `MainWindow.HandleLiveUpdate` (ClearOverlay, ClearSubtitle,
  HideOverlay, Notice, Stopped, napisy, `UpdateLiveBlocks`). Okno nakładki działa normalnie
  (CoverMonitor, Topmost, wykluczenie z capture), ale ma `Opacity = 0`, więc nie widać go na ekranie
  (`--show-overlay` pokazuje je nad odtwarzaniem; i tak nie trafia do PrintWindow).
- `updates.jsonl`: każda aktualizacja z czasem emisji (wątek sesji) i zastosowania (wątek UI),
  statusem, flagami, `Notice`, diagnostyką i blokami (klucz, box względem okna, tłumaczenie,
  `SameAsSource`, tekst źródłowy i liczba braków odczytane refleksją z prywatnego `_displayed` sesji,
  łatka: X/Y/W/H względem boxu, rozmiar w px, Soft, Anchor, BuildMs, id łatki). Nowa łatka (nowa
  tablica pikseli) jest zapisywana do `patches\pNNNNN.png` + `patches.jsonl` z chwilą przechwycenia
  klatki, z której ją zbudowano (`emit − CaptureToUpdateMs`).
- W chwili każdej wyświetlonej klatki: migawka nakładki (widoczność, elementy z `_liveElements`
  — klucz, prostokąt, krycie, tożsamość elementu). Gdy wygląd się zmienił, warstwa jest renderowana
  przez `RenderTargetBitmap` (wycinek z `VisualBrush` całego `Grid` okna, w pełnej rozdzielczości)
  do `layers\LNNNNN.png`. Co `--render-every` klatek tło + warstwa → `frames\cNNNNN.jpg`
  (skala `--composite-scale`).
- Kąt tekstu w odczytach live: po każdym OCR sesji ten sam obraz jest (w tle, najwyżej jeden naraz,
  osobnym silnikiem) czytany ponownie bez sesji tylko po `TextAngle` (`ocr.jsonl` pole `angle`;
  wyłączenie: `--no-angle-probe`).
- Przechwytywanie (`--capture`): `auto` jak aplikacja (PrintWindow, Windows Graphics Capture
  w trwającym ruchu), `wgc` — WGC jako główne źródło jak przy grze bez PrintWindow
  (`LiveSessionOptions.PreferGraphicsCapture`), `gdi` — bez WGC. Migawki nakładki zapisują też
  rodzinę, grubość i rozmiar kroju elementu (`shown.jsonl`: `family`, `weight`, `em`), a obiekt łatki bloku
  w `updates.jsonl` (`cover`) — gęstość i wysokość liter linii wzorcowej (`density`, `ascent`).
- `--no-lost-read` wyłącza lokalny odczyt OCR bloku zgubionego w ruchu (`LiveSessionOptions.ReadLostBlocks`) —
  porównanie „przed/po” na tym samym kodzie; `replay.json` zapisuje ustawienie (`readLostBlocks`). Lokalne odczyty idą
  przez ten sam `LoggingOcr`, więc w `ocr.jsonl` są zwykłymi wywołaniami na wycinku.

## Zgubienia śladu offline (`lost`)

Wymaga prawdy bez podziału imion: `MotionLab truth NAGRANIE --plain` (plik `truth.jsonl`).

Deterministyczny pomiar bez okna i bez sesji: klatki nagrania po kolei (10 kl./s), co `--ocr-every` klatek pełny
Windows OCR klatki (te same bloki co w sesji: `LostTextRecovery.Blocks` — grupowanie, ikony, imię mówiącego, obszary
pomijane z profilu — i bramka `ShouldTranslateLive`), łatki `GlyphCoverBuilder.BuildForBlock`, a między odczytami
śledzenie tą samą ścieżką co sesja (`GlyphFollow.Plan/SearchArea/Locate/Follow`, ruch i cięcie sceny z `truth.jsonl`
przez `LiveSceneValidity`). Zgubienie = ta sama reguła co w `TrackDisplayedBlocks` (w ruchu każdy brak pewnego
dopasowania, bez ruchu dwa z rzędu, przesunięcie na inny blok). Dwa przebiegi:

- **„bez”** — zgubiony blok znika do następnego pełnego OCR (jak przed zmianą). Dla każdego zgubienia narzędzie
  dodatkowo sprawdza (bez wpływu na przebieg): odczyt OCR wycinka w przewidywanym miejscu w tej klatce, ponowne
  śledzenie starym śladem w następnej klatce (prędkość ×2, szerokie szukanie) i odczyt w następnej klatce — każde wobec
  prawdy (`truth.jsonl`: ten sam tekst, podobieństwo ≥ 0,6, w obszarze odczytu / w miejscu bloku).
- **„odczyt”** — zachowanie sesji z lokalnym odczytem: uprawnienie `LostTextRecovery.ShouldRead`, limity
  `LostTextReads` (najwyżej 4 odczyty na sekundę w sesji i 2 na blok w oknie 1 s), odczyt wycinka, w następnej klatce `LostTextRecovery.Resume`; do tej chwili
  blok stoi w miejscu (liczony jako „bez poparcia prawdy”, jeśli tekst odjechał).

Wynik (`--out`, poza repo): `events.jsonl` (zgubienia: tekst, ruch/cięcie, jadący, litery w starym miejscu, prędkość,
obszar odczytu, prawda, wyniki strategii, czas OCR), `periodic.jsonl` (co `--periodic-every` klatek w ruchu: OCR
ciasnego wycinka wokół każdego śledzonego bloku — koszt i zgodność z pokazanym tekstem), `unsupported.jsonl` (klatki
ruchu z blokiem bez poparcia prawdy w obu przebiegach — klatka, blok, czy czekał na odczyt, gdzie prawda widzi ten sam
tekst; pusta lista = prawda tego tekstu w klatce nie widzi), `summary.json`, `RAPORT.md`
(pokrycie tekstu w ruchu i w spoczynku jako pary klatka×blok prawdy, bloki bez poparcia prawdy, zgubienia, odczyty,
odzyskane w miejscu prawdy i gdzie indziej, odzyskane zgubione w następnej klatce, koszt OCR wycinka i pełnej klatki).
Czasy OCR zależą od obciążenia komputera; liczby zgubień i odzysków nie (ten sam wynik przy każdym przebiegu, o ile
Windows OCR czyta te same klatki tak samo). Ograniczenia: harmonogram pełnego OCR jest stały (co 0,9 s), a nie jak
w sesji (stabilizacja, wycinki, odczyty potwierdzające); brak opóźnienia OCR i tłumaczenia; odczyt rozstrzyga się
zawsze w następnej klatce (w sesji pętla budzi się po zakończeniu odczytu i rozstrzyga przy najbliższym przechwyceniu; odczyt dłuższy niż 300 ms wygasa i blok jest zdejmowany przy kolejnym przechwyceniu).

## Prawda (`truth`)

Offline Windows OCR każdej klatki w pełnej rozdzielczości (te same zasady skalowania co pełna klatka
w live), ale **bezpośrednio przez `Windows.Media.Ocr`**: przy `TextAngle ≠ 0` silnik zwraca ramki
w układzie wyprostowanym wokół środka obrazu, więc każdy narożnik ramki słowa jest obracany o kąt
wokół (W/2, H/2) i bierzemy prostokąt otaczający (`AngleAwareOcr.Unrotate`). Potem
`TextBlockGrouper`, jak w aplikacji `TextBlockSplitter.SplitLabels` (imię mówiącego albo krótka
etykieta w innym kolorze nad tekstem staje się osobnym blokiem; kolor tekstu z `BlockColorSampler`
na klatce) i bramka `ShouldTranslateLive` / `CorpusIdentity` z tym samym korpusem i profilem.
`truth-split.jsonl` (w katalogu nagrania) ma nagłówek i dla każdej klatki: `TextAngle`, ułamek zmienionych
i mocno zmienionych komórek siatki jasności (48×27 jak w sesji, progi 10 i 25) względem poprzedniej
klatki, czas OCR i bloki (tekst, box, tłumaczalny, tożsamość korpusu, skrót klucza jak
`LiveBlockKeyer`, wiersze z boxami). Plik z pasującym nagłówkiem jest używany ponownie (`--force`
liczy od nowa). `truth --plain` liczy prawdę bez dzielenia imion do `truth.jsonl` (format i plik jak
do rundy 10 — tamten plik zostaje nietknięty).

Bez dzielenia imion blok prawdy „imię + kwestia” miał tę samą tożsamość korpusu co blok kwestii
w nakładce, więc do położenia liczył się też wiersz imienia (w prologu stały błąd ~99 px przy każdej
kwestii z imieniem), a samo imię albo nazwa z powiadomienia, których aplikacja po podziale nie
tłumaczy, liczyły się jako „nigdy nie pokazane”.

**Tryb prawdy w analizie.** Domyślnie `truth-split.jsonl` + wygładzanie odosobnionych odczytów
tekstu stałego (niżej, „Epizod”); `analyze --plain-truth` (także `series --plain-truth`) = `truth.jsonl`
bez wygładzania, czyli metryki jak do rundy 10 (sprawdzone: te same `metrics.json` dla czterech
zapisanych przebiegów rundy 10, poza nowymi polami `truthMode` i `truthSmoothing`). Liczb z rund ≤ 10
nie porównuj z domyślnym trybem — porównuj serie liczone tym samym trybem (`compare` ostrzega przy
innym pliku albo trybie prawdy). `--truth PLIK` wskazuje plik wprost (wygładzanie zależy wtedy tylko
od `--plain-truth`). Tryb jest w `metrics.json` (`truthMode`: `split+smooth`, `plain`, …).

## Metryki (`analyze` → `metrics.json`, `RAPORT.md`, `sheets\`)

Oś czasu: chwila wyświetlenia klatki = pierwszy render WPF po podmianie (`shown.jsonl`); stan
nakładki = ostatnia **zastosowana** aktualizacja (ClearOverlay czyści, Blocks zastępuje listę,
HideOverlay ukrywa do następnych bloków). Blok `SameAsSource` jest w trybie cover niewidoczny, ale
liczy się jako pokrycie (nie ma czego tłumaczyć).

- **Epizod / wiersz prawdy**: wiersze tekstu z prawdy łączone między klatkami (podobieństwo
  ≥ 0,6, zawieranie albo przyrost przy pisaniu literami; odległość środków ≤ 30% szerokości kadru
  dla podobieństwa ≥ 0,85, inaczej 12%). Luki do 2 klatek są mostkowane z interpolacją boxu; tekst
  stojący w miejscu (IoU ≥ 0,5) może zniknąć z prawdy do 15 klatek (np. OCR gubi HUD nad jasnym tłem).
  Tłumaczalny = większość odczytów przeszła bramkę. **Statyczny** = ≥ 90% odczytów ma środek w pionie
  ±8 px od mediany i środek albo lewą albo prawą krawędź ±8 px (OCR raz czyta ikonę klawisza
  „Tab”, raz nie). Do metryk wchodzą epizody tłumaczalne z ≥ 3 odczytami i ≥ 300 ms.
  **Odosobnione inne odczyty** (domyślnie, poza `--plain-truth`): w epizodzie statycznym, który nie
  jest pisany literami, odcinek ≤ 15 kolejnych klatek z innym tekstem albo innym blokiem prawdy niż
  zgodny odczyt epizodu (najczęstszy tekst, a w nim najczęstszy skrót klucza bloku), otoczony z obu
  stron zgodnymi odczytami, jest traktowany jak zgodny odczyt (tekst, skrót klucza, tożsamość,
  bramka; box zostaje z klatki). To szum offline OCR, nie zmiana napisu w grze: „Tab s” zamiast „Tab
  Items”, ikona klawisza raz z napisem, raz osobnym blokiem, „X Hint”/„x Hint”, dwa wiersze HUD w jednym
  bloku. Bez tego nakładka pokazująca dobry napis dostawała naraz dziurę („miganie”), „HUD znika”
  i „nieaktualne”. Ile odczytów zastąpiono i które (z oryginalnymi odczytami): `truthSmoothing`
  w `metrics.json`, wiersz „Prawda” w `RAPORT.md`. Odcinek na początku albo końcu epizodu i dłuższy
  niż 15 klatek zostaje bez zmian.
- **Dopasowanie** bloku nakładki do wiersza: treść — ten sam skrót klucza co blok prawdy
  (tożsamość korpusu albo tekst) albo któryś wiersz tekstu źródłowego bloku ma podobieństwo ≥ 0,6;
  położenie — IoU ≥ 0,3 albo środek wiersza w boxie nakładki powiększonym o max(8 px, ½ wysokości wiersza).
- **Pokrycie**: dla każdej klatki i każdego wiersza epizodu, w połowie czasu wyświetlania klatki —
  czy nakładka ma dopasowany blok (treść + położenie); ważone czasem klatki. Osobno: teksty statyczne
  (HUD) / ruchome, kamera w ruchu (mocne zmiany ≥ 12% komórek między klatkami — ten sam próg co
  `MotionThreshold`) / w spoczynku, klatki z kątem tekstu |α| ≥ 0,5° / bez.
- **Opóźnienie**: od wyświetlenia pierwszej klatki epizodu do pierwszej chwili (klatka albo
  zastosowanie aktualizacji), w której wiersz jest pokryty. Mediana, p90, max, lista; „nigdy” gdy nie
  pokryto do końca epizodu. Epizody obecne od startu sesji (< 1,5 s) są raportowane osobno;
  pisane literami dodatkowo od końca pisania (≥ 95% liter).
- **Nieaktualne**: czas, przez który widoczny blok nakładki nie jest poparty prawdą w bieżącej klatce
  (mniej niż połowa jego wierszy ma dopasowanie treść + położenie). Przyczyna: „przesunięty” (ta sama
  treść jest w kadrze gdzie indziej; podana odległość), „zniknął”, albo „nieznany w prawdzie” (tekstu
  bloku nie ma w żadnej klatce prawdy — zwykle inny odczyt live; liczone osobno, poza sumą). Suma s,
  liczba zdarzeń ≥ 200 ms, najdłuższe.
- **Położenie**: dla widocznych bloków z dopasowaną treścią wszystkich wierszy — przesunięcie boxu nakładki
  względem sumy dopasowanych wierszy prawdy: √(Δy² + Δx²), gdzie Δy = różnica środków w pionie, a Δx = najmniejsza
  z różnic lewych krawędzi, prawych krawędzi i środków (blok „Hint” bez ikony „X” nie jest przesunięciem); px,
  tylko ≤ 25% szerokości kadru. „Pokryte” = także położenie się
  zgadza; „dowolne” = sama treść. Osobno bloki, których box pochodzi z pełnego OCR z kątem (prawda albo
  sonda live ≥ 0,5°), i z prostego.
- **Miganie**: w epizodzie dziura pokazany → brak → pokazany (pokazany = blok z pasującą treścią
  w pobliżu wiersza: środek w boxie powiększonym o max(40 px, 2× wysokość)); liczba dziur, czas.
  Dodatkowo `elementRecreations`: zmiana elementu WPF tego samego klucza (od nowa fade-in).
- **HUD znika**: przejścia pokryty → niepokryty wiersza statycznego w kolejnych klatkach; przyczyna
  z aktualizacji, która to zrobiła: czyszczenie (zmiana widoku), usunięcie lokalne bez OCR, odczyt
  wycinka, pełny odczyt z kątem tekstu, pełny odczyt bez kąta, albo zmiana samej prawdy.
  „HUD przekręcony”: inny odczyt (podobieństwo 0,3–0,95) w miejscu HUD.
- **Świeżość łatki** (tylko widoczne bloki z łatką): łatka jest rysowana tam, gdzie powstała
  (`AnchorTo` zachowuje położenie bezwzględne). *Błąd szwu* = średnia |kolor nieprzezroczystego
  piksela łatki na brzegu − kolor bieżącej klatki tuż za brzegiem maski (pierwszy piksel o α ≈ 0, do
  16 px), na kanał, do 800 punktów; dla porównania ten sam błąd na klatce budowy. *Pierścień* = średnia
  |tło wokół prostokąta łatki (2–10 px od brzegu) w bieżącej klatce − w klatce budowy|. Łatka
  „zamrożona” = pierścień ≥ 20. Mediana/p90 w ruchu kamery i w spoczynku.
- **Koszt**: z `Diagnostics` (klatka→aktualizacja, capture, OCR, tłumaczenie, łatki, kontrole sceny),
  liczba OCR (pełne/wycinki/porzucone), cięcia sceny, czyszczenia, whiff, przerwy bez OCR ≥ 1 s
  i próbki „obserwuję” z mocnym ruchem ≥ 12% (odroczenie przez ruch); lokalne odczyty zgubionych bloków
  (`lostReads`, `lostRecovered`, `lostReadMs` — liczniki z ostatniej diagnostyki) i liczba aktualizacji
  „napis zniknął albo odjechał” (`trackingRemovals`).
- **Kroki ruchu**: `NAGRANIE.kroki.txt` obok katalogu; przesunięcie zegara dopasowane (0–3 s,
  co 50 ms) tak, by kroki ruchu pokrywały się z klatkami z ruchem; tabela metryk per krok.
- **Najgorsze momenty**: odcinki BRAK (opóźnienie/zgubione/pisanie/start), NIEAKTUALNE, POZYCJA,
  MIGANIE, ŁATKA, punktowane czasem (× waga) i deduplikowane; dla każdego stykówka
  `sheets\NN-typ-fKLATKA.jpg`: oryginał z ramkami (zielone = prawda, fioletowe = bloki nakładki,
  żółte = łatka) | oryginał + nakładka, a pod spodem nakładka −1/−0,5/+0,5/+1 s.

## Ograniczenia (czego stanowisko nie odwzorowuje wiernie)

- Nagranie ma 10 kl./s: między klatkami obraz stoi, a sesja (6 analiz/s) widzi skoki co ~100 ms
  zamiast płynnego ruchu z 60+ FPS — wykrywanie ruchu i kontrole sceny dostają nieco inne różnice.
  Obraz to JPEG q92 (artefakty kompresji ≠ piksele gry); okno odtwarzania nie ma kosztu gry (GPU/CPU).
- Prawda to też Windows OCR (pełna klatka): gubi i przekręca tekst nad ruchomym tłem, a wycinki live
  bywają czytane lepiej. Mostkowanie luk łagodzi to, ale pokrycie/nieaktualne mają szum ±kilka %.
  Odczyty, których prawda nie zna, są wydzielone („nieznany w prawdzie”).
- Pokrycie mierzy logiczny stan nakładki z aktualizacji (bez 140 ms fade-in i bez opóźnienia
  kompozycji DWM); obraz nakładki (warstwy, kompozyty, stykówki) to `RenderTargetBitmap`, a nie zrzut
  ekranu — tekst bez ClearType, bez efektów okna warstwowego. Okno nakładki ma `Opacity 0`.
- Czas „wyświetlenia” = zdarzenie `CompositionTarget.Rendering` po podmianie źródła, nie fizyczny
  vsync. Kiedy sesja przechwyciła daną klatkę, wiemy tylko z czasu (`CaptureToUpdateMs`).
- Tekst źródłowy bloków i lista elementów nakładki są czytane refleksją z prywatnych pól
  (`LiveTranslationSession._displayed`, `OverlayWindow._liveElements`); `replay.json` mówi, czy się udało.
  Bez tego dopasowanie treści działa tylko po skrócie klucza.
- Sonda kąta OCR dokłada w tle jeden dodatkowy OCR naraz (inny silnik) — nie blokuje sesji, ale
  zużywa CPU, którego w aplikacji nie ma.
- Mock zamiast prawdziwego dostawcy: opóźnienie stałe, bez błędów sieci; tekst spoza kopii bazy
  dostaje `[PL] …`.
