# GameTranslatorOverlay

Uniwersalny tłumacz gier dla Windows. Przechwytuje obraz wybranego okna lub regionu ekranu,
rozpoznaje tekst **systemowym OCR Windows**, tłumaczy go (lokalny słownik → cache SQLite →
DeepL API) i wyświetla polską wersję w **niezależnej nakładce**, która nie przejmuje sterowania
i nie blokuje kliknięć.

> **Zasada nr 1:** program działa w 100% pasywnie. Nie modyfikuje gry, jej plików, procesu
> ani pamięci; nie wysyła do gry żadnych kliknięć ani klawiszy. Pracuje wyłącznie na obrazie,
> który i tak widzisz na ekranie. Szczegóły: [docs/SECURITY.md](docs/SECURITY.md).

## Status projektu (v0.1.0)

| Etap | Zakres | Status |
|---|---|---|
| 0–1 | Analiza, dokumentacja, szkielet, CI | ✅ |
| 2 | Lista okien, wybór, przechwycenie, podgląd | ✅ |
| 3 | Systemowe OCR Windows (`Windows.Media.Ocr`) | ✅ |
| 4 | Tłumaczenie: DeepL + Mock, klucz w DPAPI | ✅ |
| 5 | Cache SQLite (priorytet ręcznych korekt, eksport/import) | ✅ |
| 6 | **MVP: Ctrl+Shift+T → region → polski wynik w panelu** | ✅ |
| 7 | Nakładka click-through (podstawowa) | ✅ |
| 8–12 | Tryb live, strategie Tooltip/Subtitle, edytor słowników, wydanie | 🔜 [docs/ROADMAP.md](docs/ROADMAP.md) |

Funkcje wymagające prawdziwego pulpitu (skróty globalne, nakładka, DPI, multi-monitor) mają
scenariusze testów ręcznych w [docs/MANUAL_TESTING.md](docs/MANUAL_TESTING.md).

## Wymagania

- Windows 10 (2004+) albo Windows 11,
- pakiet językowy Windows dla języka źródłowego (np. angielski — sprawdź w
  *Ustawienia → Czas i język → Język i region*),
- klucz DeepL API do tłumaczenia online (darmowy plan: 500 000 znaków/mies.; klucz darmowy
  kończy się na `:fx`). Bez klucza działa dostawca testowy **Mock** oraz tryb **Cache-only**,
- **zero** Pythona, Ollamy, lokalnych modeli AI, CUDA i dedykowanego GPU.

## Szybki start (użytkownik)

1. Zbuduj wersję Release (albo pobierz artefakt `GameTranslatorOverlay-win-x64` z CI):

   ```bash
   dotnet publish src/GameTranslatorOverlay.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
   ```

2. Uruchom `publish/GameTranslatorOverlay.exe`.
3. Wklej klucz DeepL w sekcji „Klucz API” i kliknij **Zapisz klucz** (trafia zaszyfrowany
   przez DPAPI do `%LOCALAPPDATA%\GameTranslatorOverlay\secrets`), potem **Testuj**.
4. Uruchom grę w trybie okienkowym albo borderless fullscreen.
5. Wciśnij **Ctrl+Shift+T**, zaznacz myszą tooltip/dialog — polskie tłumaczenie pojawi się
   w panelu obok (albo w nakładce, zależnie od ustawienia „Wyświetlanie wyniku").
6. **Ctrl+Shift+H** chowa/pokazuje nakładkę. Błędne tłumaczenie poprawisz przyciskiem
   **Popraw** (korekta zostaje na zawsze), a termin dodasz do słownika przyciskiem **+ Słownik**.

Tryb **prywatny** (checkbox) wyłącza zapis na dysk; tryb **Cache-only** pokazuje wyłącznie
tłumaczenia z lokalnej bazy i niczego nie wysyła do internetu.

## Szybki start (deweloper)

```bash
dotnet build
```

```bash
dotnet test
```

```bash
dotnet run --project tools/GameTranslatorOverlay.SmokeTest
```

```bash
dotnet run --project src/GameTranslatorOverlay.App
```

Smoke test renderuje syntetyczny tooltip RPG, puszcza go przez prawdziwe OCR Windows,
grupowanie bloków i pełny pipeline tłumaczenia z cache SQLite (dostawca Mock) — kod wyjścia 0
oznacza działający pion. Testy jednostkowe/integracyjne (107) nie wymagają klucza API ani GUI.

## Struktura repozytorium

```
src/
  GameTranslatorOverlay.Core/            czysta logika: pipeline, normalizacja, glossary, modele
  GameTranslatorOverlay.Infrastructure/  SQLite cache, DeepL, DPAPI, ustawienia, katalogi plików
  GameTranslatorOverlay.App/             WPF: UI, capture (GDI), Windows OCR, nakładka, skróty
tests/                                   xUnit: Core.Tests (73), Infrastructure.Tests (34)
tools/GameTranslatorOverlay.SmokeTest/   smoke test E2E bez GUI
profiles/                                profile gier (generic, path-of-exile-2)
glossaries/                              słowniki EN→PL (globalny 44, PoE2 118 terminów)
docs/                                    wizja, architektura, decyzje, bezpieczeństwo, testy
.github/workflows/ci.yml                 build + testy (windows-latest) + artefakt Release
```

## Prywatność w skrócie

Przechwytywany jest tylko wybrany region/okno. OCR działa lokalnie. Do API tłumaczeniowego
wysyłany jest **wyłącznie rozpoznany tekst** — nigdy obraz. Program nie zapisuje screenshotów
i nie nagrywa rozgrywki. Dane (cache, ustawienia, logi) trzyma w
`%LOCALAPPDATA%\GameTranslatorOverlay`. Pełna polityka: [docs/PRIVACY.md](docs/PRIVACY.md).

## Znane ograniczenia (v0.1.0)

- Gry w trybie **exclusive fullscreen** nie są obsługiwane (użyj borderless/okienkowego).
- Zaznaczanie regionu działa na monitorze, na którym stoi kursor.
- Tryb live (automatyczna analiza ekranu), ikona zasobnika i edytor słowników — w kolejnych
  etapach ([docs/ROADMAP.md](docs/ROADMAP.md)).
- Jakość OCR zależy od czcionki gry; małe/ozdobne czcionki mogą wymagać większego regionu.

## Dokumentacja

[PRODUCT_VISION](docs/PRODUCT_VISION.md) • [ARCHITECTURE](docs/ARCHITECTURE.md) •
[TECHNOLOGY_DECISIONS](docs/TECHNOLOGY_DECISIONS.md) • [ROADMAP](docs/ROADMAP.md) •
[SECURITY](docs/SECURITY.md) • [PRIVACY](docs/PRIVACY.md) •
[API_PROVIDERS](docs/API_PROVIDERS.md) • [TESTING](docs/TESTING.md) •
[MANUAL_TESTING](docs/MANUAL_TESTING.md)

---

*GameTranslatorOverlay nie jest powiązany z twórcami żadnej z obsługiwanych gier. Korzystanie
z nakładek może podlegać regulaminom poszczególnych gier — sprawdź zasady swojej gry.*
