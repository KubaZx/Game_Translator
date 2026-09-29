# Instrukcja użytkownika — GameTranslatorOverlay

GameTranslatorOverlay tłumaczy na żywo angielski tekst z gier na polski. Działa jak
zewnętrzna nakładka: przechwytuje obraz, rozpoznaje tekst systemowym OCR Windows
i wyświetla tłumaczenie nad grą — **nie dotykając plików ani procesu gry**.

Projekt jest przeznaczony do różnych gier; nie wymaga profilu konkretnego tytułu.
Instrukcja opisuje bieżący kod na `main`. Ostatnie wydanie 0.2.2 jest starsze od
najnowszych poprawek stabilności — szczegóły w
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

### Modele językowe: nazwa gry i słownik w tłumaczeniu

Dostawcy **Model językowy** i **Claude** dostają razem z tekstem nazwę gry z aktywnego
profilu oraz te terminy z Twojego słownika, które występują w tłumaczonych zdaniach
(np. „Energy Shield” → „Tarcza energetyczna” także wewnątrz dłuższego opisu). Dzięki temu
nazwy są spójne w całej grze. Klasyczni tłumacze (DeepL, Azure, Google) używają słownika
tylko dla tekstów, które w całości są terminem.

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

1. Wybierz okno gry z listy i kliknij **▶ Start live**.
2. Program obserwuje okno kilka razy na sekundę; gdy pojawi się nowy, stabilny tekst,
   tłumaczy go automatycznie i pokazuje w nakładce.
3. „Wyświetlanie” wybiera układ: **Przy oryginale** (dymki przy tekście) albo
   **Napisy na dole** (pasek jak napisy filmowe — najlepszy do dialogów).
4. **⏹ Stop** kończy tryb live. Minimalizacja gry chowa nakładkę automatycznie.

Jeżeli aplikacja rozpozna dostarczony profil, może dobrać go do wybranego okna.
Pozostałe gry działają na ustawieniach ogólnych. Profil PoE2 w zestawie jest
opcjonalnym dodatkiem ze słownikiem terminów.

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

## Słownik i dane

- **📖 Słownik…** — edytor prywatnego słownika: dodawanie, edycja, priorytety,
  import/eksport JSON, wykrywanie konfliktów.
- **Wyczyść cache** — usuwa automatyczne tłumaczenia (ręczne poprawki zostają).
- **Eksport/Import cache…** — kopia zapasowa tłumaczeń (w tym poprawek) do pliku JSON.
- **Folder danych** — otwiera `%LOCALAPPDATA%\GameTranslatorOverlay` (cache, ustawienia, logi).

## Prywatność

- Do internetu wysyłany jest **wyłącznie rozpoznany tekst** (nigdy obraz) i tylko do
  wybranego dostawcy tłumaczeń. Modele językowe dostają dodatkowo nazwę gry z profilu
  i pasujące terminy słownika. Lokalny serwer LLM (`localhost`) nie wysyła niczego.
- **Tryb prywatny**: nic nie zapisuje się na dysku — cache działa tylko w pamięci,
  a „+ Słownik” obowiązuje do końca sesji.
- **Tryb Cache-only**: tłumaczenie korzysta z lokalnych wyników i nie wysyła brakującego tekstu do dostawcy.
- Pełna polityka: `PRIVACY.md`.

## Skróty

| Skrót | Działanie |
|---|---|
| Ctrl+Shift+T | przetłumacz zaznaczony region |
| Ctrl+Shift+H | ukryj / pokaż nakładkę |
| Esc lub Ctrl+Shift+T (podczas zaznaczania) | anuluj |

Skróty można zmienić w pliku `settings.json` w folderze danych (wymagany modyfikator
Ctrl/Alt/Shift dla liter i cyfr).

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
