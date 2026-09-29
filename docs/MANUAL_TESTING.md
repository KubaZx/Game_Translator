# MANUAL_TESTING.md — scenariusze testów ręcznych

Testy z tego dokumentu wymagają żywego pulpitu Windows (okna, fokus, skróty globalne,
prawdziwy OCR) i **nie** biegają w CI. Wykonuj je przed każdym wydaniem oraz po zmianach
w capture, OCR, nakładce lub obsłudze skrótów. Testy automatyczne: [docs/TESTING.md](TESTING.md).

Każdy scenariusz ma dopisek „Dotyczy od: Etap N" — wykonuj go dopiero, gdy dana
funkcja istnieje (roadmap w dokumentacji produktu).

## Procedura wspólna: logi i zgłaszanie błędów

Ta procedura obowiązuje w każdym scenariuszu — sekcja „Logi i zgłoszenie" przy scenariuszu
podaje tylko, czego szukać w logu.

**Zebranie logu:**

1. Odtwórz błąd i zapisz godzinę (co do minuty).
2. Otwórz folder logów: `%LOCALAPPDATA%\GameTranslatorOverlay\logs`
   (w Eksploratorze wklej ścieżkę w pasek adresu; pliki są rolowane — bierz najnowszy).
3. Skopiuj plik logu z czasu błędu. Nie edytuj go.
4. Uwaga: w trybie prywatnym log nie zawiera treści tłumaczeń — to zamierzone.
   Kluczy API log nie zawiera nigdy; jeśli zobaczysz klucz w logu, to **osobny błąd krytyczny**.

**Zgłoszenie błędu** (issue w repozytorium projektu) musi zawierać:

- wersję aplikacji, wersję Windows (`winver`), skalę DPI monitora, liczbę monitorów,
- nazwę gry/okna testowego i tryb okna (windowed / borderless fullscreen),
- numer scenariusza + krok, w którym wystąpił problem,
- co się stało vs. co było oczekiwane,
- fragment logu z czasu błędu (lub cały plik jako załącznik),
- zrzut ekranu **aplikacji** (nie zrzucaj ekranu gry, jeśli nie jest konieczny).

Jako „gra" do testów wystarczy dowolne okno z tekstem EN (np. Notatnik z przykładowym
tooltipem, strona www) — pełny test rób na Path of Exile 2 lub innej realnej grze
w trybie borderless.

---

## M01 — Wybór okna z listy

Dotyczy od: Etap 2.

**Warunki wstępne:** uruchomione min. 3 okna innych aplikacji (w tym jedno zminimalizowane).

**Kroki:**
1. Uruchom aplikację i otwórz listę okien do przechwytywania.
2. Przejrzyj listę.
3. Zminimalizuj jedno z widocznych okien, odśwież listę.
4. Wybierz okno testowe (np. Notatnik).

**Oczekiwany wynik:** lista pokazuje tytuły i nazwy procesów rzeczywistych okien top-level;
bez okien-widm (niewidoczne okna systemowe); odświeżenie odzwierciedla zmiany; wybór
zostaje zapamiętany i widoczny w UI jako aktywne źródło.

**Logi i zgłoszenie:** wg procedury wspólnej; w logu szukaj enumeracji okien i wybranego
uchwytu (HWND). Zgłoś, jeśli brakuje okna, które na pewno jest otwarte.

## M02 — Pojedyncze przechwycenie i podgląd

Dotyczy od: Etap 2.

**Warunki wstępne:** wybrane okno testowe z widocznym, wyraźnym tekstem (M01).

**Kroki:**
1. Wykonaj pojedyncze przechwycenie wybranego okna.
2. Obejrzyj podgląd w aplikacji.
3. Przysłoń częściowo okno testowe innym oknem i przechwyć ponownie.

**Oczekiwany wynik:** podgląd pokazuje aktualną zawartość okna, ostrą, bez przesunięć
i czarnych pasów; wymiary zgodne z oknem. Przy przysłonięciu `PrintWindow`
(PW_RENDERFULLCONTENT) nadal oddaje zawartość okna, a w razie niepowodzenia działa
fallback na crop ekranu (wtedy przysłonięcie może być widoczne — to dopuszczalne,
ale musi być deterministyczne, bez pustych/uszkodzonych bitmap).

**Logi i zgłoszenie:** w logu szukaj ścieżki capture (PrintWindow vs fallback) i wymiarów
bitmapy. Do zgłoszenia dołącz zrzut podglądu z aplikacji.

## M03 — OCR na rzeczywistym oknie

Dotyczy od: Etap 3.

**Warunki wstępne:** zainstalowany pakiet języka angielskiego Windows (Ustawienia →
Czas i język → Język i region); okno z tekstem zawierającym liczby: `+25%`, `10–15`,
`1.5 seconds`, `Level 20`.

**Kroki:**
1. Przechwyć okno i uruchom OCR.
2. Porównaj rozpoznany tekst z oryginałem.
3. (Wariant negatywny) usuń/wskaż brakujący pakiet językowy i uruchom OCR ponownie.

**Oczekiwany wynik:** tekst rozpoznany z poprawnymi liczbami, procentami i zakresami
(normalizacja nie zniekształca `+25%`, `10–15`, `1.5`); śmieciowe pojedyncze znaki
odfiltrowane. Przy braku pakietu językowego: czytelny komunikat z instrukcją
doinstalowania języka w ustawieniach Windows — nie crash, nie pusty wynik bez wyjaśnienia.

**Logi i zgłoszenie:** w logu szukaj czasu OCR i liczby rozpoznanych linii (bez treści
w trybie prywatnym). Zgłaszając błędne rozpoznanie, podaj czcionkę/rozmiar tekstu źródłowego.

## M04 — Globalny skrót Ctrl+Shift+T i zaznaczenie regionu

Dotyczy od: Etap 6.

**Warunki wstępne:** aplikacja uruchomiona i zminimalizowana/w tle; okno testowe na wierzchu.

**Kroki:**
1. Mając fokus w oknie testowym, naciśnij `Ctrl+Shift+T`.
2. Zaznacz myszą region z tekstem.
3. Naciśnij `Esc` zamiast zaznaczania (drugi przebieg) — anulowanie.
4. Sprawdź, że skrót działa też, gdy okno aplikacji jest zminimalizowane.

**Oczekiwany wynik:** skrót działa globalnie (bez fokusu na aplikacji); pojawia się tryb
zaznaczania regionu (przyciemnienie/krzyżyk); po zaznaczeniu rusza tłumaczenie regionu;
`Esc` anuluje bez efektów ubocznych; skrót nie jest wysyłany do gry (gra nie reaguje na
T/kombinację) i nie zawłaszcza innych skrótów systemowych.

**Logi i zgłoszenie:** w logu szukaj rejestracji hotkeya i zdarzeń wyzwolenia. Jeśli skrót
nie działa, sprawdź w logu błąd rejestracji (konflikt z inną aplikacją) i wpisz to w zgłoszeniu.

## M05 — Panel wyniku

Dotyczy od: Etap 6.

**Warunki wstępne:** skonfigurowany provider tłumaczenia (DeepL z kluczem lub Mock); M04 działa.

**Kroki:**
1. Przetłumacz region z kilkoma liniami tekstu (`Ctrl+Shift+T` → zaznaczenie).
2. Obejrzyj panel wyniku.
3. Przetłumacz ten sam region ponownie.

**Oczekiwany wynik:** panel pokazuje tekst źródłowy i tłumaczenie, pogrupowane w bloki
(tooltip jako całość, nie rozsypane linie); liczby/procenty w tłumaczeniu zgodne ze
źródłem; UI nie zamarza podczas tłumaczenia (można ruszać oknem). Drugie tłumaczenie
tego samego tekstu wraca natychmiast z cache (widocznie szybciej, licznik API bez zmian).

**Logi i zgłoszenie:** w logu szukaj źródła wyniku (glossary/cache/API) i czasów etapów
pipeline. Przy złym tłumaczeniu odnotuj, czy wynik szedł z cache czy z API.

## M06 — Nakładka click-through (kliknięcia przechodzą do gry, brak kradzieży fokusu)

Dotyczy od: Etap 7.

**Warunki wstępne:** gra/okno testowe reagujące na kliknięcia; nakładka włączona z widocznym tłumaczeniem.

**Kroki:**
1. Wyświetl tłumaczenie w nakładce nad oknem gry.
2. Kliknij **dokładnie w obszar tekstu nakładki**.
3. Przeciągnij myszą przez nakładkę (np. obrót kamery w grze).
4. Obserwuj pasek zadań i fokus podczas pojawiania się/znikania nakładki.
5. Naciśnij kilka klawiszy ruchu w grze, gdy nakładka jest widoczna.

**Oczekiwany wynik:** kliknięcia i przeciągnięcia przechodzą przez nakładkę do gry
(WS_EX_TRANSPARENT); nakładka **nigdy** nie przejmuje fokusu (WS_EX_NOACTIVATE) — gra
cały czas przyjmuje input klawiatury; nakładka nie ma przycisku na pasku zadań
(WS_EX_TOOLWINDOW); pozostaje Topmost nad grą w trybie borderless; tekst czytelny.

**Logi i zgłoszenie:** w logu szukaj utworzenia okna nakładki i ustawionych stylów.
Kradzież fokusu (gra przestaje reagować na klawiaturę choć na moment) = błąd o wysokim
priorytecie — opisz dokładny moment (pojawienie się / aktualizacja / zniknięcie nakładki).

## M07 — Wiele monitorów

Dotyczy od: Etap 7 (capture regionów na drugim monitorze: od Etapu 6).

**Warunki wstępne:** min. 2 monitory; okno testowe na monitorze **drugim** (nie głównym).

**Kroki:**
1. Wybierz okno z monitora drugiego i wykonaj tłumaczenie regionu (`Ctrl+Shift+T`).
2. Sprawdź pozycję nakładki/panelu względem zaznaczonego regionu.
3. Przenieś okno gry na monitor główny i powtórz.
4. Zaznacz region przechodzący przez granicę monitorów (jeśli UI na to pozwala).

**Oczekiwany wynik:** zaznaczanie regionu działa na każdym monitorze (przyciemnienie
obejmuje właściwy ekran, współrzędne trafiają w zaznaczony obszar); nakładka pojawia się
przy regionie na właściwym monitorze, bez przesunięcia o szerokość innego ekranu
(klasyczny błąd współrzędnych wirtualnego pulpitu); po przeniesieniu okna wszystko działa
bez restartu aplikacji.

**Logi i zgłoszenie:** w logu szukaj współrzędnych regionu i monitora. W zgłoszeniu podaj
układ monitorów (rozdzielczości, który główny, pozycje względem siebie — zrzut z
Ustawienia → Ekran).

## M08 — Różne skale DPI (100% / 150% / 200%)

Dotyczy od: Etap 6 (region), pełnie: Etap 7 (nakładka).

**Warunki wstępne:** możliwość zmiany skali w Ustawienia → Ekran → Skala.

**Kroki:**
1. Ustaw skalę 100%, uruchom aplikację, wykonaj pełny cykl: zaznaczenie regionu → tłumaczenie → nakładka.
2. Powtórz przy 150% i 200% (po każdej zmianie skali **zrestartuj aplikację**, potem
   powtórz też bez restartu — oba przypadki mają działać, manifest PerMonitorV2).
3. Przy dwóch monitorach o różnych skalach: przeciągnij okno aplikacji między monitorami
   i wykonaj cykl na każdym.

**Oczekiwany wynik:** zaznaczony region pokrywa się 1:1 z tym, co trafia do OCR (brak
przesunięcia/przeskalowania — tekst z brzegu regionu jest rozpoznany); nakładka siedzi
dokładnie na regionie; UI aplikacji nierozmyte; zmiana skali w locie nie psuje pozycji
(dopuszczalna korekta przy następnym zaznaczeniu, niedopuszczalny trwały offset).

**Logi i zgłoszenie:** w logu szukaj DPI monitora przy capture. W zgłoszeniu zawsze podaj
skale wszystkich monitorów i na którym była gra.

## M09 — Tryb borderless fullscreen

Dotyczy od: Etap 6.

**Warunki wstępne:** gra ustawiona w tryb **borderless/windowed fullscreen** (nie exclusive).

**Kroki:**
1. Wykonaj pełny cykl tłumaczenia regionu na grze w borderless.
2. Sprawdź nakładkę (Etap 7): widoczność nad grą, click-through, Alt+Tab do innej
   aplikacji i powrót do gry.
3. (Kontrola negatywna) przełącz grę w **exclusive fullscreen** i spróbuj ponownie.

**Oczekiwany wynik:** w borderless wszystko działa jak w trybie okienkowym; nakładka
zostaje nad grą po Alt+Tab i powrocie. W exclusive fullscreen capture/nakładka mogą nie
działać — to **udokumentowane ograniczenie**; aplikacja ma to komunikować czytelnie
(sugestia przełączenia na borderless), a nie crashować czy pokazywać czarny obraz bez słowa.

**Logi i zgłoszenie:** w logu szukaj wyniku capture (pusta/czarna bitmapa → wpis).
W zgłoszeniu koniecznie podaj dokładny tryb wyświetlania z ustawień gry.

## M10 — Minimalizacja i zamknięcie gry w trakcie

Dotyczy od: Etap 2 (capture), pełnie: Etap 7.

**Warunki wstępne:** wybrane okno gry, działający cykl tłumaczenia.

**Kroki:**
1. Zminimalizuj grę i spróbuj przechwycenia/tłumaczenia.
2. Przywróć grę — sprawdź, że działanie wraca bez restartu aplikacji.
3. Zamknij grę całkowicie, gdy aplikacja ma ją wybraną jako źródło; spróbuj tłumaczenia.
4. Uruchom grę ponownie i wybierz ją z listy jeszcze raz.

**Oczekiwany wynik:** minimalizacja → czytelny komunikat (okno zminimalizowane /
niedostępne), bez crasha i bez tłumaczenia śmieci; przywrócenie → normalne działanie.
Zamknięcie gry → aplikacja wykrywa zniknięcie okna (komunikat, wyszarzenie akcji),
nakładka znika, brak wyjątków; ponowny wybór działa bez restartu aplikacji.

**Logi i zgłoszenie:** w logu szukaj błędów nieważnego uchwytu okna (invalid HWND) —
mają być obsłużone (wpis ostrzeżenia), nie wylatywać jako nieobsłużony wyjątek.

## M11 — Brak internetu

Dotyczy od: Etap 4.

**Warunki wstępne:** skonfigurowany DeepL z poprawnym kluczem; kilka tekstów już w cache
(przetłumaczone wcześniej).

**Kroki:**
1. Wyłącz sieć (tryb samolotowy / odłącz kabel / zablokuj aplikację w firewallu).
2. Przetłumacz tekst, który **jest** w cache.
3. Przetłumacz tekst, którego **nie ma** w cache.
4. Włącz sieć i powtórz krok 3.

**Oczekiwany wynik:** tekst z cache tłumaczy się normalnie (offline nie blokuje cache);
tekst spoza cache → czytelny komunikat „brak połączenia" z informacją, że tłumaczenie
online stoi i co zrobić — bez zawieszenia UI, bez lawiny okienek błędów przy kolejnych
próbach; po powrocie sieci tłumaczenie działa bez restartu.

**Logi i zgłoszenie:** w logu szukaj wyjątku sieciowego zapisanego ze stack trace (stack
trace tylko w logu, nigdy w UI). Zgłoś, jeśli aplikacja ponawia żądania w agresywnej pętli.

## M12 — Błędny klucz API

Dotyczy od: Etap 4.

**Warunki wstępne:** dostęp do ustawień klucza API.

**Kroki:**
1. Wpisz syntaktycznie poprawny, ale nieistniejący klucz (np. zmień znak w prawdziwym).
2. Użyj przycisku testu połączenia (`/v2/usage`).
3. Mimo błędnego klucza spróbuj przetłumaczyć region.
4. Wpisz poprawny klucz i powtórz test połączenia.

**Oczekiwany wynik:** test połączenia zwraca jednoznaczny komunikat „nieprawidłowy klucz
API" (odpowiedź 403), z podpowiedzią sprawdzenia klucza i typu konta (free `:fx` vs pro);
próba tłumaczenia daje ten sam czytelny błąd, nie surowy kod HTTP; poprawny klucz →
test przechodzi i pokazuje zużycie. Klucz nie pojawia się w żadnym komunikacie ani logu.

**Logi i zgłoszenie:** w logu szukaj odpowiedzi 403 (status, bez klucza). Klucz widoczny
w logu/komunikacie = błąd krytyczny, zgłoś natychmiast.

## M13 — Przekroczony limit DeepL

Dotyczy od: Etap 4.

**Warunki wstępne:** konto DeepL z wyczerpanym limitem (realnie: konto free pod koniec
limitu miesięcznego) **albo** wymuszenie odpowiedzi 456 przez ustawienie testowe/mock,
jeśli dostępne w wersji dev.

**Kroki:**
1. Spróbuj przetłumaczyć nowy (nieskeszowany) tekst.
2. Przetłumacz tekst, który jest w cache.
3. Sprawdź licznik zużycia w aplikacji.

**Oczekiwany wynik:** odpowiedź 456 → czytelny komunikat „limit DeepL wyczerpany"
z informacją, kiedy/jak się odnawia i sugestią trybu cache-only; cache działa dalej;
licznik zużycia pokazuje stan zgodny z `/v2/usage`; aplikacja nie ponawia żądań
w pętli (nie pali requestów po 456).

**Logi i zgłoszenie:** w logu szukaj odpowiedzi 456 i zatrzymania kolejnych wywołań API.

## M14 — Zmiana rozdzielczości w trakcie

Dotyczy od: Etap 6.

**Warunki wstępne:** działający cykl tłumaczenia; możliwość zmiany rozdzielczości
(ustawienia gry lub Windows).

**Kroki:**
1. Wykonaj tłumaczenie regionu przy rozdzielczości bazowej.
2. Zmień rozdzielczość gry (np. z 2560×1440 na 1920×1080) przy działającej aplikacji.
3. Wykonaj nowe zaznaczenie regionu i tłumaczenie.
4. Wróć do rozdzielczości bazowej i powtórz.

**Oczekiwany wynik:** po zmianie rozdzielczości nowe zaznaczenia trafiają we właściwe
miejsce (współrzędne przeliczone od nowa, bez offsetu ze starej rozdzielczości); nakładka
(Etap 7) pozycjonuje się poprawnie; brak crasha w momencie samej zmiany, nawet jeśli
trwało wtedy przechwytywanie.

**Logi i zgłoszenie:** w logu szukaj wymiarów ekranu/okna przy kolejnych capture — mają
odzwierciedlać zmianę. W zgłoszeniu podaj obie rozdzielczości i tryb okna gry.

## M15 — Tryb prywatny (brak historii i logów treści)

Dotyczy od: etapu, w którym wchodzi tryb prywatny (patrz roadmap; polityka: bez historii,
bez logowania treści, cache tylko w pamięci, czyszczenie po sesji).

**Warunki wstępne:** aplikacja z pustym stanem lub znanym stanem cache; dostęp do
`%LOCALAPPDATA%\GameTranslatorOverlay` (logi, baza cache).

**Kroki:**
1. Włącz tryb prywatny.
2. Przetłumacz kilka **unikalnych** tekstów (łatwych do wyszukania, np. z rzadkim słowem).
3. Otwórz najnowszy log i wyszukaj te teksty oraz ich tłumaczenia.
4. Sprawdź historię w UI (jeśli już istnieje — Etap History Mode).
5. Zamknij aplikację, uruchom ponownie **bez** trybu prywatnego i przetłumacz ten sam tekst.
6. Sprawdź plik bazy cache (data modyfikacji podczas sesji prywatnej).

**Oczekiwany wynik:** logi z sesji prywatnej nie zawierają treści źródłowych ani tłumaczeń
(wpisy operacyjne — czasy, statusy — są dozwolone); historia w UI pusta dla sesji
prywatnej; tłumaczenia z sesji prywatnej **nie** wylądowały w trwałym cache (po restarcie
tekst idzie do API/pamięci od nowa, plik bazy niezmodyfikowany treścią z sesji);
po wyłączeniu trybu wszystko wraca do normalnego zapisu.

**Logi i zgłoszenie:** tu log jest przedmiotem testu — do zgłoszenia dołącz fragment
pokazujący wyciek treści (zamaż samą treść, zostaw kontekst wpisu i timestamp).
Wyciek treści w trybie prywatnym = błąd o wysokim priorytecie.

## M16 — Tryb cache-only

Dotyczy od: Etap 5.

**Warunki wstępne:** cache z kilkoma wpisami; skonfigurowany poprawny klucz DeepL
(żeby wykluczyć, że „działa", bo i tak nie ma klucza).

**Kroki:**
1. Włącz tryb cache-only.
2. Przetłumacz tekst obecny w cache.
3. Przetłumacz tekst nieobecny w cache.
4. Obserwuj licznik zużycia API / monitor sieci (np. zakładka sieci w Process Explorer
   lub firewall log) podczas kroków 2–3.
5. Wyłącz tryb i powtórz krok 3.

**Oczekiwany wynik:** tekst z cache tłumaczy się natychmiast; tekst spoza cache →
jednoznaczny stan „brak w cache (tryb cache-only)" zamiast tłumaczenia — bez błędu
sieciowego, bo **żadne żądanie nie wychodzi**; zero ruchu do api.deepl.com przez cały
czas trwania trybu; po wyłączeniu trybu brakujący tekst normalnie idzie do API.

**Logi i zgłoszenie:** w logu szukaj decyzji pipeline (cache-hit / cache-miss w trybie
cache-only) i braku wywołań providera API. Jakiekolwiek wyjście do sieci w cache-only
zgłoś jako błąd o wysokim priorytecie (to obietnica prywatności/kosztów).

## M17 — Tryb live: start, tłumaczenie, stop

Dotyczy od: Etap 8.

**Warunki wstępne:** uruchomiona gra w trybie okienkowym/borderless (albo okno testowe
LiveDiag: `dotnet run --project tools/GameTranslatorOverlay.LiveDiag -- 36`).

**Kroki:**
1. Wybierz okno gry z listy i kliknij „▶ Start live".
2. Poczekaj, aż na ekranie pojawią się dymki tłumaczeń nad tekstem gry.
3. Wywołaj w grze nowy tekst (dialog, tooltip przedmiotu).
4. Kliknij „⏹ Stop".

**Oczekiwany wynik:** pierwsze tłumaczenia w ~1 s od startu; nowy tekst tłumaczy się
w poniżej sekundy; bloki NIE migają przy statycznej scenie (okres łaski maskuje czknięcia
OCR — pasek statusu może pokazywać „podtrzymane N"); po Stop nakładka znika w całości.

**Logi i zgłoszenie:** status live pokazuje kadencję (klatka/OCR/tłum. w ms). Miganie
bloków na nieruchomej scenie zgłoś z fragmentem statusów (liczby bloków między przebiegami).

## M18 — Tryb live: zmiana ustawień W TRAKCIE sesji

Dotyczy od: Etap 8 (regresja audytu #3 — cicha śmierć pętli).

**Kroki:**
1. Uruchom tryb live na oknie z widocznym tekstem.
2. W trakcie działania zmień kolejno: dostawcę (DeepL↔Mock), profil gry, styl czcionki,
   tło dymków; kliknij też inne okno na liście okien.
3. Obserwuj pasek statusu live przez ~10 s po każdej zmianie.

**Oczekiwany wynik:** sesja live NIGDY nie zamiera — po każdej zmianie tłumaczenie
kontynuuje z nowymi ustawieniami (najwyżej jedna klatka pominięta ze statusem
„ustawienia zmienione w trakcie klatki"); przyciski Start/Stop odzwierciedlają stan.

**Logi i zgłoszenie:** zamarcie nakładki przy aktywnym „⏹ Stop" = błąd HIGH (regresja).

## M19 — Tryb live: ukrycie nakładki skrótem (Ctrl+Shift+H)

Dotyczy od: Etap 8 (regresja audytu #3 — SWP_SHOWWINDOW obchodził ukrycie).

**Kroki:**
1. Uruchom tryb live, poczekaj na dymki.
2. Naciśnij Ctrl+Shift+H i odczekaj ≥5 s przy zmieniającym się tekście gry.
3. Naciśnij Ctrl+Shift+H ponownie.

**Oczekiwany wynik:** po ukryciu nakładka NIE wraca sama (nawet gdy sesja dalej
tłumaczy w tle) i nie pokazuje zamrożonych dymków; po ponownym skrócie wraca
z AKTUALNYMI tłumaczeniami; skrót nigdy nie działa „odwrotnie".

## M20 — Tryb live: bieg przez mapę i cięcie sceny

Dotyczy od: Etap 8.

**Kroki:**
1. Uruchom tryb live w grze z nazwami NPC/obiektów na ekranie.
2. Biegnij przez lokację ~10 s, potem zatrzymaj się.
3. Zrób twarde przejście sceny (teleport/wejście do miasta/loading).

**Oczekiwany wynik:** podczas biegu dymki podążają za tekstem (mogą lekko „gonić"
pozycje, do ~1 przebiegu opóźnienia); po zatrzymaniu stabilizują się w ≤1 s; po twardym
przejściu sceny stare dymki znikają najpóźniej po jednym przebiegu (bez „duchów"
wiszących nad nową sceną dłużej niż ~1 s).

## M21 — Tryb live: strategia napisów

Dotyczy od: Etap 10.

**Kroki:**
1. Przełącz „Styl live" na „Napisy na dole" i uruchom tryb live.
2. Wywołaj w grze dialog z kilkoma kolejnymi linijkami.

**Oczekiwany wynik:** nowe teksty pojawiają się jako pasek napisów u dołu okna gry;
kolejna linia ZASTĘPUJE poprzednią (bez dublowania); pasek znika po skonfigurowanym
czasie; przesunięcie okna gry dosuwa pasek bez ponownego OCR.

## M22 — Tryb live: minimalizacja i zamknięcie okna gry

Dotyczy od: Etap 8.

**Kroki:**
1. Uruchom tryb live, poczekaj na dymki.
2. Zminimalizuj okno gry na ~5 s i przywróć.
3. Zamknij okno gry przy aktywnym trybie live.

**Oczekiwany wynik:** przy minimalizacji nakładka znika, status mówi o czekaniu; po
przywróceniu tłumaczenia wracają bez restartu trybu; po zamknięciu gry tryb live
zatrzymuje się z komunikatem, przyciski wracają do stanu wyjściowego.

## Regresja M20/M22 — przerywany ruch kamery (2026-09-13)

W dowolnej grze z tekstem poruszaj kamerą przez 10 s, robiąc bardzo krótkie
zatrzymania (krótsze niż 250 ms), następnie zatrzymaj ją całkowicie i otwórz opis.
Odczyt nie powinien czekać na nowy, pełny okres 2,5 s po każdym zatrzymaniu.
Limit dotyczy dopuszczenia OCR przy pierwszej próbce po terminie; opóźnienie
sieci i prezentacja nakładki są mierzone osobno. Zapisz oddzielnie znikanie,
przestarzałe napisy oraz czas pojawienia się nowego opisu — ta poprawka dotyczy
odraczania OCR i nie oznacza zaliczenia wszystkich kryteriów M20.

Powtórz próbę po minimalizacji na 5 s i przywróceniu gry (M22): stary termin
oczekiwania nie może wymuszać dodatkowego odczytu po wznowieniu. Test czystej
logiki resetu jest automatyczny; zachowanie okna i nakładki sprawdza użytkownik.
## Regresja M17/M20 — „Zbadaj” po otwarciu opisu (2026-09-13)

1. Uruchom live w dowolnej grze; w Escape Academy ustaw widok z Inspect/Zbadaj.
2. Otwórz opis tak, aby poprzednia etykieta zniknęła z obrazu gry.
3. Stare tłumaczenie ma zniknąć po wykryciu zmiany widoku, przed nową odpowiedzią.
   Nie może wrócić po nadejściu spóźnionego tłumaczenia poprzedniej sceny.
4. Powtórz dla pasa napisów oraz po ręcznym ukryciu Ctrl+Shift+H: automatyczne
   czyszczenie nie może cofnąć wyboru użytkownika i samo pokazać nakładki.
5. Powtórz z minimalizacją/przywróceniem okna podczas oczekiwania (M22).

Próg cięcia i potwierdzanie ruchu pozostają wspólne dla gier. Niewykryte, małe
zmiany lokalne są osobnym przypadkiem; ta poprawka nie gwarantuje natychmiastowej
odpowiedzi dostawcy. Zapisz osobno czas zniknięcia starego i gotowości nowego tekstu.
Sonda SceneReplay powtarza te dwa pomiary na własnym oknie z Mockiem 2 s. Kontrolne
próby nie zastępują obserwacji nakładki i skrótów w prawdziwej grze.
## Regresja M17 — zmiana widoku podczas wolnej odpowiedzi (2026-09-13)

1. Otwórz opis, a zanim nadejdzie tłumaczenie, przejdź do następnego.
2. Nowy tekst może być tłumaczony, gdy poprzednia odpowiedź nadal trwa. Stara
   odpowiedź nie może powrócić na nakładkę. Zapisz czas gotowości nowego opisu.
3. Wróć do poprzedniego opisu i powtórz szybkie przełączanie kilku widoków.
   Po zatrzymaniu ma pojawić się bieżący tekst, bez odtwarzania kolejki starych.
4. Zatrzymaj live podczas oczekiwania, następnie uruchom ponownie. Sprawdź brak
   późnych aktualizacji zatrzymanej sesji. Powtórz zmianę ustawień podczas oczekiwania.

SceneReplay sprawdza wolną odpowiedź lokalnie przez Mock; testy Core obejmują
limit dwóch zadań, deduplikację i rezerwację znaków. Liczniki lokalne nie zastępują
rozliczenia dostawcy, zwłaszcza po timeoutach. Próbę prawdziwej nakładki i sieci
wykonuje użytkownik; raport sondy nie mierzy fizycznego renderowania.

## Regresja M17 — podobny opis i sporadyczne błędy OCR (2026-09-13)

1. Przełącz dwa podobne opisy w tym samym miejscu; nowy może być krótszy. Sprawdź,
   czy świeże tłumaczenie zastępuje stare bez konieczności zmiany całej sceny.
2. Pozostaw napis nad animowanym tłem. Pojedyncza pomyłka rozdzielona poprawnym
   odczytem lub pustym wynikiem nie powinna wystarczać do podmiany tłumaczenia.
3. Sprawdź liczbę/statystykę zmieniającą się w tym samym miejscu; potwierdzona
   nowa wartość powinna wejść, podczas gdy pojedyncze pomyłki nie powinny.
4. Wróć do statycznego menu: brak dodatkowego migania, zmiany rozmiaru i usuwania
   poprawnych napisów. Zatrzymaj i ponownie uruchom live; stare potwierdzenia
   nie powinny przechodzić do nowej sesji.

Lokalna sonda SceneReplay: local-reading (Windows OCR) oraz reading-jitter i
reading-whiff (jawnie syntetyczne odczyty przy niezmienionym obrazie). Podsumowania
mierzą callbacki i liczniki Mocka, a nie fizyczne renderowanie nakładki. Dwa kolejne
identyczne błędy rozpoznawania pozostają ograniczeniem tej reguły.

## Regresja M17/M20 — wcześniejsza próbka przy terminie stabilności (2026-09-13)

1. Przełącz nowy opis, a następnie podobny opis w tym samym miejscu. Porównaj
   gotowość tekstu osobno od usunięcia poprzedniego tłumaczenia.
2. Powtórz po krótkim ruchu kamery i w nieruchomym menu. Wcześniejsza próbka nadal
   musi sprawdzić świeży obraz; nowa zmiana odsuwa termin stabilności odczytu.
3. Sprawdź błędny odczyt rozdzielony prawidłowym albo pustym wynikiem. Przyspieszenie
   nie znosi wymogu dwóch kolejnych potwierdzeń podobnej nowej treści.
4. Przy profilu z niskim FPS pętla zachowuje zwykły rytm przechwytywania. Testy Core
   sprawdzają również przeterminowany termin po nieudanym capture oraz reset.

SceneReplay umożliwia parę przed/po dla displayed i faz 0/50/100/150 ms, z prawdziwym
Windows OCR oraz Mock 0 lub 2000 ms. Inflight przy domyślnym Mock 2000 ms sprawdza
zmianę podczas oczekiwania. Wynik około 2,5 s w tej próbie obejmuje sztuczne 2 s;
nie jest pomiarem DeepL. W grze ocenę szybkości i fizycznej stabilności wykonuje
użytkownik. Progi stabilności 250 ms, wymuszenia 600 ms i ruchu 2500 ms są bez zmian.

## Regresja M17/M20 — kontrola OCR, lokalne znikanie i pozycja (2026-09-13)

**Status: do oceny wizualnej w aplikacji.** Poniższa lista nie oznacza zaliczenia
scenariuszy. Testy czystej logiki i sondy callbacków nie zastępują obserwacji
fizycznej nakładki. Wybierz dowolną grę lub własne okno z tekstem; zapisz tryb
nakładki, profil, DPI i rozdzielczość. Do lokalnych prób wystarcza Mock.

1. **Kontrola OCR:** obserwuj nieruchome menu, potem kilka razy szybko zmień opis.
   Zapisz osobno gotowość nowego tekstu i czas usunięcia poprzedniego. Wolna odpowiedź
   nie może przywracać starej sceny. Pierwsza okresowa kontrola podczas OCR czeka
   1,5 interwału (przy 6 FPS około 250 ms zamiast 167 ms); kolejne mają zwykły rytm.
   To świadomy dodatkowy czas do pierwszej kontroli, około 83 ms przy 6 FPS.
   OCR trwający co najmniej jeden interwał nadal dostaje świeżą kontrolę końcową.
   Szczegóły harmonogramu weryfikuje się sondą; obserwacja gry służy ocenie skutków.
2. **Mała zmiana na jednolitym tle:** zostaw kilka etykiet, a jedną, np. „Inspect”,
   zasłoń lokalnym panelem bez zmiany reszty sceny. Powtórz dla potwierdzonej zamiany
   krótkiego opisu w tym samym miejscu. Stare tłumaczenie ma zniknąć przed czekaniem
   na nowe; niezależne etykiety mają pozostać. Stary napis nie powinien wrócić
   po późnej odpowiedzi ani po następnym odczycie.
3. **Zmiana podczas oczekiwania:** zasłoń jedną etykietę, zanim przyjdą tłumaczenia
   kilku nowych bloków. Po odrzuceniu nieaktualnej klatki nadal widoczne bloki
   powinny dostać ponowny odczyt i pojawić się, choć same nie zmieniły obrazu.
   Powtórz z wycinkiem OCR, powiększeniem i tekstem przy brzegu obszaru. Na teksturze,
   przy niepewnych kolorach lub niepełnym polu brak natychmiastowego usunięcia
   pozostaje ograniczeniem: obowiązuje wcześniejszy okres łaski. Widoczny cienki
   znak nie powinien zostać uznany za jednolite, puste pole.
4. **Pasek napisów:** najpierw pokaż A („Inspect”), następnie niezależny dialog B.
   Usunięcie A nie powinno skasować paska B. Gdy pasek zawiera A i B jednocześnie,
   usuń A: B ma pozostać; usunięcie ostatniego źródła ma wyczyścić pasek. Przy
   włączonym wygasaniu częściowa aktualizacja nie powinna wydłużyć czasu B.
   Po wygaśnięciu paska ponów częściowe usunięcie: pasek nie może się odtworzyć.
5. **Pozycja i rozmiar:** w trybie bloków obserwuj drobne drżenie nieruchomego napisu,
   potem przesuń tekst lub kamerę o kilka pikseli, także przy szerokim opisie.
   Po nowym odczycie pozycja ma podążać za źródłem, zachowując spokojny rozmiar.
   Tłumione są różnice do 2 fizycznych pikseli osobno na każdej osi; zmiana rozmiaru
   nie powinna wymuszać skoku pozycji. Powtórz dla większego DPI i zmiany rozmiaru
   etykiety po najechaniu. Ta poprawka nie zmienia globalnego ukrywania przy ruchu.
6. Powtórz lokalne usunięcie po ręcznym ukryciu `Ctrl+Shift+H` oraz po zatrzymaniu
   i ponownym uruchomieniu Live. Automatyczna aktualizacja nie może cofać ręcznego
   ukrycia ani przenosić starego paska lub oczekującej klatki do nowej sesji.

Przy zgłoszeniu rozdziel: opóźnienie, pozostawanie starej treści, błędne zniknięcie
aktualnego napisu i przesunięcie względem oryginału. Czasy kontroli sceny i OCR
nakładają się; nie dodawaj ani nie odejmuj ich jako niezależnych etapów.

## Regresja M20/M21/M22 — stałe menu przy ruchu tła (2026-09-15)

Automatyczne scenariusze `hud-motion`, `hud-motion-whiff` i `hud-motion-small-whiff`
sprawdzają stan sesji bez gry; ich wynik nie oznacza zaliczenia fizycznej prezentacji
M17–M22. Przy późniejszej próbie w grze sprawdź:

1. Już przetłumaczone, nieruchome menu na stałym tle podczas ruchu świata.
2. Zastąpienie lub zasłonięcie jednej jego etykiety: stara znika, pozostałe zostają.
3. Rzeczywistą zmianę całego widoku i odpowiedź dostawcy przychodzącą po zmianie.
4. Pasek napisów: usunięcie części treści nie odnawia jego czasu wygaśnięcia.
5. Ukrycie skrótem, minimalizację/przywrócenie, zmianę rozmiaru/DPI oraz Stop.

Ochrona wymaga identycznych pikseli źródła z marginesem 3 px i tej samej wielkości
przechwyconego okna. Nie dotyczy klatek ze skalowaniem OCR ani przechwytywania
zapasowego z ekranu. Brak ochrony w takich warunkach nie dowodzi regresji tej funkcji.

## M23 — Wybór dostawcy tłumaczeń i dodatkowe pola (2026-09-29)

Dotyczy od: wielu dostawców tłumaczeń. Runda była sprawdzona testami i kompilacją,
bez uruchomienia okna — wygląd i zachowanie pól wymagają tej próby.

**Kroki:**
1. Przełącz „Dostawca tłumaczeń” kolejno na DeepL, Azure, Google, Model językowy, Claude, Mock.
2. Dla Azure wpisz region z błędem (np. `west europe`), przejdź tabulatorem dalej.
3. Dla Modelu językowego kliknij kolejno OpenAI / Ollama / LM Studio; wpisz `http://example.com/v1`.
4. Zapisz klucz dla Azure, przełącz na DeepL i z powrotem.
5. Przejdź tabulatorem przez pola bez zmieniania wartości przy działającym live.
6. Dla Modelu językowego zapisz klucz przy adresie OpenAI, potem kliknij Ollama.
7. W polu modelu Claude wpisz ręcznie `claude-opus-4-8` (bez wychodzenia z pola), potem kliknij gdzie indziej.

**Oczekiwany wynik:** nagłówek sekcji pokazuje wybranego dostawcę; widoczne są tylko jego
pola (region / adres i model / model Claude); dla Mock pole klucza jest wyłączone. Zły region
wraca do poprzedniej wartości z ostrzeżeniem w statusie; adres `http://` zdalnego serwera daje
ostrzeżenie o HTTPS. Klucze są osobne: klucz Azure nie zastępuje klucza DeepL. Samo przejście
przez pola nie zapisuje ustawień i nie przerywa tłumaczeń live. Po zmianie adresu na Ollamę
status mówi, że klucz należy do api.openai.com i nie jest wysyłany. Model Claude zapisuje się
dopiero po wyjściu z pola (status „Ustawienia zapisane” pojawia się raz, z pełną nazwą).
Ciemny motyw pozostaje czytelny.

## M24 — Lokalny model językowy (Ollama / LM Studio)

**Kroki:**
1. Uruchom Ollamę z pobranym modelem; w aplikacji wybierz Model językowy → Ollama, podaj model.
2. Kliknij Testuj; przetłumacz region z tekstem gry i uruchom krótko tryb live.
3. Zatrzymaj Ollamę i kliknij Testuj ponownie; podaj nieistniejący model.

**Oczekiwany wynik:** status klucza mówi, że tekst zostaje na tym komputerze; test pokazuje
próbne tłumaczenie; terminy z aktywnego słownika pojawiają się w tłumaczeniu dłuższych zdań.
Zatrzymany serwer daje komunikat o uruchomieniu Ollamy/LM Studio, zły model — o nazwie modelu.

## M25 — Claude

**Kroki:** zapisz klucz Anthropic, kliknij Testuj, przetłumacz opis przedmiotu z profilem PoE2;
zmień model na `claude-haiku-4-5` i powtórz.

**Oczekiwany wynik:** test podaje nazwę modelu; tłumaczenie używa terminów ze słownika PoE2;
zmiana modelu działa bez restartu. Zły klucz daje komunikat o kluczu, nie wyjątek.

## M26 — Ctrl+Shift+T przy otwartym zaznaczaniu

**Kroki:** wciśnij Ctrl+Shift+T, a przy otwartym zaznaczaniu wciśnij go ponownie; potem trzeci raz.

**Oczekiwany wynik:** drugie naciśnięcie zamyka zaznaczanie (status „Zaznaczanie anulowane.”),
trzecie otwiera je od nowa.

## M27 — Glosariusz DeepL

**Kroki:** dostawca DeepL z kluczem; w słowniku termin, np. „Waystone” → „Kamień drogi”.
Zaznacz Ctrl+Shift+T tekst, w którym termin stoi w środku zdania (np. „You found a Waystone.”).
Potem na koncie DeepL (lub w logu aplikacji) sprawdź glosariusze; dodaj drugi termin i powtórz
z nowym tekstem.

**Oczekiwany wynik:** tłumaczenie używa „Kamień drogi” w odpowiedniej formie (np. „Kamień
drogi”, „Kamienia drogi”). Na koncie jest jeden glosariusz „GameTranslatorOverlay …”; po
dodaniu terminu powstaje nowy, a stary znika. W trybie prywatnym glosariusz nie powstaje.
Tekst bez terminów tłumaczy się jak dotąd.

## M28 — Panel „Szybkość”

**Kroki:** uruchom live w grze z dialogami na kilka minut, potem kliknij **Kopiuj raport**
i wklej wynik do notatnika; kliknij **Wyzeruj**.

**Oczekiwany wynik:** panel pokazuje medianę „Nowy tekst”, „Znany”, dostawcy, OCR i klatki,
a p90 po co najmniej 5 pomiarach. Raport zawiera medianę, p90, maksimum i liczbę pomiarów,
bez tekstu z gry. Po wyzerowaniu panel pokazuje „Brak pomiarów”.
