# GameTranslatorOverlay

**Polskie tłumaczenia tekstu z gier, bezpośrednio nad grą.**

GameTranslatorOverlay to uniwersalny tłumacz EN→PL dla Windows. Odczytuje tekst
z obrazu przez systemowy OCR, tłumaczy go i wyświetla w osobnej nakładce,
która przepuszcza kliknięcia. Możesz tłumaczyć wybrany fragment skrótem
klawiszowym albo korzystać z automatycznego trybu live.

Projekt rozwijamy z myślą o różnych grach: dialogach, menu, zadaniach i opisach
przedmiotów. Escape Academy i Path of Exile 2 służyły do prób; profil konkretnej
gry jest opcjonalnym dodatkiem. Obsługa danego tytułu zależy od przechwytywania
obrazu i czytelności tekstu.

[Pobierz wydanie](https://github.com/KubaZx/Game_Translator/releases) ·
[Instrukcja](docs/USER_GUIDE.md) · [Plan rozwoju](docs/ROADMAP.md) ·
[Historia zmian](CHANGELOG.md) · [CI](https://github.com/KubaZx/Game_Translator/actions/workflows/ci.yml)

## Stan projektu

**Ostatnie opublikowane wydanie: 0.2.2.** Gałąź `main` zawiera również późniejsze
poprawki jakości live opisane w sekcji **Niewydane** w [CHANGELOG.md](CHANGELOG.md).
Paczka wydania 0.2.2 nie zawiera tych późniejszych zmian; aktualny kod można
zbudować instrukcją poniżej. Numer wersji aplikacji pozostaje 0.2.2 do kolejnego wydania.

Stan weryfikacji na 29 września 2026: **482 testy** — 361 Core i 121 Infrastructure —
oraz kompilacja całego rozwiązania bez ostrzeżeń. Nowi dostawcy tłumaczeń nie byli jeszcze
sprawdzani w uruchomionym oknie na Windows (scenariusze M23–M26 w
[MANUAL_TESTING.md](docs/MANUAL_TESTING.md)); ostatni smoke test Windows OCR: 15 września.
Próby wizualne w różnych grach, przy różnych DPI i monitorach pozostają osobnym
zadaniem. Projekt nadal rozwijamy, szczególnie pod kątem dynamicznej rozgrywki.

## Co potrafi

- **Tłumaczenie ręczne:** `Ctrl+Shift+T` i zaznaczenie fragmentu ekranu.
- **Tryb live:** automatyczne rozpoznawanie zmian tekstu w wybranym oknie gry.
- **Dwa sposoby prezentacji:** bloki przy oryginale, także z zakrywaniem tekstu,
  albo wspólny pasek napisów na dole.
- **Wybór dostawcy tłumaczeń:** DeepL, Azure AI Translator, Google Cloud Translation,
  Claude albo dowolny serwer zgodny z API OpenAI — także lokalna Ollama / LM Studio,
  przy której tekst nie opuszcza komputera.
- **Słowniki i własne poprawki:** edytor, import i eksport, spójne nazwy i terminy.
  Modele językowe dostają nazwę gry i terminy słownika także wewnątrz dłuższych zdań.
- **Pamięć tłumaczeń:** cache SQLite, łączenie powtarzających się zapytań oraz limity użycia.
- **Praca bez wysyłania tłumaczeń:** Cache-only korzysta z lokalnych wyników;
  Mock służy do sprawdzania działania bez klucza API.
- **Profile opcjonalne:** ustawienia ogólne i profil PoE2 w zestawie; pozostałe gry
  korzystają ze wspólnego silnika.

Priorytet wyniku: **ręczna poprawka → słownik → cache → dostawca tłumaczenia**.

## Ostatnie poprawki w kodzie

Najnowsza runda (29 września) dodaje wybór dostawcy tłumaczeń z osobnymi kluczami,
przekazywanie nazwy gry i terminów słownika do modeli językowych oraz zamyka pozycje
backlogu audytu #3 (priorytety i normalizacja słownika, jawne wyłączenie powiększania
w profilu, skalowanie prostokątów bez dryfu, `minAppVersion`, skrót przy otwartym
zaznaczaniu). Szczegóły: [CHANGELOG.md](CHANGELOG.md), [dostawcy](docs/API_PROVIDERS.md).

Stare napisy są usuwane po wykrytej zmianie sceny, a spóźniona odpowiedź nie powinna
przywracać poprzedniego widoku. Potwierdzone lokalne zastąpienie lub przykrycie
tekstu jednolitym panelem może usunąć tylko ten napis, zachowując pozostałe menu.

Nowa scena może rozpocząć tłumaczenie podczas kończenia poprzedniej odpowiedzi.
Sesja nadzoruje najwyżej dwa zadania tłumaczeń i rezerwuje znaki przed wysłaniem.
Pojedyncze, rozdzielone poprawnym odczytem pomyłki OCR nie sumują się w potwierdzenie
nowego tekstu. Stabilizacja położenia działa oddzielnie od rozmiaru napisu.

Już wyświetlony napis może pozostać podczas ruchu tła, jeśli wszystkie piksele jego
obszaru źródłowego nadal dokładnie pasują. Dotyczy to także pustego odczytu OCR.
Zmienione lub zasłonięte napisy tracą tę ochronę; spóźnione wyniki poprzedniej sceny
nadal są odrzucane. W próbie własnego menu z Windows OCR i Mock 200 ms liczba
próbek ze zgubionym menu spadła z 77 do 0, bez zmiany liczby zapytań (3 / 79 znaków).
Są to obserwacje stanu sesji, nie liczba osobnych mignięć ani ocena wyglądu w grze.

### Wybrane pomiary kontrolne

| Scenariusz | Przed poprawką | Po poprawce |
|---|---:|---:|
| Usunięcie starej etykiety po lokalnym przykryciu | 2,86 s | 0,29 s |
| Gotowość nowego opisu, gdy panel zmienia się podczas tłumaczenia | 4,11 s | 2,50 s |
| Maksymalne odsunięcie napisu po małym przesunięciu źródła | 18 px | 2 px |

Pierwsze dwie próby używały własnego okna, rzeczywistego Windows OCR i **Mocka
z celowym opóźnieniem 2 s**. Trzecia używała syntetycznego OCR z geometrią wyznaczaną
z obrazu, w 12 krokach po 3 fizyczne piksele. Pomiary kończą się przy przygotowaniu
aktualizacji przez sesję: nie mierzą czasu odpowiedzi DeepL ani fizycznego rysowania
nakładki. Nie są gwarancją wyniku w każdej grze. Warunki i ograniczenia:
[roadmapa](docs/ROADMAP.md), [SceneReplay](tools/GameTranslatorOverlay.SceneReplay/README.md).

## Wymagania

- Windows 10 (2004+) lub Windows 11; aplikacja portable dla win-x64.
- Pakiet językowy Windows OCR dla języka gry, np. angielski.
- Klucz wybranego dostawcy (**DeepL**, Azure, Google albo Anthropic), jeśli chcesz tłumaczyć
  online. Lokalny serwer LLM, Mock i Cache-only działają bez klucza.
- Gra w trybie okienkowym lub borderless fullscreen.

Paczka portable zawiera środowisko .NET. Nie wymaga Pythona, CUDA ani pobierania
lokalnych modeli AI. Do samodzielnego budowania potrzebny jest **.NET 10 SDK** na Windows.

## Szybki start

1. Pobierz i rozpakuj [wydanie portable](https://github.com/KubaZx/Game_Translator/releases).
   Uruchom `GameTranslatorOverlay.exe`.
2. Wybierz dostawcę tłumaczeń (domyślnie **DeepL**), wpisz klucz API, kliknij **Zapisz klucz**
   i **Testuj**. Do próby działania wybierz **Mock** — dodaje `[PL]`, nie tłumaczy tekstu.
   Porównanie dostawców i konfiguracja lokalnego modelu: [instrukcja](docs/USER_GUIDE.md).
3. Uruchom grę w oknie lub borderless i wybierz jej okno na liście aplikacji.
4. Kliknij **▶ Start live**. Ustaw **Przy oryginale** lub **Napisy na dole**.
   W trybie bloków opcja **Na oryginale (zakrywa)** umieszcza wynik na tekście gry.
5. Doraźnie użyj **Ctrl+Shift+T**, aby zaznaczyć tekst. **Ctrl+Shift+H** ukrywa
   lub pokazuje nakładkę, a **⏹ Stop** kończy live.

Nie potrzebujesz osobnego profilu dla każdej gry. Szczegóły konfiguracji,
prywatności i rozwiązywania problemów są w [instrukcji użytkownika](docs/USER_GUIDE.md).

## Znane ograniczenia

- **Exclusive fullscreen** nie jest obsługiwany.
- Zachowanie stałego menu wymaga identycznego obrazu źródła i rozmiaru okna. Ochrona
  nie działa przy skalowaniu OCR ani zapasowym przechwytywaniu ekranu; animowane
  i przezroczyste tło może ją wyłączyć. Silny ruch nadal może wtedy usunąć całą nakładkę.
  Śledzenie pozycji między odczytami OCR pozostaje kierunkiem rozwoju.
- Wcześniejsze usuwanie przykrytego tekstu wymaga pewnych danych. Teksturowane tło,
  niepełny wycinek lub słaby kontrast mogą wydłużyć podtrzymywanie starego napisu.
- Ozdobne i małe czcionki, animacje oraz efekty pod tekstem mogą pogarszać OCR i wygląd nakładki.
- Czas nowego tłumaczenia zależy także od OCR, sieci i dostawcy. Samo zmniejszenie
  opóźnienia aplikacji nie skraca odpowiedzi DeepL.
- Przechwytywanie używa PrintWindow/GDI. Jeśli potrzebny jest zrzut ekranu w obszarze
  gry, aplikacja ostrzega: inne okna nachodzące na grę mogą znaleźć się w odczycie.
- Automatyczne wykrywanie osobnego obszaru tooltipu jest planowane. Obecnie możesz
  zaznaczyć tooltip ręcznie lub odczytać go w ramach zwykłego live.
- Wykluczenie nakładki z przechwytywania może sprawić, że nie będzie widoczna na nagraniu.
  Zaznaczanie regionu działa na monitorze, na którym znajduje się kursor.

## Prywatność i sposób działania

Program przechwytuje wybrane okno lub region i rozpoznaje tekst lokalnie.
**Do dostawcy tłumaczeń wysyłany jest tekst, nigdy obraz.** Aplikacja nie zapisuje
zrzutów gry podczas zwykłego działania; osobne narzędzia diagnostyczne mają opisane
opcje zapisu obrazów.

Klucze API są przechowywane lokalnie z ochroną Windows DPAPI (osobno dla każdego dostawcy)
i wysyłane wyłącznie w nagłówkach żądań do wybranego dostawcy. Modele językowe dostają
dodatkowo nazwę gry i pasujące terminy słownika. Dane aplikacji znajdują się w
`%LOCALAPPDATA%\GameTranslatorOverlay`. Tryb prywatny używa cache w pamięci;
Cache-only wyłącza wysyłanie brakujących tłumaczeń do dostawcy.

Aplikacja nie modyfikuje plików ani pamięci gry, nie wstrzykuje kodu i nie wysyła
klawiszy lub kliknięć do gry. Szczegóły: [bezpieczeństwo](docs/SECURITY.md)
i [prywatność](docs/PRIVACY.md). Projekt nie jest powiązany z twórcami gier;
zgodność z regulaminem konkretnego tytułu należy sprawdzić osobno.

## Budowanie i testy

Na Windows, z .NET 10 SDK, w katalogu repozytorium:

```powershell
dotnet build GameTranslatorOverlay.slnx -c Release
dotnet build src/GameTranslatorOverlay.App -c Release --no-restore
dotnet test GameTranslatorOverlay.slnx -c Release --no-build --no-restore
dotnet run --project tools/GameTranslatorOverlay.SmokeTest -c Release --no-build --no-restore
```

Testy xUnit używają atrap i lokalnych danych; nie potrzebują kluczy API ani
aktywnej gry. Smoke test sprawdza rzeczywisty Windows OCR na syntetycznym obrazie.
Poza Windows (np. Linux) testy i kompilację całego rozwiązania uruchomisz z
`-p:EnableWindowsTargeting=true`; testy DPAPI i OCR wymagają jednak Windows.
Pełny podział: [TESTING.md](docs/TESTING.md).

Uruchomienie z kodu: `dotnet run --project src/GameTranslatorOverlay.App`.
Pełna paczka portable z bieżącego kodu: `./tools/package.ps1` — wynik w `dist/`.
Samo pakowanie nie tworzy wydania GitHub; CI publikuje wydanie po tagu `v*`.

## Narzędzia diagnostyczne

| Narzędzie | Zastosowanie |
|---|---|
| [LiveDiag](tools/GameTranslatorOverlay.LiveDiag/README.md) | pomiar sesji na własnym oknie lub wskazanej grze; Mock, prywatny cache, raport JSONL bez tekstów |
| [SceneReplay](tools/GameTranslatorOverlay.SceneReplay/README.md) | powtarzalne własne sceny: stare odpowiedzi, lokalne przykrycie, szum OCR, pozycja i czasy |
| `tools/GameTranslatorOverlay.OcrLab` | zapis klatki, geometria DPI i porównanie wariantów OCR |
| `tools/GameTranslatorOverlay.SmokeTest` | Windows OCR i pipeline na syntetycznym tekście |

LiveDiag i SceneReplay blokują HTTP i nie czytają klucza ani cache użytkownika.
Sondy nie zastępują oceny wyglądu nakładki w grze. Próby wymagające obserwacji
użytkownika opisano w [MANUAL_TESTING.md](docs/MANUAL_TESTING.md).

## Kod i dokumentacja

- `src/GameTranslatorOverlay.Core` — logika tekstu, tłumaczenia, kosztów i stabilizacji.
- `src/GameTranslatorOverlay.Infrastructure` — dostawcy tłumaczeń, SQLite, DPAPI i obsługa plików.
- `src/GameTranslatorOverlay.App` — interfejs WPF, przechwytywanie, Windows OCR i nakładka.
- `tests/` — testy Core i Infrastructure; `tools/` — narzędzia lokalne.
- `profiles/` i `glossaries/` — opcjonalne profile oraz słowniki.

[Wizja produktu](docs/PRODUCT_VISION.md) · [Architektura](docs/ARCHITECTURE.md) ·
[Decyzje techniczne](docs/TECHNOLOGY_DECISIONS.md) · [Dostawcy API](docs/API_PROVIDERS.md) ·
[Plan rozwoju](docs/ROADMAP.md) · [Historia zmian](CHANGELOG.md)
