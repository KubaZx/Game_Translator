# SECURITY.md — model bezpieczeństwa i granice działania

Ten dokument definiuje twarde granice bezpieczeństwa GameTranslatorOverlay. Zasady opisane
poniżej są nadrzędne wobec wszystkich funkcji produktu — żadna funkcjonalność (obecna ani
przyszła) nie może ich naruszyć. Priorytet nr 1 projektu to brak jakiejkolwiek ingerencji w grę.

## Zasada nadrzędna: działanie w pełni pasywne

GameTranslatorOverlay pracuje **wyłącznie na obrazie, który i tak jest widoczny dla użytkownika
na ekranie**. Program:

1. przechwytuje obraz wybranego okna lub regionu ekranu oficjalnymi mechanizmami Windows
   (tryb live: `PrintWindow` z `PW_RENDERFULLCONTENT`, w ruchu kamery Windows Graphics Capture
   okna gry; GDI `CopyFromScreen`/BitBlt dla regionu),
2. rozpoznaje tekst lokalnie systemowym OCR (`Windows.Media.Ocr`),
3. tłumaczy rozpoznany tekst (przy lokalnym korpusie gry z ADR-014 — dopasowany do niego tekst
   z korpusu),
4. wyświetla wynik we **własnym, osobnym oknie** nad grą.

Z perspektywy gry program jest nieodróżnialny od użytkownika patrzącego na ekran. Nie komunikuje
się z procesem gry w żaden sposób i nie wpływa na jej działanie.

**Wyjątek (ADR-014):** osobne narzędzie offline z `tools/` może — na polecenie użytkownika
i przy wyłączonej grze — czytać pliki gry **wyłącznie do odczytu**, żeby zbudować lokalny korpus
tekstów źródłowych. Nie dotyczy gier online ani gier z anti-cheatem, zaszyfrowanych lub
podpisanych kontenerów, nie obejmuje żadnego zapisu w folderze gry ani obchodzenia zabezpieczeń.
Sama nakładka plików gry nie czyta — z danych wytworzonych przez narzędzie czyta tylko plik
korpusu (`<folder danych>\corpus\<id aktywnego profilu>.corpus.jsonl`) i wpisy, które
`CorpusTool translate` zapisał w lokalnej bazie tłumaczeń.

Warunki ADR-014 są w kodzie `tools/GameTranslatorOverlay.CorpusTool` jako twarde straże z testami,
bez opcji obejścia: proces gry jest sprawdzany przed odczytem jakiegokolwiek pliku w folderze
gry, także nagłówków `.pak`/`.utoc` (`RunningGameGuard`); znaczniki anti-cheata (EasyAntiCheat,
BattlEye), pliki `.sig` i zaszyfrowane kontenery są szukane w całym katalogu głównym gry, także
gdy wskazano podfolder
(`GameFolderGuard`); profile oznaczone jako online i lista wykluczeń (Path of Exile 1/2) dają
odmowę; pliki gry są otwierane tylko do odczytu, bez blokowania (`FileShare.ReadWrite |
FileShare.Delete`); wynik nie może leżeć w folderze gry, pod `steamapps\common` (ani
w bibliotekach Epic/GOG) ani w repozytorium poza `eval/private/` (`OutputLocationGuard`).
Narzędzie niczego nie pobiera; jedyny ruch sieciowy to jawne `translate --provider deepl|llm`.

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
- nakładka jedynie rysuje nad grą tekst, korzystając ze standardowej kompozycji okien Windows.
  W trybie „Na oryginale (zakrywa)” rysuje też łatkę zakrywającą oryginalny napis — wycinek
  przechwyconej klatki z literami wypełnionymi kolorami tła, policzony w pamięci procesu
  GameTranslatorOverlay. Obraz gry nie jest zmieniany: łatka i tłumaczenie leżą we własnym
  oknie nakładki. Dołączony krój Lexend Deca jest zasobem aplikacji (pozostałe to kroje
  zainstalowane w Windows) — nakładka niczego nie pobiera i nie korzysta z plików gry.

Ograniczenie: exclusive fullscreen nie jest obsługiwany (nakładka nie jest wtedy widoczna);
obsługiwane są okna i borderless fullscreen. To ograniczenie jest udokumentowane celowo —
alternatywą byłyby techniki ingerujące w grę, których nie stosujemy. Gdy okno gry zajmuje cały
monitor i nie daje się przechwycić jako okno (`PrintWindow`), nakładka pokazuje komunikat
„⚠ Pełny ekran utrudnia nakładkę — przełącz na okno bez ramki”.

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

Klucze API (DeepL, Azure, Google, Anthropic, serwer LLM — każdy w osobnym pliku) są
szyfrowane przez **Windows DPAPI** (`ProtectedData`, zakres `CurrentUser`) i zapisywane
w `%LOCALAPPDATA%\GameTranslatorOverlay`. Odszyfrować je może wyłącznie ten sam użytkownik
Windows na tej samej maszynie.

Klucze trafiają wyłącznie do nagłówków zapytań (`DeepL-Auth-Key`, `Ocp-Apim-Subscription-Key`,
`X-goog-api-key`, `x-api-key`, `Authorization: Bearer`) — nigdy do adresu URL. Adres serwera
zgodnego z OpenAI musi używać **HTTPS**; zwykłe HTTP jest dozwolone tylko dla serwera na tym
komputerze (loopback), a adres z loginem lub parametrami jest odrzucany. Dzięki temu klucz
i tekst z ekranu nie przechodzą przez sieć otwartym tekstem. Klucz serwera LLM jest przypisany
do adresu, dla którego go zapisano, i nie jest wysyłany do innego serwera. Tak samo opcje
serwera LLM (`llmServerOptions`: `thinking`, `reasoning_effort`, `max_tokens`,
`response_format`) trafiają do zapytania tylko pod host, dla którego je zapisano; wartości spoza
liter i cyfr ASCII, `_`, `-` i `.` (albo dłuższe niż 32 znaki) są pomijane. Klient Claude ma jawnie
ustawiony adres API, więc zmienne środowiskowe SDK nie przekierują klucza.

Czego **NIGDY** nie robimy z kluczami API:

- nie umieszczamy ich w repozytorium (ani w kodzie, ani w plikach konfiguracyjnych w repo),
- nie wpisujemy ich na sztywno w kodzie źródłowym,
- nie zapisujemy ich w logach (Serilog ma zakaz logowania kluczy — dotyczy też poziomu Debug),
- nie dołączamy ich do komunikatów o błędach ani treści wyjątków,
- nie wysyłamy ich w żadnej telemetrii (projekt zresztą żadnej telemetrii nie ma),
- nie przechowujemy ich w postaci jawnej na dysku.

W środowisku deweloperskim klucze podaje się przez zmienne środowiskowe lub User Secrets —
nigdy przez pliki commitowane do repo. Narzędzia `ProviderEval` i `CorpusTool translate` biorą
klucze wyłącznie ze zmiennych środowiskowych (`GTO_DEEPL_KEY`, `GTO_LLM_ENDPOINT` +
`GTO_LLM_MODEL` + opcjonalnie `GTO_LLM_KEY` i inne), nigdy z DPAPI aplikacji; `GTO_LLM_KEY` jest
wysyłany tylko pod adres z `GTO_LLM_ENDPOINT`. Narzędzia bez prawdziwych dostawców
(`OverlayPreview`, `SceneReplay`, `LiveDiag`) mają HTTP zablokowane i nie używają kluczy;
`CorpusEval` w ogóle nie tworzy klienta HTTP.
CI buduje i testuje wyłącznie z `MockTranslationProvider`, zero sekretów w pipeline.

## Zasady dla kontrybutorów

Każdy pull request musi respektować ten dokument. Konkretnie:

1. **Żadnych zabronionych technik** — PR zawierający wstrzykiwanie, hooki, dostęp do pamięci
   procesu gry, wysyłanie inputu do gry itd. zostanie odrzucony bez względu na to, jaką
   funkcję realizuje.
2. **Żadnych sekretów w repo** — klucze API, tokeny i dane dostępowe nie mogą trafić do kodu,
   testów, fixture'ów ani historii gita. Testy używają `MockTranslationProvider`. To samo
   dotyczy tekstów i obrazów z gier (korpusy, kopie baz tłumaczeń, klatki, galerie
   OverlayPreview) — zostają lokalnie, poza repozytorium albo w `eval/private/`; testy korpusu
   i łatki używają wyłącznie danych syntetycznych.
3. **Nowe funkcje = pasywne funkcje** — jeżeli funkcja wymaga interakcji z procesem gry,
   nie pasuje do tego projektu. Specyfika gry może żyć wyłącznie w opcjonalnych profilach JSON
   (`profiles/`, w tym recepta korpusu ADR-014 w sekcji `corpus` i krój nakładki
   `overlay.fontFamily`) i słownikach (`glossaries/`) — czyli w danych, nie w kodzie
   ingerującym w grę.
4. **Zależności pod lupą** — nie dodajemy bibliotek, których działanie opiera się na technikach
   z listy zabronionych (np. biblioteki overlayowe oparte na hookach graficznych).
5. **Wątpliwość = pytanie** — jeśli nie masz pewności, czy technika jest dozwolona, opisz ją
   w issue przed napisaniem kodu.

## Zgłaszanie problemów bezpieczeństwa

Jeżeli znajdziesz w projekcie kod naruszający powyższe zasady albo podatność (np. wyciek klucza
API do logu), zgłoś to jako issue z etykietą `security` — a w przypadku podatności wrażliwej
opisz ją bez publikowania szczegółów umożliwiających nadużycie.
