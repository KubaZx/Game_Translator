# SECURITY.md — model bezpieczeństwa i granice działania

Ten dokument definiuje twarde granice bezpieczeństwa GameTranslatorOverlay. Zasady opisane
poniżej są nadrzędne wobec wszystkich funkcji produktu — żadna funkcjonalność (obecna ani
przyszła) nie może ich naruszyć. Priorytet nr 1 projektu to brak jakiejkolwiek ingerencji w grę.

## Zasada nadrzędna: działanie w pełni pasywne

GameTranslatorOverlay pracuje **wyłącznie na obrazie, który i tak jest widoczny dla użytkownika
na ekranie**. Program:

1. przechwytuje obraz wybranego okna lub regionu ekranu oficjalnymi mechanizmami Windows
   (GDI: `CopyFromScreen`/BitBlt dla regionu, `PrintWindow` z `PW_RENDERFULLCONTENT` dla okna;
   Windows Graphics Capture pozostaje możliwym przyszłym ulepszeniem),
2. rozpoznaje tekst lokalnie systemowym OCR (`Windows.Media.Ocr`),
3. tłumaczy rozpoznany tekst,
4. wyświetla wynik we **własnym, osobnym oknie** nad grą.

Z perspektywy gry program jest nieodróżnialny od użytkownika patrzącego na ekran. Nie komunikuje
się z procesem gry w żaden sposób i nie wpływa na jej działanie.

## Techniki ZABRONIONE

Poniższe techniki są bezwzględnie zakazane w całym kodzie projektu — w rdzeniu, w profilach gier,
w rozszerzeniach i w pull requestach:

- **wstrzykiwanie DLL** do procesu gry (ani żadnego innego procesu),
- **hookowanie** funkcji API, wiadomości okien czy wywołań gry (SetWindowsHookEx wobec gry,
  hooki graficzne, detours itp.),
- **czytanie pamięci procesu gry** (ReadProcessMemory i odpowiedniki),
- **modyfikacja pamięci procesu gry** (WriteProcessMemory i odpowiedniki),
- **modyfikacja plików gry** — binariów, zasobów, konfiguracji, zapisów stanu,
- **przechwytywanie, analiza lub modyfikacja pakietów sieciowych** gry,
- **automatyzacja rozgrywki** — boty, makra, auto-klikanie, farmienie,
- **wysyłanie inputu do gry** — symulowanie klawiszy, myszy ani żadnych zdarzeń wejścia
  skierowanych do okna gry (SendInput/PostMessage/SendMessage do okna gry itp.),
- omijanie systemów anty-cheat lub jakakolwiek interakcja z nimi,
- ukrywanie obecności programu przed grą lub systemem.

Lista jest zamknięta co do intencji, nie co do litery: jeżeli jakaś technika ingeruje w proces,
pliki, ruch sieciowy lub sterowanie grą — jest zabroniona, nawet jeśli nie została tu wymieniona
z nazwy.

## Globalny skrót klawiszowy

Program rejestruje globalny skrót (domyślnie `Ctrl+Shift+T`), który **steruje wyłącznie
tłumaczem** — uruchamia zaznaczenie regionu i tłumaczenie. Skrót:

- nigdy nie wysyła żadnych zdarzeń do okna gry,
- nigdy nie przejmuje ani nie modyfikuje inputu skierowanego do gry,
- służy tylko do obsługi funkcji GameTranslatorOverlay.

## Nakładka: osobne okno systemowe

Nakładka (overlay) to zwykłe okno WPF należące do procesu GameTranslatorOverlay — **nie** jest
częścią okna gry ani nie jest w nie „wstrzyknięta". Style okna:
`WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, Topmost,
per-monitor DPI (manifest PerMonitorV2). W praktyce oznacza to:

- **click-through** — kliknięcia przechodzą przez nakładkę do gry, jakby jej nie było,
- **bez fokusu** — nakładka nigdy nie zabiera grze fokusu ani sterowania,
- nakładka jedynie rysuje tekst nad grą, korzystając ze standardowej kompozycji okien Windows.

Ograniczenie: exclusive fullscreen nie jest obsługiwany (nakładka nie jest wtedy widoczna);
obsługiwane są okna i borderless fullscreen. To ograniczenie jest udokumentowane celowo —
alternatywą byłyby techniki ingerujące w grę, których nie stosujemy.

## Obowiązkowy disclaimer w aplikacji

Aplikacja musi wyświetlać użytkownikowi poniższy disclaimer (przy pierwszym uruchomieniu oraz
dostępny stale w ustawieniach/oknie „O programie"). Dokładna treść:

> **Zastrzeżenie:** GameTranslatorOverlay jest zewnętrzną nakładką tłumaczącą tekst widoczny
> na ekranie. Program w żaden sposób nie modyfikuje gry — nie ingeruje w jej proces, pamięć,
> pliki ani ruch sieciowy i nie automatyzuje rozgrywki. Mimo to nie gwarantujemy zgodności
> z regulaminem każdej gry — zasady poszczególnych gier i ich systemów anty-cheat różnią się
> i mogą się zmieniać. Przed użyciem sprawdź regulamin gry, w której chcesz korzystać
> z nakładki. Używasz programu na własną odpowiedzialność. Projekt nie jest powiązany
> z twórcami ani wydawcami żadnej gry.

## Przechowywanie kluczy API

Klucz API (np. DeepL) jest szyfrowany przez **Windows DPAPI** (`ProtectedData`, zakres
`CurrentUser`) i zapisywany w `%LOCALAPPDATA%\GameTranslatorOverlay`. Odszyfrować go może
wyłącznie ten sam użytkownik Windows na tej samej maszynie.

Czego **NIGDY** nie robimy z kluczami API:

- nie umieszczamy ich w repozytorium (ani w kodzie, ani w plikach konfiguracyjnych w repo),
- nie wpisujemy ich na sztywno w kodzie źródłowym,
- nie zapisujemy ich w logach (Serilog ma zakaz logowania kluczy — dotyczy też poziomu Debug),
- nie dołączamy ich do komunikatów o błędach ani treści wyjątków,
- nie wysyłamy ich w żadnej telemetrii (projekt zresztą żadnej telemetrii nie ma),
- nie przechowujemy ich w postaci jawnej na dysku.

W środowisku deweloperskim klucze podaje się przez zmienne środowiskowe lub User Secrets —
nigdy przez pliki commitowane do repo. CI buduje i testuje wyłącznie z `MockTranslationProvider`,
zero sekretów w pipeline.

## Zasady dla kontrybutorów

Każdy pull request musi respektować ten dokument. Konkretnie:

1. **Żadnych zabronionych technik** — PR zawierający wstrzykiwanie, hooki, dostęp do pamięci
   procesu gry, wysyłanie inputu do gry itd. zostanie odrzucony bez względu na to, jaką
   funkcję realizuje.
2. **Żadnych sekretów w repo** — klucze API, tokeny i dane dostępowe nie mogą trafić do kodu,
   testów, fixture'ów ani historii gita. Testy używają `MockTranslationProvider`.
3. **Nowe funkcje = pasywne funkcje** — jeżeli funkcja wymaga interakcji z procesem gry,
   nie pasuje do tego projektu. Specyfika gry może żyć wyłącznie w opcjonalnych profilach JSON
   (`profiles/`) i słownikach (`glossaries/`) — czyli w danych, nie w kodzie ingerującym w grę.
4. **Zależności pod lupą** — nie dodajemy bibliotek, których działanie opiera się na technikach
   z listy zabronionych (np. biblioteki overlayowe oparte na hookach graficznych).
5. **Wątpliwość = pytanie** — jeśli nie masz pewności, czy technika jest dozwolona, opisz ją
   w issue przed napisaniem kodu.

## Zgłaszanie problemów bezpieczeństwa

Jeżeli znajdziesz w projekcie kod naruszający powyższe zasady albo podatność (np. wyciek klucza
API do logu), zgłoś to jako issue z etykietą `security` — a w przypadku podatności wrażliwej
opisz ją bez publikowania szczegółów umożliwiających nadużycie.
