# Korpus ewaluacyjny EN→PL

`en-pl.sample.jsonl` to mały korpus do porównywania dostawców tłumaczeń i wariantów promptów
narzędziem [`tools/GameTranslatorOverlay.ProviderEval`](../tools/GameTranslatorOverlay.ProviderEval/README.md).

**Wszystkie linie — zarówno angielskie „źródła”, jak i polskie referencje — zostały napisane
ręcznie na potrzeby tego projektu, w stylu gier (dialogi, menu, modyfikatory przedmiotów,
teksty zadań). Nie pochodzą z żadnej gry.** Nazwy własne („Waystone”, „Ember Guild”, „Ashen
Vale”) są wymyślone.

## Format (JSONL — jeden obiekt JSON na wiersz)

| Pole | Wymagane | Opis |
|---|---|---|
| `id` | tak | Unikalny identyfikator linii (w raporcie i CSV). |
| `scene` | tak | Scena. Linie są odtwarzane scenami, w kolejności z pliku — kontekst ostatnich kwestii dialogu działa jak w grze. |
| `kind` | tak | `dialog`, `ui`, `item` albo `quest`. |
| `source` | tak | Tekst angielski. `\n` = podział wiersza jak z OCR („You must find\nthe old Waystone.”). |
| `reference` | tak | Wzorcowe tłumaczenie polskie, zwracające się do gracza na „ty”, z tą samą liczbą wierszy co `source`. |
| `expect_gender` | nie | `"f"` albo `"m"` — rodzaj, którego wymagają formy rodzajowe w tej linii (mówiącego w 1. osobie: „czekałam/czekałem”, „gotowa/gotowy”, albo adresata w 2. osobie: „spisałaś/spisałeś”). |
| `terms` | nie | `[{"source": "Waystone", "target": "Kamień Drogi"}]` — terminy słownika. Trafiają do słownika pipeline'u, a kontrola sprawdza, czy tłumaczenie użyło ich polskich rdzeni. |

## Prawdziwe linie z gier

Własne linie przepisane z gier trzymaj w `eval/private/` — katalog jest w `.gitignore`
i nigdy nie trafia do repozytorium (teksty gier są chronione prawem autorskim).
Wyniki narzędzia domyślnie trafiają do `eval/out/` (także ignorowanego).
