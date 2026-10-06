# GameTranslatorOverlay

[![CI](https://github.com/KubaZx/Game_Translator/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/KubaZx/Game_Translator/actions/workflows/ci.yml)

Tłumacz EN→PL dla gier na Windows. Czyta tekst z ekranu (Windows OCR), tłumaczy go
i pokazuje polską wersję w przezroczystej nakładce nad grą. Klikanie przez nakładkę działa normalnie.

[Pobierz](https://github.com/KubaZx/Game_Translator/releases) ·
[Instrukcja](docs/USER_GUIDE.md) · [Zmiany](CHANGELOG.md) ·
[CI](https://github.com/KubaZx/Game_Translator/actions/workflows/ci.yml)

**Wersja: 0.4.0** · Windows 10 2004+ / 11 · portable, bez instalacji

## Funkcje

| Funkcja | Jak |
|---|---|
| Tłumaczenie fragmentu | `Ctrl+Shift+T` → zaznacz obszar |
| Tryb live | `Ctrl+Shift+L` w grze (start/stop) albo wybierz okno gry → **▶ Start live** |
| Ukrycie nakładki | `Ctrl+Shift+H` |
| Komunikaty w grze | brak klucza, limit, brak sieci, Cache-only, start/stop live — krótki pasek u góry okna gry |
| Wygląd | bloki przy oryginale (opcjonalnie zakrywające tekst) albo napisy na dole |
| Dostawcy | DeepL, Azure AI Translator, Google, Claude, serwer zgodny z OpenAI (także lokalna Ollama / LM Studio), Mock |
| Słownik | własne terminy i poprawki, import/eksport; w DeepL jako glosariusz (terminy odmieniane także w środku zdań); liczba mnoga („Waystones”); zakres „Etykieta” dla przycisków i nagłówków |
| Kontekst | do 6 poprzednich linii dialogu + nazwa gry + terminy słownika |
| Pamięć dialogu (LLM) | Claude i serwer zgodny z OpenAI widzą swoje poprzednie tłumaczenia; **Postać gracza** (kobieta/mężczyzna) dla form „zrobiłaś”/„zrobiłeś” |
| Kontrola jakości | pusty wynik nie trafia do cache; zgubione liczby, brak tłumaczenia i „rozgadany” wynik są oznaczane, modele językowe dostają jedno ponowienie |
| Cache | SQLite z pamięcią w RAM; ten sam tekst nie idzie drugi raz do API; błąd bazy nie zatrzymuje tłumaczenia |
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

Własne czasy z gry pokazuje panel **Szybkość** w oknie aplikacji: mediana i p90 dla
**Zmiana → napis** (od zauważonej zmiany obrazu do gotowych napisów — czas, który widzi gracz),
nowego i znanego tekstu, odpowiedzi dostawcy, OCR i przechwycenia. **Kopiuj raport** kopiuje je
do schowka.

W 0.4.0 okno stabilności liczy się od zauważonej zmiany (do ~250 ms mniej czekania), a znany
ekran nie czeka w kolejce za wolnym tłumaczeniem starszej klatki. Zysk wynika z logiki
harmonogramu i testów; w grze nie był jeszcze mierzony, a tabela wyżej pochodzi sprzed tych zmian.

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

- OCR działa lokalnie. Do dostawcy trafia **tylko tekst**, nigdy obraz. Komunikaty w nakładce
  nie zawierają tekstu z ekranu.
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
- `Ctrl+Shift+L` uruchamia live na **dowolnym** aktywnym oknie (także przeglądarce) — to jawna
  akcja użytkownika. Gry UWP / Microsoft Store / Game Pass nie są wybierane jako aktywne okno:
  wybierz je raz na liście i kliknij **▶ Start live**.
- Ostrzeżenie kontroli jakości nie jest jeszcze pokazywane w oknie ani nakładce.

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
- Testy: 1114 (880 Core + 234 Infrastructure; na Linuksie 3 testy DPAPI są pomijane), bez kluczy
  API i bez gry. CI sprawdza pokrycie kodu (Core ≥ 91%, Infrastructure ≥ 80%).
  Szczegóły: [TESTING.md](docs/TESTING.md).
- Porównanie dostawców i promptów: `tools/GameTranslatorOverlay.ProviderEval` (chrF, czasy,
  kontrole EN→PL) — [README narzędzia](tools/GameTranslatorOverlay.ProviderEval/README.md).
- Benchmarki BenchmarkDotNet: `benchmarks/` — [BENCHMARKS.md](docs/BENCHMARKS.md).

## Struktura

| Katalog | Zawartość |
|---|---|
| `src/GameTranslatorOverlay.Core` | tekst, tłumaczenia, cache, stabilizacja live |
| `src/GameTranslatorOverlay.Infrastructure` | dostawcy API, SQLite, DPAPI |
| `src/GameTranslatorOverlay.App` | WPF, przechwytywanie, OCR, nakładka |
| `tests/` | testy xUnit |
| `tools/` | LiveDiag, SceneReplay, OcrLab, SmokeTest, ProviderEval, CorpusTool, CorpusEval, OverlayPreview |
| `benchmarks/` | benchmarki BenchmarkDotNet (poza `dotnet test`) |
| `eval/` | korpus EN→PL do ProviderEval |
| `profiles/`, `glossaries/` | profile gier i słowniki |

Dokumenty: [Instrukcja](docs/USER_GUIDE.md) · [Dostawcy API](docs/API_PROVIDERS.md) ·
[Architektura](docs/ARCHITECTURE.md) · [Decyzje](docs/TECHNOLOGY_DECISIONS.md) ·
[Plan](docs/ROADMAP.md) · [Testy ręczne](docs/MANUAL_TESTING.md) ·
[Testy](docs/TESTING.md) · [Benchmarki](docs/BENCHMARKS.md)
