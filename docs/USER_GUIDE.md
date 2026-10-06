# Instrukcja użytkownika — GameTranslatorOverlay

GameTranslatorOverlay tłumaczy na żywo angielski tekst z gier na polski. Działa jak
zewnętrzna nakładka: przechwytuje obraz, rozpoznaje tekst systemowym OCR Windows
i wyświetla tłumaczenie nad grą — **nie dotykając plików ani procesu gry**. Pliki gry
(tylko do odczytu, przy wyłączonej grze) może czytać wyłącznie osobne, opcjonalne narzędzie
`CorpusTool`, które uruchamiasz sam — patrz [Spolszczenie z wyprzedzeniem](#spolszczenie-z-wyprzedzeniem-korpus-gry).

Projekt jest przeznaczony do różnych gier; nie wymaga profilu konkretnego tytułu.
Instrukcja opisuje wydanie 0.5.0 — szczegóły w
[historii zmian](https://github.com/KubaZx/Game_Translator/blob/main/CHANGELOG.md).

**Nowe w 0.5.0** (w skrócie):

- tryb **Na oryginale (zakrywa)** wygląda jak napis gry: znikają same litery oryginału, a polski
  tekst dostaje kolor, kontur, cień i wielkość napisu gry, krój **Jak w grze (krój z profilu)** —
  [opis](#tryb-na-oryginale-zakrywa--napis-jak-w-grze),
- tłumaczenie znika razem z napisem w grze, także na teksturowanym tle i z paska napisów,
- **korpus gry**: teksty gry przetłumaczone z wyprzedzeniem, dopasowanie odczytów z błędami OCR,
  a w trybie zakrywania kwestia pisana literami czeka na koniec pisania (dziś dla Escape Academy) —
  [opis](#spolszczenie-z-wyprzedzeniem-korpus-gry),
- przycisk **DeepSeek** przy modelu językowym (model bez „myślenia”) i komunikat
  „⚠ Pełny ekran utrudnia nakładkę — przełącz na okno bez ramki”.

Nowego wyglądu, dopasowania etykiet, odrzucania śmieci OCR i czekania na koniec kwestii nikt
jeszcze nie sprawdzał w grze na żywo: wygląd sprawdzono na klatkach 4K z Escape Academy (narzędzie
OverlayPreview), a zachowanie w powtórkach syntetycznych (SceneReplay, CorpusEval). Uwagi z gry
zgłaszaj według [ostatniej sekcji](#jak-zgłosić-problem-jakości).

## Instalacja

1. Rozpakuj archiwum `GameTranslatorOverlay-vX.Y.Z-win-x64.zip` do dowolnego folderu.
2. Uruchom `GameTranslatorOverlay.exe`. Środowisko .NET jest w paczce; nie musisz instalować Pythona ani lokalnych modeli AI.
3. Wymagania: Windows 10 (2004+) lub Windows 11 oraz **pakiet językowy Windows dla języka
   gry** (dla angielskiego: *Ustawienia → Czas i język → Język i region → Dodaj język →
   English (United States)*). Status OCR widać na dole głównego okna.

## Pierwsze uruchomienie — wybór dostawcy tłumaczeń

W polu **Dostawca tłumaczeń** wybierz, kto tłumaczy tekst. Sekcja pod spodem pokazuje
pola potrzebne dla wybranego dostawcy. Każdy dostawca ma **osobny klucz** — zapisanie
klucza Azure nie usuwa klucza DeepL. Klucze są przechowywane lokalnie z ochroną Windows
DPAPI. Po wpisaniu klucza kliknij **Zapisz klucz**, a potem **Testuj**.

| Dostawca | Kiedy wybrać | Co przygotować |
|---|---|---|
| **DeepL** | domyślny, dobra jakość EN→PL | klucz DeepL API (darmowy kończy się na `:fx`, 500 tys. znaków/mies.) |
| **Azure AI Translator** | najwięcej darmowych znaków | klucz zasobu Translator (plan F0: 2 mln znaków/mies.) i region, np. `westeurope` (puste dla zasobu globalnego) |
| **Google Cloud Translation** | alternatywa dla DeepL | klucz API projektu z włączonym Cloud Translation API |
| **Model językowy (OpenAI / Ollama / LM Studio…)** | tłumaczenie z kontekstem gry; lokalnie — bez internetu | adres serwera i nazwa modelu; klucz tylko dla usług w chmurze |
| **Claude (Anthropic)** | tłumaczenie z kontekstem gry przez Claude | klucz z console.anthropic.com; model domyślny `claude-opus-5-5` |
| **Mock** | test działania bez internetu | nic — dokleja `[PL]` zamiast tłumaczyć |

Bez klucza możesz też korzystać z trybu **Cache-only** (lokalne poprawki, słownik
i zapisane tłumaczenia). Zwykły dostęp do tłumacza na stronie DeepL lub Google nie
zastępuje klucza API.

### Słownik wewnątrz zdań (DeepL, Claude, model językowy)

Terminy z Twojego słownika działają także w środku dłuższych zdań
(np. „Energy Shield” → „Tarcza energetyczna” w „+40 to maximum Energy Shield”):

- **DeepL** dostaje glosariusz zbudowany ze słownika i sam odmienia terminy po polsku.
  Glosariusz zapisuje się na Twoim koncie DeepL jako „GameTranslatorOverlay …”; po zmianie
  słownika aplikacja podmienia go na nowy. Glosariusz przygotowuje się w tle: pierwsza partia
  po zmianie słownika czeka na niego najwyżej 0,3 s, a jeśli nie jest gotowy, idzie bez niego.
  W trybie prywatnym glosariusz nie powstaje, a terminy dodane w trybie prywatnym nie trafiają
  do niego nigdy — także po wyłączeniu trybu prywatnego.
- **Model językowy** i **Claude** dostają pasujące terminy i nazwę gry z profilu.
- **Azure** i **Google** używają słownika tylko dla tekstów, które w całości są terminem.

Terminy są rozpoznawane także w liczbie mnogiej i dopełniaczu („Waystones”, „Exalted Orbs”,
„Waystone's”) oraz gdy OCR złamie je na dwa wiersze („Energy⏎Shield”). Formy mnogiej aplikacja
nie tłumaczy sama (słownik nie zna polskiej odmiany) — termin trafia do dostawcy jako podpowiedź.
Etykieta z dwukropkiem („Rarity:”) jest tłumaczona lokalnie („Rzadkość:”).

**Zakres „Etykieta”.** Krótkie słowa, które w grze są przyciskiem albo nagłówkiem („Save”,
„Attack”), w zdaniu często znaczą coś innego („save the village”). Zaznacz dla takiego terminu
kolumnę **Etykieta** w edytorze słownika (w pliku JSON: `"scope": "label"`). Termin działa wtedy
tylko jako cały napis — nie jest podpowiadany w zdaniach i nie trafia do glosariusza DeepL.
W słowniku ogólnym tak oznaczono Save, Chest, Key, Trade, Attack i Upgrade, w PoE2 — Staff.

Gdy dwa słowniki mają ten sam termin z różnym tłumaczeniem, wygrywa wyższy priorytet, potem
termin z rozróżnianiem wielkości liter, a na końcu słownik wczytany później (ogólny → profil
gry → Twój → sesja). DeepL i tłumaczenie lokalne stosują tę samą regułę.

Tłumaczenia zapisane w cache przed dodaniem terminu nie zmieniają się same. Popraw je ręcznie
albo wyczyść cache.

### Lokalny model — tłumaczenie bez wysyłania tekstu

1. Zainstaluj [Ollamę](https://ollama.com) albo [LM Studio](https://lmstudio.ai)
   i pobierz model (np. w Ollamie: `ollama pull qwen2.5:7b`).
2. Wybierz dostawcę **Model językowy**, kliknij przycisk **Ollama** albo **LM Studio**
   (wpisze adres serwera) i podaj nazwę modelu.
3. Klucz zostaw pusty i kliknij **Testuj** — zobaczysz próbne tłumaczenie.

Adres `http://localhost…` oznacza, że tekst nie opuszcza komputera; aplikacja pokazuje to
pod polem klucza. Adres zdalnego serwera musi zaczynać się od `https://`. Klucz zapisany
dla jednego serwera (np. OpenAI) nie jest wysyłany do innego — po zmianie adresu zapisz
klucz ponownie. Mały model
lokalny tłumaczy wolniej i słabiej niż usługi w chmurze — sprawdza się najlepiej
w trybie ręcznym i przy dialogach.

Obok pola **Adres serwera** są przyciski **OpenAI**, **DeepSeek**, **Ollama** i **LM Studio**.
**DeepSeek** (usługa w chmurze, potrzebny klucz) wpisuje adres `https://api.deepseek.com/v1`,
model `deepseek-flash` i wyłącza „myślenie” modelu — bez tego model zawsze najpierw rozumował,
a paczka 5 linii trwała 7–20 s. **Ollama** wyłącza rozumowanie modelu (`reasoning_effort: none`).
Takie opcje obowiązują tylko dla serwera, dla którego je zapisano; widać je w statusie klucza
(„Opcje serwera: thinking=disabled.”) i w wyniku **Testuj** („…, opcje serwera: …”).

### Claude

Domyślnym modelem jest `claude-opus-5-5`; w polu **Model** możesz wybrać tańszy
i szybszy model (np. `claude-haiku-4-5`) albo wpisać inny identyfikator. Gdy filtr
bezpieczeństwa odrzuci fragment (zdarza się rzadko, np. przy brutalnych opisach),
Claude sam ponawia zapytanie na modelu zastępczym wskazanym przez Anthropic; dotyczy to
modeli `claude-opus-5-5`, `claude-opus-5`, `claude-sonnet-5-5` i `claude-fable-5-1`.
Opłaty rozlicza Anthropic według zużytych tokenów, nie znaków.

### Pamięć dialogu i postać gracza (tylko Claude i model językowy)

Model językowy dostaje do 6 ostatnich linii z tej sesji (łącznie ok. 1500 znaków) razem
z tłumaczeniami, które **sam już dla nich zwrócił**. Dzięki temu trzyma się wybranych form
(„gotowy” czy „gotowa”), formy zwracania się i pisowni imion. Do pamięci nie trafiają teksty
z cache i słownika ani wyniki puste, nieprzetłumaczone i „rozgadane”. Ręczna poprawka linii
(**Popraw**) zastępuje jej tłumaczenie w pamięci, więc kolejne linie trzymają się poprawionej
formy. Pamięć jest tylko w RAM; czyści ją zmiana dostawcy, profilu lub ustawień tłumaczenia
i zamknięcie programu. Zmiana samego wyglądu (czcionka, tło, styl napisów, skróty,
komunikaty) jej nie czyści — status pokazuje wtedy „Wygląd zapisany.”. DeepL dostaje jako
kontekst tylko angielskie linie, jak dotąd.

**Postać gracza** (w oknie głównym obok kroju czcionki): *nieznana*, *mężczyzna* albo
*kobieta*. Po angielsku „you” nie ma rodzaju; przy wybranej płci model odmienia zwroty do
gracza („zrobiłaś”, „jesteś gotowa” / „zrobiłeś”, „jesteś gotowy”). DeepL, Azure, Google
i Mock ignorują to ustawienie. Po zmianie płci linie z cache, które zwracają się do gracza
(you/your/yours/yourself), są raz tłumaczone ponownie — to płatne zapytania. Ręczne poprawki
nie są nadpisywane; w Cache-only i przy błędzie dostawcy zostaje stary wynik.

## Tłumaczenie ręczne (podstawowy tryb)

1. Uruchom grę w trybie **okienkowym** lub **borderless fullscreen** (pełny ekran
   „wyłączny” nie jest obsługiwany). Zalecane: **okno bez ramki** (w ustawieniach grafiki gry
   zwykle „Borderless”, „Windowed fullscreen” albo „Pełny ekran w oknie”). Na wyłącznym pełnym
   ekranie gra często nie daje się przechwycić jako okno — aplikacja przechodzi wtedy na zrzut
   ekranu (w kadrze mogą się znaleźć inne okna), a nakładka może migać albo chować się pod grą.
   W trybie live, gdy okna gry nie da się przechwycić, a gra zajmuje cały monitor, w nakładce
   (przy włączonych **Komunikatach w nakładce**) pojawi się raz na sesję (na 8 s) komunikat „⚠ Pełny ekran utrudnia nakładkę — przełącz na okno
   bez ramki”, a w oknie aplikacji „⚠ Przełącz grę na okno bez ramki — pełny ekran utrudnia
   nakładkę.”. Aplikacja nie odróżnia jeszcze wyłącznego pełnego ekranu od okna bez ramki na cały
   monitor — jeśli gra już działa bez ramki, komunikat znaczy tylko, że live czyta zrzut ekranu
   (patrz [Uwaga o prywatności w trybie live](#uwaga-o-prywatności-w-trybie-live)).
2. Wciśnij **Ctrl+Shift+T** — ekran przyciemni się; zaznacz myszą fragment z tekstem
   (tooltip, dialog). **Esc** albo ponowne **Ctrl+Shift+T** anuluje zaznaczanie.
3. Tłumaczenie pojawi się w panelu obok zaznaczenia (albo w nakładce — do wyboru
   w „Wyświetlanie wyniku”). Panel nie zabiera grze fokusu; **Ctrl+Shift+H** chowa nakładkę.

W panelu wyniku możesz:

- **Kopiuj** — skopiować tłumaczenie,
- **Popraw** — wpisać własne tłumaczenie; zostanie zapamiętane i od tej pory zawsze wygrywa,
- **+ Słownik** — dodać parę termin→tłumaczenie do prywatnego słownika.

## Tryb live (automatyczny)

1. Wybierz okno gry z listy i kliknij **▶ Start live** — albo w grze wciśnij **Ctrl+Shift+L**
   (opis niżej).
2. Program obserwuje okno kilka razy na sekundę; gdy pojawi się nowy, stabilny tekst,
   tłumaczy go automatycznie i pokazuje w nakładce.
3. „Wyświetlanie” wybiera układ: **Przy oryginale** (dymki przy tekście) albo
   **Napisy na dole** (pasek jak napisy filmowe — najlepszy do dialogów). Przy oryginale
   pole **Położenie dymków** decyduje, czy tłumaczenie stoi **Pod oryginałem**, czy
   **Na oryginale (zakrywa)** — w miejscu napisu gry
   ([opis niżej](#tryb-na-oryginale-zakrywa--napis-jak-w-grze)).
4. **⏹ Stop** (albo ponownie **Ctrl+Shift+L**) kończy tryb live. Minimalizacja gry chowa
   nakładkę automatycznie.

Jeżeli aplikacja rozpozna dostarczony profil, może dobrać go do wybranego okna.
Pozostałe gry działają na ustawieniach ogólnych. Profil PoE2 w zestawie jest
opcjonalnym dodatkiem ze słownikiem terminów. Profil Escape Academy rozpoznaje grę po nazwie
procesu (`Escape Academy.exe`; bez zmiany ustawień OCR), wskazuje krój napisów (Lexend Deca)
i zawiera receptę korpusu dla narzędzia `CorpusTool`; samej recepty aplikacja nie używa.

### Start i stop live skrótem (Ctrl+Shift+L)

Skrót działa bez przełączania się do okna tłumacza. Gdy live działa, skrót je zatrzymuje
(jak **⏹ Stop**). Gdy nie działa, tłumacz wybiera okno w tej kolejności:

1. **aktywne okno** — to, w którym jesteś w chwili wciśnięcia skrótu, chyba że to pulpit,
   pasek zadań, menu Start, wyszukiwarka, klawiatura ekranowa albo sam tłumacz;
2. **ostatnio tłumaczona gra** (najpierw okno o tym samym tytule, potem największe okno
   tego procesu);
3. **gra z profilem**, jeśli jest uruchomiona;
4. jeśli nic nie pasuje — komunikat „Przełącz się do gry i wciśnij skrót ponownie.” w pasku
   statusu i w zasobniku.

Gdy wybrano „brak profilu”, a gra ma profil, profil włącza się przed startem (jak przy kliknięciu
okna na liście). Po uruchomieniu aplikacji i po **Odśwież** ostatnia gra jest tylko zaznaczana
na liście — live nie startuje sam, a profil się nie zmienia.

Menu ikony w zasobniku ma pozycję **▶ Start live na aktywnej grze** (w trakcie sesji
**⏹ Stop live**); podpowiedź ikony pokazuje „live: włączony / wyłączony”. Przycisk
**▶ Start live** pokazuje skrót, jeśli udało się go zarejestrować.

Skrót zmienisz w `settings.json` (folder danych) polem `liveToggleHotkey`, np.
`"liveToggleHotkey": "Ctrl+Alt+F9"`. Gdy skrót jest zajęty przez inny program albo zapisany
błędnie, pasek statusu to pokazuje, a reszta aplikacji działa.

Ograniczenia:

- Skrót tłumaczy dokładnie aktywne okno — jeśli na pierwszym planie jest przeglądarka,
  live uruchomi się na przeglądarce. Zatrzymasz je tym samym skrótem.
- Gry UWP / Microsoft Store / Game Pass (okno `ApplicationFrameHost`) nie są wybierane jako
  aktywne okno. Wybierz grę raz na liście i kliknij **▶ Start live**; później skrót znajdzie
  ją jako ostatnią grę.

### Komunikaty w nakładce

W trakcie gry okno aplikacji jest zwykle schowane, więc ważne zdarzenia pokazują się jako
krótki, półprzezroczysty pasek wyśrodkowany przy górnej krawędzi okna gry. Pasek znika po
3–5 s (komunikat o pełnym ekranie po 8 s) i nie przyjmuje kliknięć. W tabeli „DeepL”
i „Claude” stoją za nazwą wybranego dostawcy; pełny opis błędu jest w oknie aplikacji.

| Komunikat w nakładce | Co znaczy | Co zrobić |
|---|---|---|
| „▶ Tłumaczenie na żywo włączone”, „■ Tłumaczenie na żywo zatrzymane” | start i stop trybu live | — |
| „⚠ Brak klucza DeepL” | dostawca nie ma zapisanego klucza | wpisz klucz, **Zapisz klucz**, **Testuj** |
| „⚠ Klucz DeepL został odrzucony” | dostawca odrzucił klucz | sprawdź klucz ([Rozwiązywanie problemów](#rozwiązywanie-problemów)) |
| „⚠ Limit znaków DeepL wyczerpany” | wyczerpany limit konta u dostawcy | zmień dostawcę albo włącz Cache-only |
| „⏳ DeepL ogranicza zapytania”, „⏳ DeepL nie odpowiada na czas” | dostawca chwilowo dławi zapytania albo odpowiada za wolno | zwykle mija samo; gdy się powtarza, sprawdź połączenie |
| „⚠ Brak połączenia z dostawcą”, „⚠ DeepL chwilowo niedostępny” | brak sieci, serwer nieosiągalny albo awaria usługi | sprawdź internet; lokalny model — uruchom jego serwer |
| „⚠ Model Claude niedostępny — sprawdź ustawienia”, „⚠ Ustawienia DeepL są niepełne” | zła nazwa modelu albo brak adresu, modelu lub regionu | popraw pola dostawcy w oknie aplikacji |
| „⚠ Claude odmówił tłumaczenia fragmentu” | filtr treści dostawcy | inny model albo dostawca dla tego fragmentu |
| „⚠ Tekst za długi dla DeepL”, „⚠ DeepL odrzucił zapytanie” | dostawca nie przyjął tekstu | zaznacz mniejszy fragment (**Ctrl+Shift+T**); szczegóły w logu |
| „⚠ DeepL zwrócił pusty wynik” | pusty wynik dostawcy (zostaje oryginał) | [Kontrola jakości](#kontrola-jakości-tłumaczeń) |
| „⚠ Błąd tłumaczenia (DeepL)” | inny, nierozpoznany błąd dostawcy | opis w oknie aplikacji; gdy się powtarza, zajrzyj do logów |
| „⚠ Limit znaków tej sesji wyczerpany” | osiągnięty limit `sessionCharacterLimit` z `settings.json` | zwiększ albo usuń limit w `settings.json` |
| „Cache-only: 5 tekstów bez tłumaczenia” | tych tekstów nie ma w lokalnych wynikach | celowe — Cache-only nie pyta dostawcy |
| „⚠ Cache niedostępny — tłumaczenia nie są zapisywane” | baza tłumaczeń nie działa | [Gdy baza tłumaczeń nie działa](#słownik-i-dane) |
| „⚠ Pełny ekran utrudnia nakładkę — przełącz na okno bez ramki” | okna gry nie da się przechwycić, a gra zajmuje cały monitor | przełącz grę na okno bez ramki ([wyżej](#tłumaczenie-ręczne-podstawowy-tryb)) |
| „ℹ Nie rozpoznano tekstu w zaznaczeniu” | ręczne tłumaczenie nie znalazło tekstu | zaznacz większy fragment |
| „⚠ Tłumaczenie nie powiodło się” | ręczne tłumaczenie skończyło się błędem | zajrzyj do logów (folder danych → `logs\`) |

Ten sam komunikat pojawia się najwyżej raz na 30 s. Błąd wypiera informację, a informacja
nie przykrywa widocznego błędu. Start i stop live są pokazywane zawsze. Braki w Cache-only
są zbierane w jeden licznik, który wraca dopiero przy nowych tekstach; ostrzeżenie
o niedziałającym cache pojawia się raz na sesję live.

Przy nakładce schowanej **Ctrl+Shift+H** przechodzą tylko komunikaty krytyczne: zatrzymany
live, brak lub odrzucony klucz, wyczerpany limit dostawcy albo sesji. Napisy pozostają schowane.
Ręczne tłumaczenie (**Ctrl+Shift+T**) w trybie „Nakładka na ekranie” zawsze daje odpowiedź —
także przy schowanej nakładce: tłumaczenie albo komunikat („ℹ Nie rozpoznano tekstu
w zaznaczeniu”, błąd dostawcy, braki Cache-only, „⚠ Tłumaczenie nie powiodło się”).

Komunikaty zawierają tylko stały opis i nazwę dostawcy, nigdy tekst z ekranu. Wyłączysz je
polem **Komunikaty w nakładce** (pod Cache-only i trybem prywatnym); błędy widać wtedy tylko
w oknie aplikacji.

### Jak uzyskać czytelny wynik

- Do opisów i menu wybierz **Przy oryginale**. W polu **Położenie dymków** opcja
  **Na oryginale (zakrywa)** stawia tłumaczenie w miejscu tekstu gry
  ([opis niżej](#tryb-na-oryginale-zakrywa--napis-jak-w-grze)); **Pod oryginałem** pokazuje
  je poniżej.
- Do dialogów możesz wybrać **Napisy na dole**. Jest to wspólny pasek świeżych
  tekstów rozpoznanych przez live, bez automatycznego rozpoznawania rodzaju wypowiedzi.
- Po ruchu kamery daj obrazowi na chwilę się zatrzymać. Silny ruch nadal może
  tymczasowo usuwać nakładkę, a nowe tłumaczenie może czekać na OCR i dostawcę.
- Przy trudnej czcionce lub konkretnym opisie użyj **Ctrl+Shift+T** i zaznacz
  interesujący fragment. Automatyczne wydzielanie obszaru tooltipu jest w planach.

Stałe menu może pozostać podczas ruchu tła, jeśli obraz pod jego napisami nie zmienił
się ani o piksel. Zmieniony lub zasłonięty napis traci tę ochronę. Animowane tło,
skalowanie odczytu lub zapasowe przechwytywanie ekranu mogą uniemożliwić zachowanie menu.

**Znikanie starego napisu.** Gdy napis gry znika, tłumaczenie znika razem z nim. Jeśli
najbliższy odczyt OCR nie widzi napisu, a w jego miejscu nie zostało nawet ćwierć kontrastu,
jaki napis miał przy ostatnim odczycie, nakładka zdejmuje tłumaczenie — w powtórkach
syntetycznych (narzędzie SceneReplay, nie w grze) po 0,3–0,55 s, także na teksturowanym tle
(przed zmianą: 0,9 s, ok. 6 s albo wcale). Gdy w miejscu napisu zostaje coś jasnego i piksele tego
nie rozstrzygają, tłumaczenie znika po kilku odczytach OCR — śmieciowy odczyt w tym miejscu go
nie podtrzymuje, a pełny odczyt ekranu przychodzi co 4 s także wtedy, gdy gdzie indziej coś
ciągle się rusza. Napis przygaszony albo ciemniejszy, który OCR nadal czyta, zachowuje
tłumaczenie; napis przygaszony poniżej ćwierci dawnej jasności może raz zniknąć na ok. 0,6 s.
W **Napisach na dole** tłumaczenie znika z paska razem z napisem gry, a gdy napis wróci
w ciągu 10 s, wraca też na pasek. Nowy napis w miejscu poprzedniego, gdy zniknięcia
poprzedniego nie widać w pikselach, może czekać na tłumaczenie do 10 s. Bieżący kod dokładniej
dopasowuje też pozycję. Te mechanizmy ograniczają błędy, ale nie zapewniają jednakowego czasu
i wyglądu w każdej grze.

### Tryb „Na oryginale (zakrywa)” — napis jak w grze

Ustaw **Wyświetlanie: Przy oryginale** i **Położenie dymków: Na oryginale (zakrywa)**, a grę
uruchom w oknie bez ramki. Tryb działa jak spolszczenie, ale nadal wyłącznie na obrazie:

- **Z obrazu gry znikają same litery oryginału** — ich piksele są wypełniane kolorami tła
  z otoczenia, a reszta obrazu gry zostaje żywa i nietknięta (łatka poza literami jest
  przezroczysta). Taka łatka powstaje w trybie live; ręczne tłumaczenie regionu w nakładce
  zakrywa tekst jak dotąd.
- **Polski tekst wygląda jak napis gry:** kolor, kontur, cień, wysokość liter i linia bazowa są
  zmierzone z napisu gry. Pomiar na klatkach 4K z Escape Academy: wysokość liter 0,96–1,10×
  oryginału, linia bazowa w 0–1 px (przed zmianą 0,54–0,58×).
- **Rozmiar wynika z oryginału.** Pole **Rozmiar czcionki (0 = jak oryginał)** w tym trybie nie
  działa. **Tło dymków** dotyczy tylko dawnej, rozmytej łatki, której aplikacja używa, gdy
  łatki z wypełnionymi literami nie udało się zbudować.
- **Dłuższy polski tekst** w dialogu najpierw lekko się zmniejsza (najwyżej do 85%), potem łamie
  się w szerokości oryginału z jego odstępem wierszy. Pojedyncza etykieta może wejść w wolne
  miejsce obok (do następnego napisu albo krawędzi monitora) i nie wychodzi poza monitor.
  Jednowierszowy napis wyśrodkowany na ekranie rośnie w obie strony.
- **Ikony zostają:** ikona klawisza przed napisem („X Hint” → ikona X + „Podpowiedź”) i ikonka
  za napisem (np. ✓) nie są zamazywane, a litera odczytana z ikony nie pojawia się w tłumaczeniu
  na ekranie.
- **Tłumaczenie identyczne z oryginałem** (nazwy, „OK”, logo) nie jest rysowane — widać grę.
- **Jedna grubość w jednym stylu:** napisy o tym samym rozmiarze, kolorze i obrysie (np. pozycje
  menu) dostają tę samą grubość kroju.
- **Ruchome tło:** gdy tło pod napisem się rusza, łatka obejmuje całe pole napisu z miękkim
  brzegiem, dopóki obraz nie stanie.
- **Ruch kamery:** tłumaczenie jedzie razem z napisem (HUD stoi, napis na ścianie przesuwa się
  z kamerą), a tło łatki jest odświeżane z bieżącego obrazu do ok. 30 razy na sekundę. Napis,
  którego nie da się już znaleźć w ruchu (odjechał, zasłonięty, mocno zmienił rozmiar przy
  podchodzeniu), znika i wraca po kolejnym odczycie OCR — w ruchu co ok. 0,9 s.
- **Kwestia pisana literami** (gdy gra ma korpus) zostaje po angielsku, dopóki gra ją wypisuje —
  [opis w sekcji o korpusie](#spolszczenie-z-wyprzedzeniem-korpus-gry).

**Krój czcionki → Jak w grze (krój z profilu)** (domyślnie) bierze krój z profilu gry, we
wszystkich układach nakładki. Profil Escape Academy używa dołączonego kroju Lexend Deca (licencja
SIL OFL 1.1, patrz `THIRD-PARTY-NOTICES.md`); w trybie zakrywania wariant (od zwykłego do
pogrubionego) jest dobierany do grubości liter w grze. Bez profilu albo przy profilu bez kroju
jest to Segoe UI. Wybór innej czcionki z listy ma pierwszeństwo przed profilem. Ustawienia
z poprzednich wersji z domyślnym Segoe UI przechodzą raz na „Jak w grze” — jeśli wolisz Segoe UI,
wybierz je ponownie. Krój dla innej gry wskazuje profil gry polem `overlay.fontFamily` (nazwa
kroju zainstalowanego w Windows albo `Lexend Deca`).

Znane ograniczenia (z przeglądu klatek gry i powtórek):

- Przy szybkim ruchu kamery tło łatki zostaje o ułamek sekundy za obrazem gry (w pomiarach na
  nagraniach brzeg łatki w ruchu różni się od tła (mediana) o 8–11 poziomów jasności, w spoczynku
  o 2–3). Napis na ścianie, który przy podchodzeniu rośnie albo obraca się w perspektywie,
  bywa gubiony i pokazywany ponownie po odczycie. Na gęstej teksturze w kolorze liter łatka może
  wygładzić fragment tła albo nie powstać (wtedy dawna, rozmyta łatka).
- Kursywa, odstępy między literami i szerokość kroju gry nie są odwzorowane; Lexend Deca jest
  węższy niż krój Escape Academy, a polski wiersz dialogu bywa dłuższy od oryginału. Profil ma
  jeden krój — także napis szeryfowy dostaje Lexend Deca.
- Długi pojedynczy wiersz bez wolnego miejsca obok zmniejsza się najwyżej do 85% i dalej wystaje;
  przy przyciskach z ramką może wyjść poza ramkę.
- Wygląd sprawdzono tylko na klatkach 4K z Escape Academy (narzędzie OverlayPreview) — nie
  w oknie gry na żywo ani w innych grach.

## Spolszczenie z wyprzedzeniem (korpus gry)

Dla gier offline, których teksty da się bezpiecznie odczytać z plików (dziś: **Escape
Academy**), możesz raz przygotować lokalny **korpus** — listę angielskich tekstów gry — i od razu
przetłumaczyć go w całości. W trakcie gry nakładka rozpoznaje wtedy odczyt OCR jako znany tekst
(także z błędami OCR, innym zawinięciem wierszy albo WIELKIMI LITERAMI) i pokazuje gotowe
tłumaczenie z lokalnej bazy, bez czekania na dostawcę. Nakładka nadal nie czyta plików gry —
robi to wyłącznie osobne narzędzie `CorpusTool`, uruchamiane przez Ciebie przy wyłączonej grze
([ADR-014](https://github.com/KubaZx/Game_Translator/blob/main/docs/TECHNOLOGY_DECISIONS.md)).

### Kiedy korpus ma sens

- Gra jest **jednoosobowa i offline**, a jej teksty leżą w plikach jako czytelne tabele opisane
  „receptą” w profilu gry (sekcja `corpus` w `profiles/<id>/profile.json`). Dziś receptę ma tylko
  profil Escape Academy; narzędzie czyta rodzinę formatów Unity TextAsset (kontener UnityFS albo
  luźny plik serializowany Unity).
- Najwięcej zyskujesz w grze z dużą ilością menu, dialogów i napisów: znane teksty nie czekają na
  dostawcę, a odczyty z błędami OCR trafiają w poprawne zdanie.
- Tekst namalowany w teksturach i grafikach nie trafi do korpusu — dalej tłumaczy go OCR
  i dostawca.

Zasady narzędzia (ADR-014) są zaszyte w kodzie, bez opcji obejścia:

- **gra wyłączona** — odmowa, gdy działa proces gry z profilu albo dowolny program z folderu gry,
- **bez gier online i z anti-cheatem** — odmowa przy folderach EasyAntiCheat / BattlEye, profilu
  oznaczonym jako online i grach z listy wykluczeń (Path of Exile 1 i 2),
- **bez podpisanych i zaszyfrowanych plików** — narzędzie nie ma kodu deszyfrującego,
- **tylko odczyt** — niczego nie zapisuje w folderze gry; wyniki nie mogą leżeć w folderze gry,
  pod `steamapps\common` ani w bibliotekach Epic i GOG,
- **bez sieci przy odczycie** — `extract` niczego nie pobiera ani nie wysyła; jedyny ruch
  sieciowy to tłumaczenie korpusu (`translate`) u dostawcy, którego sam wskażesz,
- **tylko lokalnie** — korpus i tłumaczenia zostają w folderze danych aplikacji; teksty gier są
  chronione prawem autorskim, więc nie przenoś ich i nie udostępniaj,
- **tryb prywatny** — gdy jest włączony w aplikacji, `translate` odmawia pracy, zanim cokolwiek
  wyśle (przebieg próbny `--dry-run` tylko ostrzega).

Gry, których to nie obejmuje, tłumaczysz jak dotąd — z obrazu.

### Krok po kroku

Narzędzie nie jest w paczce aplikacji; uruchamiasz je ze źródeł projektu
(`git clone https://github.com/KubaZx/Game_Translator`, potrzebny .NET 10 SDK), w PowerShellu
z folderu repozytorium. Pierwsze `dotnet run` najpierw buduje narzędzie.

1. **Wyłącz grę** (także jej launcher z folderu gry, jeśli go ma). Gdy gra działa, narzędzie
   kończy się komunikatem „Gra jest uruchomiona (proces: …). Zamknij ją i spróbuj ponownie.”.
   Gdy wskażesz podfolder gry, zabezpieczenia sprawdzą cały jej folder (np.
   `steamapps\common\<gra>`).
2. **Odczytaj korpus** (tylko do odczytu, bez sieci; sam odczyt Escape Academy trwa ok. 0,2 s):

   ```powershell
   dotnet run --project tools/GameTranslatorOverlay.CorpusTool -c Release -- extract `
     --profile escape-academy --game-dir "C:\Program Files (x86)\Steam\steamapps\common\Escape Academy"
   ```

   `--game-dir` to folder instalacji gry (w Steam pokazuje go *Zarządzaj → Przeglądaj pliki
   lokalne*). Narzędzie wypisuje same liczby (wpisy, unikalne teksty, znaki) i ścieżkę zapisu.
   Korpus trafia do `%LOCALAPPDATA%\GameTranslatorOverlay\corpus\escape-academy.corpus.jsonl`
   (**Folder danych** → `corpus\`) — tylko tam szuka go aplikacja.
3. **Przetłumacz korpus** u wybranego dostawcy. Klucz podajesz w zmiennej środowiskowej —
   narzędzie nie czyta kluczy zapisanych w aplikacji. Przykład z DeepSeek bez myślenia: najpierw
   przebieg próbny (liczba tekstów, znaków i partii oraz szacunek kosztu; nic nie wysyła i nic
   nie zapisuje do bazy), potem właściwy przebieg:

   ```powershell
   $env:GTO_LLM_ENDPOINT = "https://api.deepseek.com/v1"
   $env:GTO_LLM_MODEL = "deepseek-flash"
   $env:GTO_LLM_KEY = "<Twój klucz>"
   dotnet run --project tools/GameTranslatorOverlay.CorpusTool -c Release -- translate `
     --profile escape-academy --provider llm --dry-run
   dotnet run --project tools/GameTranslatorOverlay.CorpusTool -c Release -- translate `
     --profile escape-academy --provider llm --llm-thinking disabled
   ```

   Przebieg próbny dla Escape Academy: 7 632 teksty, 248 979 znaków, szacunek DeepSeek bez
   myślenia ok. 0,12–0,24 USD za całą grę. DeepL: `$env:GTO_DEEPL_KEY = "<Twój klucz>"`
   i `--provider deepl` (szacunek: ok. połowy miesięcznego limitu API Free; terminy słownika
   trafiają do glosariusza na koncie DeepL, chyba że dodasz `--no-deepl-glossary`). Lokalny
   serwer (Ollama, LM Studio): adres `http://localhost…` w `GTO_LLM_ENDPOINT`, bez klucza —
   szacunek kosztu wynosi wtedy 0.

   Przerwany przebieg (Ctrl+C, błąd sieci) dokończysz, uruchamiając to samo polecenie jeszcze
   raz: zapisane wpisy zostają, a teksty z aktualnym wpisem są pomijane. Twoje ręczne poprawki,
   wpisy zatwierdzone i słownik zostają nietknięte. Na ekranie są same liczby — teksty gry
   i tłumaczenia trafiają tylko do lokalnej bazy tłumaczeń (`cache.db` w folderze danych).
4. **Uruchom aplikację** (jeśli działała — zamknij ją i otwórz ponownie, żeby wczytała korpus
   i nowe wpisy) i wybierz **Escape Academy** w polu **Profil gry**. Przy profilu „— brak profilu
   (tryb uniwersalny) —” wystarczy zaznaczyć okno gry na liście — profil włączy się sam
   z komunikatem „Wykryto grę „Escape Academy” — profil włączony automatycznie.”. Po ręcznym
   wyborze profilu pasek stanu pokazuje „Ustawienia zapisane (dostawca: …, profil: Escape
   Academy, korpus: … tekstów).”; liczbę tekstów korpusu zapisuje też log (folder danych →
   `logs\`) przy starcie i przy każdej przebudowie ustawień tłumaczenia. Brak „korpus: …”
   = aplikacja nie znalazła pliku korpusu albo nie mogła go wczytać (powód w logu).
5. **Graj z trybem live** jak zwykle (`Ctrl+Shift+L`). Po przetłumaczeniu korpusu możesz włączyć
   **Cache-only** — znane teksty działają wtedy bez internetu, a nowe nie są wysyłane.

Najczęściej przydatne opcje (pełna lista: `--help` albo
[README narzędzia](https://github.com/KubaZx/Game_Translator/blob/main/tools/GameTranslatorOverlay.CorpusTool/README.md)):

| Polecenie | Opcja | Działanie |
|---|---|---|
| `extract` | `--out PLIK` | inny plik korpusu (aplikacja czyta tylko domyślne miejsce) |
| `extract`, `translate` | `--data-dir KATALOG` | inny folder danych niż `%LOCALAPPDATA%\GameTranslatorOverlay` |
| `extract`, `translate` | `--stats PLIK` | statystyki do pliku JSON — same liczby, bez tekstów gry |
| `translate` | `--limit N` | najwyżej N tekstów (dialogi, napisy, potem UI) — tani przebieg pilotażowy |
| `translate` | `--kinds ui,dialog,subtitle` | tylko wybrane rodzaje tekstów |
| `translate` | `--skip-cached` | pomija teksty, które mają już aktualny automatyczny wpis (także bez profilu, np. z gry na żywo) |
| `translate` | `--force` | tłumaczy ponownie automatyczne wpisy profilu (nigdy korekt, wpisów zatwierdzonych i słownika) |
| `translate` | `--player-gender male\|female\|unknown` | płeć postaci gracza dla modelu językowego; domyślnie z ustawień aplikacji |
| `translate` | `--batch N`, `--parallel N` | mniejsze partie, liczba partii naraz (domyślnie 3, lokalny serwer 1) |
| `translate` | `--price-in`, `--price-out`, `--price-chars` | własne stawki do szacunku kosztu (USD za 1 mln tokenów modelu albo znaków DeepL) |

Kody wyjścia: 0 — gotowe, 1 — błąd (np. brak korpusu albo klucza), 2 — złe opcje, 3 — odmowa
(zabezpieczenia, tryb prywatny, brak profilu albo recepty), 4 — część partii `translate`
nieudana (ponowne uruchomienie je dokończy), 130 — przerwano.

Co się zmienia w grze:

- Znane teksty pojawiają się bez zapytania do dostawcy — w powtórce 242 bloków z dawnych sesji
  Escape Academy (przez pipeline z Mockiem, nie w grze) ok. 76% znaków i 62% bloków było gotowych
  lokalnie (bez korpusu: 0,3% znaków), a do dostawcy poszło 57 zapytań zamiast 241.
- Odczyt z błędami OCR („Itls”, „11m”, ucięty koniec zdania) dostaje tłumaczenie poprawnego zdania.
- **Krótkie etykiety z pomyłkami OCR** („Ihspect” zamiast „Inspect”, „Userltem” zamiast
  „Use Item”) dostają tłumaczenie etykiety z korpusu. Bez przyciągania zostają odczyty, gdy dwie
  etykiety są podobnie blisko, gdy odczyt jest innym słowem z gry („Exit” nie zostaje „Edit”),
  gdy na brzegu brakuje albo przybywa zwykłej litery („The fir” nie zostaje „The Fire”) i gdy
  nie zgadzają się liczby. Etykieta odczytana raz poprawnie, raz z pomyłką nie jest podmieniana
  na ekranie.
- **Dialog pisany literami.** Gdy odczyt jest jednoznacznym początkiem jednej linii dialogu albo
  napisów z korpusu (od 12 liter i 3 słów), aplikacja zna całą kwestię:
  - w trybie **Na oryginale (zakrywa)** kwestia zostaje po angielsku, dopóki gra ją wypisuje,
    a gdy gra skończy, od razu pojawia się całe polskie tłumaczenie (w powtórce syntetycznej
    ok. 0,1–0,3 s po ostatniej literze, bez polskiego tekstu na dopisywanych angielskich literach).
    Za koniec pisania aplikacja uznaje pełną linię albo brak nowych liter przez 0,9 s;
    najdłużej kwestia czeka 8 s. W tym trybie czeka też krótszy początek kwestii z korpusu (od
    4 liter). Niedokończony początek nie jest w tym czasie wysyłany do dostawcy,
  - w trybach **Pod oryginałem** i **Napisy na dole** tłumaczenie całej kwestii może pojawić się,
    zanim gra wypisze ją do końca; dłuższe odczyty tej samej kwestii trafiają w ten sam wpis
    (bez nowych zapytań) i aktualizują napis w miejscu,
  - gdy tej kwestii nie ma jeszcze w bazie, do dostawcy idzie całe zdanie z korpusu, także jego
    niewyświetlona jeszcze część,
  - ograniczenia: pauza dłuższa niż 0,9 s w środku kwestii pokaże pełne tłumaczenie już w tej
    pauzie (dopisane potem litery wyjdą spod łatki do następnego odczytu); tekst spoza korpusu,
    który zaczyna się jak kwestia z korpusu, czeka 0,9 s; dwie kwestie o wspólnym początku czekają,
    aż odczyt je rozróżni; w trybach z tekstem obok oryginału jednowierszowy początek kwestii
    dostaje tłumaczenie w jednym wierszu (może wyjść poza okno dialogu), dopóki gra nie zacznie
    drugiego wiersza.
- **Śmieci OCR** (zlepki liter odczytane z ikon i tekstur, same liczby typu „11/11”) przy aktywnym
  korpusie nie idą w trybie live do dostawcy ani na nakładkę — oryginał zostaje widoczny. Termin
  słownika nie jest śmieciem. Koszt: prawdziwy krótki napis, którego słów nie ma w plikach gry
  (np. napis z tekstury), też zostaje bez tłumaczenia — dodaj go w **📖 Słownik…** z własnym
  tłumaczeniem albo przetłumacz ręcznie (**Ctrl+Shift+T**; tłumaczenie regionu nie używa tego filtra).
- Pomiar na tekstach z pierwszej sesji gracza na wersji z korpusem (36 tekstów, które poszły do
  DeepL; powtórka z Mockiem): po poprawkach etykiet, dialogu i śmieci 15 obsłużonych lokalnie,
  11 odrzuconych jako śmieci, do dostawcy 10 zamiast 36.
- Liczby muszą się zgadzać: odczyt z innym znakiem, walutą albo procentem („-10%” zamiast „+10%”)
  albo z inną liczbą sklejoną z literami („6kg” zamiast „5kg”) nie jest przyciągany do korpusu
  i idzie do tłumaczenia tak, jak go odczytano. Wyjątek: cyfra w miejscu litery, którą OCR myli
  z cyfrą („o1d” → „old”, „11m” → „I'm”).
- Tłumaczenie zachowuje układ wierszy z ekranu. Imię mówcy („Captain:”, „[CAPTAIN]”), klawisze
  obok etykiet („E Inspect” → „E Zbadaj”) i pojedyncze liczby zostają bez zmian.
- Nieznana część bloku (np. nowa linia pod znaną etykietą) idzie do dostawcy sama — znana część
  nie jest płacona drugi raz.
- Ręczna poprawka bloku, który w całości jest jednym tekstem z korpusu odczytanym dokładnie
  (najwyżej inna wielkość liter albo zawinięcie wierszy), zapisuje się pod tym tekstem, więc
  działa także dla innych odczytów tego samego zdania. Gdy blok został dopasowany przybliżeniem
  (odczyt z błędami OCR) albo składa się z kilku części, poprawka działa dla tego odczytu, jak dotąd.
- Bez pliku korpusu i bez profilu aplikacja działa dokładnie jak dotąd. Cache-only działa także
  z korpusem. **W trybie prywatnym** aplikacja nie czyta bazy z dysku, więc tłumaczenia
  z wyprzedzeniem są wtedy niedostępne (przyciąganie nadal ujednolica odczyty w pamięci).
- Po nowym `extract` albo `translate` uruchom aplikację ponownie — działająca aplikacja pamięta
  w RAM tłumaczenia, które już odczytała z bazy, i do restartu może je pokazywać zamiast nowych
  wpisów korpusu.

Opcja dla zaawansowanych: `"paragraphCacheKeys": true` w `settings.json` zapisuje tłumaczenia
po akapitach także bez korpusu (każda gra) — ten sam akapit w innym bloku nie jest płacony drugi
raz (np. statystyki przedmiotów w PoE2). Domyślnie wyłączone.

## Szybkość

Panel **Szybkość** pokazuje medianę czasów z bieżącej sesji (p90 = 9 na 10 przypadków
było szybszych):

- **Zmiana → napis** (tylko live) — od pierwszej klatki, w której zauważono zmianę obrazu, do
  gotowych napisów: łącznie z czekaniem na stabilizację, OCR, kolejką i dostawcą. To czas
  najbliższy temu, co widzi gracz,
- **Nowy tekst** — od przechwycenia klatki do gotowego napisu, gdy trzeba było zapytać dostawcę,
- **Znany** — to samo dla tekstu z cache albo słownika,
- **nazwa dostawcy** (np. DeepL) — samo zapytanie do dostawcy,
- **OCR** i **Klatka** — rozpoznanie tekstu i przechwycenie obrazu.

**Kopiuj raport** kopiuje szczegóły do schowka (bez tekstu z gry), a **Wyzeruj** zaczyna
pomiar od nowa, np. po zmianie dostawcy. Czasy nie obejmują samego rysowania nakładki.
Pomiary są tylko w pamięci, bez tekstu z gry.

## Kontrola jakości tłumaczeń

Każdy wynik dostawcy jest sprawdzany lokalnie (bez sieci, bez ustawień):

- **pusty wynik** — pokazywany jako błąd „Dostawca zwrócił pusty wynik.”, nie trafia do cache
  i liczy się do licznika „Błędy”; w grze zostaje oryginał,
- **zmienione lub zgubione liczby** („1,000”, „1 000” i „1.000” oraz „2.5” i „2,5” są
  traktowane jako równe; dodatkowe liczby w tłumaczeniu są dozwolone),
- **wynik identyczny z angielskim oryginałem**,
- **podejrzanie długi wynik** (model dopisał komentarz).

Wynik z problemem jest pokazywany i zapisywany w cache ze znacznikiem. Modele językowe
(Claude, model językowy) dostają od razu jedno ponowienie tego samego tekstu — to dodatkowe,
płatne zapytanie, liczone w limicie znaków sesji (przy wyczerpanym limicie ponowienia nie ma).
DeepL, Azure i Google nie są ponawiane, bo zwróciłyby to samo. Przy następnym wystąpieniu
oznaczony tekst jest tłumaczony ponownie jeszcze raz; jeśli problem zostaje, wynik jest
ostateczny. Razem tekst z trwałym problemem kosztuje najwyżej 3 zapytania u modeli językowych
i 2 u DeepL/Azure/Google. Opis problemu nie jest jeszcze pokazywany w oknie ani w nakładce.

## Słownik i dane

- **📖 Słownik…** — edytor prywatnego słownika: dodawanie, edycja, priorytety,
  import/eksport JSON, wykrywanie konfliktów.
- **Wyczyść cache** — usuwa automatyczne tłumaczenia (ręczne poprawki zostają).
- **Eksport/Import cache…** — kopia zapasowa tłumaczeń (w tym poprawek) do pliku JSON.
- **Folder danych** — otwiera `%LOCALAPPDATA%\GameTranslatorOverlay` (cache, ustawienia, logi).
- Eksport/import zachowuje znacznik formatu i jakości wpisu (pole `context`); starsze pliki
  bez tego pola nadal się importują.

**Gdy baza tłumaczeń nie działa** (uszkodzony plik, blokada przez inny program, pełny dysk,
plik tylko do odczytu): tłumaczenie działa dalej, a w grze raz na sesję live pojawia się
„⚠ Cache niedostępny — tłumaczenia nie są zapisywane”. Wyniki są wtedy pamiętane tylko
w pamięci programu (do 2000 tekstów, do zmiany dostawcy lub ustawień), więc ten sam tekst
nie idzie drugi raz do dostawcy w tej sesji — ale każdy nowy tekst kosztuje jedno zapytanie
i po restarcie nic z tego nie zostaje. Błąd zapisu samych liczników użycia trafia tylko do logu.
W Cache-only przy niedziałającej bazie nic nie jest wysyłane.

## Prywatność

- Do internetu wysyłany jest **wyłącznie rozpoznany tekst** (nigdy obraz) i tylko do
  wybranego dostawcy tłumaczeń. Modele językowe dostają dodatkowo nazwę gry z profilu
  i pasujące terminy słownika, a DeepL glosariusz ze słownika (zapisany na Twoim koncie
  DeepL). Lokalny serwer LLM (`localhost`) nie wysyła niczego.
- **Tryb prywatny**: nic nie zapisuje się na dysku — cache działa tylko w pamięci,
  a „+ Słownik” obowiązuje do końca sesji. Glosariusz DeepL nie jest wtedy tworzony, a terminy
  dodane w tym trybie nigdy do niego nie trafiają. Ostatnia gra nie jest zapisywana
  w `settings.json`.
- **Tryb Cache-only**: tłumaczenie korzysta z lokalnych wyników i nie wysyła brakującego tekstu do dostawcy.
- **Korpus gry**: plik korpusu i baza tłumaczeń nie są nigdzie wysyłane. Gdy odczyt z ekranu
  zostanie dopasowany do tekstu korpusu, do dostawcy może trafić pełne zdanie z korpusu — także
  jego część, której gra jeszcze nie wyświetliła. `CorpusTool translate` wysyła teksty korpusu
  wyłącznie do dostawcy, którego sam wskażesz, i tylko na Twoje polecenie.
- Pełna polityka: `PRIVACY.md`.

## Skróty

| Skrót | Działanie |
|---|---|
| Ctrl+Shift+T | przetłumacz zaznaczony region |
| Ctrl+Shift+L | start / stop trybu live na aktywnej grze |
| Ctrl+Shift+H | ukryj / pokaż nakładkę |
| Esc lub Ctrl+Shift+T (podczas zaznaczania) | anuluj |

Skróty można zmienić w pliku `settings.json` w folderze danych (`translateHotkey`,
`toggleOverlayHotkey`, `liveToggleHotkey`; wymagany modyfikator Ctrl/Alt/Shift dla liter i cyfr).

## Rozwiązywanie problemów

| Problem | Rozwiązanie |
|---|---|
| „Brak pakietu językowego OCR” | doinstaluj język w ustawieniach Windows (patrz wyżej) |
| OCR nie widzi tekstu | zaznacz większy fragment; zwiększ rozmiar czcionki w grze; unikaj mocno ozdobnych fontów |
| Czarny podgląd okna | gra blokuje przechwytywanie okna — przełącz na borderless; tryb regionu (Ctrl+Shift+T) zwykle działa mimo to |
| „⚠ Pełny ekran utrudnia nakładkę” | okna gry nie da się przechwycić, a gra zajmuje cały monitor — przełącz ją w ustawieniach grafiki na okno bez ramki (borderless); jeśli już jest bez ramki, live działa na zrzucie ekranu ([Uwaga o prywatności](#uwaga-o-prywatności-w-trybie-live)) |
| Polski napis ma inny krój niż gra | wybierz **Krój czcionki → Jak w grze (krój z profilu)**; gra bez profilu (albo z profilem bez `overlay.fontFamily`) dostaje Segoe UI |
| **Rozmiar czcionki** nic nie zmienia | w trybie **Na oryginale (zakrywa)** rozmiar wynika z napisu gry; własny rozmiar działa **Pod oryginałem** i w **Napisach na dole** |
| Nad nazwą albo „OK” nie ma tłumaczenia | w trybie zakrywania tłumaczenie identyczne z oryginałem nie jest rysowane — celowo, widać grę |
| Wypełnione litery odstają od ruchomego tła | łatka jest liczona z odczytu OCR, więc między odczytami nie nadąża za tłem; zatrzymaj na chwilę kamerę albo wybierz **Pod oryginałem** |
| Kwestia dialogu zostaje po angielsku, dopóki gra ją pisze | celowe w trybie zakrywania z korpusem: tłumaczenie pojawia się po zakończeniu pisania (najdłużej po 8 s); tłumaczenie całej kwestii jeszcze w trakcie pisania dają **Pod oryginałem** i **Napisy na dole** |
| Pasek stanu nie pokazuje „korpus: … tekstów” | przy automatycznym wykryciu profilu pasek pokazuje „Wykryto grę …”, a liczbę tekstów korpusu znajdziesz w logu; poza tym sprawdź, czy wybrano profil gry i czy w folderze danych jest `corpus\<profil>.corpus.jsonl` (powód błędu wczytania jest w logu) |
| Krótki prawdziwy napis nie jest tłumaczony przy aktywnym korpusie | filtr śmieci OCR uznał go za śmieć (jego słów nie ma w plikach gry); dodaj go w **📖 Słownik…** z własnym tłumaczeniem albo przetłumacz go ręcznie (**Ctrl+Shift+T**) |
| CorpusTool: „Gra jest uruchomiona (proces: …)” | zamknij grę i jej launcher z folderu gry, potem uruchom polecenie ponownie |
| CorpusTool: „Gra ma zabezpieczenie anti-cheat …” albo inna odmowa (kod 3) | celowe zabezpieczenie ADR-014 bez opcji obejścia; tę grę tłumacz z obrazu |
| CorpusTool: „Brak korpusu … Najpierw uruchom: extract …” | najpierw `extract` dla tego profilu (z tym samym `--data-dir`, jeśli go podajesz) |
| CorpusTool: „Tryb prywatny jest włączony …” | wyłącz tryb prywatny w aplikacji albo uruchom tylko `--dry-run` |
| CorpusTool: „Dostawca llm niedostępny: …” albo „Dostawca deepl niedostępny: …” | dla `llm` ustaw `GTO_LLM_ENDPOINT` i `GTO_LLM_MODEL` (i `GTO_LLM_KEY` dla usługi w chmurze), dla `deepl` — `GTO_DEEPL_KEY`, w tym samym oknie PowerShella |
| „DeepL odrzucił klucz” | sprawdź klucz (darmowy kończy się na `:fx`) i czy plan API jest aktywny |
| „Azure Translator odrzucił klucz” | sprawdź klucz i **region** zasobu; dla zasobu globalnego zostaw region pusty |
| „Cloud Translation API nie jest włączone” | włącz Cloud Translation API w projekcie Google, do którego należy klucz |
| „Serwer LLM na tym komputerze nie odpowiada” | uruchom Ollamę albo serwer w LM Studio; sprawdź port w adresie |
| „Wybrany model nie istnieje” | popraw nazwę modelu; w Ollamie pobierz go: `ollama pull <model>` |
| „Model językowy odmówił przetłumaczenia” | filtr treści dostawcy; spróbuj innego modelu albo dostawcy dla tego fragmentu |
| Nakładka niewidoczna na nagraniu OBS | to celowe — nakładka jest wykluczona z przechwytywania ekranu |
| Widać `[PL]` i nadal angielski tekst | wybrano Mock; do prawdziwego tłumaczenia wybierz innego dostawcę i skonfiguruj go |
| Live jest opóźniony lub znika przy ruchu | zatrzymaj na chwilę kamerę; sprawdź osobno czas nowego wyniku i znikania starego; porównaj z ręcznym regionem |
| W Cache-only nie pojawia się nowy opis | tego tekstu może nie być w lokalnych wynikach; tryb celowo nie pyta dostawcy |
| Ctrl+Shift+L nie reaguje | sprawdź pasek statusu — skrót mógł być zajęty przez inny program; zmień `liveToggleHotkey` w `settings.json` |
| „Przełącz się do gry i wciśnij skrót ponownie.” | kliknij w okno gry i wciśnij skrót; gry UWP/Game Pass wybierz raz na liście |
| „⚠ Cache niedostępny — tłumaczenia nie są zapisywane” | zamknij program blokujący plik bazy w folderze danych, zwolnij miejsce na dysku; tłumaczenie działa dalej bez zapisu |
| Inny problem | zajrzyj do logów: folder danych → `logs\` (logi nie zawierają treści z ekranu) |

### Najczęstsze pytania

**Czy korpus zmienia grę albo jej pliki?** Nie. `CorpusTool` otwiera pliki gry tylko do
odczytu, przy wyłączonej grze, i zapisuje wynik w folderze danych aplikacji (nigdy w folderze
gry). Nakładka czyta tylko ten wynik, nigdy plików gry.

**Czy zrobię korpus dla innej gry?** Tylko wtedy, gdy jej profil ma receptę `corpus`, gra
przechodzi zabezpieczenia (offline, bez anti-cheatu, bez podpisanych i zaszyfrowanych plików),
a jej teksty są w rodzinie formatów Unity TextAsset. Dziś receptę ma tylko Escape Academy.
Pozostałe gry działają jak dotąd — z obrazu.

**Czy mogę udostępnić korpus albo przetłumaczoną bazę?** Nie rób tego: teksty gier są chronione
prawem autorskim, więc korpus i jego tłumaczenia mają zostać na Twoim komputerze.

**Czy tryb „Na oryginale (zakrywa)” działa w każdej grze?** Działa na obrazie każdej gry
w trybie live, ale wygląd strojono na klatkach Escape Academy. Przy kroju „Jak w grze” gra
bez kroju w profilu dostaje Segoe UI; inne ograniczenia opisuje
[sekcja o trybie zakrywania](#tryb-na-oryginale-zakrywa--napis-jak-w-grze).

**Czy potrzebuję korpusu, żeby używać trybu zakrywania?** Nie. Korpus dodaje tylko gotowe
tłumaczenia, dopasowanie odczytów z błędami OCR, odrzucanie śmieci OCR i czekanie na koniec
kwestii pisanej literami.

## Uwaga o prywatności w trybie live

Tryb live czyta obraz **wyłącznie z okna gry** — przez PrintWindow, a w ruchu kamery przez
Windows Graphics Capture (to samo systemowe API, którego używa np. Pasek gry Xbox; Windows 11 nie
pokazuje przy tym żółtej ramki; na Windows 10 aplikacja z niego nie korzysta). Gdy gra na
pełnym ekranie nie daje się przechwycić przez PrintWindow, Windows Graphics Capture przejmuje
całe przechwytywanie (nadal tylko okno gry). Wyłączenie Windows Graphics Capture: `"liveGraphicsCapture":
false` w `settings.json`. Jeżeli gra nie wspiera żadnego z nich (część tytułów DirectX/Vulkan),
aplikacja przechodzi na
zrzut ekranu w prostokącie okna gry i **wyraźnie o tym ostrzega** w statusie live —
w takim wypadku fragmenty innych okien nachodzących na grę (np. powiadomienia)
mogłyby zostać rozpoznane i przetłumaczone. Jeśli to dla Ciebie problem: zamknij
poufne okna znad gry, korzystaj z trybu ręcznego (zaznaczasz dokładnie ten fragment,
który chcesz) albo zatrzymaj tryb live. Okna samej aplikacji są zawsze wykluczone
z przechwytywania.

## Uwaga o grach online

Program niczego nie wstrzykuje do gry i nie automatyzuje rozgrywki — działa wyłącznie
na obrazie ekranu. Mimo to regulaminy niektórych gier różnie traktują nakładki.
**Sprawdź zasady swojej gry przed użyciem.** Narzędzie korpusu (`CorpusTool`) odmawia pracy
dla gier oznaczonych w profilu jako online, gier z listy wykluczeń (Path of Exile 1 i 2) i gier
z wykrytym anti-cheatem EasyAntiCheat albo BattlEye. Projekt nie jest powiązany z twórcami
żadnej z gier.

## Jak zgłosić problem jakości

Podaj nazwę gry, rozdzielczość, skalowanie Windows, tryb okna gry (okno, okno bez ramki,
pełny ekran), wybrany profil, sposób wyświetlania (**Wyświetlanie**, **Położenie dymków**,
**Krój czcionki**) i liczbę tekstów korpusu (z paska stanu „korpus: … tekstów” albo z logu),
jeśli go używasz. Opisz konkretną sytuację: pojawienie się dialogu, otwarcie opisu, zamknięcie
panelu lub obrót kamery.
Rozdziel objawy: spóźnienie, pozostawanie starego napisu, znikanie aktualnego tekstu,
przesunięcie względem oryginału i wygląd napisu (krój, wielkość, prześwitujące litery gry).
Nie dołączaj klucza API, całego folderu danych aplikacji ani pliku korpusu.
