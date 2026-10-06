# Sprawdzenie wydania 0.5.0 w grze — checklista (~45 min)

Cel: potwierdzić w prawdziwej grze to, co w 0.5.0 sprawdzono tylko automatycznie (testy,
powtórki sesji przez pipeline, SceneReplay, zapisane klatki 4K w OverlayPreview): znikanie
starego napisu, korpus gry, napis w stylu gry w trybie „Na oryginale (zakrywa)” i kwestię pisaną
literami. W grze na żywo była dotąd tylko krótka sesja Escape Academy z korpusem — przed
dopasowaniem etykiet, odrzucaniem śmieci OCR, nowym wyglądem i wstrzymywaniem kwestii pisanej
literami. Opis funkcji:
[USER_GUIDE.md](USER_GUIDE.md); szczegółowe scenariusze 0.5.0 (M36–M40), ogólne scenariusze
trybu live (M17–M22) i szybkości (M29): [MANUAL_TESTING.md](MANUAL_TESTING.md).

Wynik wystarczy zapisać w tabelach poniżej (✔ / ✘ / uwaga) i odesłać — bez kluczy API.

## 0. Przygotowanie (10 min)

- [ ] Zainstalowane 0.5.0 z [Releases](https://github.com/KubaZx/Game_Translator/releases);
      stare ustawienia i klucz są na miejscu (status „Klucz …: zapisany ✔”).
- [ ] **Krój czcionki** pokazuje „Jak w grze (krój z profilu)” (ustawiony Segoe UI, dawny domyślny,
      przechodzi na niego raz; inny wybrany krój zostaje).
- [ ] Korpus przygotowany przy wyłączonej grze i zamkniętej aplikacji: `CorpusTool extract`,
      potem `translate` (najpierw `--dry-run`) —
      [USER_GUIDE.md → Spolszczenie z wyprzedzeniem](USER_GUIDE.md#spolszczenie-z-wyprzedzeniem-korpus-gry).
- [ ] Escape Academy w oknie bez ramki; **Profil gry: Escape Academy** (przy „— brak profilu
      (tryb uniwersalny) —” włącza się sam po wybraniu okna gry z listy). Po ręcznym wyborze
      profilu pasek stanu pokazuje „Ustawienia zapisane (…, korpus: … tekstów).”; przy
      automatycznym wykryciu liczbę tekstów korpusu podaje log w `logs\`.
- [ ] Opcjonalnie do porównania: gra bez korpusu (np. PoE2) — tam teksty mają być tłumaczone jak
      w 0.4.0 (bez przyciągania do korpusu i bez nowego odrzucania śmieci).

## 1. Stary napis znika (5 min)

Ustawienia: **Wyświetlanie: Przy oryginale**, dowolne położenie dymków.

| Sprawdzenie | Wynik |
|---|---|
| „Zbadaj” znika ok. pół sekundy po zniknięciu „Inspect”, gdy w jego miejscu nie zostaje nic równie jasnego (w teście 0,3–0,55 s, także na teksturowanym tle), a w pozostałych przypadkach po kilku odczytach OCR (w teście ok. 0,9–3,2 s); nie wraca sam | |
| Napis, który przygasa albo zmienia kolor przy najechaniu, a nadal jest czytelny, zachowuje tłumaczenie bez mrugania (wyjątek: napis przygaszony poniżej ćwierci jasności może raz zniknąć na ok. 0,6 s) | |
| Nowy napis w miejscu poprzedniego (np. z wielokropkiem) dostaje własne tłumaczenie; stare nie wraca | |
| Stały napis menu nie miga, gdy w innym miejscu ekranu coś się rusza | |
| **Napisy na dole:** linia znika z paska razem z napisem w grze i wraca, gdy napis wróci w ciągu 10 s | |

## 2. Korpus gry (10 min)

| Sprawdzenie | Wynik |
|---|---|
| `extract` przy włączonej grze odmawia pracy (kod wyjścia 3); przy wyłączonej zapisuje korpus (kod 0) | |
| `translate --dry-run` podaje liczbę tekstów, znaków i szacunek kosztu, niczego nie wysyła | |
| Znane etykiety, dialogi i napisy pojawiają się od razu, bez czekania na dostawcę | |
| Krótkie etykiety (np. „Inspect”, „Use Item”) mają poprawne tłumaczenie także wtedy, gdy OCR je przekręca; napis nie jest podmieniany przy drżeniu odczytu | |
| Zlepki liter z ikon i tekstur nie dostają tłumaczenia (widać oryginał) | |
| Kwestia dialogu dostaje tłumaczenie całej kwestii; kolejne odczyty tej samej kwestii nie podmieniają napisu | |
| Gra bez korpusu: teksty idą do tłumaczenia jak w 0.4.0 | |

## 3. Napis w stylu gry (10 min)

Ustawienia: **Wyświetlanie: Przy oryginale**, **Położenie dymków: Na oryginale (zakrywa)**,
**Krój czcionki: Jak w grze (krój z profilu)**.

| Sprawdzenie | Wynik |
|---|---|
| Z obrazu znikają same litery oryginału; tło wokół zostaje żywe, bez ciemnej plamy i smugi | |
| Polski napis ma kolor, kontur, cień i wysokość liter napisu gry i stoi na jego linii bazowej | |
| Ikona klawisza przed napisem (np. „X Hint”) i ikonka za napisem (np. ✓) zostają widoczne | |
| Napisy w tym samym stylu (np. pozycje menu) mają tę samą grubość liter | |
| Wyśrodkowany napis (baner w menu) zostaje na środku | |
| Dłuższy polski tekst lekko się zmniejsza albo łamie w szerokości oryginału i nie wychodzi poza monitor | |
| Nazwy i „OK” identyczne z oryginałem nie są przykrywane i nie migają | |
| Gdy tło pod napisem się rusza, łatka jest miękka; gdy obraz stanie, wraca ostra | |
| Start live nie zacina gry ani okna aplikacji | |
| Opcjonalnie (M40): gra na pełnym ekranie, której nie da się przechwycić jako okno — pojawia się „⚠ Pełny ekran utrudnia nakładkę — przełącz na okno bez ramki” | |

## 4. Kwestia pisana literami (5 min)

Ustawienia jak w sekcji 3, z korpusem.

| Sprawdzenie | Wynik |
|---|---|
| Dialog wpisywany litera po literze zostaje po angielsku, dopóki gra pisze — bez polskiego tekstu na dopisywanych angielskich literach | |
| Po ostatniej literze od razu pojawia się całe polskie tłumaczenie kwestii (w teście ok. 0,1–0,3 s) | |
| Kwestia z dłuższą pauzą w środku (ok. 1 s): zanotuj, czy tłumaczenie pojawia się już w pauzie | |

## 5. Szybkość (5 min)

| Sprawdzenie | Wynik |
|---|---|
| Po ok. 10 min gry z korpusem: **Kopiuj raport** w panelu **Szybkość** — wklej raport (bez tekstu z gry) | |
| Wrażenie: nowy dialog (szybciej / tak samo / wolniej niż w 0.4.0) | |
| Wrażenie: powrót do menu tłumaczonego wcześniej | |

## 6. Jakość — porównanie dostawców (opcjonalnie, 10 min)

Wybierz 5–8 tekstów z gry: krótkie kwestie dialogu (szczególnie z „I'm…”, „ready”, „sure”
— rodzaj gramatyczny), opis przedmiotu z terminem ze słownika i jeden dłuższy opis.
Przetłumacz każdy ręcznie (Ctrl+Shift+T) u 2–3 dostawców. Oceń 1–5 (5 = jak dobra lokalizacja).

| # | Tekst oryginalny (skrót) | DeepL | Azure / Google | Model językowy / Claude | Uwagi (rodzaj, termin, sens) |
|---|---|---|---|---|---|
| 1 | | | | | |
| 2 | | | | | |
| 3 | | | | | |
| 4 | | | | | |
| 5 | | | | | |
| 6 | | | | | |

Kontekst dialogu: przy kilku kolejnych kwestiach tej samej postaci sprawdź, czy rodzaj
gramatyczny jest spójny (DeepL i modele dostają poprzednie linie jako kontekst).

Zawinięte zdania: przy dialogu lub opisie na 2–3 wiersze sprawdź, czy tłumaczenie jest jednym
poprawnym zdaniem rozłożonym na wiersze (a nie osobno przetłumaczonymi kawałkami) i czy
nakładka nadal pasuje do pola oryginału. Zwracanie się do gracza powinno być w formie „ty”.

## 7. Co odesłać

- Tabele z wynikami (można zdjęciem ekranu albo wklejone jako tekst).
- Raport z panelu **Szybkość**.
- Przy błędzie: co zrobiłeś, co się stało, co oczekiwałeś; wpis z `logs\` z folderu danych
  (logi nie zawierają treści tłumaczeń ani kluczy).
- Nie dołączaj korpusu ani bazy tłumaczeń — teksty gry są chronione prawem autorskim.
