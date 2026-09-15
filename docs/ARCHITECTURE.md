# Architektura

Dokument opisuje architekturę aplikacji **GameTranslatorOverlay** — desktopowego tłumacza
tekstu z gier (EN→PL) działającego jako zewnętrzna, przezroczysta nakładka, bez jakiejkolwiek
ingerencji w grę. Decyzje technologiczne (i ich uzasadnienia) są w
[TECHNOLOGY_DECISIONS.md](TECHNOLOGY_DECISIONS.md).

## 1. Podział na projekty

Rozwiązanie (`GameTranslatorOverlay.slnx`) składa się z trzech projektów produkcyjnych
i dwóch testowych; osobne projekty w `tools/` służą do lokalnej diagnostyki:

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

## 2. Przepływ danych

1. **Capture:** wybrane okno lub region, PrintWindow/GDI, bitmapa lokalna.
2. **OCR:** Windows.Media.Ocr zwraca linie i prostokąty.
3. **Tekst:** normalizacja, grupowanie i filtr śmieci; w live także stabilizacja
   odczytów i sprawdzenie aktualności sceny.
4. **Wyniki lokalne:** odczyt ręcznej poprawki, dokładne dopasowanie słownika,
   następnie zwykły cache. Trafienie kończy wyszukiwanie wyniku bez API.
5. **Dostawca:** brakujące teksty po deduplikacji i rezerwacji znaków, batchowanie,
   wynik zachowywany w cache, o ile nadal pozwala na to token konfiguracji.
6. **Prezentacja:** panel lub nakładka. Sesja live odrzuca wynik wykrytej
   nieaktualnej sceny przed jego publikacją.

Do dostawcy trafia wyłącznie tekst; bitmapy pozostają lokalnie. Próbki obrazu
służą również do stylu nakładki i sprawdzania obecności tekstu. Cache-only wyłącza
krok dostawcy. OCR i oczekiwanie na sieć nie blokują wątku interfejsu.

Aplikacja działa pasywnie: bez ingerencji w pamięć lub pliki gry, wstrzykiwania
kodu i wysyłania sterowania do gry. Aktualność live opisuje rozdział 8.

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

## 5. Usługi aplikacji

| Element | Odpowiedzialność | Projekt |
|---|---|---|
| `ScreenCapture` | przechwycenie okna/regionu, geometria i lokalne próbki pikseli | App |
| `WindowsOcrProvider` | systemowy OCR i jego adapter | App |
| `LiveTranslationSession` | pętla capture/OCR, aktualność sceny, stan bloków i publikacja aktualizacji | App |
| `TranslationOrchestrator` | składanie pipeline'u, konfiguracja i jej token życia | App |
| `TranslationPipeline`, `UsageTracker` | wyniki lokalne, deduplikacja, dostawca i rezerwacje znaków | Core |
| `BoundedTranslationWork` | ograniczona liczba nadzorowanych zadań i ich domknięcie | Core |
| `LiveSceneValidity`, `LiveReadingStabilizer` | generacja sceny i kolejne potwierdzenia tekstu | Core |
| `LiveBlockGeometry`, `LiveSubtitleContent` | niezależna stabilizacja położenia/rozmiaru i źródła paska napisów | Core |
| `TextPresenceProbe` | konserwatywna ocena całego jednolitego pola starego tekstu | Core |
| `OverlayWindow` | prezentacja bloków/paska, click-through, DPI i ręczne ukrywanie | App |

Tryb ręczny, live w blokach i pasek napisów są zaimplementowane. Automatyczne
wydzielanie tooltipów, History Mode i wyjaśnianie przez LLM pozostają poza obecną
implementacją. Capture live nadal używa GDI/PrintWindow; WGC jest opcją do rozważenia.

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

## 8. Aktualność i praca trybu live

Jedna pętla sesji obsługuje capture, OCR i stan nakładki. Domyślnie próbkuje obraz
przy 6 FPS, wymaga 250 ms stabilności, może wymusić przetwarzanie po 600 ms,
a przy ruchu ma maksymalną pauzę OCR 2,5 s. Niezmieniony obraz jest też ponownie
skanowany co 4 s; powtórki mogą wynikać z potwierdzania odczytu lub podejrzenia whiffa.
Parametry profilu mogą zmieniać część tego zachowania.

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
  i domyka także pracę pozostawioną przez poprzednie widoki.
- Zmiana konfiguracji ma osobny token, który blokuje późny zapis do poprzedniego
  cache. Zmiana sceny i zmiana konfiguracji mają różne skutki dla tego zapisu.

### Tekst lokalny i prezentacja

Podobny nowy odczyt wymaga kolejnych wiarygodnych potwierdzeń. Powrót starego tekstu
albo brak obserwacji w badanym obszarze przerywa serię. Potwierdzona zamiana usuwa
stary blok przed oczekiwaniem na dostawcę. Brak OCR nad całym dawnym polem, które
stało się jednolite i miało znany kontrast, również pozwala na lokalne usunięcie.
Cienkie znaki, tekstura, niepewne kolory i ucięte pole nie są dowodem pustego obszaru.

Podczas oczekiwania kontrolowane są też pola zaakceptowanych źródeł. Jeśli źródło
zostało jednoznacznie przykryte, klatka nie jest publikowana, a wszystkie jej
obszary trafiają do ponownego odczytu. Pozwala to zachować również pierwsze menu,
które jeszcze nie dostało wyniku. Lokalne usunięcie aktualizuje tylko powiązane
źródła paska napisów i nie odnawia jego czasu wygasania.

Położenie ma tolerancję 2 fizycznych pikseli na każdej osi, oddzielną od stabilizacji
rozmiaru. To aktualizacja przy odczycie OCR, nie śledzenie między odczytami.
`TextRegionFingerprint` porównuje wszystkie RGB źródła z marginesem 3 px, ignorując
alpha i padding. W pamięci pozostają tylko SHA-256, geometria pola i rozmiar całej
klatki. Referencja powstaje z natywnej klatki OCR; każda kontrola wykorzystuje już
przechwycony obraz i bufor jednego wiersza. Pole musi być pełne, mieć 4–262144 pikseli,
niejednolity obraz i znany kontrast tekstu. Skalowany odczyt, fallback, zmiana rozmiaru
lub brak dowodu zachowują wcześniejsze reguły usuwania. Dowód może też zachować
pominięty przez OCR blok, jeśli żaden nowy zaakceptowany blok nie zajmuje jego miejsca.

Jest to ochrona identycznego obrazu, bez semantycznego rozpoznawania HUD-u. Animowane
lub przezroczyste tło może ją wyłączyć. Generacja sceny nadal odrzuca wszystkie stare
wyniki w toku, także pierwsze tłumaczenie jeszcze niewyświetlonego menu.

### Diagnostyka i ograniczenia

`LiveFrameDiagnostics` opisuje czas przygotowania aktualizacji, operację OCR oraz
liczbę i koszt kontroli obrazu. Etapy czasowo nakładają się, a odrzucona klatka
nie ma diagnostyki ukończonego przebiegu. Te dane nie mierzą faktycznego rysowania.

[LiveDiag](../tools/GameTranslatorOverlay.LiveDiag/README.md) i
[SceneReplay](../tools/GameTranslatorOverlay.SceneReplay/README.md) używają Mocka,
prywatnego cache i zablokowanego HTTP. Wyniki porównawcze oraz ich ograniczenia
opisuje [ROADMAP.md](ROADMAP.md); testy integracji z rzeczywistym pulpitem należy
odróżniać od czystej logiki [TESTING.md](TESTING.md).
