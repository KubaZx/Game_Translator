# GameTranslatorOverlay

[![CI](https://github.com/KubaZx/Game_Translator/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/KubaZx/Game_Translator/actions/workflows/ci.yml)

Tłumacz EN→PL dla gier na Windows. Czyta tekst z ekranu (Windows OCR), tłumaczy go
i pokazuje polską wersję w przezroczystej nakładce nad grą. Klikanie przez nakładkę działa normalnie.
W trybie live z położeniem **Na oryginale (zakrywa)** polski napis zastępuje angielski w stylu
gry, bez żadnej zmiany w plikach gry.

[Pobierz](https://github.com/KubaZx/Game_Translator/releases) ·
[Instrukcja](docs/USER_GUIDE.md) · [Zmiany](CHANGELOG.md) ·
[CI](https://github.com/KubaZx/Game_Translator/actions/workflows/ci.yml)

**Wersja: 0.5.0** · Windows 10 2004+ / 11 · portable, bez instalacji

## Funkcje

| Funkcja | Jak |
|---|---|
| Tłumaczenie fragmentu | `Ctrl+Shift+T` → zaznacz obszar |
| Tryb live | `Ctrl+Shift+L` w grze (start/stop) albo wybierz okno gry → **▶ Start live** |
| Ukrycie nakładki | `Ctrl+Shift+H` |
| Komunikaty w grze | brak klucza, limit, brak sieci, Cache-only, start/stop live, gra na pełnym ekranie — krótki pasek u góry okna gry |
| Wygląd | bloki przy oryginale (pod tekstem albo zakrywające go) albo napisy na dole |
| Wygląd jak w grze | **Na oryginale (zakrywa)** w trybie live: z obrazu znikają same litery oryginału (ich piksele są wypełniane tłem z otoczenia, reszta obrazu gry zostaje nietknięta), a polski tekst dostaje kolor, kontur, cień, wysokość liter i linię bazową napisu gry; ikony klawiszy zostają; krój **Jak w grze (krój z profilu)** — w profilu Escape Academy dołączony Lexend Deca |
| Ruch kamery | w trybie zakrywania tłumaczenie jedzie razem z napisem (śledzenie kształtu liter między odczytami OCR), a tło pod nim jest odświeżane do ~30 razy na sekundę z Windows Graphics Capture; HUD nie znika przy obrotach i panoramach |
| Aktualność napisów | tłumaczenie znika razem z napisem w grze, także na teksturowanym tle (porównanie pikseli pola z ostatnim odczytem); przygaszony napis, który OCR nadal czyta, zostaje |
| Korpus gry | osobne narzędzie offline `CorpusTool` czyta teksty z plików gry (dziś Escape Academy; gra wyłączona, bez anti-cheatu i gier online) i może je przetłumaczyć z wyprzedzeniem do lokalnej bazy; sama aplikacja plików gry nie czyta |
| Dopasowanie do korpusu | odczyt OCR z błędami („Ihspect”, „11m”), innym zawinięciem albo WIELKIMI LITERAMI trafia w znany tekst gry i jego tłumaczenie; początek kwestii dialogu dostaje tłumaczenie całej kwestii; śmieci OCR z ikon i tekstur nie idą do dostawcy |
| Kwestia pisana literami | z korpusem, w trybie zakrywania: zostaje po angielsku, dopóki gra ją dopisuje; całe tłumaczenie kwestii pojawia się po ostatniej literze |
| Dostawcy | DeepL, Azure AI Translator, Google, Claude, serwer zgodny z OpenAI (gotowe adresy: OpenAI, DeepSeek bez rozumowania, lokalne Ollama i LM Studio), Mock |
| Słownik | własne terminy i poprawki, import/eksport; w DeepL jako glosariusz (terminy odmieniane także w środku zdań); liczba mnoga („Waystones”); zakres „Etykieta” dla przycisków i nagłówków |
| Kontekst | do 6 poprzednich linii dialogu + nazwa gry + terminy słownika |
| Pamięć dialogu (LLM) | Claude i serwer zgodny z OpenAI widzą swoje poprzednie tłumaczenia; **Postać gracza** (kobieta/mężczyzna) dla form „zrobiłaś”/„zrobiłeś” |
| Kontrola jakości | pusty wynik nie trafia do cache; zgubione liczby, brak tłumaczenia i „rozgadany” wynik są oznaczane, modele językowe dostają jedno ponowienie |
| Cache | SQLite z pamięcią w RAM; ten sam tekst nie idzie drugi raz do API; błąd bazy nie zatrzymuje tłumaczenia |
| Offline | Cache-only (bez API), lokalny model LLM, Mock (test bez klucza) |

Kolejność wyboru tłumaczenia: **ręczna poprawka → słownik → cache → dostawca**. Z korpusem gry
cache jest szukany pod tekstem z korpusu, więc różne odczyty tego samego zdania dzielą jeden wpis.

## Jak szybko reaguje

| Sytuacja | Czas |
|---|---:|
| Przechwycenie klatki gry (PrintWindow, PoE2) | 25–48 ms |
| Przechwycenie w ruchu kamery (Windows Graphics Capture, 4K) | ~22 ms pełna klatka, <1 ms wycinek pod napisem |
| Odświeżenie położenia i tła napisu w ruchu (tryb zakrywania) | do ~30× na sekundę |
| Sprawdzanie zmian na ekranie w live | 6× na sekundę (co ~167 ms) |
| Znany tekst (cache / słownik): od pojawienia się do gotowego napisu | ~0,3–0,45 s |
| Odczyt 20 napisów z cache | ~0,25 ms na klatkę (było 10–13 ms) |
| Nowy tekst (pierwszy raz) | ~0,3–0,5 s + czas odpowiedzi dostawcy |
| Nowy tekst z DeepL (Mock opóźniony o zmierzony czas DeepL) | ~0,50–0,59 s; model językowy bez rozumowania ~1,0–1,3 s |
| Nowy tekst przy dostawcy odpowiadającym 2 s | ~2,3–2,5 s (było do 4,1 s) |
| Zniknięcie napisu, gdy tekst w grze się zmienił | ~12–170 ms |
| Zniknięcie napisu przykrytego w grze panelem | ~14–18 ms (było ~0,3 s) |
| Zniknięcie tłumaczenia, gdy napis zniknął z teksturowanego tła | 0,29–0,55 s (było ~0,9 s, ~6 s albo wcale) |
| To samo, gdy w miejscu napisu zostaje coś jasnego (brak dowodu w pikselach) | ~0,87–3,2 s |
| Kwestia pisana literami (tryb zakrywania): pełne tłumaczenie po ostatniej literze | ok. 0,1–0,3 s |
| Najdłuższa pauza OCR przy ciągłym ruchu kamery | 0,9 s przy śledzeniu napisów (tryb zakrywania), poza tym 2,5 s |
| Pełne ponowne sprawdzenie ekranu | co 4 s, także gdy w innym miejscu ekranu coś się rusza (było: w takiej scenie ponad 10 s bez pełnego odczytu) |
| Dopasowanie odczytu do korpusu Escape Academy | p95 ~1,1–1,2 ms na blok |
| Łatka liter w trybie zakrywania (klatka 4K z samymi nowymi napisami) | 2–16 ms, liczona równolegle z tłumaczeniem; ponowne użycie 0,03–0,11 ms |

Pomiary z lokalnych sond: SceneReplay (własne okno testowe, prawdziwy Windows OCR albo OCR ze
skryptu, Mock zamiast prawdziwego dostawcy), CorpusEval (korpus Escape Academy) i OverlayPreview
(zapisane klatki 4K). Mierzą moment gotowości napisu w aplikacji, nie rysowanie na ekranie.
Wiersz DeepL to SceneReplay z Mockiem opóźnionym o czasy odpowiedzi zmierzone 2026-10-05
u prawdziwych dostawców (DeepL i DeepSeek bez rozumowania, mediana–p90;
[ADR-014](docs/TECHNOLOGY_DECISIONS.md)); w grze czas odpowiedzi zależy od sieci i długości
tekstu. Szczegóły: [ROADMAP.md](docs/ROADMAP.md).

Własne czasy z gry pokazuje panel **Szybkość** w oknie aplikacji: mediana i p90 dla
**Zmiana → napis** (od zauważonej zmiany obrazu do gotowych napisów — czas, który widzi gracz),
nowego i znanego tekstu, odpowiedzi dostawcy, OCR i przechwycenia. **Kopiuj raport** kopiuje je
do schowka.

Od 0.4.0 okno stabilności liczy się od zauważonej zmiany (do ~250 ms mniej czekania), a znany
ekran nie czeka w kolejce za wolnym tłumaczeniem starszej klatki. Zysk wynika z logiki
harmonogramu i testów; wiersze o znanym tekście, nowym tekście (pierwszy raz) i dostawcy
odpowiadającym 2 s pochodzą sprzed tych zmian. Wiersz DeepL zmierzono przed zmianami trybu
live z 0.5.0, a wiersze o znikaniu napisów, pełnym sprawdzeniu ekranu, kwestii pisanej literami,
korpusie i łatce — w rundach wydania 0.5.0 (ROADMAP: rundy 2026-10-05 i 2026-10-06; „było”
w tych wierszach to kod sprzed tych rund). Żadnego z tych czasów nie zmierzono jeszcze w grze
na żywo.

## Szybki start

1. Pobierz zip z [Releases](https://github.com/KubaZx/Game_Translator/releases), rozpakuj,
   uruchom `GameTranslatorOverlay.exe`.
2. Wybierz dostawcę, wklej klucz API → **Zapisz klucz** → **Testuj**.
3. Uruchom grę w oknie bez ramki (borderless) albo w oknie i wybierz ją z listy.
4. **▶ Start live** (albo `Ctrl+Shift+L` w grze).
5. Opcjonalnie, dla napisów w stylu gry: **Położenie dymków → Na oryginale (zakrywa)**
   (krój **Jak w grze (krój z profilu)** jest domyślny) —
   [Instrukcja → Tryb „Na oryginale (zakrywa)”](docs/USER_GUIDE.md#tryb-na-oryginale-zakrywa--napis-jak-w-grze).
6. Opcjonalnie (Escape Academy): przygotuj korpus gry i przetłumacz go z wyprzedzeniem narzędziem
   `CorpusTool` — [Instrukcja → Spolszczenie z wyprzedzeniem](docs/USER_GUIDE.md#spolszczenie-z-wyprzedzeniem-korpus-gry).

Aktualizacja: rozpakuj nową wersję w miejsce starej. Ustawienia, klucze, cache i korpus gry są
w `%LOCALAPPDATA%\GameTranslatorOverlay`, więc zostają. Ustawiony krój Segoe UI (dotychczas
domyślny) przechodzi raz na **Jak w grze (krój z profilu)**; inny wybrany krój zostaje.

## Wymagania

- Windows 10 (2004+) lub 11, x64.
- Pakiet językowy OCR dla języka gry (np. angielski).
- Klucz API wybranego dostawcy. Bez klucza działają: lokalny LLM, Mock i Cache-only.
- Gra w oknie bez ramki (zalecane) albo w oknie. **Wyłączny pełny ekran (exclusive fullscreen)
  nie działa** — gdy gra zajmuje cały monitor i nie daje się przechwycić jako okno, aplikacja
  ostrzega: „⚠ Pełny ekran utrudnia nakładkę — przełącz na okno bez ramki”.
- Korpus gry (opcjonalnie): .NET 10 SDK i źródła projektu — `CorpusTool` nie jest w paczce.

.NET jest w paczce. Nie trzeba Pythona, CUDA ani modeli AI.

## Prywatność

- OCR działa lokalnie. Do dostawcy trafia **tylko tekst**, nigdy obraz. Komunikaty w nakładce
  nie zawierają tekstu z ekranu.
- Klucze API są szyfrowane DPAPI i wysyłane tylko do wybranego dostawcy.
- Z lokalnym LLM tekst nie wychodzi z komputera.
- Aplikacja nie modyfikuje gry, nie czyta jej pamięci ani plików i nie wysyła do niej klawiszy.
- Teksty z plików gry czyta wyłącznie osobne narzędzie `CorpusTool`, uruchamiane przez Ciebie
  przy wyłączonej grze (tylko odczyt, odmowa dla gier z anti-cheatem i online); korpus zostaje
  lokalnie. Przy dopasowaniu do korpusu do dostawcy może trafić pełne zdanie z korpusu, którego
  część dopiero pojawia się na ekranie ([ADR-014](docs/TECHNOLOGY_DECISIONS.md)).

Więcej: [PRIVACY.md](docs/PRIVACY.md), [SECURITY.md](docs/SECURITY.md).

## Ograniczenia

- Ozdobne lub małe czcionki, słaby kontrast i animowane tło pogarszają OCR.
- Przy ruchu kamery napis, którego nie da się śledzić (bez trybu zakrywania, przy podchodzeniu do
  napisu na ścianie, zasłonięty), znika i wraca po kolejnym odczycie OCR. Działanie w ruchu
  zmierzono na nagraniach Escape Academy (MotionLab), nie w grze na żywo.
- Napis przygaszony poniżej ćwierci dawnej jasności może raz zniknąć z nakładki na ok. 0,6 s,
  jeśli pierwszy odczyt OCR po przygaszeniu go nie zobaczy.
- Czas tłumaczenia zależy od sieci i dostawcy.
- Przechwytywanie przez PrintWindow/GDI; czasem potrzebny jest zrzut ekranu i wtedy
  okna nad grą mogą trafić do odczytu (aplikacja ostrzega), a na teksturowanym tle tłumaczenie
  znika dopiero po kilku odczytach OCR bez napisu (sonda pikseli działa tylko przy
  przechwytywaniu okna).
- Zaznaczanie regionu działa na monitorze z kursorem.
- `Ctrl+Shift+L` uruchamia live na **dowolnym** aktywnym oknie (także przeglądarce) — to jawna
  akcja użytkownika. Gry UWP / Microsoft Store / Game Pass nie są wybierane jako aktywne okno:
  wybierz je raz na liście i kliknij **▶ Start live**.
- Ostrzeżenie kontroli jakości nie jest jeszcze pokazywane w oknie ani nakładce.
- Wygląd **Na oryginale (zakrywa)** sprawdzono na zapisanych klatkach Escape Academy
  (OverlayPreview), w SceneReplay i na nagraniach (MotionLab), nie w grze na żywo. Przy szybkim
  ruchu tło łatki zostaje o ułamek sekundy za obrazem; kursywa i szerokość kroju gry nie są
  odwzorowane.
- Korpus: jedna rodzina formatów (Unity TextAsset) i jeden profil z receptą (Escape Academy). Po
  nowym `extract`/`translate` uruchom aplikację ponownie. W trybie prywatnym tłumaczenia
  z wyprzedzeniem nie są czytane z dysku.
- Wstrzymanie kwestii pisanej literami działa tylko w trybie zakrywania; w pozostałych trybach
  tłumaczenie całej kwestii może pojawić się, zanim gra ją dopisze.
- Komunikat o pełnym ekranie nie odróżnia wyłącznego pełnego ekranu od okna bez ramki, którego
  nie da się przechwycić jako okna (liczy się zajęcie całego monitora).

## Budowanie

Windows + .NET 10 SDK:

```powershell
dotnet build GameTranslatorOverlay.slnx -c Release
dotnet test GameTranslatorOverlay.slnx -c Release --no-build
dotnet run --project src/GameTranslatorOverlay.App
```

- Linux: dodaj `-p:EnableWindowsTargeting=true` (testy DPAPI i OCR wymagają Windows).
- Paczka portable: `./tools/package.ps1` → `dist/`.
- Wydanie: tag `v*` → CI buduje zip i dodaje go do Releases.
- Testy: 1697 (1251 Core + 280 Infrastructure + 166 CorpusTool; na Linuksie 3 testy DPAPI są
  pomijane), bez kluczy API i bez gry — wyłącznie dane syntetyczne. CI sprawdza pokrycie kodu
  (Core ≥ 91%, Infrastructure ≥ 80%). Szczegóły: [TESTING.md](docs/TESTING.md).
- Benchmarki BenchmarkDotNet: `benchmarks/` — [BENCHMARKS.md](docs/BENCHMARKS.md).

Narzędzia w `tools/` uruchamia się ze źródeł
(`dotnet run --project tools/GameTranslatorOverlay.<Narzędzie> -c Release -- …`); nie ma ich
w paczce aplikacji. Tam, gdzie zaznaczono Windows, potrzebny jest systemowy OCR albo WPF.

| Narzędzie | Do czego |
|---|---|
| [CorpusTool](tools/GameTranslatorOverlay.CorpusTool/README.md) | `extract` — korpus tekstów gry z jej plików według recepty w profilu (ADR-014: gra wyłączona, tylko odczyt, bez sieci); `translate` — tłumaczenie korpusu z wyprzedzeniem do lokalnej bazy (`--dry-run`: liczby i szacunek kosztu) |
| [CorpusEval](tools/GameTranslatorOverlay.CorpusEval/README.md) | precyzja i czas dopasowania OCR do korpusu: `render`, `evaluate`, `bench`, `replay`, `session`, `prefixes`, `typing` (Windows) |
| [OverlayPreview](tools/GameTranslatorOverlay.OverlayPreview/README.md) | prawdziwa nakładka „Na oryginale (zakrywa)” na zapisanych klatkach gry → PNG i galeria do porównań przed/po, bez sieci (Windows) |
| [SceneReplay](tools/GameTranslatorOverlay.SceneReplay/README.md) | powtarzalne sceny trybu live we własnym oknie (m.in. `stale-*`, `typing`) z Mockiem; czasy callbacków sesji (Windows) |
| [LiveDiag](tools/GameTranslatorOverlay.LiveDiag/README.md) | diagnostyka sesji live na scenie testowej albo oknie gry: capture, Windows OCR, Mock (Windows) |
| [ProviderEval](tools/GameTranslatorOverlay.ProviderEval/README.md) | porównanie dostawców i promptów: chrF, czasy, kontrole EN→PL, opcje serwera LLM |
| `GameTranslatorOverlay.OcrLab` | warianty wstępnej obróbki obrazu dla Windows OCR na klatce wskazanego okna (Windows) |
| `GameTranslatorOverlay.SmokeTest` | smoke test: syntetyczny obraz → Windows OCR → pipeline ze słownikiem, SQLite i Mockiem (Windows) |

## Struktura

| Katalog | Zawartość |
|---|---|
| `src/GameTranslatorOverlay.Core` | tekst, tłumaczenia, cache, stabilizacja live, dopasowanie do korpusu (`Corpus`), łatka liter (`Vision`) |
| `src/GameTranslatorOverlay.Infrastructure` | dostawcy API, SQLite, DPAPI, wczytywanie korpusu |
| `src/GameTranslatorOverlay.App` | WPF, przechwytywanie, OCR, nakładka, dołączone kroje (`Fonts`) |
| `tests/` | testy xUnit: Core, Infrastructure, CorpusTool |
| `tools/` | CorpusTool, CorpusEval, OverlayPreview, SceneReplay, LiveDiag, ProviderEval, OcrLab, SmokeTest |
| `benchmarks/` | benchmarki BenchmarkDotNet (poza `dotnet test`) |
| `eval/` | korpus EN→PL do ProviderEval |
| `profiles/`, `glossaries/` | profile gier (ogólny, PoE2, Escape Academy z receptą korpusu i krojem napisów) i słowniki |

Dokumenty: [Instrukcja](docs/USER_GUIDE.md) · [Dostawcy API](docs/API_PROVIDERS.md) ·
[Architektura](docs/ARCHITECTURE.md) · [Decyzje](docs/TECHNOLOGY_DECISIONS.md) ·
[Plan](docs/ROADMAP.md) · [Wizja](docs/PRODUCT_VISION.md) · [Testy ręczne](docs/MANUAL_TESTING.md) ·
[Checklista wydania](docs/QUALITY_CHECK.md) · [Testy](docs/TESTING.md) ·
[Benchmarki](docs/BENCHMARKS.md) · [Prywatność](docs/PRIVACY.md) · [Bezpieczeństwo](docs/SECURITY.md)
