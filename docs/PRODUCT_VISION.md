# Wizja produktu — GameTranslatorOverlay

## Co to jest

GameTranslatorOverlay to desktopowa aplikacja Windows (.NET 10, WPF), która tłumaczy na żywo tekst widoczny w grach (EN→PL) i wyświetla wynik w zewnętrznej, przezroczystej nakładce nad grą. Aplikacja działa całkowicie pasywnie: przechwytuje obraz oficjalnymi mechanizmami Windows, rozpoznaje tekst systemowym OCR i w żaden sposób nie ingeruje w grę.

Aplikacja jest unpackaged i portable — bez MSIX, bez instalatora wymagającego uprawnień, bez Pythona, bez GPU. Wymagania: Windows 10 2004+ lub Windows 11.

## Dla kogo

- Gracze, którzy grają w tytuły bez polskiej lokalizacji i nie chcą tracić fabuły, opisów przedmiotów ani mechanik.
- Gracze, którzy znają angielski częściowo — chcą doraźnie tłumaczyć wybrany fragment ekranu jednym skrótem, bez alt-tabowania do translatora.
- Pierwszy konkretny przypadek użycia: Path of Exile 2 (gra bez oficjalnego polskiego tłumaczenia, z dużą ilością tekstu na tooltipach i w dialogach).

## Przepływ danych

```
użytkownik wybiera okno gry
        │
        ▼
przechwytywanie obrazu (oficjalne API Windows; MVP: GDI, później Windows Graphics Capture)
        │
        ▼
systemowy OCR Windows (Windows.Media.Ocr) — obraz nie opuszcza komputera
        │
        ▼
normalizacja tekstu (czyszczenie artefaktów OCR, filtr śmieci)
        │
        ▼
lokalny słownik (glossary) — terminy gry tłumaczone lokalnie, spójnie
        │
        ▼
SQLite cache — teksty już przetłumaczone nie idą ponownie do sieci
        │
        ▼
DeepL API — WYŁĄCZNIE brakujący, rozpoznany tekst (nigdy screenshoty)
        │
        ▼
przezroczysta nakładka click-through nad grą (osobne okno systemowe, bez fokusu)
```

Priorytet źródeł tłumaczenia: **ręczna korekta użytkownika > wpis profilu gry > cache globalny > API**.

Do sieci wychodzi tylko rozpoznany tekst i tylko wtedy, gdy nie ma go w słowniku ani w cache. Tryb Cache-only pozwala pracować całkowicie offline (nic nie wychodzi do sieci).

## Tryby działania (kolejność wdrażania)

1. **Manual Region Mode** — MVP i pierwszy tryb. Globalny skrót (domyślnie `Ctrl+Shift+T`) → użytkownik zaznacza region ekranu → tekst z regionu jest rozpoznawany, tłumaczony i pokazywany w panelu wyniku / nakładce. Zero automatyki, pełna kontrola i przewidywalny koszt API.
2. **Tooltip Mode** — tłumaczenie tooltipów (np. opisów przedmiotów) w miejscu ich wyświetlania.
3. **Subtitle Mode** — tłumaczenie stałego pasa dialogów/napisów.
4. **Universal Live Mode** — ciągła analiza wybranego obszaru (3–6 fps), OCR uruchamiany tylko przy wykrytej zmianie obrazu, zasada latest-frame-wins (nieaktualne klatki są porzucane, tłumaczy się zawsze najnowszą).
5. **History Mode** — przegląd historii przetłumaczonych tekstów z sesji.

Funkcja „wyjaśnij prostym językiem" (LLM przez zewnętrzne API) jest opcjonalna, poza MVP i domyślnie **wyłączona**.

## Path of Exile 2 — profil, nie hardcode

Rdzeń aplikacji jest w pełni uniwersalny i nie zawiera żadnej wiedzy o konkretnej grze. Cała specyfika gry mieszka wyłącznie w opcjonalnych plikach danych:

- **Profil gry** (`profiles/<id>/profile.json`) — nazwy procesów i tytuły okien do wykrywania gry, język źródłowy, rekomendowany tryb, parametry OCR (upscale, minimalna wysokość tekstu) i detekcji zmian (próg, fps), powiązany słownik, minimalna wersja aplikacji.
- **Słownik** (`glossaries/<id>/en-pl.json`) — terminy gry z tłumaczeniami (np. „Energy Shield" → „Tarcza energetyczna"), z priorytetami i opcjonalną wrażliwością na wielkość liter. Dopasowanie zawsze całych słów/fraz, dłuższe frazy przed krótszymi, konflikty rozstrzygają priorytety.

Profil PoE2 jest po prostu pierwszym dostarczonym profilem (Etap 11 roadmapy). Usunięcie go nie zmienia niczego w działaniu aplikacji dla dowolnej innej gry — bez profilu wszystko działa na ustawieniach ogólnych.

## Czego produkt świadomie NIE robi

Twarde ograniczenia — to nie są braki, tylko decyzje projektowe:

- **Zero lokalnych modeli AI.** Bez Ollamy, lokalnych LLM, PaddleOCR/EasyOCR/Tesseract, Pythona, CUDA, Dockera. OCR wyłącznie systemowy (Windows.Media.Ocr — wymaga zainstalowanego pakietu językowego Windows; przy jego braku aplikacja pokazuje czytelny komunikat z instrukcją doinstalowania).
- **Zero ingerencji w grę.** Bez wstrzykiwania DLL, hookowania, czytania i modyfikacji pamięci procesu, modyfikacji plików gry, przechwytywania pakietów sieciowych, automatyzacji rozgrywki i wysyłania jakiegokolwiek inputu do gry. Globalny skrót steruje wyłącznie tłumaczem. Nakładka to osobne okno systemowe (click-through, bez przejmowania fokusu) — gra nawet nie wie, że istnieje.
- **Zero wysyłania obrazu do sieci.** Do API tłumaczeniowego idzie wyłącznie rozpoznany tekst — nigdy screenshoty. Domyślnie żadne zrzuty ekranu nie są zapisywane na dysk.
- **Zero zbierania danych.** Klucz API tylko lokalnie (DPAPI, CurrentUser), nigdy w logach ani w repo. Tryb prywatny: bez historii, bez logowania treści tłumaczeń, cache tylko w pamięci, czyszczony po sesji.

## Ograniczenia znane i udokumentowane

- Exclusive fullscreen nie jest obsługiwany — aplikacja działa dla okien i borderless fullscreen (standard w nowych grach, w tym PoE2).
- Windows.Media.Ocr nie udostępnia per-słowo confidence — filtr śmieci działa na poziomie rozpoznanego tekstu.
- Przy korzystaniu z zewnętrznego API tłumaczeniowego rozpoznany tekst opuszcza komputer — aplikacja komunikuje to jasno, a tryb Cache-only pozwala to całkowicie wyłączyć.

## Disclaimer

GameTranslatorOverlay jest zewnętrzną nakładką i nie modyfikuje gry. Projekt nie gwarantuje zgodności z regulaminem każdej gry — użytkownik powinien sprawdzić zasady konkretnego tytułu przed użyciem. Projekt nie jest powiązany z twórcami żadnej gry.
