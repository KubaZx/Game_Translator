# Changelog

Wersjonowanie: SemVer. Daty w formacie RRRR-MM-DD.

## [Niewydane]

Brak zmian po wydaniu 0.5.0.

## [0.5.0] — 2026-10-06

Duże wydanie: stary napis znika razem z oryginałem, tryb „Na oryginale (zakrywa)” wygląda jak
napis gry, a dla Escape Academy jest spolszczenie z wyprzedzeniem — osobne narzędzie offline
`CorpusTool` czyta teksty z plików wyłączonej gry, a aplikacja przyciąga do nich odczyty OCR
(sama aplikacja plików gry nadal nie czyta, ADR-014). Przeszło 1697 testów xUnit na Windows
(1251 Core + 280 Infrastructure + 166 CorpusTool), build bez ostrzeżeń; zachowanie sesji live
sprawdzane w SceneReplay, wygląd na klatkach 4K z Escape Academy przez OverlayPreview. W grze na
żywo była tylko krótka sesja Escape Academy rano 2026-10-06 na wersji z korpusem — przed
dopasowaniem etykiet, odrzucaniem śmieci OCR, nowym wyglądem i wstrzymywaniem kwestii pisanej
literami; tych zmian nikt jeszcze nie oglądał w grze — czekają na scenariusze M36–M40
([MANUAL_TESTING.md](docs/MANUAL_TESTING.md)).

**Najważniejsze dla gracza:**

- **Tłumaczenie nie wisi po zniknięciu napisu** — znika razem z oryginałem także na
  teksturowanym tle (w testach SceneReplay po 0,3–0,55 s, gdy w miejscu napisu nie zostaje
  nawet ćwierć jego dawnego kontrastu; inaczej po kilku odczytach OCR), a gdy napis wróci
  w ciągu 10 s, wraca też tłumaczenie, również na pasku napisów na dole.
- **„Na oryginale (zakrywa)” wygląda jak gra:** znikają same litery oryginału (tło gry zostaje),
  a polski tekst ma kolor, kontur, cień i wielkość liter napisu gry oraz krój z profilu gry
  (Escape Academy: dołączony Lexend Deca). Ikony klawiszy zostają na miejscu.
- **Dialog pisany litera po literze** (kwestia znana z korpusu gry) w trybie zakrywania zostaje
  po angielsku, dopóki gra pisze, a potem od razu pokazuje całe polskie tłumaczenie kwestii
  (w teście ok. 0,1–0,3 s po ostatniej literze) — bez mieszania języków.
- **Spolszczenie z wyprzedzeniem dla Escape Academy:** `CorpusTool` raz wyciąga teksty z plików
  gry i może je przetłumaczyć do lokalnej bazy (DeepL, DeepSeek albo inny serwer zgodny
  z OpenAI). Odczyty OCR z pomyłkami, ucięte i krótkie etykiety trafiają wtedy w gotowe
  tłumaczenia, a śmieci odczytane z ikon nie idą do dostawcy. W powtórce 242 bloków z dawnych
  sesji gry przez pipeline (Mock, nie gra na żywo): z korpusem przetłumaczonym z wyprzedzeniem
  57 zapytań do dostawcy, bez korpusu 241.
- **DeepSeek jako gotowy serwer modelu językowego** z wyłączonym myśleniem (bez tego paczka
  5 linii trwała 7–20 s).

### Wygląd napisów „Na oryginale (zakrywa)”

- **Tłumaczenie wygląda jak napis gry.** Zamiast rozmytego prostokąta z obrazu znikają same
  litery oryginału — ich piksele są wypełniane kolorami tła z otoczenia, a reszta obrazu gry
  zostaje nietknięta. Polski tekst dostaje kolor, kontur, cień, wysokość liter i linię bazową
  zmierzone z napisu gry (na klatkach Escape Academy wysokość 0,96–1,10× oryginału, linia bazowa
  w 0–1 px; wcześniej ok. 0,55×, biały kontur pod białym dialogiem, ciemne plamy i prześwitujący
  cień oryginału). Gdy tło pod napisem się rusza, łatka jest miękka, dopóki obraz nie stanie.
- **Krój jak w grze.** Nowa pozycja kroju **Jak w grze (krój z profilu)** (domyślna; dotychczasowy
  domyślny Segoe UI przechodzi na nią raz). Profil Escape Academy używa dołączonego kroju
  Lexend Deca (licencja SIL OFL 1.1), z grubością dobraną do liter w grze; bez profilu Segoe UI.
  Profil gry może wskazać krój polem `overlay.fontFamily`.
- **Ikony zostają.** Ikona klawisza przed napisem („X Hint” → ikona X + „Podpowiedź”) i ikonka
  za napisem (np. ✓ obok języka) nie są zamazywane, a odczytana z nich litera nie trafia do
  tłumaczenia na ekranie.
- **Dłuższy polski tekst** w dialogu najpierw lekko się zmniejsza (najwyżej do 85%), potem łamie
  się w szerokości oryginału z jego odstępem wierszy; pojedyncza etykieta może wejść w wolne
  miejsce obok i nie wychodzi poza monitor.
- **Mniej migania:** tłumaczenie identyczne z oryginałem (nazwy, „OK”, logo) nie jest rysowane
  w trybie zakrywania — widać grę; taki napis nie był też już rozpoznawany jako „własne
  tłumaczenie” nakładki, przez co przy zmiennym tle znikał i wracał co kilka odczytów.
- **Kwestia pisana literami nie miesza języków.** W trybie zakrywania dialog z korpusu aktywnego
  profilu, który gra wpisuje litera po literze, zostaje po angielsku, dopóki tekst rośnie, a gdy
  gra skończy pisać, od razu pojawia się całe polskie tłumaczenie kwestii (w teście ok. 0,1–0,3 s
  po ostatniej literze; wcześniej polski tekst stał na dopisywanych angielskich literach,
  a krótki początek kwestii migał niepełnym tłumaczeniem z dostawcy).
- **Jedna grubość liter w menu:** napisy w tym samym stylu (rozmiar, kolor, obrys) dostają tę
  samą grubość kroju (wcześniej np. „Graj” cieńsze od „Ustawienia”).
- **Wyśrodkowany napis zostaje na środku:** jednowierszowy napis wyśrodkowany na ekranie
  (np. baner w menu Escape Academy) rośnie w obie strony zamiast w prawo.
- **Komunikat o pełnym ekranie:** gdy okno gry zajmuje cały monitor i nie daje się przechwycić
  jako okno (nie wspiera PrintWindow, typowe dla wyłącznego pełnego ekranu), nakładka pokazuje
  „⚠ Pełny ekran utrudnia nakładkę — przełącz na okno bez ramki” (instrukcja: zalecany tryb okna
  bez ramki).

### Tryb live — poprawki błędów

- **Tłumaczenie znika razem z napisem w grze** (zgłoszenie: „Zbadaj” zostawało na ekranie po
  zniknięciu „Inspect” w Escape Academy). Gdy najbliższy odczyt OCR nie widzi napisu, a w jego
  miejscu nie zostaje nawet ćwierć kontrastu, jaki napis miał przy ostatnim odczycie, nakładka
  zdejmuje tłumaczenie — w testach po 0,3–0,55 s (zależnie od tego, kiedy wypada ten odczyt),
  także na teksturowanym tle (wcześniej 0,9 s, ok. 6 s albo wcale). Napis, który przygasa albo
  zmienia barwę na ciemniejszą, ale OCR nadal go czyta, zachowuje tłumaczenie i nie miga.
  Wyjątek: gdy napis przygaśnie poniżej ćwierci dawnej jasności, a pierwszy odczyt po tym go
  nie zobaczy, tłumaczenie raz zniknie na ok. 0,6 s.
- Gdy tło nie pozwala tego stwierdzić (w miejscu napisu zostaje coś jasnego), tłumaczenie znika
  po kilku odczytach OCR zamiast wisieć: śmieciowy odczyt w miejscu napisu już go nie
  podtrzymuje ani nie przywraca, a pełny odczyt ekranu przychodzi co 4 s także wtedy, gdy
  w innym miejscu ekranu coś ciągle się rusza (wcześniej w grze potrafił nie przyjść przez
  blisko minutę).
- **Napisy na dole:** tłumaczenie znika z paska razem z napisem w grze, a gdy napis wróci
  w ciągu 10 s, wraca też na pasek. Gdy pasek nadal pokazuje inne linie, powrót nie przedłuża
  jego czasu.
- Nowy napis w miejscu poprzedniego dostaje własne tłumaczenie także wtedy, gdy OCR czyta go
  z wielokropkiem, cyfrą przy literze albo znakiem w rodzaju # czy € — wcześniej w jego
  miejscu zostawało albo wracało tłumaczenie poprzedniego napisu. Nad napisem, który nadal
  jest na ekranie, taki odczyt zastępuje tłumaczenie dopiero po dwóch takich samych odczytach
  z rzędu; wyraźnie śmieciowy odczyt nie staje się tłumaczeniem. Gdy zniknięcia poprzedniego
  napisu nie widać w pikselach (w jego miejscu zostało coś jasnego), taki nowy napis czeka do
  10 s — poprzedni mógł zostać, a odczyt bywa jego przekłamaniem.
- Śmieciowy odczyt OCR nad napisem, który nadal stoi na ekranie, nie zdejmuje go, gdy piksele
  pokazują, że napis jest w polu. Poprawiony odczyt z jedną inną literą podmienia tłumaczenie
  w jednej aktualizacji — bez ok. 1 s przerwy na czas tłumaczenia.

### Korpus tekstów gry (ADR-014)

- **Nowe narzędzie offline `CorpusTool`** (`tools/GameTranslatorOverlay.CorpusTool`, poza
  paczką aplikacji): `extract --profile <id> --game-dir <folder>` czyta teksty dla gracza
  z plików zainstalowanej gry i zapisuje lokalny korpus JSONL (klucz, tekst EN, kontekst,
  rodzaj ui/dialog/subtitle, mówca, węzeł dialogu, kolejność, czas napisu) w
  `%LOCALAPPDATA%\GameTranslatorOverlay\corpus` albo pod ścieżką z `--out` / `--data-dir`.
  Wyłącznie do odczytu i bez sieci; odmawia pracy, gdy gra działa, ma anti-cheat
  (EasyAntiCheat, BattlEye), podpisane lub zaszyfrowane kontenery, jest oznaczona jako online
  albo jest na liście wykluczeń (Path of Exile 1/2), i nie zapisuje niczego w folderze gry ani
  w repozytorium. Obsługiwana rodzina formatów: Unity TextAsset (kontener UnityFS z blokami LZ4
  albo luźny plik serializowany). Nakładka nadal nie czyta plików gry. Opis:
  [README narzędzia](tools/GameTranslatorOverlay.CorpusTool/README.md).
- **Profil Escape Academy** (`profiles/escape-academy`): rozpoznaje grę po nazwie procesu
  (`Escape Academy.exe`; bez zmiany ustawień OCR) i zawiera receptę korpusu dla narzędzia.
  Aplikacja włącza go automatycznie, gdy żaden profil nie jest wybrany — nowe wpisy cache
  z tej gry dostają profil `escape-academy` (dotychczasowe wpisy bez profilu nadal są
  czytane), a modele językowe dostają nazwę gry w kontekście.
- Profile gier mogą mieć opcjonalną sekcję `corpus` (recepta: kontener, plik, źródła z wzorcami
  nazw i parserami `csv` / `srt`) i pole `online`; stare profile działają bez zmian.
- **Tłumaczenie korpusu z wyprzedzeniem:** `CorpusTool translate --profile <id> --provider
  deepl|llm|mock` tłumaczy korpus partiami (DeepL do 50, model językowy do 25 tekstów) z opisem
  sceny, mówcą, kluczem i kolumną kontekstu, w kolejności linii dialogu, z kontrolą jakości jak
  w aplikacji, i zapisuje wynik do lokalnej bazy tłumaczeń z profilem gry i znacznikiem
  `src=corpus`. Aplikacja z aktywnym profilem czyta te wpisy jak każdy inny wpis cache. Ręczne
  korekty, wpisy zatwierdzone i terminy słownika nie są nadpisywane; ponowne uruchomienie
  dokańcza przerwany przebieg, a partia z nieczytelną odpowiedzią albo odmową filtra jest dzielona
  na połowy (przepadają tylko teksty nie do przetłumaczenia). Włączony tryb prywatny = odmowa,
  zanim cokolwiek zostanie wysłane (`--dry-run` tylko ostrzega). `--dry-run` podaje liczbę tekstów,
  znaków i szacunek kosztu (Escape Academy: 7 632 teksty, 248 979 znaków; DeepSeek bez myślenia
  ok. 0,12–0,24 USD, DeepL ok. 50% miesięcznego limitu API Free). Klucze wyłącznie ze zmiennych
  środowiskowych.
- **Dopasowanie do korpusu w trybie live i przy tłumaczeniu regionu.** Gdy aktywny profil ma
  lokalny korpus (`<folder danych>\corpus\<id>.corpus.jsonl` z `CorpusTool extract`), aplikacja
  wczytuje go przy przebudowie pipeline'u (wyłącznie ten plik danych, nigdy pliki gry)
  i przyciąga odczyt OCR do znanego tekstu gry: cały blok, akapit, wiersz albo część wiersza.
  Tłumaczenie jest szukane w cache pod tekstem z korpusu, więc trafiają wpisy
  z `CorpusTool translate`, a różne odczyty tego samego zdania (inne zawinięcie, wielkość liter,
  błędy OCR typu „Itls”, „11m”, ucięty koniec) dzielą jeden wpis i jedno zapytanie. Nakładka
  pokazuje tłumaczenie w układzie wierszy z ekranu; imię mówcy, klawisz obok etykiety
  („E Inspect” → „E Zbadaj”) i śmieciowy akapit zostają bez zmian; napis WIELKIMI LITERAMI
  dostaje tłumaczenie wielkimi literami. Nieznana część bloku idzie do dostawcy sama, osobnymi
  akapitami. Priorytet bez zmian: ręczna korekta (także całego bloku) > słownik > cache >
  dostawca; dawny wpis całego odczytu jest zapasem, gdy części nie są znane (bez ponownej
  płatności). Teksty z podstawieniem (`{0}`, `%d`) i ze znacznikiem przycisku, którego nie ma
  na ekranie, nie są przyciągane. Przyciąganie działa tylko w obrębie aktywnego profilu, także
  w Cache-only i w trybie prywatnym (w pamięci). Bez pliku korpusu aplikacja działa dokładnie jak
  dotąd. Powtórka 242 bloków Escape Academy z kopii cache (Mock, korpus przetłumaczony
  z wyprzedzeniem): lokalnie 60,4% znaków i 49,6% bloków zamiast 0,3% i 0,4%, zapytania do
  dostawcy 241 → 122, znaki 4 214 → 1 678 —
  [ROADMAP.md → Runda 2026-10-06 (3)](docs/ROADMAP.md). Z dopasowaniem etykiet i odrzucaniem
  śmieci (niżej) ta sama powtórka daje 76,0% znaków i 62,0% bloków lokalnie oraz 57 zapytań
  (runda (4), pomiar (c)).
- Dokładny tekst korpusu aktywnego profilu (co najmniej 2 litery) przechodzi przez filtr śmieci
  OCR, który odrzuciłby go jako zbyt krótki albo nietypowy (Escape Academy: 14 takich tekstów).
- Literalne `\n` w tekstach korpusu (30 tekstów interfejsu Escape Academy) jest nowym wierszem
  także w kluczu cache `CorpusTool translate` — wpisy przetłumaczone wcześniej pod starym
  kluczem tych tekstów nie będą czytane (prawdziwy przebieg DeepSeek z 2026-10-06 użył już nowego klucza).
- Opcjonalne ustawienie `paragraphCacheKeys` w `settings.json` (domyślnie wyłączone): klucze
  cache po akapitach także bez korpusu, w każdej grze — ten sam akapit w innym bloku nie jest
  płacony drugi raz (powtórka sesji PoE2: 21,3% znaków lokalnie zamiast 0%, zapytania 247 → 218).
- **Liczby przy dopasowaniu do korpusu** (poprawka po recenzji): odczyt różniący się znakiem,
  walutą albo procentem przy liczbie nie jest już przyciągany do tekstu korpusu („-10%” do
  „+10%”, „€25” do „$25”, samo „10” do „+10%” — wcześniej jako pewne dopasowanie dokładne), także
  w dłuższych zdaniach. Liczba sklejona z literami („5kg”, „10am”, „1st”, „x3”) musi się zgadzać
  cyfra w cyfrę — inna, dopisana albo zgubiona cyfra to brak dopasowania; dopuszczalna jest tylko
  cyfra odczytu w miejscu litery, którą OCR myli z cyfrą (1/l/I, 0/O, 5/S, 8/B) i `1` w miejscu
  apostrofu („11m” → „I'm”). Pomiar na 880 próbkach przez Windows OCR: 690 poprawnych
  przyciągnięć zamiast 697 (7 odczytów z cyfrą wstawioną przez OCR w słowo nie jest już
  dopasowywanych), błędne bez zmian (1); prawdziwe sesje Escape Academy i kontrola PoE2 bez zmian.
- Gdy dostawca zawiedzie (sieć, limit), blok z korpusem dostaje — jak bez korpusu — stary
  tłumaczony wpis całego odczytu (sprzed sklejania wierszy, z uwagą kontroli jakości albo z inną
  płcią gracza) zamiast komunikatu o błędzie.
- Ręczna korekta bloku przyciągniętego do korpusu **przybliżeniem** zapisuje się pod tekstem
  odczytu z ekranu, nie pod tekstem z korpusu — błędne przybliżenie nie przenosi korekty na inne
  odczyty. Przy dopasowaniu dokładnym (inna wielkość liter, zawinięcie) korekta nadal obowiązuje
  dla wszystkich odczytów tego tekstu.
- **CorpusTool — mocniejsze zabezpieczenia ADR-014:** proces gry jest sprawdzany przed
  jakimkolwiek odczytem w folderze gry (wcześniej po skanie nagłówków `.pak`/`.utoc`); gdy
  `--game-dir` wskazuje podfolder, anti-cheat, podpisane i zaszyfrowane kontenery są szukane
  w całym folderze gry (`steamapps\common\<gra>`, `Epic Games\<gra>`, `GOG Galaxy\Games\<gra>`
  albo folder z plikiem gry z profilu); `--out`, `--cache` i `--stats` nie mogą leżeć w folderze
  gry, pod `steamapps\common` ani w bibliotekach Epic/GOG.
- `PRIVACY.md` i ADR-014: przy dopasowaniu do korpusu do dostawcy może trafić pełne zdanie
  z korpusu, którego część dopiero pojawia się na ekranie (wcześniej dokument obiecywał „ten sam
  tekst, który jest na ekranie”).
- **Etykiety źle odczytane przez OCR** (pierwsza sesja na nowej wersji: ok. 12 krótkich etykiet
  w rodzaju „Ihspect” zamiast „Inspect” albo „Userltem” zamiast „Use Item” szło do DeepL i wracało
  bez tłumaczenia). Krótki tekst (3–15 znaków) jest przyciągany do etykiety korpusu aktywnego
  profilu, gdy różni się od niej tylko typowymi pomyłkami OCR — I/l/1/|/!/apostrof, rn↔m, cl↔d,
  vv↔w, h↔n, c↔e, cyfra w miejscu podobnej litery, spacja, interpunkcja, kreska na brzegu — oraz
  najwyżej jedną inną literą (od 7 znaków). Nakładka pokazuje tłumaczenie etykiety z korpusu (bez
  zapytania, gdy jest przetłumaczona z wyprzedzeniem). Bez przyciągania: gdy drugi kandydat jest
  prawie tak samo blisko, gdy odczyt składa się z samych innych słów gry („Exit” nie zostaje
  „Edit”), gdy na brzegu brakuje albo przybywa zwykłej litery („The fir” nie zostaje „The Fire”),
  gdy odczyt ma więcej słów niż etykieta, gdy jest początkiem dłuższego tekstu gry („Connecti” —
  „Connecting…”) i gdy nie zgadzają się liczby (ochrona liczb jak dotąd).
- **Dialog pisany literami:** odczyt, który jest początkiem dokładnie jednej linii dialogu albo
  napisów korpusu (od 12 liter i 3 słów, do 10% pomyłek OCR), od razu dostaje tłumaczenie całej
  kwestii — napis może pojawić się, zanim gra dopisze resztę. Kolejne, dłuższe odczyty tej samej
  kwestii trafiają w ten sam wpis tłumaczenia (zero nowych zapytań) i w ten sam blok nakładki:
  napis aktualizuje się w miejscu (rośnie razem z oryginałem), bez podmiany i bez ponownego
  pojawiania się. Tak jest przy umiejscowieniu „Pod oryginałem” i w stylu „Napisy na dole”;
  w trybie zakrywania kwestia czeka na koniec pisania (wyżej: „Kwestia pisana literami nie
  miesza języków”). Linie o wspólnym początku (np. ta sama kwestia w wersji dla jednego i dla dwóch
  graczy) czekają, aż odczyt je rozróżni. Gdy na ekranie są już co najmniej dwa
  wiersze, tłumaczenie od razu dostaje przewidywaną liczbę wierszy całej kwestii.
- **Śmieci OCR przy aktywnym korpusie** (zlepki liter odczytane z ikon i tekstur, same liczby
  typu „11/11”): krótki odczyt (do 16 liter), w którym większość liter nie tworzy słów znanych z tekstów
  gry albo który ma dużo nietypowych znaków, bez żadnego dopasowania i bez podobieństwa do tekstu
  korpusu, nie idzie do dostawcy ani na nakładkę (oryginał zostaje widoczny). Termin słownika nie
  jest śmieciem. W bloku z prawdziwym tekstem taki wiersz zostaje bez tłumaczenia. Bez korpusu
  filtr działa jak dotąd.
- Ta sama etykieta odczytana raz poprawnie, raz z pomyłką OCR („Inspect”/„Ihspect”) jest dla
  nakładki tym samym blokiem — napis nie jest podmieniany przy drżeniu odczytu.
- Pomiar na odczytach z pierwszej sesji na nowej wersji (36 tekstów, które poszły do DeepL;
  powtórka przez pipeline z Mockiem na kopii bazy gracza): lokalnie 15 (11 etykiet, 4 kwestie
  dialogu — wszystkie poprawne w przeglądzie ręcznym), odrzucone jako śmieci 11, do dostawcy 10
  zamiast 36 (171 zamiast 571 znaków) —
  [ROADMAP.md → Runda 2026-10-06 (4)](docs/ROADMAP.md).

### Modele językowe

- **Opcje serwera LLM przypisane do adresu** (ADR-013, dopisek 2026-10-06): ustawienie
  `llmServerOptions` dodaje do zapytania `thinking`, `reasoning_effort`, `max_tokens`
  i `response_format` — tylko dla serwera, dla którego je zapisano; bez ustawienia zapytanie
  jest identyczne jak dotąd. Nowy gotowy serwer **DeepSeek** (`https://api.deepseek.com/v1`,
  model `deepseek-flash`, myślenie wyłączone — bez tego model zawsze myślał: paczka 5 linii
  trwała 7–20 s), a **Ollama** dostaje `reasoning_effort: none`. Status klucza i test połączenia
  pokazują aktywne opcje.
- Zużycie tokenów z odpowiedzi serwera (wejście, w tym z cache, wyjście, w tym rozumowanie)
  trafia do logu jako same liczby, bez treści.

### Wydajność

Oszczędności rzędu mikrosekund na klatkę — mniej pracy procesora w trakcie gry, nie krótszy
czas tłumaczenia (ten wyznaczają OCR i dostawca). Liczby przed/po:
[BENCHMARKS.md → Optymalizacje 2026-10](docs/BENCHMARKS.md#optymalizacje-2026-10).

- **Odcisk regionu tekstu** (trzymanie statycznego menu przy ruchu kamery) liczony skrótem
  XxHash128 zamiast SHA-256, z wektorowym maskowaniem kanału alfa i wektorowym testem kontrastu.
  Nadal każdy zmieniony piksel RGB unieważnia odcisk, a alfa i dopełnienie wiersza się nie liczą.
  Nowa zależność: `System.IO.Hashing` (Microsoft, MIT).
- **Trafienia cache w klatce live:** pipeline pyta cache o całą klatkę jednym odczytem partii,
  normalizacja nie kopiuje tekstu, który jest już czysty, pamięć trafień SQLite i cache trybu
  prywatnego nie liczą SHA-256 przy każdym trafieniu. Format kolumny `text_hash` bez zmian —
  istniejące bazy działają jak dotąd.
- **Odczyt z bazy przy zimnej pamięci trafień** (pierwsza klatka po starcie): wszystkie teksty
  klatki spoza pamięci czytane jednym zadaniem w tle, na jednym połączeniu i jednym
  przygotowanym zapytaniu zamiast osobnego zadania, połączenia i zapytania na każdy tekst.
- **Siatka luminancji** (decyzja „czy klatka się zmieniła”) liczona wierszami pikseli zamiast
  komórka po komórce — wynik co do bitu ten sam.

### Dla deweloperów

- **Łatka z wypełnionymi literami (`Core.Vision.GlyphCoverBuilder`, `GlyphCover`, `InkProfile`):**
  maska liter (odchylenie od tła z pierścienia + top-hat, próg 50% kontrastu), obwódka (kontur,
  cień), wypełnienie push-pull, kolor/kontur/cień/linia bazowa/gęstość/wyrównanie/ikony; duże
  napisy w 1/2–1/3 rozdzielczości. Koszt na klatce 4K: 1,7–24 ms na blok, cała klatka nowych
  napisów równolegle 2–16 ms, ponowne użycie po podpisie pola 0,03–0,11 ms. Liczona
  w `LiveTranslationSession` równolegle z tłumaczeniem (`LiveSessionOptions.BuildGlyphCovers`,
  `LiveFrameDiagnostics.GlyphCoverMs/GlyphCoverWaitMs`, w SceneReplay `glyphCoverMs`).
  Rysowana przez `App.Ui.GameTextElement` (geometria, kontur piórem, cień) z krojem
  z `OverlayFonts` (zasoby `App/Fonts`, wybór grubości po gęstości tuszu). Szczegóły i pomiary:
  [ROADMAP.md → Runda 2026-10-06 (5)](docs/ROADMAP.md).
- **OverlayPreview** (`tools/GameTranslatorOverlay.OverlayPreview`, Windows): składa prawdziwą
  nakładkę (wspólny `App.Ui.OverlayBlockRenderer`, wydzielony z `OverlayWindow`) na klatce gry
  przez Windows OCR, bramkę live i lokalny pipeline z kopii bazy, zapisuje PNG, porównanie
  i powiększenia bloków oraz `galeria.md`; sieć zablokowana, brak w bazie = Mock `[PL]`. Opcje
  `--cover crisp|soft|off` i `--font-family auto` (domyślnie, jak gracz); rozgrzanie krojów
  i czasy układu WPF/łatek w konsoli. Galerie PRZED/PO:
  `GTO Diagnostics\20261005-natywne-spolszczenie\krok-wyglad` (poza repo).
- **Wstrzymanie niedokończonej kwestii:** `LiveSessionOptions.HoldTypingPrefixes`,
  `TypingPrefixSettleTime` (0,9 s), `TypingPrefixHoldLimit` (8 s), `TranslationPipeline.IsCorpusPrefix`,
  `CorpusSnapper.StartsSpokenLine`; SceneReplay `typing` / `typing-nohold` (pomiar w
  [ROADMAP.md → Runda 2026-10-06 (6)](docs/ROADMAP.md)). `OverlayFonts.ChooseStyleWeight`
  (grubość z głosów bloków w jednym stylu), `LiveSessionOptions.IdentityEchoSafe`.
- Testy: 24 nowe w Core (łatka, profil tuszu, ikony, kotwica, podpis, komunikat pełnego ekranu,
  `overlay.fontFamily`) i 4 w Infrastructure (migracja kroju) — wyłącznie dane syntetyczne.
- **SceneReplay: scenariusze starego napisu** (`stale-junk`, `stale-junk-ghost`, `stale-texture`,
  `stale-newtext`, `stale-busy`) do zgłoszenia „tłumaczenie Inspect/Zbadaj zostaje po zniknięciu
  etykiety”. Etykieta znika lokalnie (5% okna, bez cięcia sceny) w oknie 1500×900 fizycznych
  pikseli; mierzą czas do pierwszego callbacku bez starego bloku, powroty, łączny czas
  widoczności, czas nowego tekstu, pełne i częściowe OCR oraz zapytania Mock. Opcje `--assets`,
  `--texture`, `--texture-origin` czytają prawdziwy wycinek etykiety i teksturę tylko lokalnie
  (bez kopiowania, bez zapisu pikseli); bez nich scenariusz rysuje etykietę i teksturę sam.
  `--bright-spot-px N` zostawia w polu etykiety jasną plamkę, która odbiera sesji dowód
  w pikselach (test ścieżki zapasowej). Znacznik geometrii w wariantach junk ma teraz ciemny
  kolor #900090 — jasny #FF00FF sam udawał resztkę tekstu. Wyniki przed/po:
  [ROADMAP.md → Runda 2026-10-05](docs/ROADMAP.md).
- **SceneReplay: scenariusze napisu, który zostaje lub wraca** — `stale-fade` (wygaszanie
  przez `--fade-ms`), `stale-dim` (przygaszenie do `--dim-percent` przy pulsującym podkładzie),
  `stale-present-junk` (śmieci OCR nad widoczną etykietą, `--junk-run`), `stale-newdirty`
  (nowy napis z wielokropkiem w miejscu zniknętego) i `stale-blink` (etykieta znika i wraca;
  raport odtwarza pasek napisów z MainWindow). `--phase-ms 0..200` działa teraz także dla
  `stale-*` i przesuwa zmianę względem zegara przechwytywania.
- **Core:** `KnownTextAbsenceProbe` (dowód zniknięcia znanego tekstu na teksturze),
  `FullScanSchedule` (zegar pełnego skanu niezależny od wycinków),
  `LiveReadingStabilizer.IsUnrelatedDirtierReading`, `LiveBlockSurvival.UnconfirmedRegion`;
  po recenzji: `KnownTextReference` (wzorzec sondy z ostatniego odczytu i liczba pikseli
  rdzenia glifów), `LiveOverlayBlock.Probe`, `LiveReadingStabilizer.IsPlausibleText`,
  `IsImplausibleReading`, `IsVariantOf`, `LiveBlockSurvival.PartialOcrSeed`,
  `LiveSubtitleContent.Restore`; `LiveReadingStabilizer` przyjmuje wiarygodny, niepowiązany
  odczyt gorszej jakości po dwóch kolejnych potwierdzeniach (wcześniej odrzucał go zawsze);
  71 nowych testów jednostkowych (36 + 35).
- **Core: przyciąganie odczytu OCR do korpusu gry** (`GameTranslatorOverlay.Core.Corpus`):
  `CorpusIndex` (trigramy po tekście bez wielkości liter) i `CorpusSnapper` dopasowują cały
  blok, akapity po `TextReflow.Unwrap` i pojedyncze wiersze (dokładnie, przybliżenie albo
  fragment; składanie fragmentów z kolejnych wierszy). Zabezpieczenia: liczby 1:1, minimalna
  długość przybliżeń, odstęp do drugiego kandydata, krótkie etykiety dokładnie (od rundy (4)
  także z typowymi pomyłkami OCR — niżej). Wpięte w pipeline przez `TranslationUnitPlanner`
  (patrz wyżej). Pomiar na Escape Academy przy pierwszej wersji: 59,4% znaków z prawdziwych sesji
  obsłużonych lokalnie (wcześniej 17,0%), 0,14% błędnych przyciągnięć na 880 próbkach przez
  Windows OCR, p95 0,9 ms (150 tys. tekstów: 4,8 ms) —
  [ROADMAP.md → Runda 2026-10-06](docs/ROADMAP.md).
- **CorpusEval** (`tools/GameTranslatorOverlay.CorpusEval`, Windows): prawda syntetyczna przez
  Windows OCR, przegląd progów na kopii cache i czasy dopasowania; do katalogu wyników trafiają
  same liczby. Nowy projekt testów `tests/GameTranslatorOverlay.CorpusTool.Tests` (85 testów na
  danych syntetycznych) i 70 nowych testów Core. Polecenie `replay` powtarza bloki z kopii cache
  przez prawdziwy `TranslationPipeline` z Mockiem (bez korpusu, klucze po akapitach, korpus na
  pustym cache, korpus na bazie z `translate`).
- **Pipeline z jednostkami tłumaczenia:** `TranslationPipelineOptions.Corpus` i `SplitParagraphs`,
  `TranslationOutcome.Parts` (części bloku: tekst z ekranu, klucz cache, pochodzenie, korpus)
  i `CacheKey` (klucz, pod którym orkiestrator zapisuje ręczną korektę bloku będącego jednym
  tekstem korpusu), `TranslationPipeline.IsExactCorpusText`, `CorpusCatalog` i
  `AppPaths.CorpusDirectory` (Infrastructure). Testy: Core +30, Infrastructure +6 (dane
  syntetyczne).
- **Kontekst partii dla dostawców:** `TranslationContext.Scene` i `TextNotes` (opis sceny
  i notatka do każdego tekstu — wiadomość modelu dostaje `"notes"`, DeepL scenę w `context`);
  pipeline na żywo ich nie ustawia. `SqliteTranslationCache.PeekManyAsync` (odczyt bez liczników
  użycia, połączenie tylko do odczytu) i `StoreManyAsync` (zapis partii w transakcji, bez
  nadpisywania korekt i wpisów zatwierdzonych). `TranslationCacheContext` czyta i składa część
  `src=…`; `CorpusTranslationKey` liczy klucz cache wpisu korpusu. Testy: Core +15,
  Infrastructure +28, CorpusTool +62 (dostawcy przez atrapę HTTP, baza SQLite w katalogu
  tymczasowym).
- **ProviderEval:** opcje `--llm-thinking`, `--llm-effort`, `--llm-max-tokens`, `--llm-json`
  i `--llm-no-preset` (pola zapytania serwera LLM bez zmiennych globalnych); po przebiegu suma
  tokenów z `usage`. `EnvironmentTranslationProviders` (Infrastructure) buduje dostawców
  z kluczami ze zmiennych środowiskowych dla narzędzi.
- **Poprawki po recenzji korpusu (krok 2b):** `EditDistance.BoundedGuarded` / `GuardedRatio`
  (odległość edycyjna z ochroną cyfr i znaków liczb), `CorpusText.IsNumberSign`,
  `IsNumberCharacter`, `HasDigit`, `IsDigitMistakenFor`; `LooseKey` zostawia znak liczby na
  brzegu. `TranslationOutcome.CacheKey` jest kanoniczny tylko przy dokładnym dopasowaniu
  (`UnitPlan.CorrectionKey`). CorpusTool: `GameFolderGuard.ResolveGameRoot`, `LibraryDirectory`,
  `LibraryGameRoot`, `FindDirectoryWithExecutable`, `CheckFolder(..., processNames)`,
  `RunningGameGuard.FindRunning(..., gameRoot)`, `OutputLocationGuard.CheckOutsideGame`.
  `CorpusEval evaluate` liczy klucz prawdy przy ocenie (`truthKeysRecomputed`); eksperyment
  przeliczony na tych samych próbkach: 690 / 880 poprawnych (79,2% → 78,4%), błędne 0,14%,
  cache EA 59,4% znaków bez zmian, p95 dopasowania 0,9 ms; powtórka przez pipeline bez zmian
  (EA 217: 64,2% znaków, 52,1% bloków, 104 zapytania) —
  [ROADMAP.md → Krok 2b](docs/ROADMAP.md). Testy: Core +58, CorpusTool +19.
- **Etykiety, początek kwestii i szum przy korpusie** (runda 2026-10-06 (4)): `OcrEditDistance`
  (`Distance`, `Prefix` — odległość edycyjna ważona typowymi pomyłkami OCR), wyszukiwanie
  krótkich etykiet i początków linii dialogu w `CorpusSnapper` (`CorpusMatch.IsLabel`),
  `CorpusSnapper.LooksLikeNoise` i bramka live `TranslationPipeline.ShouldTranslateLive`;
  `TranslationPipeline.CorpusIdentity` z `LiveBlockKeyer.AssignKeys(..., identity)` daje ten sam
  klucz nakładki kolejnym odczytom tej samej kwestii i drżeniu OCR etykiety. Przełączniki
  `CorpusSnapOptions.AllowLabels`, `AllowPrefixes`, `RejectNoise`. CorpusEval: polecenia
  `session`, `prefixes`, `typing` i `--features off` (dopasowanie sprzed rundy). Testy: Core +71
  (dane syntetyczne). Pomiary: [ROADMAP.md → Runda 2026-10-06 (4)](docs/ROADMAP.md).
- **Zależności i testy po 0.4.0:** Dependabot podniósł `System.Security.Cryptography.ProtectedData`
  do 10.0.12, w projektach testów `coverlet.collector` do 10.1.0, `Microsoft.NET.Test.Sdk` do
  18.10.1 i `xunit.runner.visualstudio` do 4.0.0, a w GitHub Actions `actions/checkout` do v7,
  `actions/setup-dotnet` do v6, `actions/upload-artifact` do v7, `actions/cache` do v6
  i `softprops/action-gh-release` do v3. Testy równoległych tłumaczeń nie zakładają już, że
  wątek zdąży w określonym czasie (padały na wolnym runnerze CI), a sprzątanie bazy po teście
  liczników działa także na Windows.
- Testy: 1697 (1251 Core + 280 Infrastructure + 166 CorpusTool), zielone na Windows; build całego
  rozwiązania bez ostrzeżeń.

## [0.4.0] — 2026-10-01

Duże wydanie: szybszy tryb live, komunikaty w grze, start live jednym skrótem, pamięć dialogu
dla modeli językowych, kontrola jakości tłumaczeń, lepszy słownik, glosariusze DeepL, panel
„Szybkość” oraz poprawki błędów cache i prywatności. Nowe funkcje były sprawdzane na Linuksie
(1114 testów xUnit, kompilacja aplikacji WPF); w oknie na Windows i z prawdziwymi dostawcami
czekają na scenariusze M27–M35 ([MANUAL_TESTING.md](docs/MANUAL_TESTING.md)).

### Tryb live

- **Start i stop live jednym skrótem: `Ctrl+Shift+L`** na grze, która jest na pierwszym planie,
  bez przełączania się do okna tłumacza. Gdy aktywne jest okno systemowe (pulpit, pasek zadań)
  albo sam tłumacz, wybierana jest ostatnio tłumaczona gra, a potem gra z profilem. Gdy wybrano
  „brak profilu”, profil pasujący do gry włącza się przed startem. Pozycja
  „▶ Start live na aktywnej grze” / „⏹ Stop live” w zasobniku. Ostatnia gra jest zaznaczana
  na liście po starcie aplikacji i po „Odśwież” (live nie startuje sam, profil się nie zmienia).
  Skrót zmienisz polem `liveToggleHotkey` w `settings.json`.
- **Komunikaty w nakładce:** brak lub odrzucony klucz, wyczerpany limit, ograniczanie zapytań,
  brak sieci, pusty wynik, braki w Cache-only, niedziałający cache oraz start/stop live widać
  w grze jako krótki pasek przy górnej krawędzi okna (znika po 3–5 s, ten sam komunikat
  najwyżej raz na 30 s). Przy nakładce schowanej skrótem przechodzą tylko błędy krytyczne.
  Ręczne tłumaczenie (`Ctrl+Shift+T`) w trybie „Nakładka na ekranie” zawsze daje odpowiedź
  (np. „ℹ Nie rozpoznano tekstu w zaznaczeniu”). Komunikaty wyłączysz polem
  „Komunikaty w nakładce”.
- **Szybsza reakcja na nową linię:** okno stabilności 250 ms liczy się od zauważonej zmiany,
  a nie od końca poprzedniego OCR/tłumaczenia (do ~250 ms mniej czekania — wyliczone
  z harmonogramu, nie zmierzone w grze). Obraz, który dalej się zmienia (pisany tekst), czeka
  jak dotąd. Celowe drugie czytanie przy niepewnym OCR bez zmian.
- **Znany ekran nie czeka w kolejce:** klatka, której cały tekst jest w cache lub słowniku,
  pokazuje się od razu, bez czekania na wolne tłumaczenie starszej klatki.
- **Panel „Szybkość”** w oknie aplikacji: mediana i p90 z tej sesji dla **„Zmiana → napis”**
  (od zauważonej zmiany obrazu do gotowych napisów, czyli czas, który widzi gracz), nowego
  tekstu, znanego tekstu (cache/słownik), odpowiedzi dostawcy, OCR i przechwycenia klatki.
  **Kopiuj raport** kopiuje szczegóły (mediana, p90, maksimum, liczba pomiarów) — bez tekstu
  z gry. Czasy są tylko w pamięci.
- Zmiana samego wyglądu (czcionka, tło, styl, tryb wyniku, skróty, komunikaty) nie przebudowuje
  już pipeline'u: nie anuluje tłumaczeń w locie i nie czyści pamięci dialogu
  (status „Wygląd zapisany.”).

### Jakość tłumaczeń

- **Pamięć dialogu EN→PL dla modeli językowych** (Claude, serwer zgodny z OpenAI): model
  dostaje do 6 ostatnich linii (ok. 1500 znaków) razem z tłumaczeniami, które sam już zwrócił,
  więc trzyma się tych samych form („gotowy”/„gotowa”), formy zwracania się i pisowni imion.
  Ręczna poprawka linii zastępuje ją w tej pamięci. DeepL nadal dostaje tylko angielskie linie.
- **Postać gracza** (nieznana / mężczyzna / kobieta) dla modeli językowych: zwroty do gracza
  w odpowiedniej formie („zrobiłaś”/„zrobiłeś”). Po zmianie ustawienia linie z „you” z cache
  są raz tłumaczone ponownie (ręczne poprawki zostają).
- **Kontrola jakości wyniku:** pusty wynik nie jest już zapisywany w cache (zostaje oryginał
  i komunikat); zmienione liczby, wynik identyczny z angielskim i „rozgadany” wynik są
  oznaczane w cache. Modele językowe dostają jedno ponowienie dla oznaczonego tekstu (dodatkowe,
  płatne zapytanie w limicie znaków sesji). Tekst z trwałym problemem kosztuje najwyżej
  3 zapytania u modeli językowych i 2 u DeepL/Azure/Google. Ostrzeżenie jakości nie jest
  jeszcze pokazywane w oknie ani nakładce.
- **Glosariusze DeepL ze słownika:** gdy tłumaczony tekst zawiera termin ze słownika, DeepL
  dostaje glosariusz zbudowany z aktywnego słownika. Nazwy przedmiotów, postaci i miejsc są
  tłumaczone spójnie także w środku zdań, z polską odmianą. Glosariusz powstaje raz dla danej
  zawartości słownika (po restarcie jest ponownie używany), a stare wersje tej aplikacji są
  usuwane z konta DeepL w tle. Przygotowanie działa w tle: nowy glosariusz opóźnia najwyżej
  jedną partię o najwyżej 300 ms (kolejne idą bez niego, dopóki nie będzie gotowy), zapytania
  o glosariusz mają limit 5 s i nie są ponawiane. Błąd nie blokuje tłumaczenia; kolejna próba
  po 10 minutach. W trybie prywatnym glosariusz nie jest tworzony.
- **Słownik w zdaniach:** rozpoznaje angielską liczbę mnogą i dopełniacz („Waystones”,
  „Exalted Orbs”, „Waystone's”), terminy rozbite na dwa wiersze („Energy\nShield”) i etykiety
  z dwukropkiem („Rarity:” → „Rzadkość:”).
- **Zakres terminu „Etykieta”** (`"scope": "label"`, kolumna w edytorze słownika): termin działa
  tylko jako cały napis przycisku/nagłówka, nie jest wciskany w zdania ani do glosariusza DeepL.
  W słowniku ogólnym tak oznaczono Save, Chest, Key, Trade, Attack i Upgrade, w PoE2 — Staff.
- Prompt modeli językowych: termin słownika jest odmieniany zgodnie z polską gramatyką, a słowo
  w zwykłym znaczeniu tłumaczone normalnie.
- DeepL i tłumaczenie lokalne wybierają ten sam wariant terminu przy konflikcie (wyższy
  priorytet → termin z rozróżnianiem wielkości liter → termin wczytany później).

### Poprawki błędów

- **Pętla płatnych tłumaczeń:** stare wieloliniowe wpisy cache przypisane do profilu gry
  (ze starej bazy albo z importu JSON) oraz wpisy atrapy Mock z profilem były wysyłane do
  dostawcy przy każdym wystąpieniu. Teraz są tłumaczone ponownie jeden raz.
- **Prywatność:** terminy dodane w trybie prywatnym nie trafiają do glosariusza na koncie
  DeepL — także po wyłączeniu trybu prywatnego w tej samej sesji.
- Błąd zapisu liczników użycia (pełny dysk, zablokowana baza) nie przerywa już odczytu z cache
  i trybu live. Niedziałająca baza jest traktowana jak brak wpisu; wyniki są wtedy pamiętane
  w pamięci programu (do 2000 tekstów), żeby nie płacić drugi raz, a w grze pojawia się
  komunikat „⚠ Cache niedostępny — tłumaczenia nie są zapisywane”.
- Klatka, w której część tekstów jest znana, nie odczytuje ich z bazy drugi raz — licznik
  użyć wpisów (`use_count`, także w eksporcie) nie jest już zawyżany.
- Eksport/import cache zachowuje znacznik formatu i jakości (`context`); stare pliki działają.
- Przy braku sieci stary wynik nieaktualnego wpisu trafia do wszystkich równoległych tłumaczeń
  tego samego tekstu, nie tylko do jednego.

### Dla deweloperów

- **ProviderEval** (`tools/GameTranslatorOverlay.ProviderEval`): porównanie dostawców i wariantów
  promptu na korpusie EN→PL — chrF (zgodny z sacreBLEU), mediana/p90 czasu (pierwsze zapytanie
  osobno), kontrole liczb, formy „ty”, rodzaju mówiącego i terminów. Przykładowy korpus
  `eval/en-pl.sample.jsonl` (42 linie napisane na potrzeby projektu). Smoke test z Mockiem w CI.
- **Benchmarki BenchmarkDotNet** (`benchmarks/`, [docs/BENCHMARKS.md](docs/BENCHMARKS.md)): cache
  w klatce live, detekcja zmian, odcisk regionu, obróbka tekstu, słownik; ręczny workflow
  „Benchmarks” z wynikami jako artefakt.
- CI: raport pokrycia kodu z progiem (Core ≥ 91%, Infrastructure ≥ 80%), testy tylko dla
  Windows oznaczane `[WindowsFact]` (Skipped na Linuksie), anulowanie starszych przebiegów
  tego samego PR-a, token tylko do odczytu poza jobem publikacji, Dependabot dla NuGet
  i GitHub Actions, OcrLab, ProviderEval i benchmarki w rozwiązaniu.
- Testy: 1114 (880 Core + 234 Infrastructure; na Linuksie 3 testy DPAPI są pomijane).

## [0.3.1] — 2026-09-29

### Jakość tłumaczeń

- **Zawinięte zdania tłumaczone w całości:** dialog lub opis rozbity w grze na kilka wierszy
  był wysyłany z podziałami, a tłumacz (m.in. DeepL) traktował każdy wiersz jak osobne zdanie
  — gramatyka rozpadała się na granicach wierszy. Teraz miękkie zawinięcia są sklejane przed
  tłumaczeniem, a wynik jest rozkładany z powrotem na tyle samo, równych wierszy. Menu,
  statystyki przedmiotów i osobne zdania pozostają rozdzielone. Dotyczy wszystkich dostawców.
- Wcześniejsze automatyczne tłumaczenia wieloliniowe z cache są tłumaczone ponownie jeden raz
  (ręczne poprawki zostają; przy błędzie sieci, limicie i w Cache-only używany jest stary wynik).
- Naprawa typowych pomyłek OCR przed tłumaczeniem: `l'm` / `l'll` / `l've` / `l'd` → `I…`,
  samotne `|` przed słowem → `I`.
- Forma „ty” wobec gracza: DeepL `formality: prefer_less`, a modele językowe dostają tę samą
  wskazówkę oraz polecenie spójnego rodzaju mówiących.

Wpływ na jakość nie był jeszcze mierzony na prawdziwych dialogach — do sprawdzenia
checklistą [docs/QUALITY_CHECK.md](docs/QUALITY_CHECK.md).

## [0.3.0] — 2026-09-29

Wybór dostawcy tłumaczeń (DeepL, Azure, Google, Claude, serwer zgodny z OpenAI — także
lokalny), słownik i poprzednie kwestie jako kontekst tłumaczenia, szybszy cache i połączenia,
zamknięty backlog audytu #3 oraz poprawki stabilności live wprowadzone po 0.2.2.
Scenariusze ręczne M23–M26 (nowe pola okna, lokalny model, Claude, skrót przy zaznaczaniu)
nie były jeszcze wykonane na Windows przed tym wydaniem.

### Szybciej i lepiej — cache, połączenia, kontekst dialogu (2026-09-29)

- **Szybszy cache w trybie live:** trafienia są pamiętane w RAM, a liczniki użycia zapisywane
  zbiorczo zamiast zapisu na dysk przy każdym odczycie. Lokalny pomiar (50 klatek × 20 bloków
  z cache, Mock, Linux): ~10–13 ms → ~0,25 ms na klatkę. Na Windowsie z antywirusem zysk
  zależy od dysku; nie był tam mierzony.
- **Szybsze pierwsze tłumaczenie po przerwie:** połączenia z dostawcą żyją w puli do 10 minut
  (domyślnie 1 min), a przy rozpoczęciu zaznaczania regionu lub starcie live aplikacja
  zestawia połączenie pustym zapytaniem `HEAD` (bez klucza i tekstu; nie w Cache-only).
- **Kontekst dialogu:** do 6 ostatnich linii wysłanych wcześniej do tego samego dostawcy
  trafia jako kontekst — DeepL `context`, modele językowe `previous_lines`. Pomaga m.in.
  w polskim rodzaju gramatycznym pojedynczych kwestii. Nic nowego nie opuszcza komputera.
- **CI:** dodatkowy szybki job testów na Linuksie i cache pakietów NuGet.
- Testy: 509 (363 Core + 146 Infrastructure).

### Dodane — wybór dostawcy tłumaczeń (2026-09-29)

- **Azure AI Translator** (klucz + opcjonalny region; plan F0: 2 mln znaków miesięcznie),
  **Google Cloud Translation** (klucz API projektu), **model językowy zgodny z API OpenAI**
  (OpenAI, OpenRouter, Groq, lokalne Ollama/LM Studio — lokalnie tekst nie opuszcza komputera)
  oraz **Claude** (oficjalne SDK Anthropic, domyślnie `claude-opus-5-5`). DeepL pozostaje domyślny.
- Każdy dostawca ma osobny klucz w DPAPI; okno pokazuje tylko pola wybranego dostawcy,
  gotowe adresy OpenAI/Ollama/LM Studio i informację, dokąd trafia tekst.
- Modele językowe dostają nazwę gry z profilu i **terminy słownika występujące w zdaniach**
  — nazwy są spójne także wewnątrz dłuższych opisów. Odpowiedź z inną liczbą tłumaczeń nie
  jest przypisywana blokom (małe partie są wtedy tłumaczone pojedynczo).
- Claude: odpowiedź wymuszona schematem JSON, niski `effort` dla krótkich tekstów oraz
  serwerowy fallback przy odmowie filtra bezpieczeństwa (dla Opus 5.5/5, Sonnet 5.5, Fable 5.1).
- Adres zdalnego serwera LLM musi używać HTTPS; zwykłe HTTP tylko dla `localhost`.
  Klucz LLM jest przypisany do serwera, dla którego go zapisano, i nie wychodzi pod inny adres.
- Odpowiedź modelu z echem wejścia, pustymi lub nietekstowymi elementami jest odrzucana
  przed zapisem do cache; klient Claude ignoruje `ANTHROPIC_BASE_URL`/`ANTHROPIC_AUTH_TOKEN`.
- Nowe czytelne błędy: nieznany model, odmowa modelu, niepełna konfiguracja dostawcy.

### Naprawione — backlog audytu #3 (2026-09-29)

- Słownik: priorytet działa także między terminem z rozróżnianiem wielkości liter i bez;
  terminy z podwójną lub twardą spacją w JSON trafiają w znormalizowany tekst z OCR.
- Profil gry może jawnie wyłączyć automatyczne powiększanie małych regionów (`ocr.upscale: 1.0`);
  brak pola oznacza ustawienia aplikacji.
- Przeskalowane prostokąty OCR nie dryfują o 1 px na prawej/dolnej krawędzi.
- Profil z `minAppVersion` nowszym niż aplikacja jest pomijany z czytelnym komunikatem.
- Ctrl+Shift+T przy otwartym zaznaczaniu regionu zamyka je, zamiast być ignorowanym.
- Przejście między polami ustawień bez zmiany wartości nie przebudowuje już pipeline'u
  (nie anuluje tłumaczeń live w locie).

### Wewnętrzne

- Wspólna pętla HTTP dostawców (`ProviderHttp`): timeout, ograniczony retry, mapowanie
  błędów, obsługa odpowiedzi portalu/proxy; DeepL przeniesiony bez zmiany zachowania.
- Testy po rundzie dostawców: 493 (361 Core + 132 Infrastructure), m.in. dostawcy na fałszywym HTTP bez sieci.
- Nowa zależność: `Anthropic` (MIT) — w THIRD-PARTY-NOTICES.

### Dla gracza

- Poprawki wspólne dla różnych gier, mierzone m.in. w Escape Academy.
- Szybsze usuwanie potwierdzonych starych opisów i ochrona przed ich powrotem
  po spóźnionej odpowiedzi dostawcy.
- Stabilniejsze kolejne odczyty i dokładniejsze położenie tekstu.
- Krótsze zbędne oczekiwanie w sesji, z zachowaniem kontroli zapytań i znaków.

### Dokumentacja

- Odświeżone README, instrukcja, wizja produktu, architektura i opis testów.
- Rozróżnienie wydania 0.2.2, bieżącego kodu `main` i niewdrożonych kierunków rozwoju.
- Stan przed rundą dostawców: 371 testów (334 Core + 37 Infrastructure) i opis ograniczeń pomiarów.
- Nowe: przewodnik po dostawcach w instrukcji, ADR-013, scenariusze ręczne M23–M26.

### Naprawione

- Silny ruch może zachować już przetłumaczone, nieruchome napisy, jeśli pełny obszar
  źródłowy z marginesem 3 px ma identyczne RGB. Sprawdzanie używa istniejących klatek
  i skrótu SHA-256 w pamięci, bez przechowywania obrazu ani dodatkowego OCR/API.
  Ten sam dowód chroni przed kolejnymi pustymi odczytami. Zmieniony napis nadal jest
  usuwany, a generacja sceny unieważnia spóźnione wyniki. Brak ochrony dla skalowania
  OCR, fallback, zmiany rozmiaru okna, niepełnego pola lub niepewnego kontrastu.
  Sondy własnego menu obejmują duży i częściowy ruch tła, podmianę/usunięcie napisów
  oraz wymuszone błędy OCR; nie zastępują prób wyglądu w grach.

- Pierwsza okresowa kontrola obrazu podczas OCR czeka 1,5 zwykłego interwału;
  kolejne wracają do normalnego rytmu. OCR trwający co najmniej jeden interwał
  nadal wymaga świeżej kontroli po zakończeniu. Ogranicza to zbędne przechwycenie
  tuż przed końcem krótkiego OCR; przy 6 FPS pierwsza okresowa kontrola trwającego
  odczytu może nastąpić około 83 ms później. Harmonogram dostawcy pozostaje bez zmian.
- Potwierdzona zamiana treści lub brak OCR w całym dawnym polu, które stało się
  jednolite i wcześniej miało znany kontrast, usuwa lokalny stary napis przed
  oczekiwaniem na tłumaczenie. Pozostałe bloki zostają. Zasłonięcie takiego pola
  podczas oczekiwania odrzuca nieaktualną klatkę i ponawia wszystkie jej obszary,
  także nadal widoczne. Tekstura, ucięty obszar i niepewne kolory zachowują okres łaski.
  Pamięć odtwarzania jest czyszczona lokalnie; filtr własnych tłumaczeń chroni kolejny odczyt.
- Pasek napisów usuwa tylko treść powiązaną ze znikającymi źródłami. Usunięcie
  niezależnej starej etykiety nie czyści nowszego dialogu; częściowa aktualizacja
  nie odnawia czasu wyświetlania i nie przywraca już wygasłego paska.
- Stabilizacja pozycji jest oddzielona od stabilizacji rozmiaru: różnice do 2
  fizycznych pikseli na każdej osi pozostają tłumione, większa zmiana pozycji
  przechodzi niezależnie od zachowania szerokości i wysokości pola. Duży napis
  nie zwiększa już tolerancji przesunięcia. Globalna detekcja ruchu pozostaje bez zmian.
- Pętla live może obudzić się przy najbliższym terminie stabilności obrazu zamiast
  czekać do kolejnej zwykłej próbki. Nadal przechwytuje świeżą klatkę i wymaga
  250 ms stabilności oraz kolejnych potwierdzeń podobnego odczytu. Przy próbkowaniu
  nie częstszym niż okres stabilizacji zachowuje ustawiony rytm, także w ruchu.
  Zmiana skraca narzut aplikacji; nie przyspiesza odpowiedzi DeepL.

- Podobna, poprawnie rozpoznana nowa treść może zastąpić stary napis także wtedy,
  gdy jest równej długości lub krótsza (np. „locked” → „open”). Pozostaje ochrona
  przed wyraźnie gorszym odczytem i oczywistym ucięciem początku/końca zdania.
- Potwierdzenia OCR muszą być kolejne. Powrót poprawnego tekstu albo pusty odczyt
  w badanym obszarze przerywa serię błędnego wariantu; pomyłki nie sumują się.
- Wiarygodna zmiana dostaje szybką powtórkę OCR swojego obszaru, bez oczekiwania
  na okresowy pełny skan i bez wysyłania niepotwierdzonego tekstu do tłumaczenia.
  Bloki tylko podtrzymywane nie czekają na miejsce w kolejce tłumaczeń.

- Nowy widok może rozpocząć tłumaczenie podczas kończenia odpowiedzi poprzedniego.
  Sesja utrzymuje najwyżej dwa zadania tłumaczeń; przy zajętych miejscach pomija
  nieaktualne klatki zamiast kolejkować opisy. OCR i stan nakładki nadal obsługuje
  jedna pętla. Zatrzymanie obejmuje także zadania pozostawione przez stare sceny.
- Limit sesji rezerwuje znaki przed wysłaniem zapytania, wspólnie dla trwających
  tłumaczeń. Współdzielony odczyt nie rezerwuje ich ponownie. Powtórne sprawdzenie
  cache zamyka wyścig mogący wysłać ponownie właśnie zakończony tekst.
- Zmiana ustawień unieważnia zapis starej odpowiedzi do poprzedniego cache.
  Samo przejście do innej sceny nadal pozwala zachować ukończone tłumaczenie.

- Wykryta zmiana sceny usuwa stare napisy i pamięć ich odtwarzania przed OCR
  i tłumaczeniem. Zaszumiony odczyt nowego opisu nie przywraca „Inspect” z poprzedniej
  sceny. Dotyczy wspólnego silnika wszystkich gier, bez zmiany progów detekcji.
- Podczas dłuższego oczekiwania na OCR lub dostawcę sesja sprawdza obraz. Wynik
  nieaktualnej sceny nie wraca do nakładki; cache dostawcy może zachować odpowiedź.
  Dodatkowe przechwycenia kosztują czas CPU, ale nie wykonują dodatkowego OCR/API.
- Automatyczne czyszczenie obejmuje również pasek napisów i zachowuje ręczne
  ukrycie Ctrl+Shift+H. Zmiany zaobserwowane podczas oczekiwania nie przepadają,
  także po błędzie lub zmianie ustawień.
- Lokalna sonda SceneReplay odtwarza zmianę widoku, odpowiedź starej sceny w locie
  i błędne przywracanie poprzedniego tekstu. Używa wyłącznie Mocka i własnego okna;
  pomiary callbacków nie są pomiarami fizycznej prezentacji nakładki.

- Krótkie zatrzymanie ruchu kamery nie zeruje już maksymalnego oczekiwania na OCR.
  Termin 2,5 s działa także przy naprzemiennym ruchu i spokoju; odnawia się dopiero
  przy rozpoczęciu przetwarzania. Poprawka wspólna dla wszystkich gier i profili.
  Nie jest to gwarancja czasu pojawienia się tłumaczenia: OCR, sieć i rysowanie
  nakładki nadal mają własne opóźnienia.
- Dwanaście przypadków regresji obejmuje przerywany/ciągły ruch, spokojne menu,
  reset sesji i współpracę ze stabilizatorem zmian.

### Narzędzia dev

- Sonda live dla różnych gier: jawny wybór profilu, raport użytych ustawień i lokalne
  metryki czasu OCR, tłumaczenia oraz wieku klatki w chwili przygotowania aktualizacji.
  Wiek klatki nie jest pomiarem czasu od pojawienia się tekstu do widocznej nakładki.
- Raport JSONL bez treści OCR i tłumaczeń; wypisywanie tekstów i diagnostyczne zrzuty
  wymagają osobnego włączenia. Sonda korzysta z prywatnego cache w pamięci i Mocka,
  bez dostępu do klucza DeepL ani danych aplikacji użytkownika.
- Raport sondy zawiera liczbę zapytań i znaków do Mocka oraz trafień cache/słownika.
  Służy do porównania kosztu przetwarzania; nie mierzy zużycia ani opłat DeepL.
  Miganie, utrzymywanie starych napisów i wygląd w rozgrywce wymagają dalszych prób.

## [0.2.2] — 2026-09-04

Runda jakości trybu live strojona na żywych grach (Path of Exile 2, Escape Academy).

### Działa lepiej

- Jeden silnik OCR na język (zamiast tworzenia go co przebieg) i **detektor zmian odporny
  na szum tła**: migotanie mgły/pogody nie liczy się jako zmiana — region OCR obejmuje tylko
  nowy tekst, reakcja na nowy napis ~0,1–0,4 s, na spokojnej scenie zero pracy.
- **Kontekst DeepL**: teksty z tej samej klatki jako kontekst (nietłumaczone, niebilingowane) —
  krótkie kwestie tłumaczone z sensem sąsiednich bloków.
- **Stabilizacja odczytów** nad ruchomą/zajętą grafiką: podobny lub brudniejszy odczyt przejmuje
  tłumaczenie istniejącego bloku zamiast tworzyć nowy; ocena jakości odczytu (symbole,
  wielkie litery w środku słowa, cyfry w słowach) — śmieć nie wypiera poprawnego tekstu.
- Pamięć zgubionych bloków (10 s), fragmenty jednego wiersza łączone spacją, linie o różnej
  wysokości nie sklejają się (podpowiedź „Tab" vs data), filtr artefaktów z ikon (`sc.@ß`).
- Najechany element menu (rośnie w grze) skaluje dymek w miejscu — bez odtwarzania i skoków;
  zmiana tła pod napisem (hover) przelicza kolory dopiero po dwóch zgodnych przebiegach.
- Deterministyczne dopasowanie rozmiaru czcionki (koniec naprzemiennego duży/mały).

### Wygląda lepiej

- Łatka w trybie zakrywania to **rozmyta kopia tła** spod napisu, wyłącznie pod boxem oryginału,
  z miękkimi krawędziami; dłuższe tłumaczenie wystaje poza nią czytelne dzięki konturowi.
- **Kontur czcionki w kolorze z gry** (próbkowanie trzech tonów: tło / tekst / obwódka).
- Bloki wieloliniowe: wiersze tłumaczenia na wysokości wierszy oryginału.
- Zakrywanie zawsze w rozmiarze oryginału (ręczny rozmiar dotyczy panelu/napisów), czcionka
  kurczy się najwyżej do 85% oryginału.
- Odświeżone okno aplikacji: spójny ciemny motyw, wskaźnik stanu live.

### Narzędzia dev

- `OcrLab` (zrzut klatki + warianty preprocessingu OCR), manifest PerMonitorV2 dla narzędzi
  (bez niego kadr z okna 4K@150% był ucięty), tryb diagnostyczny aplikacji
  (`GTO_AUTOLIVE`, `GTO_DIAG_CAPTURABLE`).

## [0.2.1] — 2026-08-06

### Gry ze statycznym obrazem (dialogi „co 2 kwestie")

- **Czułość na małe zmiany**: każda komórka siatki z realną zmianą jasności budzi
  przetwarzanie — krótkie linijki dialogów (kilka komórek) nie są już pomijane
  (dawny próg 2% siatki wymagał ~26 zmienionych komórek).
- **Powtórka po czknięciu OCR**: gdy silnik OCR zgubi tekst na niezmienionej scenie,
  pętla sama wymusza do 2 ponownych przebiegów — przegapiona kwestia odzyskuje się
  w niecałą sekundę (wcześniej przepadała na zawsze, bo statyczna scena nie budziła pętli).
- **Pełny przebieg bezpieczeństwa co 4 s** na scenach bez ruchu — łapie zmiany zbyt
  subtelne dla siatki jasności.
- Profil uniwersalny: próg 0 i 6 analiz/s.

### Wtapianie tłumaczeń w oryginał (tryb „Na oryginale (zakrywa)")

- Łatka tłumaczenia maluje się **prawdziwym kolorem tła gry** spod tekstu (próbkowanie
  dwóch kolorów bloku: znaków i tła) — na oknach dialogowych i tooltipach wygląda
  jak natywny napis, nie naklejka.
- Poprawny kolor czcionki także dla **ciemnego tekstu na jasnych oknach** (visual novele).
- Bez dymkowych rogów i paddingu; 3 px zapasu na krawędzie antyaliasingu oryginału.
- Czcionka jednoliniowych napisów **kurczy się do pola oryginału** (polski bywa ~20%
  dłuższy) zamiast rozpychać łatkę po interfejsie gry.
- Gwarancja kontrastu: kolor tekstu musi odstawać od tła łatki, inaczej czerń/biel.

## [0.2.0] — 2026-08-06

Pierwsze publiczne wydanie z kompletnym trybem live.

### Tryb live (Etapy 8–11)

- Automatyczne tłumaczenie wybranego okna gry: tanie wykrywanie zmian (siatka jasności),
  OCR wycinka zmian z upscalingiem, dymki pozycjonowane na tekście oryginału.
- Auto-rozmiar czcionki z wysokości linii OCR, krój czcionki per profil (Georgia dla PoE2),
  tło dymków Ciemne/Delikatne/Brak, położenie Pod/Na oryginale, kolor tekstu próbkowany
  z oryginału (kolory rzadkości przedmiotów), fade-in.
- Strategia napisów („Napisy na dole") jako alternatywa dla dymków przy oryginale.
- Wykrywanie ruchu sceny po MOCNYCH zmianach pikseli + bezpiecznik maksymalnej pauzy.
- Histereza stylu bloków (rozmiar/pozycja/kolor trzymają się między przebiegami OCR).
- Ikona w zasobniku, blokada drugiej instancji (mutex), profile gier z auto-detekcją.

### Stabilizacja live na podstawie diagnozy na żywym Path of Exile 2

- **Okres łaski bloków**: czknięcie Windows OCR (pusty wynik na niezmienionej scenie)
  nie zdejmuje już całej nakładki — koniec migania; blok znika po serii nieobecności,
  natychmiast przy cięciu sceny albo gdy nowy tekst przejmie jego miejsce.
- **Szybsza kadencja**: wymuszone przetwarzanie co 600 ms (reakcja ~0,3–0,7 s).
- **Rekalibracja progu ruchu** pod izometryczne kamery (0,35 → 0,12 mocnych zmian).
- Cięcie sceny oceniane po szczycie zmian od ostatniego przebiegu (bez „duchów" po teleporcie).

### Poprawki z audytu przedwydaniowego

- Zmiana ustawień w trakcie trybu live nie zabija już po cichu pętli tłumaczenia.
- Ctrl+Shift+H niezawodnie ukrywa nakładkę także w trybie live (i nie działa „odwrotnie").
- Atomowe zapisy `settings.json` i słownika użytkownika (crash nie kasuje danych);
  chwilowa blokada pliku słownika nie wymazuje już jego zawartości.
- Opłacone tłumaczenie z DeepL zawsze trafia do cache, nawet gdy operacja została
  w międzyczasie anulowana (kontrola kosztów).
- Naprawiony wyścig przebudowy pipeline'u (tryb prywatny obowiązuje bez luk).
- Czytelny błąd zamiast surowego wyjątku przy odpowiedzi przechwyconej przez proxy/captive portal.
- Obowiązkowe zastrzeżenie wyświetlane przy pierwszym uruchomieniu i dostępne z okna głównego.
- Artefakt CI zrównany z paczką wydania (embedded PDB, komplet dokumentów i licencji).

## [0.1.0] — 2026-08-06

MVP (Etapy 0–7): tłumaczenie zaznaczonego regionu (Ctrl+Shift+T), Windows OCR,
grupowanie linii w bloki, słowniki (globalny/profilowe/użytkownika), cache SQLite,
DeepL z kluczem w DPAPI, tryb prywatny i cache-only, panel wyniku i nakładka
click-through, licznik zużycia API, pakowanie portable win-x64.
