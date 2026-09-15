# Instrukcja użytkownika — GameTranslatorOverlay

GameTranslatorOverlay tłumaczy na żywo angielski tekst z gier na polski. Działa jak
zewnętrzna nakładka: przechwytuje obraz, rozpoznaje tekst systemowym OCR Windows
i wyświetla tłumaczenie nad grą — **nie dotykając plików ani procesu gry**.

Projekt jest przeznaczony do różnych gier; nie wymaga profilu konkretnego tytułu.
Instrukcja opisuje bieżący kod na `main`. Ostatnie wydanie 0.2.2 jest starsze od
najnowszych poprawek stabilności — szczegóły w
[historii zmian](https://github.com/KubaZx/Game_Translator/blob/main/CHANGELOG.md).

## Instalacja

1. Rozpakuj archiwum `GameTranslatorOverlay-vX.Y.Z-win-x64.zip` do dowolnego folderu.
2. Uruchom `GameTranslatorOverlay.exe`. Środowisko .NET jest w paczce; nie musisz instalować Pythona ani lokalnych modeli AI.
3. Wymagania: Windows 10 (2004+) lub Windows 11 oraz **pakiet językowy Windows dla języka
   gry** (dla angielskiego: *Ustawienia → Czas i język → Język i region → Dodaj język →
   English (United States)*). Status OCR widać na dole głównego okna.

## Pierwsze uruchomienie — klucz DeepL

1. Przygotuj konto i klucz **DeepL API**. Zwykły dostęp do internetowego tłumacza DeepL nie zastępuje klucza API.
2. Skopiuj swój klucz API, wklej w sekcji **Klucz API** i kliknij
   **Zapisz klucz**, potem **Testuj**. Klucz jest przechowywany lokalnie z ochroną
   Windows DPAPI i używany do uwierzytelniania żądań do DeepL.
3. Bez klucza możesz używać dostawcy **Mock** (testowy — dokleja `[PL]` zamiast tłumaczyć)
   albo trybu **Cache-only** (lokalne poprawki, słownik i zapisane tłumaczenia).

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

Jeżeli aplikacja rozpozna dostarczony profil, może dobrać go do wybranego okna.
Pozostałe gry działają na ustawieniach ogólnych. Profil PoE2 w zestawie jest
opcjonalnym dodatkiem ze słownikiem terminów.

### Jak uzyskać czytelny wynik

- Do opisów i menu wybierz **Przy oryginale**. Opcja **Na oryginale (zakrywa)**
  umieszcza tłumaczenie nad tekstem gry; **Pod oryginałem** pokazuje je poniżej.
- Do dialogów możesz wybrać **Napisy na dole**. Jest to wspólny pasek świeżych
  tekstów rozpoznanych przez live, bez automatycznego rozpoznawania rodzaju wypowiedzi.
- Po ruchu kamery daj obrazowi na chwilę się zatrzymać. Silny ruch nadal może
  tymczasowo usuwać nakładkę, a nowe tłumaczenie może czekać na OCR i dostawcę.
- Przy trudnej czcionce lub konkretnym opisie użyj **Ctrl+Shift+T** i zaznacz
  interesujący fragment. Automatyczne wydzielanie obszaru tooltipu jest w planach.

Stałe menu może pozostać podczas ruchu tła, jeśli obraz pod jego napisami nie zmienił
się ani o piksel. Zmieniony lub zasłonięty napis traci tę ochronę. Animowane tło,
skalowanie odczytu lub zapasowe przechwytywanie ekranu mogą uniemożliwić zachowanie menu.

Bieżący kod usuwa potwierdzone stare opisy przed nadejściem nowego tłumaczenia
i dokładniej dopasowuje pozycję. Na wzorzystym tle albo przy niepewnym OCR stary
napis może być chwilowo podtrzymany. Te mechanizmy ograniczają błędy, ale nie
zapewniają jednakowego czasu i wyglądu w każdej grze.

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
- **Tryb Cache-only**: tłumaczenie korzysta z lokalnych wyników i nie wysyła brakującego tekstu do dostawcy.
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
| Widać `[PL]` i nadal angielski tekst | wybrano Mock; do prawdziwego tłumaczenia online wybierz DeepL i skonfiguruj klucz API |
| Live jest opóźniony lub znika przy ruchu | zatrzymaj na chwilę kamerę; sprawdź osobno czas nowego wyniku i znikania starego; porównaj z ręcznym regionem |
| W Cache-only nie pojawia się nowy opis | tego tekstu może nie być w lokalnych wynikach; tryb celowo nie pyta dostawcy |
| Inny problem | zajrzyj do logów: folder danych → `logs\` (logi nie zawierają treści z ekranu) |

## Uwaga o prywatności w trybie live

Dla większości gier tryb live czyta obraz **wyłącznie z okna gry** (PrintWindow).
Jeżeli gra tego nie wspiera (część tytułów DirectX/Vulkan), aplikacja przechodzi na
zrzut ekranu w prostokącie okna gry i **wyraźnie o tym ostrzega** w statusie live —
w takim wypadku fragmenty innych okien nachodzących na grę (np. powiadomienia)
mogłyby zostać rozpoznane i przetłumaczone. Jeśli to dla Ciebie problem: zamknij
poufne okna znad gry, korzystaj z trybu ręcznego (zaznaczasz dokładnie ten fragment,
który chcesz) albo zatrzymaj tryb live. Okna samej aplikacji są zawsze wykluczone
z przechwytywania.

## Uwaga o grach online

Program niczego nie wstrzykuje do gry i nie automatyzuje rozgrywki — działa wyłącznie
na obrazie ekranu. Mimo to regulaminy niektórych gier różnie traktują nakładki.
**Sprawdź zasady swojej gry przed użyciem.** Projekt nie jest powiązany z twórcami
żadnej z gier.

## Jak zgłosić problem jakości

Podaj nazwę gry, rozdzielczość, skalowanie Windows, wybrany profil i sposób
wyświetlania. Opisz konkretną sytuację: pojawienie się dialogu, otwarcie opisu,
zamknięcie panelu lub obrót kamery. Rozdziel objawy: spóźnienie, pozostawanie
starego napisu, znikanie aktualnego tekstu i przesunięcie względem oryginału.
Nie dołączaj klucza API ani całego folderu danych aplikacji.
