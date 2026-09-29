# Sprawdzenie wydania 0.3.0 w grze — checklista (~30 min)

Cel: potwierdzić na Windows to, czego nie dało się sprawdzić automatycznie (okno aplikacji,
nowi dostawcy, kontekst dialogu), i porównać jakość tłumaczeń na prawdziwych tekstach.
Szczegółowe scenariusze: [MANUAL_TESTING.md](MANUAL_TESTING.md) (M23–M26).

Wynik wystarczy zapisać w tabelach poniżej (✔ / ✘ / uwaga) i odesłać — bez kluczy API.

## 0. Przygotowanie (5 min)

- [ ] Zainstalowane 0.3.0 z [Releases](https://github.com/KubaZx/Game_Translator/releases/tag/v0.3.0);
      stare ustawienia i klucz DeepL są na miejscu (sekcja „Dostawca: DeepL”, status „zapisany ✔”).
- [ ] Wybrana gra z dialogami i menu (np. Escape Academy albo PoE2 z profilem).
- [ ] Opcjonalnie: klucz Azure (darmowy F0) albo Ollama z małym modelem — do porównania.

## 1. Okno aplikacji (M23, 5 min)

| Sprawdzenie | Wynik |
|---|---|
| Lista „Dostawca tłumaczeń” pokazuje 6 pozycji; nagłówek sekcji zmienia się z wyborem | |
| Azure: pole regionu; zły region (`west europe`) wraca z ostrzeżeniem | |
| Model językowy: przyciski OpenAI / Ollama / LM Studio wpisują adres; `http://example.com/v1` daje ostrzeżenie o HTTPS | |
| Claude: pole modelu zapisuje się po wyjściu z pola, nie w trakcie pisania | |
| Mock: pole klucza wyłączone | |
| Klucz zapisany dla Azure nie zastępuje klucza DeepL | |
| Ciemny motyw czytelny, nic nie wychodzi poza okno | |

## 2. Działanie (M24–M26, 5 min)

| Sprawdzenie | Wynik |
|---|---|
| „Testuj” dla DeepL pokazuje zużycie znaków | |
| „Testuj” dla lokalnej Ollamy / innego dostawcy pokazuje próbne tłumaczenie | |
| Ctrl+Shift+T drugi raz zamyka zaznaczanie, trzeci otwiera | |
| Tryb live startuje i tłumaczy jak w 0.2.2 (bez nowych mignięć, znikania, opóźnień) | |
| Przejście tabulatorem po polach ustawień w trakcie live nie przerywa tłumaczenia | |

## 3. Szybkość — odczucia (5 min)

| Sytuacja | Wrażenie (szybciej / tak samo / wolniej) |
|---|---|
| Pierwsze tłumaczenie regionu po uruchomieniu aplikacji | |
| Pierwszy dialog po kilku minutach bez tekstu na ekranie | |
| Powrót do menu, które było już tłumaczone (odczyt z cache) | |

## 4. Jakość — porównanie dostawców (10 min)

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

## 5. Co odesłać

- Tabele z wynikami (można zdjęciem ekranu albo wklejone jako tekst).
- Przy błędzie: co zrobiłeś, co się stało, co oczekiwałeś; wpis z `logs\` z folderu danych
  (logi nie zawierają treści tłumaczeń ani kluczy).
