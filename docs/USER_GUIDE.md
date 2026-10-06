# Instrukcja użytkownika — GameTranslatorOverlay

GameTranslatorOverlay tłumaczy na żywo angielski tekst z gier na polski. Działa jak
zewnętrzna nakładka: przechwytuje obraz, rozpoznaje tekst systemowym OCR Windows
i wyświetla tłumaczenie nad grą — **nie dotykając plików ani procesu gry**.

Projekt jest przeznaczony do różnych gier; nie wymaga profilu konkretnego tytułu.
Instrukcja opisuje wydanie 0.4.0 — szczegóły w
[historii zmian](https://github.com/KubaZx/Game_Translator/blob/main/CHANGELOG.md).

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
   „wyłączny” nie jest obsługiwany).
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
   **Napisy na dole** (pasek jak napisy filmowe — najlepszy do dialogów).
4. **⏹ Stop** (albo ponownie **Ctrl+Shift+L**) kończy tryb live. Minimalizacja gry chowa
   nakładkę automatycznie.

Jeżeli aplikacja rozpozna dostarczony profil, może dobrać go do wybranego okna.
Pozostałe gry działają na ustawieniach ogólnych. Profil PoE2 w zestawie jest
opcjonalnym dodatkiem ze słownikiem terminów.

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
3–5 s i nie przyjmuje kliknięć. Przykłady:

- „⚠ Brak klucza DeepL”, „⚠ Klucz DeepL został odrzucony”, „⚠ Limit znaków DeepL wyczerpany”,
- „⏳ DeepL ogranicza zapytania”, „⚠ Brak połączenia z dostawcą”,
  „⚠ Limit znaków tej sesji wyczerpany”, „⚠ DeepL zwrócił pusty wynik”,
- „Cache-only: 5 tekstów bez tłumaczenia”, „⚠ Cache niedostępny — tłumaczenia nie są zapisywane”,
- „▶ Tłumaczenie na żywo włączone”, „■ Tłumaczenie na żywo zatrzymane”.

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

- Do opisów i menu wybierz **Przy oryginale**. Opcja **Na oryginale (zakrywa)**
  umieszcza tłumaczenie nad tekstem gry; **Pod oryginałem** pokazuje je poniżej.
- Do dialogów możesz wybrać **Napisy na dole**. Jest to wspólny pasek świeżych
  tekstów rozpoznanych przez live, bez automatycznego rozpoznawania rodzaju wypowiedzi.
- Po ruchu kamery daj obrazowi na chwilę się zatrzymać. Silny ruch nadal może
  tymczasowo usuwać nakładkę, a nowe tłumaczenie może czekać na OCR i dostawcę.
- Przy trudnej czcionce lub konkretnym opisie użyj **Ctrl+Shift+T** i zaznacz
  interesujący fragment. Automatyczne wydzielanie obszaru tooltipu jest w planach.

Stałe menu może pozostać podczas ruchu tła, jeśli obraz pod jego napisami nie zmienił
się ani o piksel. Zmieniony lub zasłonięty napis traci tę ochronę. Animowane tło,
skalowanie odczytu lub zapasowe przechwytywanie ekranu mogą uniemożliwić zachowanie menu.

Bieżący kod usuwa potwierdzone stare opisy przed nadejściem nowego tłumaczenia
i dokładniej dopasowuje pozycję. Na wzorzystym tle albo przy niepewnym OCR stary
napis może być chwilowo podtrzymany. Te mechanizmy ograniczają błędy, ale nie
zapewniają jednakowego czasu i wyglądu w każdej grze.

## Spolszczenie z wyprzedzeniem (korpus gry)

Dla gier offline, których teksty da się bezpiecznie odczytać z plików (dziś: **Escape
Academy**), możesz raz przygotować lokalny **korpus** — listę angielskich tekstów gry — i od razu
przetłumaczyć go w całości. W trakcie gry nakładka rozpoznaje wtedy odczyt OCR jako znany tekst
(także z błędami OCR, innym zawinięciem wierszy albo WIELKIMI LITERAMI) i pokazuje gotowe
tłumaczenie z lokalnej bazy, bez czekania na dostawcę. Nakładka nadal nie czyta plików gry —
robi to wyłącznie osobne narzędzie `CorpusTool`, uruchamiane przez Ciebie przy wyłączonej grze
([ADR-014](TECHNOLOGY_DECISIONS.md)). Narzędzie nie jest w paczce aplikacji; uruchamiasz je ze
źródeł projektu (potrzebny .NET 10 SDK), w PowerShellu z folderu repozytorium.

1. **Wyłącz grę** (także launcher Steam z jej oknem). Narzędzie odmówi pracy, gdy proces gry
   działa, gdy gra ma anti-cheat albo zaszyfrowane pliki i dla gier online (np. Path of Exile).
2. **Odczytaj korpus** (tylko do odczytu, bez sieci, kilka sekund):

   ```powershell
   dotnet run --project tools/GameTranslatorOverlay.CorpusTool -c Release -- extract `
     --profile escape-academy --game-dir "C:\Program Files (x86)\Steam\steamapps\common\Escape Academy"
   ```

   Korpus trafia do `%LOCALAPPDATA%\GameTranslatorOverlay\corpus\escape-academy.corpus.jsonl` —
   tylko tam szuka go aplikacja. Nie przenoś go i nie udostępniaj (teksty gry są chronione
   prawem autorskim).
3. **Przetłumacz korpus** u wybranego dostawcy. Najpierw przebieg próbny — pokazuje liczbę
   tekstów, znaków i szacunek kosztu, niczego nie wysyła:

   ```powershell
   dotnet run --project tools/GameTranslatorOverlay.CorpusTool -c Release -- translate `
     --profile escape-academy --provider llm --dry-run
   ```

   Potem właściwy przebieg z kluczem w zmiennej środowiskowej (narzędzie nie czyta kluczy
   zapisanych w aplikacji), np. DeepSeek bez myślenia (ok. 0,12–0,24 USD za całą grę):

   ```powershell
   $env:GTO_LLM_ENDPOINT = "https://api.deepseek.com/v1"
   $env:GTO_LLM_MODEL = "deepseek-flash"
   $env:GTO_LLM_KEY = "<Twój klucz>"
   dotnet run --project tools/GameTranslatorOverlay.CorpusTool -c Release -- translate `
     --profile escape-academy --provider llm --llm-thinking disabled
   ```

   albo DeepL (`$env:GTO_DEEPL_KEY`, `--provider deepl`; ok. połowy miesięcznego limitu API Free).
   Przerwany przebieg dokończysz, uruchamiając to samo polecenie jeszcze raz. Przy włączonym
   trybie prywatnym narzędzie odmówi zapisu. Twoje ręczne poprawki i słownik zostają nietknięte.
4. **Uruchom aplikację** (jeśli działała — zamknij ją i otwórz ponownie, żeby wczytała korpus
   i nowe wpisy) i wybierz profil **Escape Academy** (albo zaznacz okno gry na liście — profil
   włączy się sam). Po zapisaniu ustawień pasek stanu pokazuje „korpus: … tekstów” (to samo
   trafia do logu przy każdym starcie). Brak tej informacji = aplikacja nie znalazła pliku korpusu.
5. **Graj z trybem live** jak zwykle (`Ctrl+Shift+L`).

Co się zmienia w grze:

- Znane teksty pojawiają się bez zapytania do dostawcy — w powtórce prawdziwych sesji Escape
  Academy ok. 60% znaków i połowa bloków była gotowa lokalnie (bez korpusu: 0,3%).
- Odczyt z błędami OCR („Itls”, „11m”, ucięty koniec zdania) dostaje tłumaczenie poprawnego zdania.
  Gdy gra wypisuje zdanie literka po literce, nakładka może pokazać tłumaczenie całego zdania,
  zanim gra wypisze je do końca.
- Tłumaczenie zachowuje układ wierszy z ekranu. Imię mówcy („Captain:”, „[CAPTAIN]”), klawisze
  obok etykiet („E Inspect” → „E Zbadaj”) i pojedyncze liczby zostają bez zmian.
- Nieznana część bloku (np. nowa linia pod znaną etykietą) idzie do dostawcy sama — znana część
  nie jest płacona drugi raz.
- Ręczna poprawka bloku, który w całości jest jednym tekstem z korpusu, zapisuje się pod tym
  tekstem, więc działa także dla innych odczytów tego samego zdania. Poprawka bloku złożonego
  z kilku części działa dla tego odczytu, jak dotąd.
- Bez pliku korpusu i bez profilu aplikacja działa dokładnie jak dotąd. Cache-only działa także
  z korpusem. **W trybie prywatnym** aplikacja nie czyta bazy z dysku, więc tłumaczenia
  z wyprzedzeniem są wtedy niedostępne (przyciąganie nadal ujednolica odczyty w pamięci).

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

## Uwaga o prywatności w trybie live

Dla większości gier tryb live czyta obraz **wyłącznie z okna gry** (PrintWindow).
Jeżeli gra tego nie wspiera (część tytułów DirectX/Vulkan), aplikacja przechodzi na
zrzut ekranu w prostokącie okna gry i **wyraźnie o tym ostrzega** w statusie live —
w takim wypadku fragmenty innych okien nachodzących na grę (np. powiadomienia)
mogłyby zostać rozpoznane i przetłumaczone. Jeśli to dla Ciebie problem: zamknij
poufne okna znad gry, korzystaj z trybu ręcznego (zaznaczasz dokładnie ten fragment,
który chcesz) albo zatrzymaj tryb live. Okna samej aplikacji są zawsze wykluczone
z przechwytywania.

## Uwaga o grach online

Program niczego nie wstrzykuje do gry i nie automatyzuje rozgrywki — działa wyłącznie
na obrazie ekranu. Mimo to regulaminy niektórych gier różnie traktują nakładki.
**Sprawdź zasady swojej gry przed użyciem.** Projekt nie jest powiązany z twórcami
żadnej z gier.

## Jak zgłosić problem jakości

Podaj nazwę gry, rozdzielczość, skalowanie Windows, wybrany profil i sposób
wyświetlania. Opisz konkretną sytuację: pojawienie się dialogu, otwarcie opisu,
zamknięcie panelu lub obrót kamery. Rozdziel objawy: spóźnienie, pozostawanie
starego napisu, znikanie aktualnego tekstu i przesunięcie względem oryginału.
Nie dołączaj klucza API ani całego folderu danych aplikacji.
