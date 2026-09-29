# GameTranslatorOverlay

Tłumacz EN→PL dla gier na Windows. Czyta tekst z ekranu (Windows OCR), tłumaczy go
i pokazuje polską wersję w przezroczystej nakładce nad grą. Klikanie przez nakładkę działa normalnie.

[Pobierz](https://github.com/KubaZx/Game_Translator/releases) ·
[Instrukcja](docs/USER_GUIDE.md) · [Zmiany](CHANGELOG.md) ·
[CI](https://github.com/KubaZx/Game_Translator/actions/workflows/ci.yml)

**Wersja: 0.3.1** · Windows 10 2004+ / 11 · portable, bez instalacji

## Funkcje

| Funkcja | Jak |
|---|---|
| Tłumaczenie fragmentu | `Ctrl+Shift+T` → zaznacz obszar |
| Tryb live | wybierz okno gry → **▶ Start live** |
| Ukrycie nakładki | `Ctrl+Shift+H` |
| Wygląd | bloki przy oryginale (opcjonalnie zakrywające tekst) albo napisy na dole |
| Dostawcy | DeepL, Azure AI Translator, Google, Claude, serwer zgodny z OpenAI (także lokalna Ollama / LM Studio), Mock |
| Słownik | własne terminy i poprawki, import/eksport; w DeepL jako glosariusz (terminy odmieniane także w środku zdań) |
| Kontekst | do 6 poprzednich linii dialogu + nazwa gry + terminy słownika |
| Cache | SQLite z pamięcią w RAM; ten sam tekst nie idzie drugi raz do API |
| Offline | Cache-only (bez API), lokalny model LLM, Mock (test bez klucza) |

Kolejność wyboru tłumaczenia: **ręczna poprawka → słownik → cache → dostawca**.

## Jak szybko reaguje

| Sytuacja | Czas |
|---|---:|
| Przechwycenie klatki gry (PrintWindow, PoE2) | 25–48 ms |
| Sprawdzanie zmian na ekranie w live | 6× na sekundę (co ~167 ms) |
| Znany tekst (cache / słownik): od pojawienia się do gotowego napisu | ~0,3–0,45 s |
| Odczyt 20 napisów z cache | ~0,25 ms na klatkę (było 10–13 ms) |
| Nowy tekst (pierwszy raz) | ~0,3–0,5 s + czas odpowiedzi dostawcy |
| Nowy tekst przy dostawcy odpowiadającym 2 s | ~2,3–2,5 s (było do 4,1 s) |
| Zniknięcie napisu, gdy tekst w grze się zmienił | 16–95 ms (najwyżej ~156 ms) |
| Zniknięcie napisu przykrytego w grze panelem | 0,29 s (było 2,86 s) |
| Najdłuższa pauza OCR przy ciągłym ruchu kamery | 2,5 s |
| Pełne ponowne sprawdzenie nieruchomego ekranu | co 4 s |

Pomiary z lokalnych sond (własne okno testowe, prawdziwy Windows OCR, Mock zamiast
prawdziwego dostawcy). Mierzą moment gotowości napisu w aplikacji, nie rysowanie na ekranie.
Czas odpowiedzi DeepL i innych dostawców nie był jeszcze mierzony. Zależy od sieci i długości tekstu.
Szczegóły: [ROADMAP.md](docs/ROADMAP.md).

Własne czasy z gry pokazuje panel **Szybkość** w oknie aplikacji: mediana i p90 dla nowego
i znanego tekstu, odpowiedzi dostawcy, OCR i przechwycenia. **Kopiuj raport** kopiuje je do schowka.

## Szybki start

1. Pobierz zip z [Releases](https://github.com/KubaZx/Game_Translator/releases), rozpakuj,
   uruchom `GameTranslatorOverlay.exe`.
2. Wybierz dostawcę, wklej klucz API → **Zapisz klucz** → **Testuj**.
3. Uruchom grę w oknie lub borderless, wybierz ją z listy.
4. **▶ Start live**.

Aktualizacja: rozpakuj nową wersję w miejsce starej. Ustawienia, klucze i cache są
w `%LOCALAPPDATA%\GameTranslatorOverlay`, więc zostają.

## Wymagania

- Windows 10 (2004+) lub 11, x64.
- Pakiet językowy OCR dla języka gry (np. angielski).
- Klucz API wybranego dostawcy. Bez klucza działają: lokalny LLM, Mock i Cache-only.
- Gra w oknie lub borderless. **Exclusive fullscreen nie działa.**

.NET jest w paczce. Nie trzeba Pythona, CUDA ani modeli AI.

## Prywatność

- OCR działa lokalnie. Do dostawcy trafia **tylko tekst**, nigdy obraz.
- Klucze API są szyfrowane DPAPI i wysyłane tylko do wybranego dostawcy.
- Z lokalnym LLM tekst nie wychodzi z komputera.
- Aplikacja nie modyfikuje gry, nie czyta jej pamięci i nie wysyła do niej klawiszy.

Więcej: [PRIVACY.md](docs/PRIVACY.md), [SECURITY.md](docs/SECURITY.md).

## Ograniczenia

- Ozdobne lub małe czcionki, słaby kontrast i animowane tło pogarszają OCR.
- Przy silnym ruchu kamery nakładka może znikać i wracać.
- Czas tłumaczenia zależy od sieci i dostawcy.
- Przechwytywanie przez PrintWindow/GDI; czasem potrzebny jest zrzut ekranu i wtedy
  okna nad grą mogą trafić do odczytu (aplikacja ostrzega).
- Zaznaczanie regionu działa na monitorze z kursorem.

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
- Testy: 562 (402 Core + 160 Infrastructure), bez kluczy API i bez gry. Szczegóły: [TESTING.md](docs/TESTING.md).

## Struktura

| Katalog | Zawartość |
|---|---|
| `src/GameTranslatorOverlay.Core` | tekst, tłumaczenia, cache, stabilizacja live |
| `src/GameTranslatorOverlay.Infrastructure` | dostawcy API, SQLite, DPAPI |
| `src/GameTranslatorOverlay.App` | WPF, przechwytywanie, OCR, nakładka |
| `tests/` | testy xUnit |
| `tools/` | LiveDiag, SceneReplay, OcrLab, SmokeTest |
| `profiles/`, `glossaries/` | profile gier i słowniki |

Dokumenty: [Instrukcja](docs/USER_GUIDE.md) · [Dostawcy API](docs/API_PROVIDERS.md) ·
[Architektura](docs/ARCHITECTURE.md) · [Decyzje](docs/TECHNOLOGY_DECISIONS.md) ·
[Plan](docs/ROADMAP.md) · [Testy ręczne](docs/MANUAL_TESTING.md)
