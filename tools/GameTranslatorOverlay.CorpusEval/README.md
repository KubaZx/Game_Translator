# CorpusEval — eksperyment precyzji przyciągania do korpusu (narzędzie dev)

Mierzy, jak `CorpusSnapper` (Core) przyciąga odczyty OCR do korpusu gry zbudowanego przez
[CorpusTool](../GameTranslatorOverlay.CorpusTool/README.md). Wymaga Windows (systemowe OCR
z aplikacji). Nie wysyła niczego do sieci i nie używa kluczy.

## Polecenia

```bash
# 1. Prawda syntetyczna: losowe teksty korpusu (biały tekst, czarny obrys, 32–72 px, 9 czcionek
#    systemowych) na prawdziwych klatkach 4K, przez Windows OCR. --mode full (cała klatka,
#    jak pełny skan live) albo crop (wycinek, jak OCR regionu). Wynik zawiera teksty gry!
CorpusEval render --corpus private/game.corpus.jsonl --backgrounds a.png;b.png --out private/ocr.jsonl --count 440 --seed 1

# 2. Przegląd progów: prawda syntetyczna, prawdziwy cache (dni z grą) i kontrola negatywna
#    (sesja innej gry); --only T,margines,minDługość[,minDługośćDokładnej] dla jednej konfiguracji.
CorpusEval evaluate --corpus … --ocr private/a.jsonl;private/b.jsonl --cache KOPIA/cache.db --out LICZBY --private PRYWATNE

# 3. Czas dopasowania p50/p95 dla korpusu gry i syntetycznego korpusu 150 tys. tekstów.
CorpusEval bench --corpus … --ocr … --cache KOPIA/cache.db --out LICZBY --fuzzy 0.80 --margin 0.08 --min-length 16

# 4. Powtórka bloków z kopii cache przez prawdziwy TranslationPipeline z Mockiem (ścieżka live:
#    TranslateLocalAsync, potem TranslateAsync z wynikiem próby). Warianty: A — jak dotąd, bez
#    korpusu; A2 — bez korpusu, klucze po akapitach; B0 — korpus i przyciąganie na pustym cache;
#    B — jak B0 na bazie wypełnionej wcześniej przez `CorpusTool translate --provider mock`.
#    Każdy wariant na dniach EA i na sesji PoE2 (kontrola: korpus EA przy innej grze).
CorpusEval replay --corpus … --cache KOPIA/cache.db --prefilled PRYWATNY/b/cache.db --work PRYWATNY/robocze --out LICZBY

# 5. Odczyty jednej sesji gracza (wpisy cache od --since, czyli to, co poszło do dostawcy) przez
#    bramkę live (ShouldTranslateLive) i TranslationPipeline (Mock): przed (dopasowanie jak dotąd)
#    i po (etykiety z pomyłkami OCR, dialog pisany literami, odrzucanie szumu). Cache = kopia bazy
#    bez tych wpisów (VACUUM INTO + DELETE), więc tłumaczenia z wyprzedzeniem działają jak w sesji.
CorpusEval session --corpus … --cache KOPIA_SESJI/cache.db --since 2026-10-06T06:28 --work PRYWATNY/sesja --out LICZBY

# 6. Progi dialogu pisanego literami: ucięte odczyty OCR linii dialogu, ucięte linie korpusu
#    i bloki PoE2 (całe i ucięte) przy różnych minimalnych długościach, liczbie słów, odsetku
#    pomyłek i marginesie; --only L,W,e,m zapisuje błędne przyciągnięcia do --private.
CorpusEval prefixes --corpus … --ocr … --cache KOPIA/cache.db --out LICZBY --private PRYWATNE

# 7. Dialog pisany literami przez pipeline: każda linia dopisywana co 3 znaki albo w przerwach po
#    interpunkcji — ile zapytań po pierwszym trafieniu, ile zmian klucza nakładki i tekstu.
CorpusEval typing --corpus … --ocr … --out LICZBY --private PRYWATNE
```

`--features off` (evaluate, bench, replay, typing) wyłącza etykiety, prefiksy dialogu i odrzucanie
szumu — stan dopasowania sprzed rundy 2026-10-06 (4), do porównań przed/po na tych samych danych.
`session` zawsze liczy oba warianty (przed i po), więc tej opcji nie używa.

Opcje spoza przykładów: `render --mode full|crop` (domyślnie `full`) i `render --examples KATALOG`
(PNG pierwszych 24 próbek — teksty gry, tylko folder prywatny), `bench --synthetic N` (rozmiar
korpusu syntetycznego, domyślnie 150 000), `replay` / `session --profile ID` (domyślnie
`escape-academy`), `typing --wrap N` (szerokość zawijania czystych linii korpusu, domyślnie 48).

`evaluate` liczy klucz prawdy przy ocenie z pola `truth` bieżącym `CorpusText.MatchKey`, a nie
bierze go z próbki (`truthKey` zapisany przy renderze jest nieaktualny po zmianie klucza, np. po
zamianie literalnego `\n` na nowy wiersz); liczbę takich próbek podaje `truthKeysRecomputed`.
Stare pliki próbek można więc oceniać bez ponownego renderu.

`replay` tworzy w `--work` świeże bazy dla każdego wariantu (bazy `--cache` i `--prefilled` tylko
czyta albo kopiuje) i zapisuje tam `przyciagniecia-do-przegladu.tsv` z odczytami i tym, co
pokazałaby nakładka — to teksty gry, więc `--work` musi być folderem prywatnym. Do `--out` trafiają
`powtorka.json` i `powtorka.md`: bloki i znaki obsłużone lokalnie, wystąpienia ważone `use_count`,
zapytania, teksty i znaki wysłane do dostawcy, bloki przyciągnięte (ze zmianą treści albo tylko
wielkości liter), zgodność liczby wierszy wyświetlanego tłumaczenia z odczytem i czas próby lokalnej.

Do katalogu `--out` trafiają wyłącznie liczby: `sweep.json`, `wybrane-progi.json`,
`wyniki-progi.md` (evaluate), `czasy-dopasowania.json` (bench), `powtorka.json` / `.md` (replay),
`sesja.json` / `.md` (session), `prefiksy.json` / `.md` (prefixes) i `pisanie.json` / `.md`
(typing). Szczegóły z tekstami gry (błędne przyciągnięcia, próbka do ręcznego przeglądu,
trafienia kontroli negatywnej, odczyty sesji) trafiają do `--private` albo `--work` — nigdy do
repozytorium: `bledy-syntetyczne.tsv`, `przeglad-przyblizone.tsv`, `poe2-trafienia.tsv`
(evaluate), `prefiksy-bledne.tsv` (prefixes z `--only`), `pisanie-problemy.tsv` (typing),
`przyciagniecia-do-przegladu.tsv` (replay) i `sesja-przeglad.tsv` (session). Bazę cache czytaj
z kopii (razem z `-wal` i `-shm`), w trybie tylko do odczytu.

Dni w cache są zaszyte w `EvalData` (pomiar z rundy 2026-10-06): Escape Academy 2026-09-04/12/13/15,
kontrola negatywna — sesja Path of Exile 2 z 2026-08-06 do 12:00 UTC (po 12:00 tego dnia w cache
jest już Escape Academy). Wyniki: [ROADMAP.md → Runda 2026-10-06](../../docs/ROADMAP.md).
