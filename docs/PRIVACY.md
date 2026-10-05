# PRIVACY.md — polityka prywatności

Ten dokument opisuje, jakie dane GameTranslatorOverlay przetwarza, gdzie one trafiają i jak
użytkownik może to kontrolować. Zasada przewodnia: **przetwarzamy możliwie mało, możliwie
lokalnie, i mówimy wprost, co opuszcza komputer**.

## Co program przechwytuje

- Program przechwytuje obraz **wyłącznie wybranego przez użytkownika okna lub zaznaczonego
  regionu ekranu** — nigdy całego pulpitu „w tle" ani innych okien.
- Przechwytywanie odbywa się oficjalnymi mechanizmami Windows (GDI:
  `PrintWindow`/`CopyFromScreen`; Windows Graphics Capture pozostaje możliwym przyszłym
  ulepszeniem) i dzieje się tylko wtedy, gdy użytkownik tego zażąda
  (skrót/tryb tłumaczenia). Program niczego nie nagrywa.

## OCR działa lokalnie

Rozpoznawanie tekstu wykonuje **systemowy OCR Windows** (`Windows.Media.Ocr`) — w całości
na komputerze użytkownika. Obraz nie jest nigdzie wysyłany w celu rozpoznania tekstu.
Wymagany jest zainstalowany pakiet językowy Windows dla języka źródłowego; rozpoznawanie
tekstu nie korzysta z żadnych modeli AI ani zewnętrznych silników OCR. Model językowy
(LLM) może być użyty wyłącznie do **tłumaczenia**, i tylko gdy użytkownik sam wybierze
takiego dostawcę (patrz niżej).

## Co trafia do API tłumaczeniowego

- Do zewnętrznego API (np. DeepL) wysyłany jest **wyłącznie rozpoznany tekst** — krótkie
  fragmenty, które faktycznie wymagają tłumaczenia (po odfiltrowaniu śmieci i po sprawdzeniu
  słownika oraz cache).
- **Nigdy nie są wysyłane screenshoty** ani żadne inne obrazy.
- Dostawcy oparci na modelach językowych (**Claude** oraz **Model językowy** zgodny z API
  OpenAI) dostają razem z tekstem nazwę gry z aktywnego profilu i te terminy słownika
  (źródło → tłumaczenie), które występują w tłumaczonych zdaniach — również terminy dodane
  przez użytkownika. Azure i Google dostają tylko tekst.
- Gdy w ustawieniu **Postać gracza** wybrano kobietę albo mężczyznę, prompt systemowy modelu
  językowego zawiera jedno zdanie o tym, w jakiej formie gramatycznej zwracać się do gracza.
  To ustawienie nie jest wysyłane do DeepL, Azure ani Google.
- **DeepL** dostaje glosariusz ze słownika (terminy źródło → tłumaczenie, także dodane przez
  użytkownika), gdy tłumaczony tekst zawiera co najmniej jeden termin. Glosariusz jest
  **przechowywany na koncie DeepL** użytkownika pod nazwą `GameTranslatorOverlay …`; po zmianie
  słownika aplikacja usuwa swoje poprzednie wersje. W trybie prywatnym glosariusz nie jest tworzony.
  Terminy dodane w trybie prywatnym **nigdy** nie trafiają do glosariusza DeepL — także po
  wyłączeniu trybu prywatnego w tej samej sesji. Terminy z zakresem „Etykieta” (`scope: label`)
  też nie trafiają do glosariusza.
- Podpowiedzi terminów dla modeli językowych (Claude, model językowy) mogą zawierać także
  terminy prywatne, jeśli występują w tłumaczonym tekście. Idą wyłącznie w tym samym zapytaniu
  co tłumaczony tekst i niczego nie tworzą na koncie dostawcy.
- Dostawcy z kontekstem (DeepL, Claude, model językowy) dostają też do 6 ostatnich linii,
  które **wcześniej wysłano już do tego samego dostawcy** w bieżącej sesji — jako kontekst,
  bez ponownego tłumaczenia. Zmiana dostawcy, profilu lub ustawień tłumaczenia czyści tę
  historię; teksty z cache i słownika do niej nie trafiają.
- **Pamięć dialogu (modele językowe):** Claude i model językowy dostają te linie razem
  z tłumaczeniami, które **ten sam dostawca** już dla nich zwrócił w tej samej sesji
  (ręczna poprawka zastępuje tłumaczenie w pamięci). Pamięć jest tylko w RAM, nigdy na dysku,
  a pary wracają wyłącznie do dostawcy, który je przetłumaczył. DeepL dostaje tylko angielskie
  linie. Logi nie zawierają linii, tłumaczeń ani par.
- **Kontrola jakości:** gdy wynik modelu językowego ma problem (np. zgubioną liczbę), ten sam
  tekst jest raz wysyłany ponownie do **tego samego** dostawcy, w ramach limitu znaków sesji.
  Nie wychodzi nic nowego poza powtórzeniem tekstu. DeepL, Azure i Google nie są ponawiane.
- Gdy zaczynasz zaznaczać region albo uruchamiasz live, aplikacja może nawiązać połączenie
  z serwerem wybranego dostawcy pustym zapytaniem `HEAD` (bez klucza i bez tekstu), żeby
  pierwsze tłumaczenie było szybsze. W trybie Cache-only nie robi tego nigdy.
- **Lokalny serwer LLM** (Ollama, LM Studio pod adresem `localhost`) przetwarza tekst na tym
  komputerze — nic nie wychodzi do internetu. Aplikacja pokazuje przy kluczu, dokąd trafia
  tekst dla wybranego adresu serwera. Adres zdalny musi używać HTTPS.
- **Ważne i mówione wprost w aplikacji:** korzystanie z zewnętrznego API oznacza, że rozpoznany
  tekst **opuszcza komputer** i trafia na serwery wybranego dostawcy tłumaczeń (DeepL,
  Microsoft Azure, Google, Anthropic albo wskazany serwer LLM). Kto nie chce wysyłać niczego
  do sieci, może pracować w trybie **Cache-only** (nic nie wychodzi do sieci; tłumaczone jest
  tylko to, co już jest w cache/słowniku), z lokalnym serwerem LLM albo z `MockTranslationProvider`.
  Zasady przetwarzania tekstu po stronie dostawcy opisuje polityka prywatności tego dostawcy.

## Komunikaty w nakładce

Komunikaty w grze (brak klucza, limit, brak sieci, Cache-only, start/stop live) zawierają
tylko stały opis problemu i nazwę dostawcy — **nigdy tekst z ekranu**. Nie są zapisywane na
dysk, nie są logowane i nie wychodzą z komputera. Do liczenia braków Cache-only służą skróty
(hash) tekstów trzymane w pamięci sesji. Gdy wykluczenie nakładki z przechwytywania nie działa
(starszy Windows albo diagnostyczne `GTO_DIAG_CAPTURABLE=1`), odczyt OCR własnego komunikatu
jest odfiltrowywany i nie trafia do dostawcy. Powiadomienie w zasobniku i podpowiedź ikony mają
stały tekst bez tytułów okien.

## Screenshoty i zrzuty debugowe

- Aplikacja **nigdy nie zapisuje żadnych zrzutów ekranu na dysk** i żadnych nie wysyła.
  Przechwycony obraz żyje tylko w pamięci na czas OCR. W aplikacji nie ma żadnej opcji,
  która by to zmieniała.
- Wyjątkiem jest wyłącznie **deweloperskie narzędzie diagnostyczne** `LiveDiag`
  (`tools/GameTranslatorOverlay.LiveDiag`, nie wchodzi w skład wydawanej paczki):
  przy nieudanym przebiegu OCR zapisuje klatkę PNG do `%TEMP%\gto-livediag-frames`
  w celu diagnozy. Katalog jest czyszczony przy każdym starcie narzędzia.

## Narzędzie deweloperskie ProviderEval

`tools/GameTranslatorOverlay.ProviderEval` (nie wchodzi do paczki aplikacji) porównuje
dostawców na korpusie EN→PL. Wysyła do wybranych dostawców **wyłącznie linie korpusu** —
jak w aplikacji, z terminami słownika i poprzednimi liniami sceny jako kontekstem. Nie robi
zrzutów, nie uruchamia OCR i nie czyta ustawień ani kluczy DPAPI aplikacji: klucze bierze
tylko ze zmiennych środowiskowych i ich nie wypisuje. Cache ma tylko w pamięci, więc baza
tłumaczeń gracza zostaje nietknięta; Mock nic nie wysyła. DeepL z glosariuszem (domyślnie)
tworzy glosariusz na koncie DeepL i zastępuje poprzedni glosariusz aplikacji dla EN→PL
(aplikacja odtworzy swój przy następnym tłumaczeniu); `--no-deepl-glossary` to wyłącza.
Prawdziwe linie z gier trzymaj w `eval/private/`, a wyniki trafiają do `eval/out/` — oba
katalogi są w `.gitignore`.

## Tryb prywatny

Tryb prywatny jest przeznaczony do sytuacji, w których na ekranie może pojawić się wrażliwa
treść (np. czat w grze). Po włączeniu:

- **brak historii** — tłumaczenia nie są zapisywane w historii,
- **brak logowania treści** — logi (Serilog) nie zawierają żadnych tłumaczonych tekstów,
- **cache tylko ulotny** — wpisy cache trzymane są wyłącznie w pamięci, nie w bazie SQLite,
- **czyszczenie po sesji** — po zamknięciu programu ulotne dane sesji są usuwane,
- **bez glosariusza DeepL** — słownik nie jest zapisywany na koncie DeepL, a terminy dodane
  w tym trybie nie trafią do niego także po wyłączeniu trybu prywatnego,
- **bez zapamiętywania ostatniej gry** — nazwa procesu i tytuł okna są trzymane tylko w pamięci
  do zamknięcia programu (wartości zapisane wcześniej zostają w pliku),
- pamięć dialogu i znaczniki wpisów cache (np. płeć postaci) są wyłącznie w RAM.

Uwaga: tryb prywatny nie zmienia faktu, że przy korzystaniu z zewnętrznego API rozpoznany
tekst nadal jest wysyłany do dostawcy tłumaczeń. Aby nic nie opuszczało komputera, połącz
tryb prywatny z trybem Cache-only.

## Dane przechowywane lokalnie

Wszystkie dane programu leżą w `%LOCALAPPDATA%\GameTranslatorOverlay`:

| Dane | Co zawierają | Uwagi |
|---|---|---|
| `settings.json` | ustawienia programu, także nazwa procesu i tytuł okna ostatniej gry tłumaczonej w live (`lastGameProcess`, `lastGameTitle`) | bez klucza API w postaci jawnej; tytuł okna może zawierać np. nazwę zapisu lub postaci; w trybie prywatnym ostatnia gra nie jest zapisywana |
| cache SQLite | pary tekst źródłowy → tłumaczenie | pomijany w trybie prywatnym (cache tylko w pamięci) |
| klucze API (osobny dla każdego dostawcy) | zaszyfrowane Windows DPAPI (`CurrentUser`) | odczyta je tylko ten sam użytkownik Windows na tej maszynie |
| logi (Serilog, rolling) | zdarzenia techniczne, błędy (stack trace tylko do logu) | nigdy kluczy API; bez treści tłumaczeń w trybie prywatnym |
| profile i słowniki | pliki JSON (`profiles/`, `glossaries/`) | dane statyczne, bez treści użytkownika |

Usunięcie folderu `%LOCALAPPDATA%\GameTranslatorOverlay` usuwa wszystkie dane programu.

## Czego program nie robi

- Nie zbiera telemetrii ani statystyk użycia i niczego nie wysyła „do producenta"
  (jedyny ruch sieciowy to zapytania do wybranego przez użytkownika API tłumaczeniowego;
  to samo dotyczy narzędzia korpusu z ADR-014, które tłumaczy korpus tylko na wyraźne polecenie).
- Nie zapisuje ani nie wysyła screenshotów.
- Aplikacja (nakładka) nie czyta danych innych aplikacji, plików gry ani pamięci procesów.
  Wyjątek opisuje ADR-014: osobne narzędzie offline, uruchamiane przez użytkownika przy
  wyłączonej grze, czyta wyłącznie do odczytu teksty z plików wybranej gry offline (bez
  anti-cheata, bez szyfrowania) i zapisuje lokalny korpus w `%LOCALAPPDATA%\GameTranslatorOverlay`.
  Korpus i jego tłumaczenia nigdy nie opuszczają komputera, chyba że użytkownik sam zleci
  ich tłumaczenie u wybranego dostawcy.
- Nie tworzy kont, nie wymaga logowania, nie profiluje użytkownika.
