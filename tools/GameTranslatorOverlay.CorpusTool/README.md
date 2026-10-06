# CorpusTool — lokalny korpus tekstów gry (ADR-014)

Konsolowe narzędzie offline (net10.0, bez WPF), które czyta teksty dla gracza z plików
zainstalowanej gry i zapisuje je jako lokalny korpus JSONL. Korpus służy do przyciągania odczytów
OCR do znanych zdań (`GameTranslatorOverlay.Core.Corpus`) i do tłumaczenia z wyprzedzeniem
(polecenie `translate`, opis niżej). Nakładka nie czyta plików gry — korzysta wyłącznie z danych,
które wytworzy to narzędzie.

Zasady wynikają z [ADR-014](../../docs/TECHNOLOGY_DECISIONS.md) i są zaszyte w kodzie z testami
(`tests/GameTranslatorOverlay.CorpusTool.Tests`).

## Uruchomienie

```bash
dotnet run --project tools/GameTranslatorOverlay.CorpusTool -c Release -- extract \
  --profile escape-academy \
  --game-dir "C:\Program Files (x86)\Steam\steamapps\common\Escape Academy"
```

| Opcja | Znaczenie |
|---|---|
| `--profile ID` | Profil gry z receptą korpusu (`profiles/<id>/profile.json`, sekcja `corpus`). |
| `--profile-file PLIK` | Profil z pliku zamiast z katalogu profili. |
| `--profiles-dir KATALOG` | Dodatkowy katalog profili (przeszukiwany przed `<data-dir>\profiles` i profilami obok narzędzia). |
| `--game-dir FOLDER` | Folder instalacji gry; czytany wyłącznie do odczytu. |
| `--out PLIK` | Plik korpusu; domyślnie `<data-dir>\corpus\<id>.corpus.jsonl` — tylko tam szuka go aplikacja. |
| `--data-dir KATALOG` | Dane lokalne; domyślnie `%LOCALAPPDATA%\GameTranslatorOverlay`. |
| `--stats PLIK` | Statystyki ekstrakcji (same liczby, bez tekstów gry) jako JSON. |

Kod wyjścia: 0 — korpus zapisany, 1 — nieczytelny plik lub nieobsługiwany format, 2 — złe
opcje, 3 — odmowa (zabezpieczenia ADR-014, brak profilu albo recepty).

## Zabezpieczenia (twarde, bez opcji obejścia)

- **Gra wyłączona.** Odmowa, gdy działa proces z `processNames` profilu albo dowolny plik
  `*.exe` z `--game-dir` lub z katalogu głównego gry. Procesy są sprawdzane, zanim narzędzie
  przeczyta cokolwiek w folderze gry (także nagłówki `.pak`/`.utoc`).
- **Katalog główny gry.** Folder `<…>\steamapps\common\<gra>` (albo `Epic Games\<gra>`,
  `GOG Galaxy\Games\<gra>`), a poza bibliotekami — najwyższy folder nad `--game-dir` z plikiem
  gry z `processNames` profilu. Gdy `--game-dir` wskazuje podfolder, zabezpieczenia niżej
  sprawdzają cały katalog główny.
- **Bez anti-cheatu i gier online.** Odmowa, gdy w folderze gry (także w katalogu głównym nad
  `--game-dir`) są foldery EasyAntiCheat / BattlEye albo pliki ich usług, gdy profil ma
  `"online": true` albo jest na liście wykluczeń (`path-of-exile`, `path-of-exile-2`), gdy
  ścieżka gry prowadzi do „Path of Exile” / „Grinding Gear Games” albo w folderze są
  `Content.ggpk`, `Bundles2`, `PathOfExile*.exe`.
- **Bez podpisanych i zaszyfrowanych kontenerów.** Odmowa przy plikach `.sig`, pakach Unreal
  z zaszyfrowanym indeksem, kontenerach IoStore (`.utoc`) z flagą Encrypted/Signed oraz
  kontenerach UnityFS z nieznanymi flagami albo znacznikiem szyfrowania UnityCN. Narzędzie nie
  ma kodu deszyfrującego.
- **Tylko odczyt.** Pliki gry są otwierane z `FileAccess.Read` i `FileShare.ReadWrite |
  FileShare.Delete` (nie blokują aktualizacji Steam). Wynik i statystyki nie mogą leżeć
  w folderze gry ani w jej katalogu głównym, pod `steamapps\common` (ani w bibliotekach
  Epic/GOG) ani w folderze z plikiem gry z profilu; korpus nie może też trafić do repozytorium
  (poza `eval/private/`).
- **Bez sieci.** Narzędzie niczego nie pobiera; test sprawdza, że jego biblioteka nie odwołuje
  się do bibliotek sieciowych .NET, a kod `extract` (czytniki, parsery, straże) — także do
  dostawców tłumaczeń. Dekompresja LZ4 jest własna (format bloku LZ4). Jedyny możliwy ruch
  sieciowy to jawne `translate --provider deepl|llm` (ADR-014 pkt 5), przez dostawców
  z `GameTranslatorOverlay.Infrastructure`.

## Recepta w profilu

Specyfika gry to dane w profilu; czytnik jest wspólny dla rodziny formatów. Obsługiwana
rodzina: `unity-textasset` — kontener UnityFS (bloki LZ4, LZ4HC albo bez kompresji; LZMA
nieobsługiwane) albo luźny plik serializowany Unity (wersje 14–23), z którego czytane są
obiekty TextAsset (nazwa i bajty).

```json
"corpus": {
  "format": "unity-textasset",
  "container": "Escape Academy_Data/data.unity3d",
  "file": "resources.assets",
  "sources": [
    { "id": "gameplay-strings", "parser": "csv", "kind": "ui", "include": ["GameplayStrings*"],
      "keyColumn": "Key", "textColumn": "Value-En", "contextColumns": ["Area", "Context", "Loc Context"] },
    { "id": "yarn-dialog", "parser": "csv", "kind": "dialog", "include": ["* (en-US)"],
      "keyColumn": "id", "textColumn": "text", "nodeColumns": ["file", "node"], "orderColumn": "lineNumber",
      "speakerPattern": "^(?<speaker>[^:\\r\\n]{0,40}):\\s*" },
    { "id": "subtitles", "parser": "srt", "kind": "subtitle", "include": ["*"], "exclude": ["*_fr", "*_de", "*_es"],
      "speakerPattern": "^\\[(?<speaker>[^\\]\\r\\n]{1,40})\\]\\s*", "inheritSpeaker": true }
  ]
}
```

- `include` / `exclude` — wzorce nazw TextAssetów (`*`, `?`, bez wielkości liter).
- `parser: csv` — tabela z nagłówkiem; kolumny po nazwie. Wiersze bez tekstu albo bez liter
  i cyfr są pomijane. Tekst, który nie jest czystym UTF-8, czytany jest odpornie (błędne bajty
  jak Windows-1252).
- `parser: srt` — napisy z czasem; `durationMs` to czas wyświetlania napisu.
- `speakerPattern` — wyrażenie z grupą `speaker`, zdejmowane z początku tekstu;
  `inheritSpeaker` przenosi mówcę na kolejne napisy tego samego pliku.
- `stripRichText` (domyślnie `true`) — usuwa znaczniki Unity rich text (`<i>`, `<color=…>`…).

## Wynik

Jeden wpis JSON w wierszu:

```json
{"key":"…","en":"…","context":"…","kind":"ui|dialog|subtitle","speaker":"…","node":"…","order":3,"durationMs":1250,"source":"<nazwa TextAssetu>"}
```

Pola opcjonalne są pomijane, gdy są puste. Plik zapisywany jest atomowo (plik tymczasowy obok
i podmiana). Teksty gier są chronione prawem autorskim — korpus zostaje lokalnie.

**W aplikacji.** Gdy aktywny profil gry ma plik `<folder danych>\corpus\<id>.corpus.jsonl`,
aplikacja wczytuje go przy zmianie ustawień albo profilu (tylko ten plik — nigdy plików gry)
i przyciąga odczyty OCR do znanych tekstów: tłumaczenie szuka w cache tekstu z korpusu, a nie
odczytu z błędami. Krótkie etykiety są dopasowywane także z typowymi pomyłkami OCR, początek
linii dialogu albo napisów dostaje tłumaczenie całej kwestii, a krótki odczyt niepodobny do
tekstów gry (śmieci z ikon i tekstur) nie idzie do dostawcy. Po ponownym `extract` albo
`translate` uruchom aplikację ponownie. Literalne `\n` w tekstach korpusu są traktowane jak nowy
wiersz (także w kluczu cache `translate`). Szczegóły:
[USER_GUIDE.md → Spolszczenie z wyprzedzeniem](../../docs/USER_GUIDE.md).

## Tłumaczenie z wyprzedzeniem (`translate`)

Tłumaczy korpus u wybranego dostawcy i zapisuje wynik do lokalnej bazy tłumaczeń aplikacji
(`cache.db`) z profilem gry — aplikacja z aktywnym profilem czyta te wpisy jak każdy inny wpis
cache (ten sam klucz: znormalizowany tekst EN po zdjęciu znaczników rich text,
`Core.Corpus.CorpusTranslationKey`). Bez aktywnego profilu wpisy nie są czytane.

```powershell
# najpierw przebieg próbny: liczby i szacunek kosztu, nic nie jest wysyłane ani zapisywane
dotnet run --project tools/GameTranslatorOverlay.CorpusTool -c Release -- translate `
  --profile escape-academy --provider llm --dry-run

# DeepSeek bez myślenia (klucze wyłącznie ze zmiennych środowiskowych)
$env:GTO_LLM_ENDPOINT = "https://api.deepseek.com/v1"
$env:GTO_LLM_MODEL = "deepseek-flash"
$env:GTO_LLM_KEY = "…"
dotnet run --project tools/GameTranslatorOverlay.CorpusTool -c Release -- translate `
  --profile escape-academy --provider llm --llm-thinking disabled

# DeepL (GTO_DEEPL_KEY) albo Mock na kopii bazy (Mock wymaga jawnego --cache / --data-dir)
dotnet run --project tools/GameTranslatorOverlay.CorpusTool -c Release -- translate `
  --profile escape-academy --provider mock --cache D:\kopia\cache.db
```

| Opcja | Znaczenie |
|---|---|
| `--provider ID` | `deepl` (partie do 50 tekstów), `llm` (serwer zgodny z OpenAI, partie do 25), `mock` (bez sieci, atrapa „[PL] …”). |
| `--profile ID` / `--profile-file PLIK` / `--profiles-dir KATALOG` | Profil gry jak w `extract` (id, nazwa gry dla modelu, słownik profilu). |
| `--corpus PLIK` | Korpus JSONL; domyślnie `<data-dir>\corpus\<id>.corpus.jsonl`. |
| `--data-dir KATALOG` | Dane aplikacji: `cache.db`, `settings.json`, prywatny słownik; domyślnie `%LOCALAPPDATA%\GameTranslatorOverlay`. |
| `--cache BAZA` | Inny plik bazy (np. kopia). Bez `--data-dir` ustawienia i słownik są czytane z folderu bazy. |
| `--dry-run` | Liczba tekstów, znaków, partii i szacunek kosztu; bez wysyłania i bez zapisu (baza tylko do odczytu). |
| `--limit N` | Najwyżej N tekstów, w kolejności: dialogi, napisy, UI (tani przebieg pilotażowy). |
| `--kinds LISTA` | Tylko wybrane rodzaje: `ui`, `dialog`, `subtitle`. |
| `--batch N` / `--parallel N` | Mniejsze partie (nie większe niż limit dostawcy); liczba partii naraz (domyślnie 3, lokalny serwer LLM 1, Mock 4; najwyżej 16). |
| `--player-gender P` | `male`, `female`, `unknown`; domyślnie `playerGender` z `settings.json`. |
| `--force` | Tłumacz ponownie automatyczne wpisy profilu (nigdy korekt, zatwierdzonych i słownika). |
| `--skip-cached` | Pomiń teksty, które mają już aktualny automatyczny wpis (także globalny bez profilu). |
| `--no-deepl-glossary` | DeepL bez glosariusza na koncie DeepL. |
| `--llm-thinking`, `--llm-effort`, `--llm-max-tokens`, `--llm-json`, `--llm-no-preset` | Opcje serwera LLM jak w ProviderEval (ADR-013, dopisek 2026-10-06). Bez nich adres DeepSeek dostaje `thinking: disabled`, Ollama `reasoning_effort: none`. |
| `--price-in`, `--price-out` / `--price-chars` | Stawki (USD za 1 mln tokenów / znaków) do szacunku. |
| `--stats PLIK` | Statystyki (same liczby, bez tekstów gry) jako JSON. |

**Partie i kontekst.** Teksty są scalane po kluczu (ta sama treść tłumaczona raz). Dialogi idą
węzłami (`source` + `node`) w kolejności `order`; dłuższy węzeł jest dzielony na kolejne partie,
a każda następna dostaje poprzednie linie węzła z tłumaczeniami (jak pamięć dialogu w aplikacji,
do 6 linii / 1500 znaków). Napisy są sortowane po nazwie pliku, UI dzielone w obrębie tabeli.
Każda partia ma opis sceny (`TranslationContext.Scene`), a każdy tekst notatkę
(`TextNotes`: mówca, plik napisu, klucz i kolumna kontekstu bez flag lokalizacji zaczynających się od `%`).
Model językowy dostaje je w prompcie, DeepL — scenę w parametrze `context`. Nazwa gry pochodzi
z profilu, terminy słownika z partii jak w pipeline.

**Kontrola jakości jak w pipeline.** Sklejanie i przywracanie wierszy (`TextReflow`),
`TranslationQualityGate` (pusty wynik, liczby, brak tłumaczenia, „rozgadany” wynik), jedno
ponowienie tekstów z problemem u modeli językowych, znacznik `qa=…` przy wyniku z problemem
i `qa-final`, gdy ponowne tłumaczenie oznaczonego wpisu nadal ma problem. Dodatkowo znaczniki
`{0}`, `{Imię}`, `[X]` (do 3 znaków), `%s`/`%d` muszą przetrwać — wynik, który je gubi, nie
trafia do bazy. Pusty wynik też nie. Partia z nieczytelną odpowiedzią, złą liczbą tłumaczeń,
odmową filtra treści albo zbyt długim tekstem jest dzielona na połowy (najwyżej 3 poziomy), więc
przepadają tylko teksty, których nie da się przetłumaczyć. Błąd sieci, limitu zapytań albo
serwera nie dzieli partii; błąd klucza, brak środków, zły model albo 3 kolejne nieudane partie
przerywają przebieg.

**Co trafia do bazy.** `game_profile` = id profilu, `provider` = prawdziwa nazwa dostawcy
(`DeepL`, `LLM`, `Mock`), `context` = `reflow-1[;qa=…][;qa-final][;pg=f|m];src=corpus`
(`pg` tylko u dostawcy świadomego płci, czyli LLM), `source_text` = tekst bez znaczników rich
text. Zapis jest partiami w transakcji i nie nadpisuje wpisów ręcznych ani zatwierdzonych.

**Co jest pomijane.** Teksty równe terminowi słownika (globalny, profilu i prywatny słownik
użytkownika), teksty z ręczną korektą lub wpisem zatwierdzonym (także globalnym) oraz teksty,
które mają już aktualny wpis w profilu — ponowne uruchomienie dokańcza przerwany przebieg.
Nieaktualny wpis profilu (stary format, `qa=…` bez `qa-final`, inna płeć gracza przy LLM,
atrapa Mock przy prawdziwym dostawcy) jest tłumaczony ponownie. Wpis globalny (bez profilu,
np. z trybu live) zostaje, a nowy wpis profilu go przesłania przy aktywnym profilu; `--skip-cached`
pomija takie teksty.

**Zabezpieczenia.** Klucze wyłącznie ze zmiennych środowiskowych (`GTO_DEEPL_KEY`,
`GTO_LLM_ENDPOINT` + `GTO_LLM_MODEL` + opcjonalnie `GTO_LLM_KEY` wysyłany tylko na ten adres),
nigdy z DPAPI. Włączony tryb prywatny w `settings.json` albo nieczytelny plik ustawień =
odmowa (kod 3) przed wysłaniem czegokolwiek; `--dry-run` tylko ostrzega. Baza i statystyki nie
mogą leżeć w repozytorium (poza `eval/private/`), pod `steamapps\common` (ani w bibliotekach
Epic/GOG) ani w folderze z plikiem gry z `processNames` profilu. Mock zapisuje tylko do jawnie wskazanej bazy —
jego atrapy przy prawdziwym dostawcy zasłaniają wpisy globalne. Na wyjściu są same liczby;
teksty gry i tłumaczenia zostają w bazie. Przerwanie (Ctrl+C) kończy po bieżących partiach;
zapisane wpisy zostają.

**Szacunek kosztu (±30%).** DeepL: znaki wysłanego tekstu, odsetek miesięcznego limitu API Free
(500 tys.) i koszt po stawce Growth 27,50 USD za 1 mln (źródło wtórne). LLM: tokeny z promptów
złożonych tak jak przy wysyłce (3,5 znaku na token wejścia, 3 na token wyjścia, tłumaczenie
1,15× dłuższe); dla `api.deepseek.com` koszt `deepseek-flash` poza szczytem i w szczycie (bez
trafień cache i bez tokenów rozumowania), dla serwera lokalnego 0, dla innych — po podaniu
`--price-in`/`--price-out`. Po prawdziwym przebiegu LLM narzędzie podaje sumę tokenów z `usage`.

Kod wyjścia: 0 — gotowe (albo nic do zrobienia), 1 — błąd (brak korpusu, brak klucza, błąd
klucza/limitu dostawcy, 3 kolejne nieudane partie), 2 — złe opcje, 3 — odmowa, 4 — część partii
nieudana (ponowne uruchomienie je dokończy), 130 — przerwano.

Uwaga dla uruchomień z sesji, w których `%LOCALAPPDATA%` jest wirtualizowany (pakiet MSIX):
podawaj `--data-dir` albo `--cache` jawnie. Działająca aplikacja pamięta w RAM trafienia, które
już odczytała — po tłumaczeniu do jej bazy uruchom ją ponownie, żeby nowe wpisy profilu
przesłoniły wcześniej odczytane wpisy globalne. Wpisy działają tylko przy aktywnym profilu gry
(wybranym albo wykrytym automatycznie).
