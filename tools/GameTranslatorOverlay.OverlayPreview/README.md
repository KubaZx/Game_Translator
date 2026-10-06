# OverlayPreview — podgląd nakładki na zapisanych klatkach z gry (narzędzie dev)

Renderuje dokładnie to, co rysuje okno nakładki w trybie live „Na oryginale (zakrywa)”, ale na
zapisanej klatce zamiast na żywej grze. Służy do poprawiania wyglądu tłumaczeń bez uruchamiania
gry: ta sama klatka daje za każdym razem ten sam obraz, więc zmiany w rysowaniu można porównać
„przed/po”. Wymaga Windows (systemowe OCR i WPF). Nie wysyła niczego do sieci (HTTP zablokowane),
nie używa kluczy, nie czyta ustawień ani bazy gracza.

## Co robi

1. Wczytuje klatkę PNG/JPG (zrzut okna gry w natywnej rozdzielczości).
2. Przetwarza ją jak pierwszy pełny przebieg sesji live: skala OCR jak w `LiveTranslationSession`,
   Windows OCR, `TextBlockGrouper`, bramka `ShouldTranslateLive` (JunkFilter + szum korpusu),
   klucze `LiveBlockKeyer`, kolory/kontur/tekstura tła z `BlockColorSampler`, wysokość linii
   z `TextBlockMetrics`, a przy `--cover crisp|soft` łatka z wypełnionymi literami
   (`GlyphCoverBuilder.BuildForBlock`, ta sama co w sesji live; rysowana tylko przy
   `--placement cover`). Zamiast OCR można podać gotową listę bloków (`--blocks`).
3. Tłumaczy przez prawdziwy `TranslationOrchestrator` (korekty → słownik → baza/korpus), na
   **kopii roboczej** podanej bazy. Tekst, którego nie ma lokalnie, dostaje Mock (`[PL] …`) —
   w aplikacji poszedłby do dostawcy. Kopia robocza powstaje w `<out>\_praca-…` i jest usuwana.
4. Buduje elementy nakładki przez `OverlayBlockRenderer` — tę samą klasę, której używa
   `OverlayWindow` (natywnie: `GameTextElement` z łatką, krojem z profilu, rozmiarem z linii
   bazowej i konturem z geometrii; bez łatki: `CoverPatchHost` + `OutlinedTextBlock`) — i renderuje
   je `RenderTargetBitmap` w rozdzielczości klatki przy zadanym DPI. Przed klatkami rozgrzewa
   kroje (`OverlayFonts.WarmUp`) jak sesja live, a konsola podaje czas układu WPF i łatek.
5. Składa warstwę nakładki z klatką i zapisuje galerię.

## Uruchomienie

```powershell
dotnet build tools/GameTranslatorOverlay.OverlayPreview -c Release
dotnet run --project tools/GameTranslatorOverlay.OverlayPreview -c Release --no-build -- `
  --frames-dir "C:\…\GTO Diagnostics\20260912-escape-menu" --pattern "frame-original.png" `
  --out "C:\…\GTO Diagnostics\…\krok-wyglad\przed" `
  --cache "C:\…\sesja-2026-10-06\private\cache.db" `
  --corpus "C:\…\krok2\private\escape-academy.corpus.jsonl" --profile escape-academy
```

| Opcja | Domyślnie | Znaczenie |
|---|---|---|
| `--frame PLIK` | — | klatka; można powtórzyć |
| `--frames-dir KATALOG` | — | klatki z katalogu (rekurencyjnie), filtr `--pattern` (`*.png;*.jpg;*.jpeg`) |
| `--out KATALOG` | — | galeria; **poza repozytorium** (zawiera obrazy i teksty gry) |
| `--dpi` | `144` | DPI monitora gracza (144 = 4K przy 150%), zakres 96–480 |
| `--cache PLIK` | brak | baza tłumaczeń; czytana wyłącznie przez kopię roboczą |
| `--corpus PLIK` | brak | korpus gry dla profilu (`*.corpus.jsonl` z CorpusTool) |
| `--profile ID` | `escape-academy` | profil gry; `none` = bez profilu |
| `--blocks PLIK` | brak | bloki zamiast OCR (tylko z jedną klatką), format niżej |
| `--placement` | `cover` | `cover` (zakrywa) albo `below` (pod oryginałem) |
| `--font-size` | `0` | 0 = auto, jak ustawienie gracza |
| `--font-family` | `auto` | czcionka nakładki; `auto` = krój z profilu gry (`overlay.fontFamily`), jak domyślnie u gracza |
| `--cover` | `crisp` | `crisp` = wypełnione litery (jak live), `soft` = miękka łatka (tło w ruchu), `off` = dawna łatka z tekstury |
| `--opacity` | `0.4` | `overlayBackgroundOpacity` z ustawień (0–1) |
| `--zoom` | `3` | powiększenie wycinków bloków, 1–6 (zmniejszane, gdy wycinek przekracza 2400 px) |

Kod wyjścia: 0 — galeria zapisana; 2 — błędne opcje; 3 — nie wczytano profilu lub korpusu;
4 — brak pakietu Windows OCR dla języka źródłowego.

## Galeria

Dla każdej klatki katalog `<rodzic>-<nazwa>`:

- `nakladka.png` — klatka z nakładką w pełnej rozdzielczości (tak, jak widzi gracz),
- `warstwa.png` — sama warstwa nakładki (przezroczysta),
- `porownanie.png` — oryginał | nakładka w połowie rozdzielczości; numery bloków
  (żółty — baza/słownik, pomarańczowy — Mock, czerwony — bez tłumaczenia), turkusowe boxy OCR
  i czerwone przerywane ramki tekstów odrzuconych przez bramkę live,
- `bloki/blok-NN.png` — powiększony wycinek bloku: oryginał, nakładka, nakładka z obrysami
  (turkus — box OCR, żółty — łatka, różowy — zasięg polskiego tekstu) i metryki
  (czcionka, wysokość linii oryginału, kolory, stosunek tekst/łatka),
- `bloki.json` — dane bloków i wyrenderowanych elementów.

W katalogu głównym `galeria.md` zbiera wszystkie klatki w tabelach.

## Bloki z pliku (`--blocks`)

Przyjmuje tablicę albo obiekt z polem `blocks`; wyjściowy `bloki.json` nadaje się wprost na wejście.
Wymagane pola: `text`, `x`, `y`, `width`, `height` (fizyczne piksele klatki). Opcjonalne:
`lineHeight` (domyślnie wysokość boxu podzielona przez liczbę linii) i `translation` — gdy jest,
pomija tłumaczenie (pochodzenie „JSON”); bez niej tekst idzie przez pipeline. Kolory zawsze są
próbkowane z klatki. Pozwala sprawdzić długie tłumaczenia, przepełnienia i wieloliniowe bloki
bez zmienności OCR.

## Ograniczenia

- Odtwarza pierwszy pełny przebieg OCR. Wycinki z powiększeniem, histereza stylu, ponowne użycie
  łatki po podpisie pola, podtrzymywanie bloków między klatkami i wstrzymanie niedokończonej
  kwestii (`HoldTypingPrefixes` w `LiveTranslationSession`) nie są symulowane — niedokończona
  kwestia korpusu jest tłumaczona od razu (przyciągnięty początek: tłumaczeniem całej kwestii,
  krótszy początek poniżej progów przyciągania: z bazy albo Mockiem), choć aplikacja w trybie
  zakrywania by ją wstrzymała. Tłumaczenia identyczne z oryginałem nie są rysowane przy
  `--placement cover` (jak w aplikacji).
- `RenderTargetBitmap` rysuje programowo (antyaliasing w skali szarości, jak okno warstwowe
  nakładki); drobne różnice wygładzania względem GPU są możliwe.
- Okno gry jest traktowane jak cały monitor (gra na pełnym ekranie, początek w 0,0).
