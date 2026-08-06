# API_PROVIDERS.md — dostawcy tłumaczeń

Ten dokument opisuje warstwę tłumaczenia: wspólny interfejs `ITranslationProvider`, istniejące
implementacje (DeepL, Mock), mechanizmy kontroli kosztów oraz sposób dodawania nowych dostawców.

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

Planowani dostawcy (Google, Azure Translator, OpenAI, CustomHttp) są **na roadmapie i nie są
zaimplementowani** — MVP zawiera wyłącznie DeepL i Mock. Gdy przyjdzie na nich czas, procedura:

1. Utwórz implementację `ITranslationProvider` w `src/GameTranslatorOverlay.Infrastructure`
   (osobny podfolder; bez nowego assembly, dopóki nie ma twardego powodu).
2. Zaimplementuj: tłumaczenie wsadowe z `CancellationToken`, test połączenia (odpowiednik
   `/v2/usage`), mapowanie błędów dostawcy na te same kategorie co DeepL (zły klucz / limit /
   rate limit / awaria / timeout / offline) z ograniczonym retry.
3. Klucz/sekret dostawcy przechowuj wyłącznie przez istniejący mechanizm DPAPI
   (`%LOCALAPPDATA%\GameTranslatorOverlay`) — zakazy z `docs/SECURITY.md` (repo/kod/logi/
   wyjątki/telemetria) obowiązują bez wyjątków.
4. Wepnij providera w DI i w ustawienia wyboru dostawcy; wysyłka musi przechodzić przez
   wspólne mechanizmy kontroli kosztów (cache, batching, dedup, limity, Cache-only).
5. Testy jednostkowe piszcie na poziomie kontraktu `ITranslationProvider` (bez sieci);
   integracja z realnym API — wyłącznie ręcznie, poza CI.
6. Dopisz dostawcę do tego dokumentu (endpointy, limity, mapowanie błędów).

Wymóg niezmienny dla każdego przyszłego dostawcy: do API idzie **wyłącznie rozpoznany tekst**
(nigdy obrazy), a użytkownik jest jasno informowany, że tekst opuszcza komputer.
