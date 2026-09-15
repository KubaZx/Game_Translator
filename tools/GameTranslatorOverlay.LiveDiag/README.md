# LiveDiag

Lokalna diagnostyka `LiveTranslationSession`: rzeczywisty capture i Windows OCR, dostawca Mock, świeży cache wyłącznie w pamięci. Nie otwiera bazy ani ustawień/sekretów użytkownika. HTTP jest zablokowane także w nieużywanym kliencie DeepL. Domyślnie nie zapisuje zrzutów ani nie wypisuje treści.

## Użycie

Z katalogu repozytorium:

```powershell
dotnet run --project tools/GameTranslatorOverlay.LiveDiag -- --help
dotnet run --project tools/GameTranslatorOverlay.LiveDiag -- --list-windows
dotnet run --project tools/GameTranslatorOverlay.LiveDiag -- 36 --output measurements/synthetic-01.jsonl
dotnet run --project tools/GameTranslatorOverlay.LiveDiag -- "Escape Academy" 120 --profile none --output measurements/escape-academy-01.jsonl
```

Argumenty pozycyjne pozostają zgodne ze starym narzędziem: sam czas uruchamia scenę testową; tytuł/fragment tytułu podpina istniejące okno. Bez argumentów czas wynosi 36 s; sam tytuł oznacza 25 s. Zakres czasu to 1–86400 s. Pasować musi dokładnie jedno okno; brak lub niejednoznaczne dopasowanie kończy pomiar błędem. `--list-windows` nie przechwytuje obrazu.

| Opcja | Znaczenie |
| --- | --- |
| `--profile none` | Domyślne ustawienia aplikacji bez profilu: 6 FPS, próg 0, OCR auto. |
| `--profile ID` | Wybierz istniejący profil i jego słownik. FPS, próg i upscale są pobierane tak jak w MainWindow. Nie rozpoznaje profilu automatycznie po tytule gry. |
| `--upscale 0..4` | Ustawienie aplikacji bez profilu, 0 = auto. Liczba z kropką; jawne łączenie z innym profilem jest błędem. |
| `--output PLIK` | Utwórz nowy JSONL, bez nadpisywania istniejącego pliku. |
| `--dump-frames` | Jawnie zezwól na PNG wybranych podejrzanych klatek. Unikalny katalog sesji w temp jest wypisywany i zachowywany. Niczego nie usuwa przy starcie. |
| `--include-text` | Jawnie wypisuj statusy i do 12 bloków tłumaczeń w konsoli. **Nie dodaje treści do JSONL.** |

Wybór `none` różni się od jawnego `generic`: generic ma upscale 2. PoE2 ma dodatkowo próg 0,03. Oba profile oraz słowniki są kopiowane do katalogu wynikowego narzędzia. Narzędzie używa języków en→pl tak jak nowe, domyślne ustawienia aplikacji.

## Co mierzy JSONL

- `configuration`: faktyczny profil, opcje sesji, wersja aplikacji, provider Mock i cache w pamięci.
- `update`: monotoniczny czas callbacku w ms od rozpoczęcia raportu, liczby bloków, hide/clear/stopped i `diagnostics` dla zakończonego OCR. Nie ma statusów tekstowych, tytułów okien, OCR ani tłumaczeń.
- `end`: przyczyna zakończenia, kod wyniku i podsumowanie. Liczby pełnych/częściowych OCR oraz podejrzeń whiffa; p50/p95 (nearest-rank) przechwycenia, OCR, tłumaczenia Mock, capture→update i odstępów między callbackami zakończonych OCR. Puste zbiory mają percentyle null. Percentyle etapów w podsumowaniu łączą pełne i częściowe przebiegi; rekordy pozwalają rozdzielić te grupy.

Capture→update kończy się przed renderowaniem nakładki. **Nie jest opóźnieniem od pojawienia się tekstu do widocznego tłumaczenia.** Odstępy między callbackami opisują kadencję ukończonych przebiegów, nie latency ani rzeczywiste FPS przechwytywania. LiveDiag nie wyświetla nakładki i nie mierzy opóźnienia DeepL. OCR i stan sceny obsługuje jedna pętla; po wykrytej zmianie widoku sesja może utrzymywać najwyżej dwa zadania tłumaczeń. Dodatkowe przechwycenia podczas dłuższego oczekiwania sprawdzają aktualność sceny. Odrzucone klatki nie mają rekordu zakończonego OCR; translateMs nowej klatki może obejmować oczekiwanie na wolne miejsce.

`WhiffSuspected` oznacza podejrzenie algorytmu, nie potwierdzony błąd OCR. Warunek PNG jest węższy: pełny OCR, zero rozpoznanych bloków po filtracji, wcześniej co najmniej 3 wyświetlane bloki. Brak PNG nie oznacza braku ubytków tekstu. `UsedScreenFallback` wskazuje ścieżkę zapasowego przechwycenia ekranu; w takim przebiegu istotna jest widoczność wybranego okna.

Pierwszy pomiar gry powinien mieć zapisane osobno: scenariusz (np. nieruchomy dialog, następnie zmiana kwestii), rozdzielczość gry, DPI, wybrany profil i zakres czasowy. Dla porównania powtórz tę samą scenę i zmieniaj pojedynczą opcję. Weryfikację faktycznej prezentacji nakładki wykonuje się oddzielnie w aplikacji. Nie uruchamiaj jednocześnie dwóch sesji przechwytujących ten sam obraz, jeżeli porównujesz wydajność.

## Zakończenie i błędy

Ctrl+C kończy sesję i zapisuje podsumowanie z kodem 130. Po Stop narzędzie czeka na Completion najwyżej 10 s, zanim zakończy raport i zamknie własne okno na jego dispatcherze. Zrzuty pozostają na dysku tylko po jawnym `--dump-frames`.

Kody: 0 = ukończony pomiar (co najmniej jeden zakończony OCR), 2 = argumenty/wybór okna, 3 = konfiguracja/profil/błąd wykonania, 4 = brak OCR albo przerwana sesja, 5 = zapis raportu, 6 = przekroczony czas/nieudane zamknięcie, 130 = Ctrl+C. Udana pomoc i lista okien mają kod 0. Błąd utworzenia pliku wyjściowego nie może zapisać rekordu end do tego pliku.

Testy parsera znajdują się w Core.Tests; kompilują czysty parser przez link, bez App/WPF i bez dostępu do pulpitu.

## Porównanie obciążenia dostawcy

Podsumowanie podaje mockProviderRequests, mockProviderCharacters, cacheHits, glossaryHits i failedProviderRequests. Są to liczniki istniejącego pipeline'u: pomyślne wywołania Mocka i znaki przekazane do tłumaczenia, nie opłaty DeepL. Każda sonda zaczyna z pustym cache w pamięci; tekst przejęty z już wyświetlanego bloku może ominąć pipeline i nie zwiększyć licznika cacheHits. Porównuj ten sam scenariusz i czas, bo częstszy OCR może znaleźć dodatkowy tekst lub nowe warianty błędnego odczytu.

## Kontrola sceny podczas oczekiwania (runda 2026-09-13)

Po poprawce aktualności napisów dłuższy etap OCR/tłumaczenia może zawierać dodatkowe
próbki capture, które wykrywają zmianę sceny. Pola ocrMs i translateMs opisują teraz
czas oczekiwania na dany etap wraz z tym sprawdzaniem, a nie izolowany czas samego
silnika/dostawcy. captureMs nadal opisuje tylko pierwotne przechwycenie klatki;
captureToUpdateMs zawiera całe oczekiwanie i kontrolę. Odrzucony wynik starej sceny
nie dostaje rekordu zakończonego OCR. Automatyczne usunięcie starej zawartości
raportowane jest jako clear, dlatego samego licznika hide nie porównuj między wersjami.

Rekord `update.diagnostics` zawiera dodatkowe pola (nazwy JSON zapisane camelCase):

| Pole | Znaczenie w jednym zakończonym przebiegu |
| --- | --- |
| `ocrOperationMs` | Czas od uruchomienia OCR do zaobserwowania zakończenia zadania; obejmuje przygotowanie i planowanie kontynuacji, nie tylko pracę silnika OCR. `null` oznacza brak pomiaru. |
| `ocrSceneChecks` | Liczba kontroli sceny wykonanych podczas oczekiwania na OCR oraz po jego zakończeniu. |
| `ocrSceneCheckMs` | Łączny czas tych kontroli, wraz z przechwyceniem i analizą obrazu. |
| `translationSceneChecks` | Liczba kontroli w etapie tłumaczenia, także przed wysłaniem tekstu i podczas oczekiwania na wolne miejsce. |
| `translationSceneCheckMs` | Łączny czas kontroli zaliczonych do etapu tłumaczenia. |

W `end.summary` liczniki kontroli są sumami dla zakończonych przebiegów, a czasy mają `count`, `p50` i `p95`. Percentyle `ocrSceneCheckMs` i `translationSceneCheckMs` dotyczą sum czasu kontroli w poszczególnych przebiegach, nie pojedynczych przechwyceń. Brakujące `ocrOperationMs` są pomijane.

**Te czasy nakładają się i nie są niezależnymi składnikami.** OCR może nadal pracować podczas kontroli sceny; `ocrMs`, `translateMs` i `captureToUpdateMs` już obejmują odpowiednie oczekiwanie oraz kontrole. Nie sumuj tych wartości ani nie odejmuj ich w celu wyliczenia izolowanego kosztu OCR, capture lub dostawcy. W szczególności `ocrMs - ocrOperationMs` nie jest pomiarem samego kosztu kontroli sceny. Percentyle również nie podlegają takiemu rozkładowi.

Do powtarzalnego sprawdzenia starego napisu i wolnego dostawcy służy osobne narzędzie
GameTranslatorOverlay.SceneReplay. Własna scena oraz opóźniony Mock pozwalają odróżnić
usunięcie starego tekstu od czasu gotowości nowego, bez używania DeepL.