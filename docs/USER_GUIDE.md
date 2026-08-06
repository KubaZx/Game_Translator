# Instrukcja użytkownika — GameTranslatorOverlay

GameTranslatorOverlay tłumaczy na żywo angielski tekst z gier na polski. Działa jak
zewnętrzna nakładka: przechwytuje obraz, rozpoznaje tekst systemowym OCR Windows
i wyświetla tłumaczenie nad grą — **nie dotykając plików ani procesu gry**.

## Instalacja

1. Rozpakuj archiwum `GameTranslatorOverlay-vX.Y.Z-win-x64.zip` do dowolnego folderu.
2. Uruchom `GameTranslatorOverlay.exe`. Nie potrzebujesz .NET, Pythona ani karty graficznej.
3. Wymagania: Windows 10 (2004+) lub Windows 11 oraz **pakiet językowy Windows dla języka
   gry** (dla angielskiego: *Ustawienia → Czas i język → Język i region → Dodaj język →
   English (United States)*). Status OCR widać na dole głównego okna.

## Pierwsze uruchomienie — klucz DeepL

1. Załóż darmowe konto **DeepL API Free** na deepl.com (500 000 znaków/mies.).
2. Skopiuj klucz (kończy się na `:fx`), wklej w sekcji **Klucz API** i kliknij
   **Zapisz klucz**, potem **Testuj**. Klucz jest szyfrowany przez Windows (DPAPI)
   i nie opuszcza Twojego komputera.
3. Bez klucza możesz używać dostawcy **Mock** (testowy — dokleja `[PL]` zamiast tłumaczyć)
   albo trybu **Cache-only** (tylko tłumaczenia zapisane wcześniej).

## Tłumaczenie ręczne (podstawowy tryb)

1. Uruchom grę w trybie **okienkowym** lub **borderless fullscreen** (pełny ekran
   „wyłączny” nie jest obsługiwany).
2. Wciśnij **Ctrl+Shift+T** — ekran przyciemni się; zaznacz myszą fragment z tekstem
   (tooltip, dialog). **Esc** anuluje.
3. Tłumaczenie pojawi się w panelu obok zaznaczenia (albo w nakładce — do wyboru
   w „Wyświetlanie wyniku”). Panel nie zabiera grze fokusu; **Ctrl+Shift+H** chowa nakładkę.

W panelu wyniku możesz:
- **Kopiuj** — skopiować tłumaczenie,
- **Popraw** — wpisać własne tłumaczenie; zostanie zapamiętane i od tej pory zawsze wygrywa,
- **+ Słownik** — dodać parę termin→tłumaczenie do prywatnego słownika.

## Tryb live (automatyczny)

1. Wybierz okno gry z listy i kliknij **▶ Start live**.
2. Program obserwuje okno kilka razy na sekundę; gdy pojawi się nowy, stabilny tekst,
   tłumaczy go automatycznie i pokazuje w nakładce.
3. „Wyświetlanie” wybiera układ: **Przy oryginale** (dymki przy tekście) albo
   **Napisy na dole** (pasek jak napisy filmowe — najlepszy do dialogów).
4. **⏹ Stop** kończy tryb live. Minimalizacja gry chowa nakładkę automatycznie.

Wskazówka: profil gry (np. Path of Exile 2) włącza się sam po wybraniu okna gry —
dodaje słownik terminów i lepsze ustawienia OCR.

## Słownik i dane

- **📖 Słownik…** — edytor prywatnego słownika: dodawanie, edycja, priorytety,
  import/eksport JSON, wykrywanie konfliktów.
- **Wyczyść cache** — usuwa automatyczne tłumaczenia (ręczne poprawki zostają).
- **Eksport/Import cache…** — kopia zapasowa tłumaczeń (w tym poprawek) do pliku JSON.
- **Folder danych** — otwiera `%LOCALAPPDATA%\GameTranslatorOverlay` (cache, ustawienia, logi).

## Prywatność

- Do internetu wysyłany jest **wyłącznie rozpoznany tekst** (nigdy obraz) i tylko do
  wybranego dostawcy tłumaczeń.
- **Tryb prywatny**: nic nie zapisuje się na dysku — cache działa tylko w pamięci,
  a „+ Słownik” obowiązuje do końca sesji.
- **Tryb Cache-only**: aplikacja w ogóle nie łączy się z internetem.
- Pełna polityka: `PRIVACY.md`.

## Skróty

| Skrót | Działanie |
|---|---|
| Ctrl+Shift+T | przetłumacz zaznaczony region |
| Ctrl+Shift+H | ukryj / pokaż nakładkę |
| Esc (podczas zaznaczania) | anuluj |

Skróty można zmienić w pliku `settings.json` w folderze danych (wymagany modyfikator
Ctrl/Alt/Shift dla liter i cyfr).

## Rozwiązywanie problemów

| Problem | Rozwiązanie |
|---|---|
| „Brak pakietu językowego OCR” | doinstaluj język w ustawieniach Windows (patrz wyżej) |
| OCR nie widzi tekstu | zaznacz większy fragment; zwiększ rozmiar czcionki w grze; unikaj mocno ozdobnych fontów |
| Czarny podgląd okna | gra blokuje przechwytywanie okna — przełącz na borderless; tryb regionu (Ctrl+Shift+T) zwykle działa mimo to |
| „DeepL odrzucił klucz” | sprawdź klucz (darmowy kończy się na `:fx`) i czy plan API jest aktywny |
| Nakładka niewidoczna na nagraniu OBS | to celowe — nakładka jest wykluczona z przechwytywania ekranu |
| Inny problem | zajrzyj do logów: folder danych → `logs\` (logi nie zawierają treści z ekranu) |

## Uwaga o grach online

Program niczego nie wstrzykuje do gry i nie automatyzuje rozgrywki — działa wyłącznie
na obrazie ekranu. Mimo to regulaminy niektórych gier różnie traktują nakładki.
**Sprawdź zasady swojej gry przed użyciem.** Projekt nie jest powiązany z twórcami
żadnej z gier.
