# Wizja produktu — GameTranslatorOverlay

## Cel

Gracz ma móc czytać tekst po polsku bez przerywania rozgrywki i przełączania się
do osobnego tłumacza. GameTranslatorOverlay rozpoznaje angielski tekst z obrazu
Windows OCR, tłumaczy go i wyświetla w zewnętrznej nakładce.

Rozwijamy wspólny silnik dla różnych gier: przygodowych, fabularnych, logicznych,
strategicznych i innych tytułów z tekstem na ekranie. Escape Academy jest bieżącym
przypadkiem pomiarowym i pierwszą grą z korpusem tekstów; Path of Exile 2 pozostaje
jednym z testowanych tytułów z dodatkowym profilem i słownikiem. Jakość zależy od
przechwytywania i czytelności obrazu, więc uniwersalny zakres projektu nie oznacza
potwierdzonej zgodności z każdą grą.

## Dla kogo

- Dla graczy potrzebujących tłumaczenia dialogów, zadań, menu i opisów przedmiotów.
- Dla osób, które chcą tłumaczyć tylko trudny fragment jednym skrótem.
- Dla graczy budujących własny słownik i poprawiających nazwy lub sformułowania.

Aplikacja jest portable dla Windows 10 2004+ i Windows 11. Wydanie zawiera .NET;
nie wymaga Pythona, CUDA ani lokalnego modelu AI. Narzędzia deweloperskie, w tym narzędzie
korpusu gry, nie są w paczce — uruchamia się je ze źródeł (.NET 10 SDK).

## Co działa obecnie

| Funkcja | Obecne działanie |
|---|---|
| Ręczne tłumaczenie | Ctrl+Shift+T, zaznaczenie regionu, wynik w panelu lub nakładce |
| Automatyczny live | obserwacja wybranego okna, OCR zmian i okresowe ponowne sprawdzanie; start/stop skrótem Ctrl+Shift+L na aktywnej grze; stary napis znika razem z oryginałem, także na teksturowanym tle (porównanie pikseli pola z ostatnim odczytem) |
| Komunikaty w grze | brak klucza, limit, brak sieci, Cache-only, start/stop live, gra na pełnym ekranie (zalecane okno bez ramki) — krótki pasek w nakładce, bez tekstu z ekranu |
| Prezentacja | bloki przy oryginale (pod tekstem albo zakrywające go), albo pasek napisów na dole; w live w trybie zakrywania napis w stylu gry: litery oryginału wypełniane tłem z otoczenia, polski tekst z kolorem, konturem, cieniem, wysokością liter i linią bazową napisu gry, krój z profilu (Escape Academy: dołączony Lexend Deca) |
| Korpus gry | osobne narzędzie offline czyta teksty z plików gry według recepty w profilu (dziś Escape Academy) i może je przetłumaczyć z wyprzedzeniem do lokalnej bazy; aplikacja przyciąga do nich odczyty OCR (pomyłki OCR, inne zawinięcie, początek kwestii dialogu), odrzuca śmieci OCR i w trybie zakrywania czeka z kwestią pisaną literami do jej końca |
| Dostawcy tłumaczeń | DeepL, Azure AI Translator, Google, Claude, serwer LLM zgodny z OpenAI (także lokalny; gotowy adres DeepSeek bez rozumowania), Mock |
| Własne słownictwo | edycja słownika, ręczne poprawki, import i eksport; terminy wewnątrz zdań (glosariusz DeepL, podpowiedzi dla modeli językowych), liczba mnoga, zakres „Etykieta” |
| Spójność dialogu | kontekst poprzednich linii; modele językowe widzą swoje wcześniejsze tłumaczenia i płeć postaci gracza |
| Jakość wyniku | lokalna kontrola (pusty wynik, liczby, brak tłumaczenia, „rozgadany” wynik), jedno ponowienie dla modeli językowych |
| Pomiar szybkości | panel „Szybkość” z „Zmiana → napis”, mediana i p90, raport bez tekstu z gry |
| Kontrola użycia | lokalne wyniki, deduplikacja i rezerwacje znaków przed API |
| Prywatność | lokalny OCR, Cache-only i prywatny cache w pamięci |

Ostatnie wydanie to 0.6.0 ([historia zmian](../CHANGELOG.md)): tłumaczenie trzyma się napisu
w ruchu kamery (śledzenie liter między odczytami OCR, odświeżanie łatki z Windows Graphics Capture,
także w grze na pełnym ekranie bez PrintWindow); zmierzone na nagraniach gry w MotionLab, w grze na
żywo jeszcze nie oglądane. Wydanie 0.5.0: stary napis znika razem
z oryginałem, korpus gry z osobnym narzędziem offline (dopasowanie odczytów OCR, tłumaczenie
z wyprzedzeniem), napis w stylu gry w trybie zakrywania i wstrzymanie kwestii pisanej literami.
Wydanie 0.4.0 dało szybszy live, komunikaty w grze, skrót live, pamięć dialogu i postać gracza
dla modeli językowych, kontrolę jakości i lepszy słownik. Linia 0.3 dodała wybór dostawcy
tłumaczeń i kontekst dialogu, a z poprawek live m.in. odrzucanie starych odpowiedzi, lokalne
usuwanie przykrytych napisów, potwierdzanie kolejnych odczytów i dokładniejsze pozycjonowanie.
Zmiany 0.5.0 sprawdzono testami, powtórkami sesji przez pipeline, w SceneReplay i — wygląd —
na zapisanych klatkach 4K (OverlayPreview); w grze na żywo była tylko krótka sesja Escape
Academy z korpusem, przed poprawkami etykiet, dialogu, odrzucania śmieci OCR i wyglądu
([checklista sprawdzenia w grze](QUALITY_CHECK.md)). Nie oznacza to zakończenia prac nad
jakością w ruchu.

## Przepływ danych

1. Użytkownik wybiera okno gry albo zaznacza region.
2. PrintWindow/GDI przechwytuje obraz; systemowy OCR rozpoznaje go lokalnie.
3. Aplikacja grupuje i normalizuje tekst. W live sprawdza również jego aktualność. Gdy aktywny
   profil ma lokalny korpus gry, odczyt jest przyciągany do znanego tekstu gry (także z błędami
   OCR), a śmieci OCR są odrzucane.
4. Wynik wybierany jest według priorytetu: **ręczna poprawka → słownik → cache → API**;
   z korpusem cache jest szukany pod tekstem z korpusu.
5. Brakujący tekst trafia do wybranego dostawcy (domyślnie DeepL), jeżeli pozwala na to
   tryb pracy i limit użycia. Modele językowe dostają też nazwę gry, pasujące terminy słownika
   i swoje poprzednie tłumaczenia z tej sesji; DeepL — glosariusz ze słownika.
6. Aktualne tłumaczenie pojawia się w nakładce przepuszczającej kliknięcia; w trybie zakrywania
   na łatce, która zastępuje litery oryginału tłem z otoczenia.

Do dostawcy trafia wyłącznie tekst, nigdy obraz. Przy dopasowaniu do korpusu może to być pełne
zdanie z korpusu, którego część dopiero pojawia się na ekranie. Aplikacja nie czyta plików gry —
korpus tworzy osobne narzędzie offline uruchamiane przez użytkownika
([ADR-014](TECHNOLOGY_DECISIONS.md)). W live stary wynik może uzupełnić
cache, ale po wykrytym unieważnieniu sceny nie powinien wrócić do nakładki.
Cache-only wyłącza wysyłanie brakujących tłumaczeń; Mock sprawdza przepływ bez
prawdziwego tłumaczenia. Windows Graphics Capture pozostaje możliwością na przyszłość.

## Jak rozumiemy jakość

Najważniejsza jest przewidywalność: czytelny napis we właściwym miejscu, który
pozostaje, dopóki jest potrzebny, i znika po zmianie treści. Krótsze opóźnienie ma
wartość razem ze stabilnością i kontrolą liczby zapytań.

Każdą poprawkę sprawdzamy na powtarzalnej scenie przed i po zmianie, a następnie
w grze. Oddzielamy czas rozpoznania, usunięcia starej treści, gotowości nowego wyniku
i faktyczny wygląd nakładki. Wynik lokalnego Mocka nie jest pomiarem DeepL. Wygląd nakładki
porównujemy na tych samych zapisanych klatkach gry (OverlayPreview), a dopasowanie do korpusu —
na prawdzie syntetycznej przez Windows OCR, powtórkach sesji z kopii cache i kontroli na innej
grze (CorpusEval).
Liczba testów i scenariusze kontrolne są opisane w [TESTING.md](TESTING.md).

## Profile i słowniki

Profile ułatwiają dobór ustawień, terminologii i wyglądu. Rdzeń pozostaje wspólny; gra bez
osobnego profilu korzysta z ustawień ogólnych. W zestawie są profile PoE2 (ze słownikiem)
i Escape Academy (krój napisów `overlay.fontFamily` i recepta korpusu dla narzędzia offline).
Specyfika gry jest danymi w profilu: czytnik plików jest wspólny dla rodziny formatów (dziś
Unity TextAsset), a dopasowanie do korpusu — dla wszystkich gier; kolejne usprawnienia silnika
nie powinny wymagać rozpoznania konkretnego tytułu.

## Kierunki do rozważenia

- Zachowanie stałego interfejsu podczas ruchu kamery, przy usuwaniu nieaktualnego tekstu świata.
- Śledzenie położenia znanego napisu między odczytami OCR.
- Zakrywanie na ruchomym i gęsto teksturowanym tle: łatka dopasowana do bieżącego tła między
  odczytami OCR (dziś miękka i nieruchoma do następnego odczytu).
- Wierniejszy krój gry: kursywa, szerokość, osobny krój dla rodzajów tekstu w profilu.
- Automatyczny dobór tempa pracy do menu, dialogu i ruchu.

To kierunki dalszych pomiarów, nie wdrożone funkcje. Automatyczne wydzielanie
tooltipów, osobny tryb historii i wyjaśnianie treści przez LLM także nie są obecnie
funkcjami aplikacji; model językowy może być wyłącznie wybranym dostawcą tłumaczeń
(opcjonalnie, bez dołączania modelu do paczki — [ADR-013](TECHNOLOGY_DECISIONS.md)).
Szczegóły i historia prac: [ROADMAP.md](ROADMAP.md).

## Granice działania

Aplikacja nie modyfikuje gry, nie czyta jej pamięci ani plików, nie wstrzykuje kodu i nie
wysyła do niej klawiszy lub kliknięć. Skróty sterują wyłącznie tłumaczem. Teksty z plików gry
odczytuje wyłącznie osobne narzędzie offline — na polecenie użytkownika, przy wyłączonej grze,
tylko do odczytu i z odmową dla gier z anti-cheatem lub online ([ADR-014](TECHNOLOGY_DECISIONS.md)).
OCR działa lokalnie; klucz API jest przechowywany z ochroną DPAPI i używany
wyłącznie przy połączeniu z dostawcą. Zasady: [SECURITY.md](SECURITY.md),
[PRIVACY.md](PRIVACY.md).

Obsługujemy okna i okna bez ramki (zalecane); wyłączny pełny ekran pozostaje poza zakresem —
gdy gra zajmuje cały monitor i nie daje się przechwycić jako okno, aplikacja o tym ostrzega.
Silny ruch, słaby kontrast, ozdobne czcionki i przechwytywanie wymagające zrzutu
ekranu mogą pogorszyć rezultat. Projekt nie gwarantuje zgodności z regulaminem
każdego tytułu i nie jest powiązany z twórcami gier.
