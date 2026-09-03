# Changelog

Wersjonowanie: SemVer. Daty w formacie RRRR-MM-DD.

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
