using System.Text;
using GameTranslatorOverlay.App.Ui;
using GameTranslatorOverlay.OverlayPreview;

internal static class Program
{
    private const string Help = """
        OverlayPreview — render nakładki tłumaczeń na zapisanych klatkach z gry (narzędzie dev, offline).
          --frame KLATKA.png           klatka gry (PNG/JPG); można podać kilka razy
          --frames-dir KATALOG         wszystkie klatki z katalogu (rekurencyjnie), filtr --pattern "*.png;*.jpg"
          --out KATALOG                katalog galerii (lokalny, poza repo: zawiera teksty i obrazy gry)
          --dpi 144                    DPI monitora gracza (144 = 150%)
          --cache KOPIA_cache.db       kopia bazy tłumaczeń (czytana z kopii roboczej, oryginał nietknięty)
          --corpus KORPUS.jsonl        korpus gry dla aktywnego profilu
          --profile escape-academy     profil gry ("none" = bez profilu)
          --blocks BLOKI.json          gotowe bloki zamiast OCR (format jak wyjściowy bloki.json; tylko z jedną klatką)
          --placement cover            cover | below
          --font-size 0                0 = auto (jak gracz)
          --font-family auto         auto = krój z profilu gry (jak gracz), inaczej nazwa czcionki
          --opacity 0.4                krycie tła z ustawień
          --zoom 3                     powiększenie wycinków bloków (1–6)
          --cover crisp                crisp = wypełnione litery (jak live), soft = miękka łatka (tło w ruchu), off = dawna łatka z tekstury
        Brak tłumaczenia w bazie = Mock z prefiksem [PL]. HTTP jest zablokowane.
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine(Help);
            return 0;
        }

        PreviewOptions options;
        try
        {
            options = PreviewOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        Directory.CreateDirectory(options.Output);
        using var translator = PreviewTranslator.Create(options);
        var orchestrator = translator.Orchestrator;
        if (options.Profile is not null && orchestrator.ActiveProfile is null)
        {
            Console.Error.WriteLine($"Nie znaleziono profilu „{options.Profile}”. Dostępne: {string.Join(", ", orchestrator.Profiles.Select(static p => p.Id))}.");
            return 3;
        }
        if (options.Corpus is not null && !orchestrator.ActiveCorpus.IsLoaded)
        {
            Console.Error.WriteLine($"Korpus nie został wczytany: {orchestrator.ActiveCorpus.Issue ?? "nieznana przyczyna"}.");
            return 3;
        }
        if (!translator.Ocr.IsLanguageAvailable(translator.Settings.SourceLanguage))
        {
            Console.Error.WriteLine($"Brak pakietu Windows OCR dla „{translator.Settings.SourceLanguage}”.");
            return 4;
        }
        OverlayFonts.ProfileFont = orchestrator.ActiveProfile?.Overlay?.FontFamily;
        var warmUp = System.Diagnostics.Stopwatch.StartNew();
        OverlayFonts.WarmUp(OverlayFonts.ResolveFamilyName(translator.Settings, OverlayFonts.ProfileFont));
        Console.WriteLine($"Rozgrzanie krojów: {warmUp.Elapsed.TotalMilliseconds:0} ms");
        Console.WriteLine($"Profil: {orchestrator.ActiveProfile?.Id ?? "brak"} • korpus: {orchestrator.ActiveCorpus.Texts} tekstów • baza: {(options.Cache is null ? "pusta" : "kopia")}");

        var galleries = new List<FrameGallery>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in options.Frames)
        {
            var name = UniqueName(frame, names);
            Console.WriteLine($"{name}: {frame}");
            var analysis = FrameAnalyzer.AnalyzeAsync(frame, translator, options.Blocks, options.Cover).GetAwaiter().GetResult();
            var overlay = OverlayComposer.Render(analysis, translator.Settings, options.Dpi, orchestrator.ActiveProfile?.Overlay?.FontFamily);
            var gallery = GalleryWriter.Write(name, Path.Combine(options.Output, name), analysis, overlay, translator.Settings, options.Dpi, options.Zoom);
            galleries.Add(gallery);
            var mock = analysis.Blocks.Count(static b => b.Origin.Contains("Mock", StringComparison.Ordinal));
            Console.WriteLine($"  bloki: {analysis.Blocks.Count} (na nakładce {overlay.Blocks.Count}, Mock {mock}), odrzucone: {analysis.Rejected.Count}, plików: {gallery.Files.Count}, układ WPF {overlay.LayoutMs:0.0} ms, łatki {analysis.Blocks.Sum(static b => b.Cover?.BuildMs ?? 0):0.0} ms");
        }

        var index = GalleryWriter.WriteIndex(options.Output, galleries, translator.Settings, options.Dpi, options);
        Console.WriteLine($"Galeria: {index}");
        return 0;
    }

    private static string UniqueName(string frame, HashSet<string> names)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(frame)) ?? "klatka";
        var stem = Path.GetFileNameWithoutExtension(frame);
        var baseName = Sanitize($"{parent}-{stem}");
        var name = baseName;
        for (var i = 2; !names.Add(name); i++) name = $"{baseName}-{i}";
        return name;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
    }
}
