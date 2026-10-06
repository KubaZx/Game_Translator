using System.Globalization;
using System.Text;
using GameTranslatorOverlay.CorpusTool;
using GameTranslatorOverlay.CorpusTool.Safety;
using GameTranslatorOverlay.CorpusTool.Translation;

internal static class Program
{
    private const string Help = """
        CorpusTool — lokalny korpus tekstów gry z jej plików (ADR-014) i jego tłumaczenie z wyprzedzeniem.
          extract --profile ID --game-dir FOLDER [--out PLIK.jsonl] [--data-dir KATALOG] [--stats PLIK.json]
            --profile ID         Profil gry z receptą korpusu (profiles/<id>/profile.json).
            --profile-file PLIK  Profil z pliku zamiast z katalogu profili.
            --profiles-dir DIR   Dodatkowy katalog profili.
            --game-dir FOLDER    Folder instalacji gry (czytany wyłącznie do odczytu).
            --out PLIK           Plik JSONL korpusu; domyślnie <data-dir>\corpus\<id>.corpus.jsonl.
            --data-dir KATALOG   Dane lokalne; domyślnie %LOCALAPPDATA%\GameTranslatorOverlay.
            --stats PLIK         Statystyki (same liczby, bez tekstów gry) do pliku JSON.
        extract odmawia pracy, gdy gra działa, ma anti-cheat (EasyAntiCheat, BattlEye), podpisane
        albo zaszyfrowane kontenery, jest grą online albo jest na liście wykluczeń (Path of Exile 1/2).
        Niczego nie zapisuje w folderze gry ani w repozytorium i niczego nie pobiera z sieci.

          translate --profile ID --provider deepl|llm|mock [--corpus PLIK] [--data-dir KATALOG | --cache BAZA] [--limit N] [--dry-run]
            --provider ID        deepl (partie do 50), llm (serwer zgodny z OpenAI, partie do 25) albo mock (bez sieci).
            --corpus PLIK        Korpus JSONL; domyślnie <data-dir>\corpus\<id>.corpus.jsonl.
            --data-dir KATALOG   Dane aplikacji (cache.db, settings.json, słownik); domyślnie %LOCALAPPDATA%\GameTranslatorOverlay.
            --cache BAZA         Plik bazy tłumaczeń; bez --data-dir ustawienia są czytane z jej folderu.
            --dry-run            Tylko liczby i szacunek kosztu: nic nie wysyła i nic nie zapisuje.
            --limit N            Najwyżej N tekstów (kolejność: dialogi, napisy, UI).
            --kinds LISTA        Tylko wybrane rodzaje: ui, dialog, subtitle.
            --batch N            Mniejsze partie (najwyżej 50 dla DeepL, 25 dla LLM).
            --parallel N         Ile partii naraz (domyślnie 3, serwer lokalny 1).
            --player-gender P    male | female | unknown; domyślnie z settings.json.
            --force              Tłumacz ponownie automatyczne wpisy profilu (nigdy korekt i słownika).
            --skip-cached        Pomiń teksty, które mają już aktualny wpis w cache (także globalny).
            --no-deepl-glossary  DeepL bez glosariusza na koncie.
            --llm-thinking TYP   Pole thinking {"type": TYP}, np. disabled.
            --llm-effort POZIOM  Pole reasoning_effort, np. none.
            --llm-max-tokens N   Pole max_tokens.
            --llm-json           Pole response_format {"type": "json_object"}.
            --llm-no-preset      Bez domyślnych opcji z presetu serwera (DeepSeek: thinking disabled).
            --price-in/--price-out USD   Cena 1 mln tokenów wejścia/wyjścia do szacunku (LLM).
            --price-chars USD    Cena 1 mln znaków do szacunku (DeepL).
            --stats PLIK         Statystyki (same liczby) do pliku JSON.
        Klucze wyłącznie ze zmiennych środowiskowych: GTO_DEEPL_KEY, GTO_LLM_ENDPOINT + GTO_LLM_MODEL (+ GTO_LLM_KEY).
        Wpisy trafiają do cache z profilem gry i znacznikiem src=corpus; ręczne korekty, zatwierdzone wpisy
        i terminy słownika nie są nadpisywane. Tryb prywatny w ustawieniach = odmowa zapisu.
        """;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length > 0 && args[0].Equals("translate", StringComparison.OrdinalIgnoreCase))
        {
            return await TranslateAsync(args);
        }

        ExtractOptions options;
        try
        {
            options = ExtractOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message + "\nUżyj --help.");
            return 2;
        }
        if (options.Help)
        {
            Console.WriteLine(Help);
            return 0;
        }

        try
        {
            var report = new CorpusExtractor(new SystemProcessLister()).Run(options);
            Print(report);
            return 0;
        }
        catch (RefusedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or FormatException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"Nie udało się zbudować korpusu: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> TranslateAsync(string[] args)
    {
        TranslateOptions options;
        try
        {
            options = TranslateOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message + "\nUżyj --help.");
            return 2;
        }
        if (options.Help)
        {
            Console.WriteLine(Help);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
            return await TranslateCommand.RunAsync(options, Console.Out, Console.Error, cancellationToken: cancellation.Token);
        }
        catch (RefusedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return TranslateCommand.ExitRefused;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Przerwano.");
            return TranslateCommand.ExitCancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException
                                       or GameTranslatorOverlay.Infrastructure.Caching.CacheStorageException or System.Data.Common.DbException)
        {
            Console.Error.WriteLine($"Tłumaczenie korpusu nie powiodło się: {ex.Message}");
            return TranslateCommand.ExitError;
        }
    }

    private static void Print(ExtractionReport report)
    {
        var c = CultureInfo.InvariantCulture;
        Console.WriteLine(string.Create(c, $"Profil: {report.ProfileId}; kontener: {report.ContainerKind} (Unity {report.UnityVersion}, plik serializowany v{report.SerializedVersion})"));
        Console.WriteLine(string.Create(c, $"Obiekty: {report.ObjectCount}; TextAssety: {report.TextAssets}; wpisy: {report.Entries}; unikalne teksty: {report.UniqueTexts} ({report.UniqueCharacters} znaków); mówcy: {report.Speakers}"));
        foreach (var (kind, stats) in report.Kinds)
        {
            Console.WriteLine(string.Create(c, $"  {kind}: {stats.Entries} wpisów, {stats.UniqueTexts} unikalnych, {stats.UniqueCharacters} znaków, mówcy: {stats.Speakers}"));
        }
        foreach (var source in report.Sources)
        {
            Console.WriteLine(string.Create(c, $"  źródło {source.Id}: {source.Assets} assetów (pominięte {source.SkippedAssets}), {source.Entries} wpisów, odczyt awaryjny kodowania: {source.AssetsWithFallback} assetów / {source.FallbackBytes} B"));
        }
        Console.WriteLine(string.Create(c, $"Czas: odczyt {report.ReadMs} ms, parsowanie {report.ParseMs} ms, razem {report.TotalMs} ms"));
        Console.WriteLine($"Zapisano: {report.OutputPath}");
    }
}
