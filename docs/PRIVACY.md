# PRIVACY.md — polityka prywatności

Ten dokument opisuje, jakie dane GameTranslatorOverlay przetwarza, gdzie one trafiają i jak
użytkownik może to kontrolować. Zasada przewodnia: **przetwarzamy możliwie mało, możliwie
lokalnie, i mówimy wprost, co opuszcza komputer**.

## Co program przechwytuje

- Program przechwytuje obraz **wyłącznie wybranego przez użytkownika okna lub zaznaczonego
  regionu ekranu** — nigdy całego pulpitu „w tle" ani innych okien.
- Przechwytywanie odbywa się oficjalnymi mechanizmami Windows (GDI; w przyszłości Windows
  Graphics Capture — Etap 8) i dzieje się tylko wtedy, gdy użytkownik tego zażąda
  (skrót/tryb tłumaczenia). Program niczego nie nagrywa.

## OCR działa lokalnie

Rozpoznawanie tekstu wykonuje **systemowy OCR Windows** (`Windows.Media.Ocr`) — w całości
na komputerze użytkownika. Obraz nie jest nigdzie wysyłany w celu rozpoznania tekstu.
Wymagany jest zainstalowany pakiet językowy Windows dla języka źródłowego; program nie
korzysta z żadnych chmurowych ani lokalnych modeli AI (brak LLM, brak zewnętrznych silników OCR).

## Co trafia do API tłumaczeniowego

- Do zewnętrznego API (np. DeepL) wysyłany jest **wyłącznie rozpoznany tekst** — krótkie
  fragmenty, które faktycznie wymagają tłumaczenia (po odfiltrowaniu śmieci i po sprawdzeniu
  słownika oraz cache).
- **Nigdy nie są wysyłane screenshoty** ani żadne inne obrazy.
- **Ważne i mówione wprost w aplikacji:** korzystanie z zewnętrznego API oznacza, że rozpoznany
  tekst **opuszcza komputer** i trafia na serwery dostawcy tłumaczeń (np. DeepL). Kto nie chce
  wysyłać niczego do sieci, może pracować w trybie **Cache-only** (nic nie wychodzi do sieci;
  tłumaczone jest tylko to, co już jest w cache/słowniku) albo z `MockTranslationProvider`.
  Zasady przetwarzania tekstu po stronie dostawcy opisuje polityka prywatności tego dostawcy.

## Screenshoty i zrzuty debugowe

- Domyślnie program **nie zapisuje żadnych zrzutów ekranu na dysk** i żadnych nie wysyła.
  Przechwycony obraz żyje tylko w pamięci na czas OCR.
- Zrzuty debugowe (do diagnozowania problemów z OCR) można włączyć **wyłącznie jawnie**
  w ustawieniach. Po włączeniu trafiają do wyraźnie oznaczonego folderu w
  `%LOCALAPPDATA%\GameTranslatorOverlay`, a w ustawieniach dostępny jest przycisk
  **szybkiego usunięcia** całej zawartości tego folderu. Opcja nie włącza się nigdy sama.

## Tryb prywatny

Tryb prywatny jest przeznaczony do sytuacji, w których na ekranie może pojawić się wrażliwa
treść (np. czat w grze). Po włączeniu:

- **brak historii** — tłumaczenia nie są zapisywane w historii,
- **brak logowania treści** — logi (Serilog) nie zawierają żadnych tłumaczonych tekstów,
- **cache tylko ulotny** — wpisy cache trzymane są wyłącznie w pamięci, nie w bazie SQLite,
- **czyszczenie po sesji** — po zamknięciu programu ulotne dane sesji są usuwane.

Uwaga: tryb prywatny nie zmienia faktu, że przy korzystaniu z zewnętrznego API rozpoznany
tekst nadal jest wysyłany do dostawcy tłumaczeń. Aby nic nie opuszczało komputera, połącz
tryb prywatny z trybem Cache-only.

## Dane przechowywane lokalnie

Wszystkie dane programu leżą w `%LOCALAPPDATA%\GameTranslatorOverlay`:

| Dane | Co zawierają | Uwagi |
|---|---|---|
| `settings.json` | ustawienia programu | bez klucza API w postaci jawnej |
| cache SQLite | pary tekst źródłowy → tłumaczenie | pomijany w trybie prywatnym (cache tylko w pamięci) |
| klucz API | zaszyfrowany Windows DPAPI (`CurrentUser`) | odczyta go tylko ten sam użytkownik Windows na tej maszynie |
| logi (Serilog, rolling) | zdarzenia techniczne, błędy (stack trace tylko do logu) | nigdy kluczy API; bez treści tłumaczeń w trybie prywatnym |
| zrzuty debugowe | obrazy do diagnostyki OCR | tylko po jawnym włączeniu; przycisk szybkiego usunięcia |
| profile i słowniki | pliki JSON (`profiles/`, `glossaries/`) | dane statyczne, bez treści użytkownika |

Usunięcie folderu `%LOCALAPPDATA%\GameTranslatorOverlay` usuwa wszystkie dane programu.

## Czego program nie robi

- Nie zbiera telemetrii ani statystyk użycia i niczego nie wysyła „do producenta"
  (jedyny ruch sieciowy to zapytania do wybranego przez użytkownika API tłumaczeniowego).
- Nie zapisuje ani nie wysyła screenshotów.
- Nie czyta danych innych aplikacji, plików gry ani pamięci procesów.
- Nie tworzy kont, nie wymaga logowania, nie profiluje użytkownika.
