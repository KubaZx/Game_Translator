# Wizja produktu — GameTranslatorOverlay

## Cel

Gracz ma móc czytać tekst po polsku bez przerywania rozgrywki i przełączania się
do osobnego tłumacza. GameTranslatorOverlay rozpoznaje angielski tekst z obrazu
Windows OCR, tłumaczy go i wyświetla w zewnętrznej nakładce.

Rozwijamy wspólny silnik dla różnych gier: przygodowych, fabularnych, logicznych,
strategicznych i innych tytułów z tekstem na ekranie. Escape Academy jest bieżącym
przypadkiem pomiarowym; Path of Exile 2 pozostaje jednym z testowanych tytułów
z dodatkowym profilem i słownikiem. Jakość zależy od przechwytywania i czytelności
obrazu, więc uniwersalny zakres projektu nie oznacza potwierdzonej zgodności z każdą grą.

## Dla kogo

- Dla graczy potrzebujących tłumaczenia dialogów, zadań, menu i opisów przedmiotów.
- Dla osób, które chcą tłumaczyć tylko trudny fragment jednym skrótem.
- Dla graczy budujących własny słownik i poprawiających nazwy lub sformułowania.

Aplikacja jest portable dla Windows 10 2004+ i Windows 11. Wydanie zawiera .NET;
nie wymaga Pythona, CUDA ani lokalnego modelu AI.

## Co działa obecnie

| Funkcja | Obecne działanie |
|---|---|
| Ręczne tłumaczenie | Ctrl+Shift+T, zaznaczenie regionu, wynik w panelu lub nakładce |
| Automatyczny live | obserwacja wybranego okna, OCR zmian i okresowe ponowne sprawdzanie |
| Prezentacja | bloki przy oryginale lub z zakrywaniem, albo pasek napisów na dole |
| Dostawcy tłumaczeń | DeepL, Azure AI Translator, Google, Claude, serwer LLM zgodny z OpenAI (także lokalny), Mock |
| Własne słownictwo | edycja słownika, ręczne poprawki, import i eksport; dla modeli językowych także terminy wewnątrz zdań |
| Kontrola użycia | lokalne wyniki, deduplikacja i rezerwacje znaków przed API |
| Prywatność | lokalny OCR, Cache-only i prywatny cache w pamięci |

Ostatnie wydanie to 0.3.1 ([historia zmian](../CHANGELOG.md)); linia 0.3 dodaje wybór dostawcy
tłumaczeń i kontekst dialogu, a z poprawek live m.in. odrzucanie starych odpowiedzi,
lokalne usuwanie przykrytych napisów, potwierdzanie kolejnych odczytów i dokładniejsze
pozycjonowanie. Nie oznaczają zakończenia prac nad jakością w ruchu.

## Przepływ danych

1. Użytkownik wybiera okno gry albo zaznacza region.
2. PrintWindow/GDI przechwytuje obraz; systemowy OCR rozpoznaje go lokalnie.
3. Aplikacja grupuje i normalizuje tekst. W live sprawdza również jego aktualność.
4. Wynik wybierany jest według priorytetu: **ręczna poprawka → słownik → cache → API**.
5. Brakujący tekst trafia do wybranego dostawcy (domyślnie DeepL), jeżeli pozwala na to
   tryb pracy i limit użycia. Modele językowe dostają też nazwę gry i pasujące terminy słownika.
6. Aktualne tłumaczenie pojawia się w nakładce przepuszczającej kliknięcia.

Do dostawcy trafia wyłącznie tekst, nigdy obraz. W live stary wynik może uzupełnić
cache, ale po wykrytym unieważnieniu sceny nie powinien wrócić do nakładki.
Cache-only wyłącza wysyłanie brakujących tłumaczeń; Mock sprawdza przepływ bez
prawdziwego tłumaczenia. Windows Graphics Capture pozostaje możliwością na przyszłość.

## Jak rozumiemy jakość

Najważniejsza jest przewidywalność: czytelny napis we właściwym miejscu, który
pozostaje, dopóki jest potrzebny, i znika po zmianie treści. Krótsze opóźnienie ma
wartość razem ze stabilnością i kontrolą liczby zapytań.

Każdą poprawkę sprawdzamy na powtarzalnej scenie przed i po zmianie, a następnie
w grze. Oddzielamy czas rozpoznania, usunięcia starej treści, gotowości nowego wyniku
i faktyczny wygląd nakładki. Wynik lokalnego Mocka nie jest pomiarem DeepL.
Liczba testów i scenariusze kontrolne są opisane w [TESTING.md](TESTING.md).

## Profile i słowniki

Profile ułatwiają dobór ustawień i terminologii. Rdzeń pozostaje wspólny; gra bez
osobnego profilu korzysta z ustawień ogólnych. Profil PoE2 jest dodatkiem w zestawie,
a kolejne usprawnienia nie powinny wymagać rozpoznania konkretnego tytułu.

## Kierunki do rozważenia

- Zachowanie stałego interfejsu podczas ruchu kamery, przy usuwaniu nieaktualnego tekstu świata.
- Śledzenie położenia znanego napisu między odczytami OCR.
- Czytelniejsze zakrywanie tekstu na wzorzystym i animowanym tle.
- Automatyczny dobór tempa pracy do menu, dialogu i ruchu.

To kierunki dalszych pomiarów, nie wdrożone funkcje. Automatyczne wydzielanie
tooltipów, osobny tryb historii i wyjaśnianie treści przez LLM także nie są obecnie
funkcjami aplikacji; model językowy może być wyłącznie wybranym dostawcą tłumaczeń
(opcjonalnie, bez dołączania modelu do paczki — [ADR-013](TECHNOLOGY_DECISIONS.md)).
Szczegóły i historia prac: [ROADMAP.md](ROADMAP.md).

## Granice działania

Aplikacja nie modyfikuje gry, nie czyta jej pamięci, nie wstrzykuje kodu i nie
wysyła do niej klawiszy lub kliknięć. Skróty sterują wyłącznie tłumaczem.
OCR działa lokalnie; klucz API jest przechowywany z ochroną DPAPI i używany
wyłącznie przy połączeniu z dostawcą. Zasady: [SECURITY.md](SECURITY.md),
[PRIVACY.md](PRIVACY.md).

Obsługujemy okna i borderless; exclusive fullscreen pozostaje poza zakresem.
Silny ruch, słaby kontrast, ozdobne czcionki i przechwytywanie wymagające zrzutu
ekranu mogą pogorszyć rezultat. Projekt nie gwarantuje zgodności z regulaminem
każdego tytułu i nie jest powiązany z twórcami gier.
