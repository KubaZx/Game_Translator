using BenchmarkDotNet.Attributes;
using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Ocr;
using GameTranslatorOverlay.Core.Text;

namespace GameTranslatorOverlay.Benchmarks;

/// <summary>
/// Obróbka tekstu po OCR, przed tłumaczeniem: grupowanie wierszy w bloki, odsiew śmieci
/// i normalizacja (40 wierszy — okno dialogu, menu, dziennik zadań, podpowiedź i śmieci HUD),
/// sklejanie/rozkładanie wierszy dialogu (TextReflow) oraz wyszukiwanie terminów słownika
/// w tekstach wysyłanych dostawcy kontekstowemu (osobna klasa niżej, bo tylko ona zależy
/// od liczby terminów — parametr w tej klasie dublowałby pozostałe pomiary).
/// </summary>
[MemoryDiagnoser]
public class TextBenchmarks
{
    internal static readonly string[] DialogLines =
    [
        "Welcome back, traveler. The road to the",
        "northern fortress has been closed since the",
        "storm, and the blacksmith refuses to forge",
        "new blades until the iron shipment arrives.",
        "Will you help us?",
        "Speak to the Captain of the Guard.",
    ];

    private List<OcrLine> _ocrLines = [];
    private string _dialog = string.Empty;
    private ReflowPlan _plan = null!;
    private string _translatedParagraphs = string.Empty;
    [GlobalSetup]
    public void GlobalSetup()
    {
        _ocrLines = BuildOcrLines();
        if (_ocrLines.Count != 40) throw new InvalidOperationException("Oczekiwano 40 wierszy OCR.");

        _dialog = TextNormalizer.Normalize(string.Join('\n', DialogLines));
        (var unwrapped, _plan) = TextReflow.Unwrap(_dialog);
        // Tłumaczenie ma tyle akapitów co plan — inaczej Rewrap zwraca tekst bez zmian i nic nie mierzy.
        _translatedParagraphs = string.Join('\n', unwrapped.Split('\n').Select(static (_, i) => i switch
        {
            0 => "Witaj z powrotem, podróżniku. Droga do północnej fortecy jest zamknięta od czasu burzy, "
                + "a kowal odmawia wykuwania nowych ostrzy, dopóki nie dotrze dostawa żelaza.",
            1 => "Pomożesz nam?",
            _ => "Porozmawiaj z Kapitanem Straży.",
        }));
        if (!_plan.ChangesLayout) throw new InvalidOperationException("Dialog powinien wymagać sklejenia wierszy.");
    }

    /// <summary>Etap po OCR w pętli live: bloki → normalizacja → odsiew śmieci.</summary>
    [Benchmark]
    public int GrupowanieFiltrNormalizacja()
    {
        var meaningful = 0;
        foreach (var block in TextBlockGrouper.Group(_ocrLines))
        {
            var normalized = TextNormalizer.Normalize(block.Text);
            if (JunkFilter.IsMeaningful(normalized)) meaningful++;
        }
        return meaningful;
    }

    [Benchmark]
    public ReflowPlan SklejanieWierszy() => TextReflow.Unwrap(_dialog).Plan;

    [Benchmark]
    public string RozkladanieTlumaczenia() => TextReflow.Rewrap(_translatedParagraphs, _plan);

    internal static List<OcrLine> BuildOcrLines()
    {
        var lines = new List<OcrLine>();

        void Add(string text, int x, int y, int height = 24)
        {
            // Słowa z przybliżonymi ramkami — grouper patrzy na ramki wierszy, słowa są dla spójności modelu.
            var words = new List<OcrWord>();
            var cursor = x;
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var width = word.Length * height / 2;
                words.Add(new OcrWord(word, new RectPx(cursor, y, width, height)));
                cursor += width + height / 3;
            }
            lines.Add(new OcrLine(text, new RectPx(x, y, Math.Max(1, cursor - x), height), words));
        }

        // Okno dialogu na dole ekranu (6).
        for (var i = 0; i < DialogLines.Length; i++) Add(DialogLines[i], 480, 820 + i * 30);

        // Menu po lewej (10).
        string[] menu = ["Continue", "New Game", "Load Game", "Options", "Graphics", "Audio", "Controls", "Credits", "Extras", "Quit to Desktop"];
        for (var i = 0; i < menu.Length; i++) Add(menu[i], 60, 200 + i * 40, 28);

        // Dziennik zadań po prawej (8).
        string[] quests =
        [
            "Active Quests", "The Broken Blade", "Find the missing iron shipment", "near the old mill.",
            "Northern Watch", "Report to the Captain", "of the Guard at dawn.", "Reward: 250 gold",
        ];
        for (var i = 0; i < quests.Length; i++) Add(quests[i], 1450, 150 + i * 28, 22);

        // Podpowiedź przedmiotu na środku (6).
        string[] tooltip =
        [
            "Iron Longsword", "Damage: 24-31", "Durability: 80/100", "+15% critical chance",
            "A sturdy blade forged in", "the northern mountains.",
        ];
        for (var i = 0; i < tooltip.Length; i++) Add(tooltip[i], 860, 400 + i * 26, 20);

        // Śmieci HUD rozrzucone po ekranie (10): liczniki, pojedyncze znaki, symbole.
        string[] junk = ["60", "|", "x", "###", "12:45", "--", "HP", "MP", "1920", "..."];
        for (var i = 0; i < junk.Length; i++) Add(junk[i], 20 + i * 180, 20, 18);

        return lines;
    }
}

/// <summary>
/// <see cref="GlossaryService.FindTermsIn"/> na tekstach jednej partii (sklejony dialog
/// i 12 wierszy menu/dziennika) — koszt dobierania terminów słownika do zapytania dla
/// dostawców kontekstowych (LLM, Claude) przy małym i dużym słowniku gry.
/// </summary>
[MemoryDiagnoser]
public class GlossaryTermBenchmarks
{
    private List<string> _contextTexts = [];
    private GlossaryService _glossary = new();

    [Params(50, 500)]
    public int GlossaryTerms { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var dialog = TextNormalizer.Normalize(string.Join('\n', TextBenchmarks.DialogLines));
        var (unwrapped, _) = TextReflow.Unwrap(dialog);
        _glossary = new GlossaryService();
        _glossary.LoadDocument(new GlossaryDocument { Name = "bench", Terms = BuildTerms(GlossaryTerms) });
        _contextTexts = [unwrapped, .. TextBenchmarks.BuildOcrLines().Skip(6).Take(12).Select(static l => l.Text)];
        if (_glossary.FindTermsIn(_contextTexts).Count == 0)
            throw new InvalidOperationException("Teksty powinny zawierać terminy słownika.");
    }

    [Benchmark]
    public int TerminySlownikaWTekstach() => _glossary.FindTermsIn(_contextTexts).Count;

    private static List<GlossaryTerm> BuildTerms(int count)
    {
        // Kilka terminów występuje w tekstach (realne trafienia), reszta to typowe nazwy własne gry.
        var terms = new List<GlossaryTerm>
        {
            new("blacksmith", "kowal"),
            new("Captain of the Guard", "Kapitan Straży", CaseSensitive: true),
            new("northern fortress", "północna forteca"),
            new("iron shipment", "dostawa żelaza"),
            new("Iron Longsword", "Żelazny Długi Miecz", CaseSensitive: true),
        };
        string[] adjectives = ["Ancient", "Cursed", "Silver", "Shadow", "Frozen", "Burning", "Hollow", "Crimson", "Silent", "Golden"];
        string[] nouns = ["Crown", "Blade", "Tower", "Keep", "Shrine", "Grove", "Crypt", "Banner", "Relic", "Gate"];
        for (var i = 0; terms.Count < count; i++)
        {
            var name = $"{adjectives[i % adjectives.Length]} {nouns[i / adjectives.Length % nouns.Length]} {i}";
            terms.Add(new GlossaryTerm(name, $"PL {name}"));
        }
        return terms;
    }
}
