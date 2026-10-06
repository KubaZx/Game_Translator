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

- Do zewnętrznego API (np. DeepL) wysyłany jest **wyłącznie tekst** — krótkie
  fragmenty, które faktycznie wymagają tłumaczenia (po odfiltrowaniu śmieci i po sprawdzeniu
  słownika oraz cache). Zwykle to tekst rozpoznany na ekranie. Gdy aktywny profil gry ma
  lokalny korpus (ADR-014), zamiast odczytu może pójść dopasowany do niego **tekst z korpusu**
  — pełne zdanie gry, także wtedy, gdy na ekranie widać dopiero jego część (np. napis
  wypisywany litera po literze albo odczyt ucięty przez OCR). W trybie „Na oryginale (zakrywa)”
  (bez „Napisów na dole”) kwestia wypisywana litera po literze czeka na koniec pisania (0,9 s
  bez nowych liter, najwyżej 8 s), więc zwykle idzie do dostawcy dopiero wtedy, gdy jest cała na
  ekranie; w pozostałych trybach pełne zdanie może pójść już po odczycie samego początku kwestii
  (od 12 liter i 3 słów). W trybie live krótki odczyt rozpoznany przy aktywnym korpusie jako
  śmieci OCR (np. litery z ikon) nie jest wysyłany wcale.
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
  Microsoft Azure, Google, Anthropic albo wskazany serwer LLM — np. `api.deepseek.com` po
  wybraniu gotowego serwera DeepSeek). ADR-014 odnotowuje przy wyborze dostawcy, że DeepL API
  Free przetwarza teksty przez ograniczony czas do trenowania modeli (DeepL Pro nie), a DeepSeek
  przechowuje dane w ChRL. Kto nie chce wysyłać niczego do sieci, może pracować w trybie
  **Cache-only** (nic nie wychodzi do sieci; tłumaczone jest tylko to, co już jest
  w cache/słowniku), z lokalnym serwerem LLM albo z `MockTranslationProvider`. Zasady
  przetwarzania tekstu po stronie dostawcy opisuje polityka prywatności tego dostawcy.
- **Opcje serwera LLM** (`llmServerOptions`: `thinking`, `reasoning_effort`, `max_tokens`,
  `response_format`) to krótkie stałe słowa i liczby, bez tekstu z ekranu; trafiają tylko pod
  adres serwera, dla którego je zapisano. Zużycie tokenów z odpowiedzi serwera trafia do logu
  jako same liczby.

## Komunikaty w nakładce

Komunikaty w grze (brak klucza, limit, brak sieci, Cache-only, start/stop live, pełny ekran
utrudniający nakładkę) zawierają tylko stały opis problemu i nazwę dostawcy — **nigdy tekst
z ekranu**. Nie są zapisywane na dysk, nie są logowane (pełny ekran log odnotowuje osobnym
stałym zdaniem, bez tytułu okna) i nie wychodzą z komputera. Do liczenia braków Cache-only
służą skróty (hash) tekstów trzymane w pamięci sesji. Gdy wykluczenie nakładki
z przechwytywania nie działa (starszy Windows albo diagnostyczne `GTO_DIAG_CAPTURABLE=1`),
odczyt OCR własnego komunikatu jest odfiltrowywany i nie trafia do dostawcy. Powiadomienie
w zasobniku i podpowiedź ikony mają stały tekst bez tytułów okien.

## Screenshoty i zrzuty debugowe

- Aplikacja **nigdy nie zapisuje żadnych zrzutów ekranu na dysk** i żadnych nie wysyła.
  W aplikacji nie ma żadnej opcji, która by to zmieniała.
- Przechwycony obraz żyje tylko w pamięci. Poza OCR służy do porównań w pikselach (czy napis
  się zmienił albo zniknął; sonda zniknięcia pamięta z ostatniego odczytu tylko kolory i liczbę
  pikseli liter — `KnownTextReference`) oraz do wyglądu nakładki: kolorów i tekstury tła pod
  napisem, a w trybie „Na oryginale (zakrywa)” — łatki, czyli wycinka obrazu spod napisu
  z literami oryginału wypełnionymi kolorami tła (`GlyphCover`). Łatka jest liczona z klatki
  w pamięci, trzymana w pamięci, dopóki napis jest wyświetlany (i do 10 s po jego zniknięciu,
  gdyby wrócił), i rysowana wyłącznie w oknie nakładki — nie jest zapisywana ani wysyłana.
- Wyjątkiem są **narzędzia deweloperskie** z `tools/` (nie wchodzą w skład wydawanej paczki):
  - `LiveDiag` — tylko z jawną opcją `--dump-frames`: gdy pełny przebieg OCR nie widzi niczego,
    choć nakładka pokazuje co najmniej 3 bloki, zapisuje klatkę PNG do katalogu przebiegu
    `%TEMP%\gto-livediag-<identyfikator>\frames` (ścieżkę wypisuje w konsoli). Narzędzie
    niczego nie usuwa — zrzuty zostają do ręcznego usunięcia. Bez tej opcji nie zapisuje obrazów.
    Plik metryk (JSONL) nie zawiera tekstów; `--include-text` wypisuje je tylko w konsoli.
  - `OverlayPreview` — renderuje nakładkę na **klatkach podanych przez dewelopera** (pliki
    PNG/JPG, nie przechwytuje ekranu) i zapisuje galerię PNG z obrazem gry, tekstami
    i tłumaczeniami do katalogu `--out`. Bazę tłumaczeń i korpus czyta tylko przez kopię roboczą
    w `--out`, którą usuwa po przebiegu (gdy plik jest zablokowany, kopia zostaje w `--out`);
    brak tłumaczenia w bazie = atrapa Mock. Nie czyta ustawień ani kluczy, a HTTP jest w nim
    zablokowane.
  - `CorpusEval` — nie łączy się z siecią i nie używa kluczy (tłumaczy tylko Mock), bazę
    tłumaczeń z `--cache` (zalecana kopia) tylko czyta albo kopiuje do katalogu roboczego.
    Polecenia oceny zapisują do `--out` same liczby, a odczyty z tekstami gry do wskazanego
    katalogu prywatnego (`--private`, `--work`). `render` zapisuje próbki OCR z tekstami gry do
    pliku `--out` (trzymaj go w katalogu prywatnym), a z opcją `--examples` — do 24
    przykładowych PNG z tekstem korpusu narysowanym na podanych klatkach.
  - `SceneReplay` — opcje `--assets` / `--texture` czytają lokalnie wycinek etykiety i teksturę,
    bez kopiowania i bez zapisu pikseli; HTTP jest zablokowane.

  Galerie, przykłady i katalogi z tekstami gry trzymaj poza repozytorium albo w `eval/private/`
  (w `.gitignore`).

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

## Narzędzie korpusu CorpusTool (ADR-014)

`tools/GameTranslatorOverlay.CorpusTool` (nie wchodzi do paczki aplikacji) uruchamia wyłącznie
użytkownik, poleceniem w konsoli.

- **`extract`** czyta teksty z plików wybranej gry offline wyłącznie do odczytu, bez sieci,
  i zapisuje korpus (angielskie teksty gry, klucze, kontekst, mówcy) w
  `%LOCALAPPDATA%\GameTranslatorOverlay\corpus\<profil>.corpus.jsonl` albo pod `--out`. Odmawia
  zapisu w folderze gry, pod `steamapps\common` (i w bibliotekach Epic/GOG) oraz w repozytorium
  (poza `eval/private/`).
- **`translate`** to jedyny ruch sieciowy narzędzia: wysyła teksty korpusu do dostawcy
  wskazanego opcją `--provider deepl|llm` (Mock niczego nie wysyła). Model językowy dostaje też
  nazwę gry, pasujące terminy słownika, opis sceny, notatkę do każdego tekstu (mówca, plik
  napisu, klucz, kolumna kontekstu z korpusu), poprzednie linie dialogu z tłumaczeniami i — przy
  znanej płci postaci gracza — regułę formy zwracania się do gracza; DeepL — opis sceny
  i poprzednie linie w parametrze `context` oraz glosariusz ze słownika.
  Klucze bierze tylko ze zmiennych środowiskowych, nigdy z DPAPI aplikacji; `GTO_LLM_KEY` idzie
  wyłącznie pod adres z `GTO_LLM_ENDPOINT`. DeepL domyślnie tworzy glosariusz na koncie DeepL
  i zastępuje poprzedni glosariusz aplikacji dla tej pary języków (jak ProviderEval;
  `--no-deepl-glossary` to wyłącza). Wyniki zapisuje do lokalnej bazy tłumaczeń (`cache.db`)
  z profilem gry i znacznikiem `src=corpus`. Włączony tryb prywatny w `settings.json` albo
  nieczytelny plik ustawień = odmowa, zanim cokolwiek zostanie wysłane. `--dry-run` niczego nie
  wysyła ani nie zapisuje do bazy (przy włączonym trybie prywatnym tylko ostrzega).
- Na konsolę i do pliku `--stats` trafiają liczby i dane techniczne (profil, dostawca, model,
  adres serwera), bez tekstów gry i tłumaczeń.

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
- pamięć dialogu i znaczniki wpisów cache (np. płeć postaci) są wyłącznie w RAM,
- **bez bazy z dysku** — tłumaczenie nie czyta też wcześniejszych wpisów z bazy SQLite, więc
  tłumaczenia korpusu z wyprzedzeniem (`CorpusTool translate`) w tym trybie nie działają;
  lokalny plik korpusu aktywnego profilu jest nadal czytany, a dopasowanie odczytów do niego
  działa w pamięci,
- `CorpusTool translate` odmawia pracy, gdy tryb prywatny jest włączony w `settings.json`
  (przebieg próbny `--dry-run` tylko ostrzega i niczego nie wysyła).

Uwaga: tryb prywatny nie zmienia faktu, że przy korzystaniu z zewnętrznego API rozpoznany
tekst nadal jest wysyłany do dostawcy tłumaczeń. Aby nic nie opuszczało komputera, połącz
tryb prywatny z trybem Cache-only.

## Dane przechowywane lokalnie

Wszystkie dane programu leżą w `%LOCALAPPDATA%\GameTranslatorOverlay`:

| Dane | Co zawierają | Uwagi |
|---|---|---|
| `settings.json` | ustawienia programu, także nazwa procesu i tytuł okna ostatniej gry tłumaczonej w live (`lastGameProcess`, `lastGameTitle`) | bez klucza API w postaci jawnej; tytuł okna może zawierać np. nazwę zapisu lub postaci; w trybie prywatnym ostatnia gra nie jest zapisywana |
| cache SQLite (`cache.db`) | pary tekst źródłowy → tłumaczenie, także tłumaczenia korpusu z wyprzedzeniem (`CorpusTool translate`: profil gry, znacznik `src=corpus`) | w trybie prywatnym tłumaczenie go nie czyta i nie zapisuje (cache tylko w pamięci) |
| klucze API (osobny dla każdego dostawcy) | zaszyfrowane Windows DPAPI (`CurrentUser`) | odczyta je tylko ten sam użytkownik Windows na tej maszynie |
| logi (Serilog, rolling) | zdarzenia techniczne, błędy (stack trace tylko do logu); liczba tekstów wczytanego korpusu i zużycie tokenów modeli językowych jako same liczby | nigdy kluczy API; bez treści tłumaczeń w trybie prywatnym |
| profile i słowniki | pliki JSON (`profiles/`, `glossaries/`) | dane statyczne, bez treści użytkownika |
| korpus gry (`corpus\<profil>.corpus.jsonl`) | angielskie teksty gry odczytane przez narzędzie z ADR-014 | tworzy go tylko narzędzie na polecenie użytkownika; aplikacja go czyta (aktywny profil, także w trybie prywatnym) i nie wysyła pliku, ale do dostawcy może trafić pojedynczy tekst korpusu dopasowany do odczytu z ekranu; `CorpusTool translate` wysyła teksty korpusu do dostawcy wybranego przez użytkownika, tylko na jego polecenie |

Usunięcie folderu `%LOCALAPPDATA%\GameTranslatorOverlay` usuwa wszystkie dane programu.

## Czego program nie robi

- Nie zbiera telemetrii ani statystyk użycia i niczego nie wysyła „do producenta"
  (jedyny ruch sieciowy to zapytania do wybranego przez użytkownika API tłumaczeniowego;
  to samo dotyczy narzędzia korpusu z ADR-014, które tłumaczy korpus tylko na wyraźne polecenie).
  Krój nakładki Lexend Deca jest dołączony do aplikacji — nic nie jest pobierane z sieci.
- Nie zapisuje ani nie wysyła screenshotów.
- Aplikacja (nakładka) nie czyta danych innych aplikacji, plików gry ani pamięci procesów.
  Wyjątek opisuje ADR-014: osobne narzędzie offline, uruchamiane przez użytkownika przy
  wyłączonej grze, czyta wyłącznie do odczytu teksty z plików wybranej gry offline (bez
  anti-cheata, bez szyfrowania) i zapisuje lokalny korpus w `%LOCALAPPDATA%\GameTranslatorOverlay`.
  Plik korpusu i baza tłumaczeń nie są nigdzie wysyłane. Teksty korpusu trafiają do dostawcy
  w dwóch sytuacjach: gdy użytkownik sam zleci tłumaczenie korpusu (`CorpusTool translate`,
  wybrany dostawca) oraz gdy nakładka dopasuje odczyt OCR do tekstu korpusu aktywnego profilu
  i tego tekstu nie ma jeszcze w cache — wtedy zamiast odczytu z błędami OCR idzie tekst
  z korpusu. Nie zawsze jest to dokładnie to, co widać na ekranie: przy odczycie uciętym albo
  przy napisie, który dopiero się wyświetla, wysyłane jest pełne zdanie z korpusu (także jego
  część, której gracz jeszcze nie widział). Nakładka czyta tylko lokalny plik korpusu
  aktywnego profilu, nigdy pliki gry.
- Nie tworzy kont, nie wymaga logowania, nie profiluje użytkownika.
