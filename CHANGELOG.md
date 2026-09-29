# Changelog

Wersjonowanie: SemVer. Daty w formacie RRRR-MM-DD.

## [Niewydane]

Brak zmian po wydaniu 0.3.0.

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
