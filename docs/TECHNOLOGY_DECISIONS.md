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
zamiast odczytu z błędami OCR — ten sam tekst, który jest na ekranie.
