# GameTranslatorOverlay

Uniwersalny tłumacz gier dla Windows. Przechwytuje obraz wybranego okna lub regionu ekranu,
rozpoznaje tekst **systemowym OCR Windows**, tłumaczy go (lokalny słownik → cache SQLite →
DeepL API) i wyświetla polską wersję w **niezależnej nakładce**, która nie przejmuje sterowania
i nie blokuje kliknięć.

> **Zasada nr 1:** program działa w 100% pasywnie. Nie modyfikuje gry, jej plików, procesu
> ani pamięci; nie wysyła do gry żadnych kliknięć ani klawiszy. Pracuje wyłącznie na obrazie,
> który i tak widzisz na ekranie. Szczegóły: [docs/SECURITY.md](docs/SECURITY.md).

## Status projektu (v0.2.1 — wydane)

**Wszystkie etapy 0–12 ukończone.** Gotowe zipy do pobrania:
**[Releases](https://github.com/KubaZx/Game_Translator/releases)** (portable win-x64,
bez instalacji — rozpakuj i uruchom).

| Etap | Zakres | Status |
|---|---|---|
| 0–1 | Analiza, dokumentacja, szkielet, CI | ✅ |
| 2 | Lista okien, wybór, przechwycenie, podgląd | ✅ |
| 3 | Systemowe OCR Windows (`Windows.Media.Ocr`) | ✅ |
| 4 | Tłumaczenie: DeepL + Mock, klucz w DPAPI | ✅ |
| 5 | Cache SQLite (priorytet ręcznych korekt, eksport/import) | ✅ |
| 6 | **MVP: Ctrl+Shift+T → region → polski wynik w panelu** | ✅ |
| 7 | Nakładka click-through + wykluczenie z przechwytywania | ✅ |
| 8 | Tryb live: wykrywanie zmian, stabilizacja, latest-frame-wins | ✅ |
| 9 | Strategie live: przy oryginale (z wtapianiem w tło gry) / napisy na dole | ✅ |
| 10 | Edytor słownika, import/eksport JSON, konflikty | ✅ |
| 11 | Profile gier + autodetekcja po procesie (PoE2 w zestawie) | ✅ |
| 12 | Wersja produkcyjna: portable zip, tray, release z CI, instrukcje, licencje | ✅ |

Tryb live jest strojony na prawdziwych grach (Path of Exile 2, gry ze statycznym obrazem):
okres łaski maskuje czknięcia OCR, tłumaczenie reaguje w ~0,3–0,7 s, a w trybie „Na oryginale
(zakrywa)" łatka przejmuje kolor tła i czcionki gry. Automatyczna detekcja tooltipów — w planach
([docs/ROADMAP.md](docs/ROADMAP.md), tam też zmiany: [CHANGELOG.md](CHANGELOG.md)).

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

1. Pobierz najnowszy zip z **[Releases](https://github.com/KubaZx/Game_Translator/releases)**
   i rozpakuj gdziekolwiek (alternatywnie zbuduj sam: `tools/package.ps1`).
2. Uruchom `GameTranslatorOverlay.exe`.
3. Wklej klucz DeepL w sekcji „Klucz API” i kliknij **Zapisz klucz** (trafia zaszyfrowany
   przez DPAPI do `%LOCALAPPDATA%\GameTranslatorOverlay\secrets`), potem **Testuj**.
4. Uruchom grę w trybie okienkowym albo borderless fullscreen.
5. Wciśnij **Ctrl+Shift+T**, zaznacz myszą tooltip/dialog — polskie tłumaczenie pojawi się
   w panelu obok (albo w nakładce, zależnie od ustawienia „Wyświetlanie wyniku").
6. **Tryb live**: wybierz okno gry z listy i kliknij **▶ Start live** — tłumaczenia pojawiają
   się same nad tekstem gry. Położenie „Na oryginale (zakrywa)" wtapia tłumaczenie w tło gry.
7. **Ctrl+Shift+H** chowa/pokazuje nakładkę. Błędne tłumaczenie poprawisz przyciskiem
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
oznacza działający pion. Testy jednostkowe/integracyjne (143) nie wymagają klucza API ani GUI.
Diagnostyka trybu live: `dotnet run --project tools/GameTranslatorOverlay.LiveDiag -- 36`
(okno testowe z animacjami) albo `-- "fragment tytułu" 60` (podpięcie pod istniejące okno).

## Struktura repozytorium

```
src/
  GameTranslatorOverlay.Core/            czysta logika: pipeline, normalizacja, glossary, modele
  GameTranslatorOverlay.Infrastructure/  SQLite cache, DeepL, DPAPI, ustawienia, katalogi plików
  GameTranslatorOverlay.App/             WPF: UI, capture (GDI), Windows OCR, nakładka, skróty
tests/                                   xUnit: Core.Tests (106), Infrastructure.Tests (37)
tools/GameTranslatorOverlay.SmokeTest/   smoke test E2E bez GUI
tools/GameTranslatorOverlay.LiveDiag/    stanowisko diagnostyczne trybu live (telemetria)
profiles/                                profile gier (generic, path-of-exile-2)
glossaries/                              słowniki EN→PL (globalny 44, PoE2 118 terminów)
docs/                                    wizja, architektura, decyzje, bezpieczeństwo, testy
.github/workflows/ci.yml                 build + testy (windows-latest) + release na tag v*
```

## Prywatność w skrócie

Przechwytywany jest tylko wybrany region/okno. OCR działa lokalnie. Do API tłumaczeniowego
wysyłany jest **wyłącznie rozpoznany tekst** — nigdy obraz. Program nie zapisuje screenshotów
i nie nagrywa rozgrywki. Dane (cache, ustawienia, logi) trzyma w
`%LOCALAPPDATA%\GameTranslatorOverlay`. Pełna polityka: [docs/PRIVACY.md](docs/PRIVACY.md).

## Znane ograniczenia (v0.2.1)

- Gry w trybie **exclusive fullscreen** nie są obsługiwane (użyj borderless/okienkowego).
- Zaznaczanie regionu działa na monitorze, na którym stoi kursor.
- Tryb live używa PrintWindow/GDI (na DX12, np. PoE2, działa — zmierzone); u nielicznych gier
  może zajść fallback do zrzutu ekranu, o czym aplikacja wprost ostrzega. Windows Graphics
  Capture pozostaje możliwym ulepszeniem.
- Wtapianie tłumaczeń jest niemal idealne na jednolitych tłach (dialogi, tooltipy, menu);
  napisy wiszące bezpośrednio nad światem 3D dostają dyskretny podkład w kolorze otoczenia.
- Nakładka jest wykluczona z przechwytywania ekranu — nie zobaczysz jej na nagraniach OBS
  (to celowe: OCR nie może czytać własnych tłumaczeń).
- Automatyczna detekcja tooltipów (bez zaznaczania) — planowana; dziś tooltipy tłumaczy
  tryb ręczny.
- Jakość OCR zależy od czcionki gry; małe/ozdobne czcionki mogą wymagać większego regionu.

Instrukcja użytkownika: [docs/USER_GUIDE.md](docs/USER_GUIDE.md) • Pakowanie wydania:
`tools/package.ps1` → `dist/GameTranslatorOverlay-vX.Y.Z-win-x64.zip`.

## Dokumentacja

[PRODUCT_VISION](docs/PRODUCT_VISION.md) • [ARCHITECTURE](docs/ARCHITECTURE.md) •
[TECHNOLOGY_DECISIONS](docs/TECHNOLOGY_DECISIONS.md) • [ROADMAP](docs/ROADMAP.md) •
[SECURITY](docs/SECURITY.md) • [PRIVACY](docs/PRIVACY.md) •
[API_PROVIDERS](docs/API_PROVIDERS.md) • [TESTING](docs/TESTING.md) •
[MANUAL_TESTING](docs/MANUAL_TESTING.md)

---

*GameTranslatorOverlay nie jest powiązany z twórcami żadnej z obsługiwanych gier. Korzystanie
z nakładek może podlegać regulaminom poszczególnych gier — sprawdź zasady swojej gry.*
