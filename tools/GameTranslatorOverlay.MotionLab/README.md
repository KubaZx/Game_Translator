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
MotionLab truth NAGRANIE [--every 1] [--workers 4] [--force] [--corpus PLIK] [--profile escape-academy]
MotionLab replay NAGRANIE --out KATALOG [--speed 1] [--provider-delay-ms 500] [--cache KOPIA_cache.db|none]
         [--corpus PLIK] [--profile escape-academy] [--placement cover] [--live at-source] [--render-every 1]
         [--composite-scale 0.5] [--opacity 0.4] [--font-size 0] [--font-family auto] [--tail-ms 2000]
         [--max-frames 0] [--lookahead 10] [--show-overlay] [--no-analyze] [--keep-work] [--no-angle-probe]
MotionLab analyze KATALOG_REPLAY [--truth PLIK] [--worst 15]
```

`NAGRANIE` to katalog z `f00000.jpg…` i `frames.jsonl` albo sama nazwa w katalogu nagrań
(`LabDefaults.RecordingsRoot`). Domyślne `--cache` i `--corpus` wskazują lokalne kopie z rundy
2026-10-05 (`LabDefaults`); na innej maszynie podaj je jawnie (`--cache none` = pusta baza w pamięci).
Ustawienia nakładki domyślnie jak u gracza: `cover`, `at-source`, krycie 0,4, czcionka auto (z profilu).

Typowy pomiar (to samo dla każdego nagrania; nie uruchamiaj kilku replay naraz):

```powershell
$exe = "tools\GameTranslatorOverlay.MotionLab\bin\Release\net10.0-windows10.0.19041.0\GameTranslatorOverlay.MotionLab.exe"
& $exe truth pokoj-ruch2 --workers 6
& $exe replay pokoj-ruch2 --out "C:\...\GTO Diagnostics\20261006-ruch\baza\pokoj-ruch2"
```

`replay` na końcu sam uruchamia `analyze` (chyba że `--no-analyze`); `analyze` można powtarzać na
gotowym katalogu (np. po zmianie metryk) bez ponownego odtwarzania.

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
  `BuildGlyphCovers`/`HoldTypingPrefixes` dla cover, `IdentityEchoSafe`) + `EnableDiagnostics`.
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

## Prawda (`truth`)

Offline Windows OCR każdej klatki w pełnej rozdzielczości (te same zasady skalowania co pełna klatka
w live), ale **bezpośrednio przez `Windows.Media.Ocr`**: przy `TextAngle ≠ 0` silnik zwraca ramki
w układzie wyprostowanym wokół środka obrazu, więc każdy narożnik ramki słowa jest obracany o kąt
wokół (W/2, H/2) i bierzemy prostokąt otaczający (`AngleAwareOcr.Unrotate`). Potem
`TextBlockGrouper` i bramka `ShouldTranslateLive` / `CorpusIdentity` z tym samym korpusem i profilem.
`truth.jsonl` (w katalogu nagrania) ma nagłówek i dla każdej klatki: `TextAngle`, ułamek zmienionych
i mocno zmienionych komórek siatki jasności (48×27 jak w sesji, progi 10 i 25) względem poprzedniej
klatki, czas OCR i bloki (tekst, box, tłumaczalny, tożsamość korpusu, skrót klucza jak
`LiveBlockKeyer`, wiersze z boxami). Plik z pasującym nagłówkiem jest używany ponownie (`--force`
liczy od nowa).

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
  i próbki „obserwuję” z mocnym ruchem ≥ 12% (odroczenie przez ruch).
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
