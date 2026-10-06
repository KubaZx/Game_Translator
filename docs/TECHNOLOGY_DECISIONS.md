# Decyzje technologiczne

Zapis podjętych decyzji w formacie mini-ADR: **kontekst → decyzja → uzasadnienie →
odrzucone alternatywy**. Decyzje są wiążące dla MVP; zmiana wymaga nowego wpisu, nie
cichej edycji. Architektura, do której się odnoszą: [ARCHITECTURE.md](ARCHITECTURE.md).

## ADR-001: .NET 10 (LTS) + WPF

**Kontekst.** Aplikacja desktopowa Windows-only: przezroczysta nakładka click-through
nad grą, globalne skróty, integracja z Win32 (style okien `WS_EX_*`) i WinRT
(Windows.Media.Ocr, Windows.Graphics.Capture). Ma być stabilnie i długo utrzymywalnie.

**Decyzja.** .NET 10 (LTS, SDK 10.0.300), C#, **WPF**. Wsparcie: Windows 10 2004+
i Windows 11.

**Uzasadnienie.** WPF to najdojrzalszy stos okienkowy .NET: pełna, przewidywalna kontrola
nad natywnym oknem (WndProc, style rozszerzone potrzebne nakładce), per-monitor DPI przez
manifest, ogrom udokumentowanych rozwiązań na dokładnie nasze problemy (overlay, hotkeys).
LTS = przewidywalny cykl wsparcia dla projektu rozwijanego etapami.

**Odrzucone alternatywy.**
- **WinUI 3** — słabsze i mniej przewidywalne wsparcie scenariuszy „dziwnych okien"
  (click-through, no-activate, tool window); historycznie problemy poza MSIX; młodszy
  ekosystem. Dla aplikacji, której sercem jest nietypowe okno, to złe ryzyko.
- **Avalonia** — cross-platform nic nam nie daje (WinRT OCR i capture są Windows-only),
  a płacilibyśmy warstwą abstrakcji nad oknem tam, gdzie potrzebujemy gołego Win32.

## ADR-002: TFM `net10.0-windows10.0.19041.0`, aplikacja unpackaged/portable

**Kontekst.** Potrzebujemy projekcji WinRT (`Windows.Media.Ocr`,
`Windows.Graphics.Capture`) i prostej dystrybucji dla graczy.

**Decyzja.** TFM aplikacji: **`net10.0-windows10.0.19041.0`**. Aplikacja **unpackaged,
portable** (dotnet publish win-x64) — bez MSIX.

**Uzasadnienie.** Windowsowy TFM z wersją SDK 10.0.19041 daje projekcje WinRT wprost
z .NET, bez pakowania w MSIX. 19041 = Windows 10 2004, nasza minimalna wersja systemu.
Portable .exe to najniższy próg wejścia: pobierz, rozpakuj, uruchom — bez konta
dewelopera, certyfikatów i store'a. Dane aplikacji i tak trzymamy w
`%LOCALAPPDATA%\GameTranslatorOverlay`, więc brak instalatora nic nie psuje.

**Odrzucone alternatywy.**
- **MSIX** — dodaje podpisywanie, tożsamość pakietu i tarcie przy dystrybucji poza
  store'em; nie potrzebujemy żadnego API wymagającego tożsamości pakietu.
- **Czysty `net10.0-windows`** (bez wersji SDK) — brak projekcji WinRT; OCR i capture
  wymagałyby ręcznych interopów.

## ADR-003: OCR systemowy Windows.Media.Ocr

**Kontekst.** Twarde ograniczenie projektu: **zero lokalnych modeli AI** — bez Ollamy,
LLM, PaddleOCR/EasyOCR/Tesseract, Pythona, CUDA, Dockera. Użytkownik ma pobrać program
i grać, nie budować środowisko ML.

**Decyzja.** Wyłącznie **`Windows.Media.Ocr.OcrEngine`** (systemowy OCR Windows),
opakowany w `WindowsOcrProvider` za interfejsem `IOcrProvider`.

**Uzasadnienie.** Jedyny OCR spełniający ograniczenia: wbudowany w system, zero
dystrybuowanych binariów i modeli, zero zależności natywnych, działa lokalnie (tekst nie
opuszcza komputera na etapie OCR). Interfejs `IOcrProvider` izoluje resztę aplikacji od
tej decyzji. Znane koszty, które akceptujemy i dokumentujemy: (1) wymaga zainstalowanego
pakietu językowego Windows — brak pakietu obsługujemy czytelnym komunikatem z instrukcją
doinstalowania; (2) brak per-słowo confidence w API — filtr śmieci działa na tekście.

**Odrzucone alternatywy.**
- **Tesseract** — natywne binaria + pliki traineddata w dystrybucji; jakość na tekstach
  z gier (stylizowane fonty, tła) wymaga strojenia; łamie ducha „zero dodatkowych modeli".
- **PaddleOCR / EasyOCR** — wprost zakazane ograniczeniami (Python/lokalne modele/CUDA).
- **OCR chmurowy** — wysyłałby screenshoty poza komputer; twarda zasada projektu mówi:
  do sieci idzie wyłącznie rozpoznany tekst, nigdy obraz.

## ADR-004: Capture przez GDI w MVP; Windows Graphics Capture później (tryb live)

**Kontekst.** MVP (Manual Region Mode) potrzebuje zrzutu regionu ekranu lub okna na
żądanie (po skrócie klawiszowym). Tryb live (Etap 8) będzie potrzebował ciągłego strumienia
klatek 3–6 fps.

**Decyzja.** MVP: **GDI** — `CopyFromScreen`/BitBlt dla regionu ekranu, `PrintWindow`
z `PW_RENDERFULLCONTENT` dla okna + fallback na crop ekranu. **Windows Graphics Capture
(WGC) planowane na Etap 8** (tryb live). Exclusive fullscreen poza zakresem projektu
(udokumentowane ograniczenie; działa okno i borderless fullscreen).

**Uzasadnienie.** Do jednorazowych zrzutów regionu GDI w zupełności wystarcza i jest
radykalnie prostsze: brak sesji capture, brak pool-a klatek, brak cyklu życia Direct3D.
MVP tłumaczy na żądanie, nie strumieniuje. WGC ma sens dopiero przy pętli live (wydajny
strumień klatek, poprawne przechwytywanie okien akcelerowanych sprzętowo) — i na to jest
zaplanowane. Rozdzielenie decyzji zmniejsza ryzyko MVP.

**Odrzucone alternatywy.**
- **WGC od razu w MVP** — dużo dodatkowej złożoności bez zysku dla trybu ręcznego;
  wymaga też żółtej ramki systemowej/uprawnień w starszych buildach Windows.
- **DXGI Desktop Duplication** — jeszcze niższy poziom (Direct3D), nadmiarowy dla regionów.
- **Hooking / wstrzykiwanie do gry** — kategorycznie zakazane zasadami projektu
  (program w 100% pasywny).

## ADR-005: SQLite przez Microsoft.Data.Sqlite, bez EF

**Kontekst.** Cache tłumaczeń: proste tabele klucz→wartość z metadanymi, priorytety
źródeł (korekta > profil > cache globalny), migracje schematu, tryb prywatny (in-memory).

**Decyzja.** **`Microsoft.Data.Sqlite`** i ręczny SQL. Migracje przez
**`PRAGMA user_version`**. Bez Entity Framework.

**Uzasadnienie.** Schemat jest mały i stabilny — ORM nie ma tu czego mapować. Goły ADO
daje pełną kontrolę nad zapytaniami (indeksy pod lookup po znormalizowanym tekście),
mniejszy rozmiar publikacji, szybszy start, zero magii przy migracjach: `user_version`
+ sekwencyjne skrypty to całość mechanizmu.

**Odrzucone alternatywy.**
- **EF Core** — koszt (rozmiar, złożoność, migracje EF) niewspółmierny do 2–3 tabel.
- **LiteDB / pliki JSON jako cache** — brak SQL-owych indeksów i transakcji przy rosnącym
  cache; SQLite jest standardem de facto dokładnie do tego zastosowania.

## ADR-006: Klucze API w Windows DPAPI

**Kontekst.** Klucz DeepL musi przetrwać restart aplikacji, ale nie może trafić do repo,
kodu, logów ani leżeć na dysku plaintextem.

**Decyzja.** **DPAPI** (`ProtectedData`, zakres **CurrentUser**), zaszyfrowany plik w
`%LOCALAPPDATA%\GameTranslatorOverlay`. W developmencie: zmienne środowiskowe/User
Secrets. Klucz nigdy nie jest logowany.

**Uzasadnienie.** DPAPI jest wbudowane w Windows, bez dodatkowych zależności i bez
zarządzania własnym kluczem szyfrującym; zakres CurrentUser wiąże sekret z kontem
użytkownika. Dla desktopowej aplikacji single-user to właściwy poziom ochrony.

**Odrzucone alternatywy.**
- **Plaintext w settings.json** — każdy proces/backup czyta klucz.
- **Własne szyfrowanie (AES z kluczem w kodzie)** — teatr bezpieczeństwa; klucz
  szyfrujący leży obok danych.
- **Windows Credential Manager** — porównywalna ochrona, ale bardziej toporne API;
  DPAPI prościej testować i kontrolować lokalizację danych.

## ADR-007: DeepL jako pierwszy provider + MockTranslationProvider

**Kontekst.** Potrzebny provider tłumaczeń EN→PL wysokiej jakości oraz możliwość pracy
i testowania bez klucza/sieci (CI nie ma sekretów).

**Decyzja.** Interfejs **`ITranslationProvider`** z dwiema implementacjami:
**`DeepLTranslationProvider`** (pierwszy realny provider) i **`MockTranslationProvider`**
(deterministyczny, do testów i pracy bez klucza). DeepL: `api-free.deepl.com` dla kluczy
z sufiksem `:fx`, `api.deepl.com` dla pro; `/v2/translate` z batchem do 50 tekstów;
`/v2/usage` do testu połączenia i licznika; obsługa 403 (zły klucz), 456 (limit
wyczerpany), 429 (rate limit z ograniczonym retry), timeoutów i braku sieci.

**Uzasadnienie.** DeepL daje bardzo dobrą jakość EN→PL, prosty REST, darmowy tier
(500 tys. znaków/mies.) idealny na start oraz endpoint usage — wprost pod naszą kontrolę
kosztów. Mock odcina sieć w testach jednostkowych i CI oraz pozwala rozwijać cały pion
bez klucza. Interfejs utrzymuje drzwi otwarte na kolejnych providerów bez ruszania Core.

**Odrzucone alternatywy.**
- **Google/Azure Translate jako pierwszy** — nic nie blokuje ich w przyszłości (to tylko
  kolejna implementacja interfejsu), ale na start DeepL wygrywa jakością PL i prostotą.
- **Lokalny model tłumaczący** — zakazany twardymi ograniczeniami projektu.

## ADR-008: Logowanie przez Serilog

**Kontekst.** Aplikacja desktopowa u użytkownika końcowego — diagnostyka musi opierać się
na logach plikowych; jednocześnie obowiązują zasady prywatności (tryb prywatny bez treści
tłumaczeń, klucze API nigdy).

**Decyzja.** **Serilog** z sinkiem plikowym (rolling) w
`%LOCALAPPDATA%\GameTranslatorOverlay`. W trybie prywatnym bez treści tłumaczeń; kluczy
API nie loguje się nigdy. Stack trace tylko do logu — użytkownik dostaje czytelny
komunikat.

**Uzasadnienie.** Standard de facto w .NET: structured logging, dojrzały rolling-file,
konfiguracja poziomów per źródło, naturalna integracja z `Microsoft.Extensions.Hosting`
(ADR o DI/hostingu przyjęty w briefie jako element stacku).

**Odrzucone alternatywy.**
- **Microsoft.Extensions.Logging + własny file sink** — pisanie i utrzymywanie rolling
  sinka to koło, które Serilog już wynalazł.
- **NLog** — równorzędny funkcjonalnie; Serilog wybrany za structured logging i prostszą
  konfigurację w kodzie. Decyzja gustu, ale podjęta — mieszanie frameworków logowania
  to najgorszy scenariusz.

## ADR-009: Testy w xUnit

**Kontekst.** Testowalna jest cała logika Core (normalizacja, glossary, priorytety cache,
kontrola kosztów) i Infrastructure (SQLite, mapowanie błędów DeepL na Mocku). Testy muszą
chodzić w CI bez pulpitu i sekretów.

**Decyzja.** **xUnit** w `tests/GameTranslatorOverlay.Core.Tests` i
`tests/GameTranslatorOverlay.Infrastructure.Tests`. Testy wymagające pulpitu Windows
(OCR na żywo, nakładka, skróty globalne) są wyłącznie ręczne — opisane w
`docs/MANUAL_TESTING.md`.

**Uzasadnienie.** Domyślny standard nowego ekosystemu .NET: czyste zarządzanie cyklem
życia (konstruktor/`IDisposable` zamiast atrybutów setup/teardown), `Theory`/`InlineData`
pod tabele przypadków normalizacji i glossary, pierwszorzędne wsparcie `dotnet test` w CI.

**Odrzucone alternatywy.**
- **NUnit / MSTest** — pełnowartościowe, ale bez przewagi; xUnit ma najświeższą konwencję
  i najlepszą prasę w nowych projektach .NET.

**Dopisek 2026-10-06.** Trzeci projekt testów, `tests/GameTranslatorOverlay.CorpusTool.Tests`
(xUnit), obejmuje narzędzie z ADR-014 wyłącznie na danych syntetycznych, bez plików gier.
Zachowanie zależne od pulpitu i czasu (nakładka, sesja live z łatką i wstrzymaniem kwestii)
mierzą narzędzia dev SceneReplay i OverlayPreview — to pomiary opisane w `docs/ROADMAP.md`,
nie testy uruchamiane przez `dotnet test`.

## ADR-010: CI na GitHub Actions, windows-latest

**Kontekst.** Build wymaga Windows (TFM windowsowy, WPF). Repo na GitHubie. CI nie może
wymagać sekretów (klucz DeepL) ani pulpitu.

**Decyzja.** **GitHub Actions**, runner **windows-latest**: restore → build → test
(testy używają Mock providera, zero sekretów). Artefakt Release (`dotnet publish
win-x64`) budowany na tag lub manualnie.

**Uzasadnienie.** CI naturalnie zintegrowane z repo, darmowe dla projektu tej skali,
windowsowe runnery z zainstalowanym SDK .NET. Rozdzielenie „każdy push = build+test"
od „tag = artefakt Release" trzyma pętlę deweloperską szybką, a wydania powtarzalne.

**Odrzucone alternatywy.**
- **Azure DevOps / AppVeyor** — dodatkowa usługa i konfiguracja bez przewagi nad Actions
  przy repo na GitHubie.
- **Testy z realnym DeepL w CI** — wymagałyby sekretu w CI i paliłyby limit; pokrycie
  zapewnia Mock, realny provider testowany ręcznie.

## ADR-011: SemVer od 0.1.0

**Kontekst.** Projekt rozwijany etapami (0–12), wydania portable z artefaktów CI;
profile gier deklarują `minAppVersion` — potrzebna porównywalna, przewidywalna numeracja.

**Decyzja.** **Semantic Versioning**, start od **0.1.0**. Wersje 0.x = API i formaty
mogą się zmieniać; 1.0.0 dopiero przy spełnieniu Definition of Done pierwszej wersji.

**Uzasadnienie.** SemVer daje jednoznaczną semantykę dla `minAppVersion` w profilach
i czytelny sygnał dojrzałości. Start od 0.1.0 uczciwie komunikuje status i zostawia
miejsce na wydania etapowe (0.2, 0.3, …) po drodze do MVP i dalej.

**Odrzucone alternatywy.**
- **Start od 1.0.0** — kłamałby o stabilności formatów (profile/słowniki/cache jeszcze
  mogą ewoluować).
- **CalVer / build number** — brak semantyki zgodności, której wymaga `minAppVersion`.

## ADR-012: Jeden assembly Infrastructure zamiast osobnego Providers.DeepL

**Kontekst.** Klasyczna pokusa: wydzielić każdego providera tłumaczeń do osobnego
projektu (`GameTranslatorOverlay.Providers.DeepL` itd.), „bo kiedyś będzie ich więcej".

**Decyzja.** **Celowo NIE ma** osobnego assembly na DeepL — `DeepLTranslationProvider`
(i Mock) mieszkają w `src/GameTranslatorOverlay.Infrastructure`.

**Uzasadnienie.** Mniej assembly = prościej: krótszy build, prostszy solution i publish,
mniej krawędzi wersjonowania między projektami. Granicę architektoniczną wyznacza
**interfejs `ITranslationProvider` w Core**, nie fizyczny podział na pliki DLL — dodanie
kolejnego providera to nowa klasa w Infrastructure, a gdyby providerów naprawdę przybyło,
wydzielenie projektu będzie mechaniczne (kod już stoi za interfejsem).

**Odrzucone alternatywy.**
- **Osobny projekt per provider** — struktura na wyrost przy jednym realnym providerze;
  YAGNI.
- **Provider w App** — mieszałby HTTP z warstwą UI i uniemożliwił testowanie bez WPF.

## ADR-013: Kolejni dostawcy tłumaczeń, w tym opcjonalne modele językowe (2026-09-29)

**Kontekst.** DeepL Free (500 tys. znaków miesięcznie) szybko się wyczerpuje w trybie live.
Słownik działał tylko dla tekstów będących w całości terminem, więc nazwy wewnątrz dłuższych
zdań tłumaczono niespójnie. Część graczy chce też tłumaczyć bez wysyłania tekstu do internetu.

**Decyzja.** Obok DeepL (nadal domyślnego) i Mocka dochodzą: **Azure AI Translator**
(2 mln znaków miesięcznie w planie F0), **Google Cloud Translation**, **model językowy przez
API zgodne z OpenAI** (OpenAI, OpenRouter, Groq, lokalne Ollama/LM Studio) oraz **Claude**.
Wszystkie są opcjonalne i wybierane w oknie aplikacji; każdy ma osobny klucz w DPAPI.
Dostawcy HTTP dzielą pętlę `ProviderHttp`; dostawcy modeli językowych implementują
`IContextualTranslationProvider` i dostają nazwę gry oraz terminy słownika z tłumaczonej partii.

- **Claude** korzysta z oficjalnego SDK Anthropic (`Anthropic`, MIT) — SDK śledzi zmiany API
  (beta fallback przy odmowie, structured outputs, effort) lepiej niż ręczny HTTP.
  Dostaje osobny `HttpClient`.
- **Serwery zgodne z OpenAI** obsługujemy ręcznym HTTP z minimalnym zestawem pól
  (`model`, `messages`): serwery różnią się obsługą `temperature`, `max_tokens`
  i `response_format`, a SDK jednego dostawcy nie gwarantuje zgodności z pozostałymi.
  HTTPS jest wymagany poza serwerem na tym komputerze.

**Stosunek do ADR-007 i DoD pkt 1.** Paczka nadal **nie zawiera, nie instaluje i nie wymaga**
żadnego modelu AI, Pythona ani CUDA — domyślna konfiguracja działa jak dotąd. Odrzucony
w ADR-007 „lokalny model tłumaczący” oznaczał model dostarczany i wymagany przez aplikację;
tutaj użytkownik może jedynie wskazać własny, osobno uruchomiony serwer. Decyzja rozszerza
zakres produktu; **zatwierdzona przez właściciela projektu 2026-09-29**.

**Odrzucone alternatywy.**
- **Model dołączony do paczki** — łamie DoD pkt 1 i zwiększa paczkę o gigabajty.
- **Jedno SDK OpenAI dla wszystkich modeli** — lokalne serwery obsługują różne podzbiory API.
- **Słowniki DeepL (glossary API)** — wymagają zarządzania słownikami po stronie DeepL;
  możliwe w przyszłości; kontekst dla modeli językowych nie wymaga stanu na serwerze.

**Dopisek 2026-10-06 — opcjonalne pola zapytania przypisane do serwera.** Pomiar z 2026-10-05
(`GTO Diagnostics/20261005-natywne-spolszczenie`) pokazał, że przy minimalnym zestawie pól
DeepSeek V4.1 Flash (`deepseek-flash`) zawsze myśli: mediana 0,96 s na linię, paczka 5 linii
7–20 s; z myśleniem wyłączonym mediana 0,71 s. Dlatego zasada „tylko `model` i `messages`”
zostaje **domyślna**, ale serwer może dostać cztery opcjonalne pola: `thinking {type}`,
`reasoning_effort`, `max_tokens` i `response_format {type}`. Pola są w ustawieniu
`llmServerOptions` z hostem (`host[:port]`, jak `llmKeyHost`) i trafiają do zapytania
**wyłącznie** pod ten host — po zmianie adresu na inny serwer zapytanie jest takie jak dotąd
(test kontraktu porównuje treść zapytania bez opcji z dotychczasowym kształtem). Wartości są
krótkimi słowami (litery, cyfry, `_`, `-`, `.`); inne są pomijane. Gotowe ustawienia
(`TranslationProviderCatalog.LlmPresets`): **DeepSeek** — `https://api.deepseek.com/v1`,
model `deepseek-flash`, `thinking: disabled`; **Ollama** — `reasoning_effort: none`; OpenAI
i LM Studio bez pól. Adapter czyta też `usage` odpowiedzi (tokeny wejścia, wyjścia,
rozumowania i trafienia cache prefiksu) do logu — same liczby, bez treści. Łatka pomiarowa
oparta na globalnych zmiennych środowiskowych `GTO_LLM_*` nie weszła do kodu; narzędzia
deweloperskie (ProviderEval, CorpusTool) przyjmują te pola jako opcje wiersza poleceń.
Odrzucone: zmienne środowiskowe w aplikacji (globalne dla każdego serwera i niewidoczne
w ustawieniach). Poza zakresem: `temperature` (modele rozumujące części dostawców odrzucają je
błędem 400, a tłumaczenie go nie wymagało).

## ADR-014: Odczyt tekstów z plików gry przez osobne narzędzie offline (2026-10-05)

**Kontekst.** Badanie z 2026-10-05 (`GTO Diagnostics/20261005-natywne-spolszczenie`) zmierzyło,
że znany tekst (cache, słownik) daje gotowy napis po ok. 0,33 s, a nowy tekst po 0,50–0,59 s
z DeepL i 1,0–1,3 s z modelem językowym. W Escape Academy teksty interfejsu, dialogi i napisy
leżą w plikach gry jako czytelne tabele (ok. 7,6 tys. unikalnych tekstów EN). Znajomość tekstów
źródłowych pozwala przetłumaczyć je z wyprzedzeniem (bez presji czasu, z kontekstem sceny
i mówcy) i poprawiać błędy OCR przez dopasowanie odczytu do znanego zdania. Dotąd
`PRIVACY.md` obiecywał, że program nie czyta plików gry, a `SECURITY.md` ograniczał działanie
do obrazu z ekranu.

**Decyzja.** Dopuszczamy **odczyt plików gry wyłącznie przez osobne narzędzie offline**
(`tools/`), uruchamiane jawnie przez użytkownika. Nakładka nadal nie czyta plików gry — dostaje
tylko dane wytworzone przez narzędzie (lokalny korpus i wpisy pamięci tłumaczeń).

Warunki, wszystkie obowiązkowe:

1. **Tylko odczyt.** Narzędzie nigdy nie zapisuje, nie zmienia, nie blokuje ani nie tworzy
   plików w folderze gry (otwiera je z `FileShare.ReadWrite | FileShare.Delete`).
2. **Gra wyłączona.** Narzędzie odmawia pracy, gdy proces gry działa.
3. **Bez gier online i z anti-cheatem.** Twarda blokada: foldery EasyAntiCheat / BattlEye,
   podpisane lub zaszyfrowane kontenery (`.sig`, szyfrowany indeks pak/utoc) oraz jawna lista
   wykluczeń, w tym Path of Exile 1/2 (regulamin GGG zakazuje aplikacji, które „interact with
   the game or game files”).
4. **Bez obchodzenia zabezpieczeń.** Żadnych kluczy AES, deszyfrowania ani inżynierii wstecznej
   kodu gry. Czytamy tylko kontenery, które da się otworzyć bez klucza.
5. **Bez dodatkowego ruchu sieciowego.** Narzędzie niczego nie pobiera (np. bibliotek
   dekompresji); jedyny możliwy ruch to świadomie wybrane przez użytkownika tłumaczenie
   korpusu u wskazanego dostawcy.
6. **Tylko lokalnie.** Korpus i jego tłumaczenia trafiają do `%LOCALAPPDATA%\GameTranslatorOverlay`
   (albo `eval/private/` przy pomiarach). Nigdy do repozytorium ani do paczki aplikacji —
   teksty gier i ich tłumaczenia są chronione prawem autorskim.
7. **Tryb prywatny.** Gdy w aplikacji jest włączony tryb prywatny, wpisy z wyprzedzeniem nie są
   zapisywane do bazy.
8. **Specyfika gry jako dane.** Które tabele i kolumny są tekstem dla gracza, opisuje „recepta”
   w profilu gry (`profiles/<id>/`); kod czytnika jest wspólny dla rodziny formatów (np. Unity
   TextAsset), nie pisany pod konkretną grę.

Niezmienione: zakaz modyfikacji plików gry, instalowania paczek i modów, wstrzykiwania DLL,
hooków, czytania pamięci procesu i wysyłania inputu do gry (`SECURITY.md`).

**Prywatność dostawców.** Przy tłumaczeniu korpusu u dostawcy obowiązuje świadomy wybór:
DeepL API Free przetwarza teksty „przez ograniczony czas” do trenowania modeli, DeepL Pro nie;
DeepSeek przechowuje dane w ChRL. Do API trafia wyłącznie tekst, nigdy obraz.

**Odrzucone alternatywy.**
- **Odczyt plików przez samą nakładkę w trakcie gry** — miesza warstwy, ryzyko blokady plików
  przy aktualizacji i konfliktu z anti-cheatem; korzyść ta sama co z narzędzia offline.
- **Paczki językowe instalowane w grze** — modyfikacja plików gry (zakaz), a 6 z 9 zainstalowanych
  gier offline ma już oficjalny polski.
- **Dodatki w procesie gry (BepInEx, UE4SS)** — wstrzykiwanie DLL (zakaz).

**Zatwierdzona przez właściciela projektu 2026-10-05.**

**Dopisek 2026-10-06 — tłumaczenie korpusu z wyprzedzeniem (`CorpusTool translate`).** Jedyny
ruch sieciowy narzędzia (pkt 5) to jawne polecenie `translate --provider deepl|llm` u dostawcy
wybranego przez użytkownika; `extract` i kod czytników nie odwołują się do sieci ani do
dostawców (test na źródłach), a dostawcy powstają w `Infrastructure` z kluczami wyłącznie ze
zmiennych środowiskowych (`GTO_DEEPL_KEY`, `GTO_LLM_*`), nigdy z DPAPI użytkownika. Wpisy
trafiają do `cache.db` z profilem gry, prawdziwą nazwą dostawcy i znacznikiem `src=corpus`
w kolumnie `context`; ręczne korekty, wpisy zatwierdzone i terminy słownika nie są nadpisywane.
Pkt 7 jest egzekwowany: włączony tryb prywatny w `settings.json` (albo nieczytelny plik
ustawień) = odmowa zapisu, zanim cokolwiek zostanie wysłane. Baza nie może leżeć
w repozytorium (poza `eval/private/`), a Mock zapisuje wyłącznie do jawnie wskazanej bazy.

**Dopisek 2026-10-06 (2) — korpus w nakładce.** Nakładka czyta wyłącznie plik
`<folder danych>\corpus\<id aktywnego profilu>.corpus.jsonl` (dane z `extract`), nigdy pliki gry,
i tylko do dopasowania odczytu OCR do znanego tekstu oraz klucza cache. Bez aktywnego profilu albo
bez pliku zachowanie jest takie jak przed tą decyzją. Do dostawcy może trafić tekst z korpusu
zamiast odczytu z błędami OCR (sprostowanie w dopisku (3): nie zawsze jest to tekst widoczny
na ekranie w całości).

**Dopisek 2026-10-06 (3) — poprawki po recenzji kroku 2.**
- *Co idzie do dostawcy.* Wcześniejsze zdanie „ten sam tekst, który jest na ekranie” było
  nieprecyzyjne. Przy przyciągnięciu do korpusu wysyłany jest pełny tekst wpisu korpusu —
  także gdy na ekranie widać dopiero jego część (napis wypisywany litera po literze, odczyt
  ucięty przez OCR, zdanie złożone z kolejnych wierszy). Część zdania, której gracz jeszcze
  nie widział, może więc trafić do dostawcy wcześniej niż na ekran. `PRIVACY.md` opisuje to
  wprost; plik korpusu i baza tłumaczeń nadal nie są nigdzie wysyłane.
- *Pkt 2 (gra wyłączona).* Proces gry jest sprawdzany **przed** jakimkolwiek odczytem w folderze
  gry, także przed skanem nagłówków `.pak`/`.utoc`; lista procesów obejmuje pliki `*.exe`
  z katalogu głównego gry.
- *Pkt 3 (anti-cheat).* Katalog główny gry to folder `<…>\steamapps\common\<gra>` (także
  `Epic Games\<gra>`, `GOG Galaxy\Games\<gra>`) albo najwyższy folder nad `--game-dir`
  zawierający plik gry z `processNames` profilu. Gdy `--game-dir` wskazuje podfolder, znaczniki
  anti-cheata, pliki `.sig` i zaszyfrowane kontenery są szukane w całym katalogu głównym.
- *Pkt 1 i 6 (zapis).* `--out`, `--stats` (`extract`) oraz `--cache`, `--stats` (`translate`)
  nie mogą leżeć w folderze gry, w jej katalogu głównym, pod `steamapps\common` (i analogicznie
  w bibliotekach Epic/GOG) ani w folderze zawierającym plik gry z `processNames` profilu.

**Dopisek 2026-10-06 (4) — inne użycia korpusu w nakładce.** Dopisek (2) ograniczał korpus
w nakładce do dopasowania odczytu OCR i klucza cache. Od rund 2026-10-06 (4)–(6) nakładka używa
tego samego pliku (nadal wyłącznie danych z `extract`, nigdy plików gry) także do: odrzucania
szumu OCR w bramce live (`TranslationPipeline.ShouldTranslateLive` — taki odczyt nie idzie do
dostawcy ani na nakładkę), wspólnej tożsamości bloku nakładki dla kolejnych odczytów tej samej
kwestii (`CorpusIdentity`) i — w trybie „Na oryginale (zakrywa)” — wstrzymania kwestii pisanej
literami (`IsCorpusPrefix`, ADR-017). Przyciąganie początku linii dialogu albo napisów
(`OcrEditDistance.Prefix`) może wysłać do dostawcy pełny tekst wpisu korpusu, gdy na ekranie
jest dopiero jego początek — to przypadek z dopisku (3). Żadne z tych użyć nie wysyła pliku korpusu
i nie dodaje ruchu sieciowego; bez pliku korpusu aktywnego profilu nic się nie zmienia.
Doprecyzowanie pkt 6: `%LOCALAPPDATA%\GameTranslatorOverlay` jest miejscem domyślnym; `--out`,
`--data-dir` i `--cache` mogą wskazać inny folder lokalny poza grą (dopisek (3)) i poza
repozytorium (z wyjątkiem `eval/private/`), ale nakładka czyta korpus wyłącznie z folderu
`corpus` w swoich danych (`%LOCALAPPDATA%\GameTranslatorOverlay\corpus`) — korpusu zapisanego
gdzie indziej aplikacja nie widzi.

## ADR-015: Łatka z wypełnionych liter zamiast rozmytej kopii tła (2026-10-06)

**Kontekst.** W trybie „Na oryginale (zakrywa)” nakładka kładła pod tłumaczeniem rozmytą kopię
tła spod boxu oryginału (`CoverPatchHost`). Galeria OverlayPreview na 4 klatkach 4K z Escape
Academy (stan po rundzie 2026-10-06 (4)) dała 13 wad, m.in.: łatka ciemniejsza od tła
o 11–19 poziomów i pusta smuga przy krótszym tekście, prześwitujący cień oryginału (6 px za
boxem łatka kryła w 0,67), ikony klawiszy zjadane przez łatkę, kolorowe plamy w teksturze łatki,
tłumaczenie 0,54–0,58× wysokości oryginału i biały kontur pod białym tekstem dialogu (próbnik
wziął kolor szuflady dialogu #2F3940 za kolor tekstu). Ocena gracza: napisy „wyglądają mocno
średnio”.

**Decyzja.** Dla każdego bloku `GlyphCoverBuilder` (Core, `Vision/`) liczy z przechwyconej klatki
łatkę RGBA: maskę liter (odchylenie od tła szacowanego z pierścienia wokół boxu i filtry top-hat,
doprecyzowane progiem 50% kontrastu) oraz obwódkę (kontur, cień, antyaliasing). Piksele pod
poszerzoną maską są wypełniane kolorami otoczenia (push-pull); krycie ma tylko miejsce liter,
reszta łatki jest przezroczysta. Z tych samych pikseli mierzony jest styl: kolor tekstu, kontur,
cień, linia bazowa i wysokość liter (`InkProfile`), wyrównanie i ikony klawiszy, które zostają
nietknięte. Łatka powstaje w `LiveTranslationSession` w tle, równolegle z tłumaczeniem; przy
niezmienionym podpisie pola jest używana ponownie, przy ruchomym tle jest miękka. Rysuje ją App
(`GameTextElement`, tekst z geometrii). Gdy łatka nie powstanie, zostaje łatka poprzedniego
przebiegu, a bez niej dawna rozmyta kopia tła.

**Uzasadnienie (pomiar, runda 2026-10-06 (5)).** Na tych samych klatkach i ustawieniach:
wysokość liter polskiego tekstu nad linią bazową 0,96–1,10× oryginału (wcześniej 0,54–0,58×),
linia bazowa w 0–1 px od oryginału, kolor dialogu #8C8D8E przy ok. #8F9293 w grze, czarny kontur
5–6 px z cieniem jak w grze. Poza pikselami liter łatka jest przezroczysta (test jednostkowy),
więc nie ma ciemnej plamy ani smugi. Koszt na klatce 4K (32 wątki): budowa 1,7–24 ms na blok,
cała klatka nowych napisów równolegle 2,3–16 ms, ponowne użycie 0,03–0,11 ms. Przy tłumaczeniu
u dostawcy czekanie na łatkę po tłumaczeniu ma w SceneReplay medianę 0 ms (najwyżej 23,6 ms),
przy tłumaczeniu lokalnym dochodzi czas budowy (do 16 ms na klatkę 4K). Scenariusze regresji
SceneReplay bez zmian poza szumem (np. capture → aktualizacja 227–233 → 228–241 ms).

**Odrzucone alternatywy.**
- **Rozmyta kopia tła pod boxem (dotychczas)** — zmierzone wady z kontekstu; zostaje tylko jako
  zapas, gdy łatka nie powstanie.
- **Budowa łatki przy rysowaniu, na wątku UI** — do 24 ms na blok doszłoby do 5–21 ms układu WPF
  na klatkę; w tle, równolegle z dostawcą, czekanie ma medianę 0 ms.

**Konsekwencje i ryzyka.** Bitmapy i łatki zostają w pamięci komputera — do dostawcy nadal idzie
wyłącznie tekst (ADR-003). Łatka jest liczona z klatki OCR, więc na ruchomym tle między przebiegami
(ok. 0,6 s) wypełnione litery mogą odstawać od tła; miękka łatka nadal jest nieruchoma. Na gęstej
teksturze w kolorze liter maska może objąć tło albo nie powstać. W powiększeniu 1:1 widać smugi
w miejscu ogonków liter i na granicy dwóch płaskich teł. Budowa łatki dialogu w 4K alokuje
ok. 21 MB (linie ≥ 90 px) do ok. 76 MB (linie 36–89 px) pamięci (`docs/BENCHMARKS.md`). Ruchome tło, smugi i pamięć budowy łatki są na liście kierunków dalszych prac
w `docs/ROADMAP.md`.
Sprawdzone na klatkach Escape Academy i w SceneReplay; nie w grze na żywo ani na innych grach.

## ADR-016: Dołączony krój z licencją OFL i krój z profilu gry (2026-10-06)

**Kontekst.** Nakładka rysowała napisy wyłącznie czcionkami systemowymi, domyślnie Segoe UI.
W galerii przed rundą 2026-10-06 (5) cienki Segoe UI odstawał od grubego, zaokrąglonego kroju
Escape Academy; porównanie glifów na klatkach wskazało rodzinę Lexend. Krój gry zwykle nie jest
zainstalowany w systemie gracza.

**Decyzja.** Aplikacja dołącza krój **Lexend Deca** (Regular, Medium, SemiBold, Bold; wersja
1.007; SIL Open Font License 1.1) jako zasoby WPF (`src/GameTranslatorOverlay.App/Fonts`), bez
instalacji w systemie. Pliki są niezmienione i nie są sprzedawane osobno (warunek 1 OFL); tekst
licencji trafia obok programu jako `licenses/LexendDeca-OFL.txt`, wpis jest w
`THIRD-PARTY-NOTICES.md`. Krój wskazuje profil gry polem `overlay.fontFamily` (nazwa, nie
ścieżka, do 64 znaków). Nowe ustawienie kroju „Jak w grze (krój z profilu)” (`auto`) jest
domyślne; dotychczasowy domyślny Segoe UI przechodzi na nie jeden raz (`overlayFontRevision`),
inny jawnie wybrany krój zostaje. Bez profilu albo bez pola — Segoe UI. Nazwa jest szukana
najpierw wśród krojów dołączonych (`OverlayFonts`), potem w systemie; krój bez dostępnych
grubości zastępuje Segoe UI. Grubość dobiera nakładka do gęstości tuszu oryginału (ten sam
tekst EN zrasteryzowany w każdej grubości), jedną dla bloków w tym samym stylu.

**Uzasadnienie.** Krój zgodny z rodziną gry, z grubością z pomiaru: w galerii po rundzie
2026-10-06 (5) jedno menu miało grubości od Normal do SemiBold, po rundzie (6), z jedną grubością
na styl — całe menu Medium, dialog Normal. Koszt: cztery pliki TTF po ok. 79 KB (razem ok.
315 KB) w assembly aplikacji i jednorazowe rozgrzanie krojów przy starcie live (ok. 212 ms na
wątku UI). Licencja OFL pozwala dołączać krój do programu, także komercyjnego.

**Odrzucone alternatywy.**
- **Tylko czcionki systemowe** — gracz musiałby sam znaleźć i zainstalować krój gry.
- **Krój z plików gry** — nakładka nie czyta plików gry (ADR-014), a krój w plikach gry jest
  objęty licencją gry, nie naszą.
- **Instalacja kroju w systemie przez aplikację** — zmiana systemu poza folderem programu,
  sprzeczna z paczką portable (ADR-002).

**Konsekwencje i ryzyka.** Kursywa, kerning i szerokość kroju gry nie są odwzorowane, a profil ma
jeden krój dla wszystkich rodzajów tekstu (szeryfowy napis Escape Academy też dostaje Lexend) —
kierunek dalszych prac w `docs/ROADMAP.md`. Grubość z gęstości tuszu bywa za cienka, gdy obrys
gry wchodzi w lico liter. Kolejny dołączony krój wymaga pliku w `App/Fonts`, wpisu na liście
krojów dołączonych w `OverlayFonts`, tekstu licencji kopiowanego obok programu (wpis w
`GameTranslatorOverlay.App.csproj`) i wpisu w `THIRD-PARTY-NOTICES.md` — tylko krój, którego
licencja pozwala go rozpowszechniać z programem.

## ADR-017: Kwestia pisana literami czeka w trybie zakrywania (2026-10-06)

**Kontekst.** Od rundy 2026-10-06 (4) odczyt będący jednoznacznym początkiem jednej linii dialogu
albo napisów korpusu (od 12 liter i 3 słów, do 10% pomyłek OCR) od razu dostaje tłumaczenie
całej kwestii (klucz = cała linia): mniej zapytań (CorpusEval `typing`, odczyty OCR dopisywane
co 3 znaki: 5 188 → 2 214 tekstów do dostawcy) i napis wcześniej. Łatka z ADR-015 zakrywa jednak
tylko litery widoczne w chwili odczytu. Razem dawało to polski tekst na dopisywanych angielskich
literach (OverlayPreview: polski tekst 2,95× szerokości łatki nad widoczną częścią linii),
a krótki początek kwestii poniżej progów migał niepełnym tłumaczeniem z dostawcy.

**Decyzja.** W trybie „Na oryginale (zakrywa)” poza paskiem napisów
(`LiveSessionOptions.HoldTypingPrefixes`) nowy blok, którego odczyt jest niedokończoną kwestią
korpusu, nie jest tłumaczony ani pokazywany, dopóki tekst rośnie. Niedokończona kwestia
(`TranslationPipeline.IsCorpusPrefix`) to przyciągnięcie początku linii, przybliżenie całej linii
z brakiem co najmniej 3 i co najmniej 7% liter wpisu albo krótki początek poniżej progów
przyciągania (`CorpusSnapper.StartsSpokenLine`: co najmniej 4 litery lub cyfry, dosłowny
początek linii dialogu lub napisów dłuższej o co najmniej 3 litery lub cyfry, sam nie jest
tekstem korpusu). Sesja prosi o kolejny odczyt pola i pokazuje pełne tłumaczenie, gdy odczyt
przestaje być początkiem kwestii, gdy liczba liter nie rośnie przez 0,9 s
(`TypingPrefixSettleTime`) albo po 8 s (`TypingPrefixHoldLimit`).
Blok już wyświetlany pod tym samym kluczem nie jest wstrzymywany. W trybach z tekstem obok
oryginału zostaje wczesne pełne tłumaczenie z rundy (4).

**Uzasadnienie (pomiar, SceneReplay `typing` / `typing-nohold`).** Dwie syntetyczne linie
wpisywane po 35 ms na znak z pauzą 450 ms po interpunkcji, korpus syntetyczny, OCR ze skryptu.
Bez wstrzymywania: pełne tłumaczenie widoczne od ok. 4,2 s przed końcem pisania, na linię
6 aktualizacji z polskim tekstem w trakcie pisania i 2 mignięcia niepełnego tłumaczenia,
razem 4 zapytania do dostawcy.
Ze wstrzymywaniem: 0 aktualizacji z polskim tekstem w trakcie pisania, 0 niepełnych tłumaczeń,
napis ok. 0,1–0,3 s po ostatniej literze, 2 zapytania (pełne linie). Scenariusze regresji SceneReplay
po zmianie (displayed, local-occlusion, reading-jitter, hud-motion, ocr-timing, stale-junk,
stale-dim) dają oczekiwane wyniki.

**Odrzucone alternatywy.**
- **Wczesne pełne tłumaczenie także w trybie zakrywania** (stan z rundy (4)) — pomiar wyżej;
  zostaje w trybach, w których tłumaczenie nie zakrywa oryginału.
- **Tłumaczenie każdego widocznego początku osobno** (stan sprzed rundy (4)) — CorpusEval
  `typing` na liniach korpusu dopisywanych co 3 znaki: 15 945 tekstów do dostawcy wobec 6 689
  z przyciąganiem początku, a każdy dłuższy odczyt to nowe tłumaczenie.
- **Łatka na całą przyszłą szerokość kwestii** — nakładka nie zna szerokości okna dialogu
  (kierunek dalszych prac w `docs/ROADMAP.md`).
- **Łatka odświeżana z każdej przechwyconej klatki, żeby zakrywać dopisywane litery** — wymaga
  dopasowania łatki do tła między przebiegami OCR (kierunek dalszych prac w `docs/ROADMAP.md`);
  nie zrobione i nie zmierzone.

**Konsekwencje i ryzyka.** W trybie zakrywania tłumaczenie kwestii pojawia się dopiero po końcu
pisania, a wstrzymany początek nie idzie do dostawcy. Gra z pauzą w środku kwestii dłuższą niż
0,9 s pokaże pełne tłumaczenie w tej pauzie, a dopisywane potem litery wyjdą spod łatki do
następnego odczytu. Tekst spoza korpusu, który jest początkiem kwestii korpusu, czeka 0,9 s.
Nie sprawdzone w oknie gry na żywo.
