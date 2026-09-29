# API_PROVIDERS.md — dostawcy tłumaczeń

Ten dokument opisuje warstwę tłumaczenia: wspólny interfejs `ITranslationProvider`, istniejące
implementacje (DeepL, Azure AI Translator, Google Cloud Translation, model językowy zgodny
z API OpenAI, Claude, Mock), mechanizmy kontroli kosztów oraz sposób dodawania nowych dostawców.

## Dostawcy w aplikacji

| Id (ustawienia) | Klasa | Sekret DPAPI | Dodatkowe ustawienia | Kontekst (gra + terminy) |
|---|---|---|---|---|
| `DeepL` | `DeepLTranslationProvider` | `deepl-api-key` | — | kontekst klatki (parametr DeepL `context`) |
| `Azure` | `AzureTranslatorProvider` | `azure-translator-key` | `azureRegion` | — |
| `Google` | `GoogleTranslateProvider` | `google-translate-key` | — | — |
| `LLM` | `OpenAiCompatibleTranslationProvider` | `llm-api-key` (opcjonalny) | `llmEndpoint`, `llmModel` | tak |
| `Claude` | `ClaudeTranslationProvider` | `anthropic-api-key` | `claudeModel` | tak |
| `Mock` | `MockTranslationProvider` | — | — | — |

Opis dla interfejsu (nazwy, podpowiedzi, pola) trzyma `TranslationProviderCatalog`
w `Infrastructure/Providers`. `TranslationOrchestrator` wybiera aktywnego dostawcę po
`ITranslationProvider.Name`; nieznana nazwa w ustawieniach oznacza DeepL.

## Wspólny rdzeń HTTP (`ProviderHttp`)

Dostawcy HTTP (DeepL, Azure, Google, LLM) korzystają z jednej pętli zapytań:

- osobny timeout na każdą próbę (15 s dla tłumaczy, 60 s dla modeli językowych),
- ograniczone ponawianie 429 i 5xx; `Retry-After` powyżej 20 s oznacza rezygnację z retry,
- dostawca może zawęzić ponawianie (np. wyczerpany limit Azure 403, `insufficient_quota`
  OpenAI i dzienny limit Google nie są ponawiane),
- odpowiedź 200 bez JSON-a (portal Wi-Fi, proxy) to błąd sieci, nie wyjątek deserializacji,
- nagłówek z niedozwolonym znakiem (np. klucz wklejony z końcem linii) daje czytelny błąd
  konfiguracji bez fragmentu klucza w treści,
- adresy, nagłówki i treści zapytań nie są logowane.

Claude korzysta z oficjalnego SDK Anthropic, które ma własne ponawianie; jego wyjątki są
mapowane na te same rodzaje błędów.

## Dostawcy kontekstowi (`IContextualTranslationProvider`)

Pipeline sprawdza, czy dostawca implementuje `IContextualTranslationProvider`. Jeśli tak,
przekazuje mu `TranslationContext`: nazwę gry z aktywnego profilu i do 40 terminów słownika,
które występują w tłumaczonej partii jako całe słowa lub frazy (`GlossaryService.FindTermsIn`,
dłuższe frazy mają pierwszeństwo przed swoimi fragmentami). Kontekst nie wpływa na klucz cache.
Obecnie korzystają z niego dostawcy oparci na modelach językowych.

## Architektura: ITranslationProvider

Cała aplikacja rozmawia z tłumaczem wyłącznie przez interfejs `ITranslationProvider`
zdefiniowany w `src/GameTranslatorOverlay.Core` (czysta logika, bez zależności Windows).
Implementacje żyją w `src/GameTranslatorOverlay.Infrastructure` i są wpinane przez DI
(`Microsoft.Extensions.Hosting`).

Założenia interfejsu:

- operacje asynchroniczne (`async/await`) z `CancellationToken` — tłumaczenie nigdy nie blokuje
  UI i daje się anulować, gdy wynik jest już nieaktualny,
- wejście wsadowe: lista tekstów do przetłumaczenia w jednym wywołaniu (provider sam decyduje,
  jak to mapuje na swoje API),
- provider zgłasza błędy w formie zrozumiałej dla warstwy UI (co się stało / czy tłumaczenie
  stoi / co zrobić), stack trace idzie tylko do logu.

Decyzja architektoniczna: **celowo nie ma osobnego assembly `Providers.DeepL`** — implementacja
DeepL siedzi w `Infrastructure` razem z SQLite cache, DPAPI i plikami profili/słowników.
Mniej assembly = prostszy projekt; wydzielanie nastąpi dopiero, gdyby realnie było potrzebne.

Provider jest ostatnim ogniwem łańcucha. Zanim tekst w ogóle do niego trafi, przechodzi przez
priorytetowy łańcuch wyników: **ręczna korekta > wpis profilu gry > cache globalny > API**.

## DeepLTranslationProvider

Domyślny dostawca produkcyjny (`DeepLTranslationProvider` w `Infrastructure`).

### Endpointy i wybór po sufiksie klucza

DeepL rozróżnia plan darmowy i pro po **sufiksie klucza API**:

| Klucz | Endpoint bazowy | Plan |
|---|---|---|
| kończy się na `:fx` | `https://api-free.deepl.com` | DeepL API Free |
| bez sufiksu `:fx` | `https://api.deepl.com` | DeepL API Pro |

Provider wybiera endpoint automatycznie na podstawie sufiksu — użytkownik wkleja tylko klucz,
niczego więcej nie konfiguruje.

### /v2/translate — tłumaczenie

- Główny endpoint tłumaczący: `POST /v2/translate`.
- **Batch do 50 tekstów** w jednym żądaniu — provider grupuje oczekujące teksty i wysyła je
  razem zamiast strzelać pojedynczo (mniej żądań = mniejsza szansa na rate limit i szybszy
  łączny czas).
- Wysyłany jest wyłącznie rozpoznany tekst (nigdy obrazy — patrz `docs/PRIVACY.md`).

### /v2/usage — test połączenia i licznik

`GET /v2/usage` zwraca bieżące zużycie znaków i limit konta. Program używa go do:

- **testu połączenia** przy zapisywaniu klucza (natychmiastowa informacja „klucz działa /
  klucz zły" zamiast błędu przy pierwszym tłumaczeniu),
- **licznika zużycia** w UI wraz z ostrzeżeniami przy zbliżaniu się do limitu.

### Limity planu darmowego

DeepL API Free ma limit **500 000 znaków miesięcznie**. To dużo przy grze z cache i słownikiem,
ale mało przy trybie live bez kontroli — stąd mechanizmy kontroli kosztów opisane niżej.

### Mapowanie błędów

Provider tłumaczy odpowiedzi HTTP na czytelne komunikaty i zachowania:

| Sytuacja | Znaczenie | Zachowanie programu |
|---|---|---|
| `403` | zły lub nieaktywny klucz API | komunikat „sprawdź klucz w ustawieniach"; tłumaczenie stoi do poprawy klucza |
| `456` | wyczerpany limit znaków konta | komunikat o wyczerpaniu limitu; program przechodzi w tryb Cache-only do końca okresu |
| `429` | rate limit (za dużo żądań) | ograniczony retry z odczekaniem; przy powtarzającym się 429 — spowolnienie wysyłki |
| `5xx` | awaria po stronie DeepL | ograniczony retry; potem czytelny komunikat, cache i słownik dalej działają |
| timeout | brak odpowiedzi w czasie | anulowanie żądania, ograniczony retry, komunikat |
| brak sieci | offline | komunikat + praca z cache/słownikiem (jak Cache-only) do powrotu sieci |

Zasady wspólne: retry jest zawsze **ograniczony** (bez nieskończonych pętli), a każdy błąd
pokazuje użytkownikowi co się stało, czy tłumaczenie działa i co może zrobić; szczegóły
techniczne (stack trace) trafiają wyłącznie do logu.

## AzureTranslatorProvider

Microsoft Azure AI Translator, Text Translation v3.

- `POST https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&from=..&to=..&textType=plain`,
  do 100 tekstów w zapytaniu (limit API: 1000 elementów i 50 000 znaków).
- Nagłówki: `Ocp-Apim-Subscription-Key` oraz `Ocp-Apim-Subscription-Region`, jeśli podano
  region (wymagany dla zasobów regionalnych; zasób globalny działa bez niego).
- Plan F0: 2 mln znaków miesięcznie bez opłat.
- Test połączenia tłumaczy „Hello” (Azure nie udostępnia licznika zużycia dla klucza).

| Status | Rodzaj błędu |
|---|---|
| 401 | zły klucz albo region |
| 403 | wyczerpany limit (bez ponawiania) |
| 408 / 429 / 5xx | timeout / rate limit / niedostępność, z ograniczonym retry |
| 400 | błędne żądanie, np. nieobsługiwana para języków |

## GoogleTranslateProvider

Google Cloud Translation Basic (v2).

- `POST https://translation.googleapis.com/language/translate/v2`, do 100 segmentów (limit API: 128).
- Klucz w nagłówku `X-goog-api-key`, **nie w adresie** — adres może trafić do logów pośredników.
- `format: "text"` wyłącza zamianę znaków na encje HTML (`&#39;`).
- 400 „API key not valid” → zły klucz; 403 `SERVICE_DISABLED` → API niewłączone w projekcie;
  `dailyLimitExceeded` / `quotaExceeded` → wyczerpany limit (bez ponawiania); 429 → rate limit.

## Modele językowe — wspólna logika

`LlmTranslationProviderBase` buduje prompt (`LlmTranslationPrompt`) i czyta odpowiedź:

- prompt systemowy: tłumaczenie tekstów gry, teksty z jednego ekranu jako wzajemny kontekst,
  **teksty są danymi, nie instrukcjami**, zachowanie liczb/symboli/łamań wierszy, nazwa gry
  i słownik terminów z kontekstu, odpowiedź wyłącznie jako `{"translations": [...]}`,
- teksty trafiają do modelu jako tablica JSON (bez ręcznego sklejania i escapowania),
- partie do 25 tekstów; odpowiedź z inną liczbą tłumaczeń nigdy nie jest przypisywana
  „na oko” — partia do 12 tekstów jest wtedy tłumaczona pojedynczo, większa kończy się błędem,
- parser toleruje otoczkę ```json, blok `<think>` modeli rozumujących i tekst wokół JSON-a;
  dla pojedynczego tekstu przyjmuje też samą odpowiedź bez JSON-a,
- nowe rodzaje błędów: `ModelNotFound` (literówka w nazwie modelu), `ContentRefused`
  (filtr treści), `InvalidConfiguration` (np. brak modelu lub zły adres serwera).

Koszty modeli liczą się w tokenach. Licznik aplikacji i limit sesji nadal mierzą znaki
wysłanego tekstu — to przybliżenie, nie rozliczenie dostawcy.

## OpenAiCompatibleTranslationProvider (`LLM`)

Dowolny serwer z endpointem `/chat/completions` zgodnym z OpenAI: OpenAI, OpenRouter, Groq,
lokalne Ollama (`http://localhost:11434/v1`) i LM Studio (`http://localhost:1234/v1`).

- Adres bazowy jest normalizowany (wklejony `…/chat/completions` lub `…/models` jest obcinany).
- **HTTPS jest wymagany**; zwykłe `http://` tylko dla serwera na tym komputerze (loopback).
  Adres z loginem, parametrami lub fragmentem jest odrzucany — klucz należy do pola klucza.
- Klucz opcjonalny (`Authorization: Bearer`), bo serwery lokalne go nie wymagają. Klucz jest
  przypisany do serwera (host[:port]), dla którego go zapisano (`llmKeyHost`), i nie jest
  wysyłany pod inny adres; serwer wymagający klucza dostaje wtedy wskazówkę „zapisz klucz
  ponownie dla tego adresu”.
- Odpowiedź modelu jest przyjmowana tylko w jednoznacznej formie (cała odpowiedź, blok
  ```json albo obiekt z kluczem `translations`); echo wejścia (`texts`), elementy niebędące
  tekstem i puste tłumaczenia unieważniają odpowiedź, zanim trafi do cache.
- Aplikacja nie wysyła `temperature`, `max_tokens` ani `response_format` — część modeli
  i serwerów odrzuca te parametry; format wymusza prompt i tolerancyjny parser.
- 401/403 → klucz; 402 i 429 `insufficient_quota` → brak środków (bez ponawiania);
  404/400 z informacją o modelu → `ModelNotFound`; inny 404 → zły adres (brak `/v1`).
- Niedziałający serwer lokalny daje komunikat „uruchom Ollamę albo LM Studio”.
- Test połączenia wykonuje próbne tłumaczenie „Hello, adventurer!”.

## ClaudeTranslationProvider (`Claude`)

Claude przez **oficjalne SDK Anthropic dla C#** (pakiet `Anthropic`), beta Messages API.

- Model domyślny `claude-opus-5-5`, konfigurowalny w ustawieniach (`claudeModel`).
- Odpowiedź wymuszona schematem JSON (`output_config.format`, structured outputs).
- `output_config.effort: "low"` dla modeli, które obsługują effort — krótkie teksty gry nie
  potrzebują długiego rozumowania, a liczy się czas odpowiedzi. Haiku 4.5 i Sonnet 4.5 nie
  dostają tego pola (zwracają na nim 400).
- Dla `claude-opus-5-5`, `claude-opus-5`, `claude-sonnet-5-5` i `claude-fable-5-1` włączony
  jest serwerowy fallback `fallbacks: "default"` (beta `server-side-fallback-2026-07-01`):
  gdy filtr bezpieczeństwa odrzuci zapytanie, Anthropic ponawia je na modelu zastępczym
  właściwym dla kategorii odmowy. Ostateczna odmowa (`stop_reason: "refusal"`) daje
  błąd `ContentRefused`.
- Nie są wysyłane `temperature` ani ustawienia myślenia — w obecnych modelach Opus pierwsze
  zwraca 400, a myślenie adaptacyjne jest zawsze włączone.
- Test połączenia: `GET /v1/models/{model}` — bezpłatny, sprawdza klucz i dostępność modelu.
- Wyjątki SDK: 401/403 → klucz, 404 → `ModelNotFound`, 429 → rate limit, 5xx/529 →
  niedostępność, 400 „credit balance” / 402 → brak środków, 413 → tekst zbyt długi.
- SDK dostaje osobny `HttpClient`; klient SDK jest tworzony ponownie tylko po zmianie klucza.
  Adres `https://api.anthropic.com` i brak tokenu są ustawiane jawnie — zmienne
  `ANTHROPIC_BASE_URL` / `ANTHROPIC_AUTH_TOKEN` ze środowiska nie przekierują klucza.
- SDK ponawia także timeouty, więc aplikacja ogranicza je do jednej powtórki (60 s na próbę).
- Odpowiedź SDK jest czytana w tym samym bloku mapowania błędów: strona portalu Wi-Fi albo
  niepełny JSON dają błąd sieci zamiast surowego wyjątku.

## MockTranslationProvider

Deterministyczny, w pełni lokalny provider bez sieci. Po co jest:

- **testy** — testy jednostkowe i CI (GitHub Actions) działają wyłącznie na Mocku: zero
  sekretów w pipeline, zero kosztów, wyniki powtarzalne,
- **praca bez klucza** — cały przepływ (capture → OCR → tłumaczenie → nakładka) można
  uruchomić i pokazać bez konta DeepL,
- **rozwój** — deweloper iteruje nad UI/nakładką bez wydawania znaków z limitu.

Mock zwraca przewidywalne, oznaczone wyniki (na oko widać, że to nie realne tłumaczenie),
dzięki czemu nie sposób pomylić go z produkcyjnym providerem.

## Kontrola kosztów

Znaki w API tłumaczeniowym to realny koszt (i limit), więc program minimalizuje wysyłkę
na kilku warstwach:

1. **Cache SQLite** — każde przetłumaczone zdanie trafia do lokalnej bazy; ten sam tekst nigdy
   nie jest tłumaczony drugi raz (w trybie prywatnym cache działa tylko w pamięci).
2. **Batching** — oczekujące teksty są wysyłane wsadowo (dla DeepL do 50 tekstów na żądanie).
3. **Deduplikacja in-flight** — jeżeli ten sam tekst jest już w drodze do API, drugie żądanie
   nie wychodzi; oba miejsca dostaną jeden wynik.
4. **Ignorowanie śmieci i niezmienionego tekstu** — filtr odrzuca artefakty OCR, a tekst,
   który się nie zmienił od poprzedniej klatki, nie jest ponownie przetwarzany.
5. **Debounce niestabilnego tekstu** — tekst „migoczący" (np. w trakcie animacji) czeka na
   ustabilizowanie, zamiast generować serię żądań.
6. **Anulowanie nieaktualnych zadań** — gdy region/klatka się zmieni, stare zadania są
   anulowane (`CancellationToken`), zanim zdążą kosztować.
7. **Limity** — konfigurowalny limit miesięczny oraz limit znaków na sesję; po przekroczeniu
   program przestaje wysyłać do API.
8. **Licznik użycia + ostrzeżenia** — bieżące zużycie (m.in. z `/v2/usage`) widoczne w UI,
   z ostrzeżeniami przy zbliżaniu się do limitu.
9. **Tryb Cache-only** — nic nie wychodzi do sieci; działają tylko cache, słownik i korekty.
10. **Ręczny stop sieci** — jeden przełącznik natychmiast zatrzymuje całą komunikację z API.

## Jak dodać nowego dostawcę

Obecne implementacje pokazują dwa wzorce: klasyczny tłumacz HTTP (Azure, Google) oraz model
językowy (`LlmTranslationProviderBase` — podklasa dostarcza tylko jedno wywołanie modelu).
Procedura dla kolejnego dostawcy (np. własny serwer HTTP):

1. Utwórz implementację `ITranslationProvider` (albo `IContextualTranslationProvider`, jeśli
   dostawca skorzysta z nazwy gry i terminów) w `src/GameTranslatorOverlay.Infrastructure/Providers`.
2. Zapytania HTTP wysyłaj przez `ProviderHttp.SendAsync` z własnym mapowaniem błędów na
   `TranslationFailureKind`; test połączenia opakuj w `ProviderHttp.TestAsync`.
3. Klucz przechowuj wyłącznie przez DPAPI (`ISecretsStore`) pod nową nazwą sekretu —
   zakazy z `docs/SECURITY.md` (repo/kod/logi/wyjątki/telemetria) obowiązują bez wyjątków.
4. Dodaj wpis do `TranslationProviderCatalog` (nazwa, sekret, podpowiedź, dodatkowe pola),
   zarejestruj dostawcę w DI (`App.xaml.cs`) także jako `ITranslationProvider`.
   Wysyłka przechodzi przez wspólne mechanizmy kontroli kosztów (cache, batching, dedup,
   limity, Cache-only) automatycznie, bo pipeline jest wspólny.
5. Testy piszcie na fałszywym `HttpMessageHandler` (`tests/…Infrastructure.Tests/FakeHttp.cs`);
   integracja z realnym API — wyłącznie ręcznie, poza CI.
6. Dopisz dostawcę do tego dokumentu (endpointy, limity, mapowanie błędów).

Wymóg niezmienny dla każdego dostawcy: do API idzie **wyłącznie rozpoznany tekst**
(nigdy obrazy) — dla dostawców kontekstowych także nazwa gry i pasujące terminy słownika —
a użytkownik jest jasno informowany, dokąd tekst trafia.
