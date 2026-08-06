# Architektura

Dokument opisuje architekturę aplikacji **GameTranslatorOverlay** — desktopowego tłumacza
tekstu z gier (EN→PL) działającego jako zewnętrzna, przezroczysta nakładka, bez jakiejkolwiek
ingerencji w grę. Decyzje technologiczne (i ich uzasadnienia) są w
[TECHNOLOGY_DECISIONS.md](TECHNOLOGY_DECISIONS.md).

## 1. Podział na projekty

Rozwiązanie (`GameTranslatorOverlay.slnx`) składa się z trzech projektów produkcyjnych
i dwóch testowych:

```
src/
  GameTranslatorOverlay.Core            (biblioteka, bez zależności Windows)
  GameTranslatorOverlay.Infrastructure  (biblioteka, integracje: SQLite, DeepL, DPAPI, pliki)
  GameTranslatorOverlay.App             (WPF, net10.0-windows10.0.19041.0)
tests/
  GameTranslatorOverlay.Core.Tests            (xUnit)
  GameTranslatorOverlay.Infrastructure.Tests  (xUnit)
```

Zależności płyną w jedną stronę: **App → Infrastructure → Core**. Core nie zna nikogo.

### GameTranslatorOverlay.Core — czysta logika

Zero zależności od Windows, WPF i sieci. Wszystko tutaj jest testowalne zwykłym xUnitem
na dowolnym runnerze.

Zawartość:

- **Interfejsy (kontrakty)**: `IOcrProvider`, `ITranslationProvider`, `ITranslationCache`,
  `IGlossaryService` oraz kontrakty planowanych usług (rozdz. 5).
- **Modele domenowe**: wynik OCR (tekst + prostokąty), zapytanie/wynik tłumaczenia,
  profil gry, słownik i jego terminy, ustawienia.
- **Logika przetwarzania tekstu**: normalizacja tekstu z OCR (sklejanie linii, białe znaki,
  myślniki przenoszenia), filtr śmieci (odsiew nie-tekstu — API OCR nie daje per-słowo
  confidence, więc filtrujemy po treści), segmentacja na jednostki tłumaczenia.
- **Logika słownika**: dopasowanie całych słów/fraz (nigdy fragmentów słów), dłuższe frazy
  przed krótszymi, rozstrzyganie konfliktów priorytetem, wykrywanie konfliktów
  (ten sam `source` → różne `target`).
- **Priorytet źródeł tłumaczenia**: ręczna korekta > wpis profilu gry > cache globalny > API.
- **Kontrola kosztów**: deduplikacja zapytań in-flight, debounce niestabilnego tekstu,
  limity (miesięczny, znaków na sesję), tryb Cache-only.

### GameTranslatorOverlay.Infrastructure — integracje bez UI

Implementacje kontraktów z Core, które wymagają świata zewnętrznego, ale nie pulpitu:

- **Cache**: SQLite przez `Microsoft.Data.Sqlite`, migracje przez `PRAGMA user_version`.
- **Tłumaczenie**: `DeepLTranslationProvider` (HTTP, `/v2/translate` batch do 50 tekstów,
  `/v2/usage` do testu połączenia i licznika; `api-free.deepl.com` dla kluczy `:fx`,
  `api.deepl.com` dla pro; obsługa 403/456/429/timeout/braku sieci) oraz
  `MockTranslationProvider` (deterministyczny — testy i praca bez klucza).
- **Klucze API**: Windows DPAPI (`ProtectedData`, zakres CurrentUser), zapis w
  `%LOCALAPPDATA%\GameTranslatorOverlay`.
- **Pliki**: odczyt/zapis profili gier i słowników (JSON, schematy w rozdz. 7),
  `settings.json`.
- **Logowanie**: konfiguracja Serilog (plik rolling; bez treści tłumaczeń w trybie
  prywatnym, nigdy kluczy API).

Celowo **nie ma** osobnego assembly `Providers.DeepL` — DeepL siedzi w Infrastructure
(mniej assembly = prościej; szczegóły w TECHNOLOGY_DECISIONS.md).

### GameTranslatorOverlay.App — WPF i wszystko, co wymaga pulpitu

- UI (okno główne, panel wyniku, ustawienia), DI przez `Microsoft.Extensions.Hosting`.
- **Capture**: GDI (`CopyFromScreen`/BitBlt dla regionu ekranu, `PrintWindow`
  z `PW_RENDERFULLCONTENT` dla okna + fallback na crop ekranu).
- **OCR**: `WindowsOcrProvider` — adapter `Windows.Media.Ocr.OcrEngine` za interfejsem
  `IOcrProvider` (WinRT wymaga TFM windowsowego, więc siedzi w App, nie w Infrastructure).
- **Nakładka**: osobne okno WPF z `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE |
  WS_EX_TOOLWINDOW`, Topmost, click-through, bez fokusu.
- **Skróty globalne** (np. Ctrl+Shift+T) — sterują wyłącznie tłumaczem, nigdy grą.

Reguła podziału w jednym zdaniu: **Core = co i dlaczego, Infrastructure = skąd i dokąd
(dysk/sieć), App = ekran, piksele i klawiatura.**

## 2. Pionowy przepływ danych

Każde tłumaczenie — niezależnie od trybu — przechodzi ten sam pion:

```
 okno gry / region ekranu
        │
        ▼
 1. CAPTURE          GDI: BitBlt / PrintWindow  →  bitmapa (tylko wybrane okno/region)
        │
        ▼
 2. OCR              Windows.Media.Ocr  →  linie tekstu + prostokąty (lokalnie!)
        │
        ▼
 3. NORMALIZACJA     sklejanie linii, białe znaki, filtr śmieci, segmentacja
        │
        ▼
 4. GLOSSARY         lokalny słownik: całe frazy, dłuższe najpierw, priorytety
        │
        ▼
 5. CACHE            SQLite: korekta > profil > cache globalny; hit = koniec, bez sieci
        │  (tylko miss)
        ▼
 6. TŁUMACZENIE      DeepL API — wyłącznie rozpoznany TEKST, nigdy screenshot;
        │            batch, deduplikacja in-flight, wynik trafia do cache
        ▼
 7. OVERLAY          nakładka click-through nad grą (lub panel wyniku w trybie ręcznym)
```

Zasady przekrojowe:

- **Do sieci wychodzi wyłącznie tekst** — bitmapa kończy życie na kroku 2.
- Kroki 2–6 są asynchroniczne (`async/await` + `CancellationToken`); UI nigdy nie czeka.
- W trybie Cache-only krok 6 jest wyłączony — miss w cache = brak tłumaczenia, nie zapytanie.
- Program działa w 100% pasywnie: bez wstrzykiwania DLL, hooków, czytania pamięci procesu,
  modyfikacji plików gry i wysyłania inputu do gry.

## 3. Kluczowe interfejsy

Kontrakty mieszkają w Core; implementacje w Infrastructure (sieć/dysk) lub App (pulpit).

### IOcrProvider

Rozpoznaje tekst na bitmapie. Implementacja MVP: `WindowsOcrProvider`
(`Windows.Media.Ocr.OcrEngine`, w App).

- Wejście: bitmapa + język źródłowy.
- Wyjście: linie tekstu z prostokątami (współrzędne względem bitmapy).
- Brak per-słowo confidence w API systemowym — filtr śmieci działa na tekście (w Core).
- Brak pakietu językowego Windows = czytelny komunikat + instrukcja doinstalowania języka
  w ustawieniach Windows (nie wyjątek w twarz użytkownika).

### ITranslationProvider

Tłumaczy partię tekstów. Implementacje: `DeepLTranslationProvider`,
`MockTranslationProvider` (obie w Infrastructure).

- Wejście: lista tekstów + para językowa; wyjście: lista tłumaczeń w tej samej kolejności.
- Batch (DeepL: do 50 tekstów na zapytanie).
- Test połączenia + licznik zużycia (DeepL: `/v2/usage`).
- Mapowanie błędów na czytelne stany: zły klucz (403), wyczerpany limit (456),
  rate limit z ograniczonym retry (429), timeout, brak sieci.

### ITranslationCache

Trwały cache tłumaczeń (SQLite w Infrastructure; w trybie prywatnym — tylko w pamięci,
czyszczony po sesji).

- Klucz: znormalizowany tekst źródłowy + para językowa (+ kontekst profilu dla wpisów
  profilowych i korekt).
- Realizuje priorytet: **ręczna korekta > wpis profilu gry > cache globalny**; dopiero
  pełny miss idzie do `ITranslationProvider`.
- Zapis ręcznych korekt użytkownika (nadpisują wszystko inne).
- Migracje schematu przez `PRAGMA user_version`.

### IGlossaryService

Lokalny słownik terminów — działa PRZED tłumaczeniem maszynowym i bez sieci.

- Ładuje słowniki JSON (`glossaries/<id>/en-pl.json`, schemat w rozdz. 7).
- Dopasowanie: całe słowa/frazy (nigdy fragment słowa), dłuższe frazy przed krótszymi,
  konflikt rozstrzyga `priority`.
- Wykrywa i raportuje konflikty (ten sam `source` → różne `target`).
- Etap 10: edycja terminów z UI, import/eksport.

## 4. Przepływ trybu Manual Region (MVP)

Pierwszy działający tryb (Etap 6 roadmapy) — punkt odniesienia dla całej architektury:

1. Użytkownik gra; wciska globalny skrót **Ctrl+Shift+T** (skrót rejestruje App;
   gra nie dostaje żadnego inputu od nas).
2. App pokazuje półprzezroczystą warstwę wyboru regionu; użytkownik zaznacza prostokąt
   myszą (współrzędne ekranowe → przeliczenie DPI, rozdz. 6).
3. Capture regionu przez GDI (`CopyFromScreen`).
4. Bitmapa przechodzi pion z rozdz. 2: OCR → normalizacja → glossary → cache → (miss) API.
5. Wynik ląduje w panelu wyniku / nakładce; użytkownik nie traci sterowania grą
   (nakładka jest click-through i nie kradnie fokusu).
6. Użytkownik może poprawić tłumaczenie (korekta → cache z najwyższym priorytetem)
   lub dodać termin do słownika.
7. Kolejne wciśnięcie skrótu = nowe zaznaczenie; skrót zamykający chowa panel.

Każdy krok pionu jest anulowalny — jeśli użytkownik zdąży poprosić o nowy region, stare
zadanie dostaje `CancellationToken.Cancel()` i jego wynik nigdzie nie trafia.

## 5. Usługi aplikacji — MVP vs później

| Usługa | Odpowiedzialność | Projekt | Etap |
|---|---|---|---|
| **WindowDiscovery** | lista okien najwyższego poziomu, wybór okna gry, dopasowanie do profilu po `processNames`/`windowTitles` | App | **MVP** (Etap 2) |
| **Capture** | zrzut regionu/okna przez GDI; później WGC dla trybu live | App | **MVP** (Etap 2); WGC — Etap 8 |
| **TextProcessing** | normalizacja, filtr śmieci, segmentacja, stabilizacja (debounce) | Core | **MVP** (Etap 3–4) |
| **ChangeDetection** | porównywanie klatek (próg z profilu, np. `threshold: 0.02`), OCR tylko przy zmianie | Core (logika) + App (klatki) | Etap 8 (tryb live) |
| **Overlay** | okno nakładki: click-through, bez fokusu, Topmost, pozycjonowanie wyników, DPI/multi-monitor | App | Etap 7 (MVP kończy się panelem wyniku; pełna nakładka to Etap 7) |
| **Profile** | ładowanie/walidacja `profile.json`, dobór profilu do okna, `minAppVersion` | Infrastructure (pliki) + Core (model) | Etap 11 (profil PoE2) |
| **Diagnostics** | logi Serilog, licznik zużycia API, ostrzeżenia o limitach, ekran diagnostyki błędów | Infrastructure + App | podstawy w MVP (logi), pełny ekran później |

Tryby pracy (kolejność wdrażania): **1) Manual Region (MVP, Etap 6)** → 2) Tooltip Mode →
3) Subtitle Mode → 4) Universal Live Mode (Etap 8–9) → 5) History Mode. Funkcja „wyjaśnij
prostym językiem" (LLM) — opcjonalna, poza MVP, domyślnie OFF.

## 6. DPI i multi-monitor

- Aplikacja deklaruje w manifeście **PerMonitorV2** — każde okno (główne, warstwa wyboru
  regionu, nakładka) dostaje realne DPI monitora, na którym stoi.
- Współrzędne trzymamy w **pikselach fizycznych ekranu** (tak pracują GDI i Win32);
  na piksele WPF (DIP) przeliczamy dopiero przy rysowaniu, mnożnikiem DPI konkretnego
  monitora.
- Zaznaczenie regionu może przecinać monitory o różnym DPI — capture idzie po
  współrzędnych wirtualnego pulpitu, a warstwa rysująca przelicza per-monitor.
- Nakładka pozycjonuje się względem prostokąta okna gry (fizyczne piksele), więc
  przeniesienie gry na inny monitor = przeliczenie od nowa, bez „rozjechanych" ramek.
- Ograniczenie (udokumentowane): **exclusive fullscreen nie jest obsługiwany** — GDI ani
  nakładka nie widzą takiego trybu. Działa okno i borderless fullscreen.

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

### Słownik — `glossaries/<id>/en-pl.json`

```json
{
  "name": "path-of-exile-2",
  "sourceLanguage": "en",
  "targetLanguage": "pl",
  "version": 1,
  "description": "...",
  "terms": [
    { "source": "Energy Shield", "target": "Tarcza energetyczna", "caseSensitive": false, "priority": 10, "note": "opcjonalna uwaga" }
  ]
}
```

Zasady słownika: dopasowanie **całych słów/fraz** (nigdy fragmentów słów), dłuższe frazy
przed krótszymi, priorytety rozstrzygają konflikty; konflikty (ten sam `source` → różne
`target`) są wykrywane i raportowane.

## 8. Tryb live i latest-frame-wins (Etap 8)

Universal Live Mode analizuje obraz w pętli 3–6 fps. Obowiązuje zasada
**latest-frame-wins**:

- Liczy się wyłącznie NAJNOWSZA klatka. Jeśli pion (OCR→tłumaczenie) jeszcze mieli
  poprzednią, a przyszła nowa z wykrytą zmianą — stare zadanie jest **anulowane**
  (`CancellationToken`), jego wynik ląduje w koszu, a pion startuje od nowej klatki.
- Nie budujemy kolejki klatek — kolejka to rosnące opóźnienie, a nakładka pokazująca
  tekst sprzed 3 sekund jest gorsza niż brak tekstu.
- ChangeDetection pilnuje, żeby OCR w ogóle nie ruszał, gdy obraz się nie zmienił
  (próg `threshold` z profilu); do tego debounce niestabilnego tekstu (animacje,
  przewijanie) — tłumaczymy dopiero tekst, który „ustał".
- Wyniki niezmienionego tekstu idą z cache — pętla live przy statycznym ekranie
  kosztuje 0 zapytań API.
