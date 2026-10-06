using System.Globalization;

namespace GameTranslatorOverlay.OverlayPreview;

internal sealed class PreviewOptions
{
    public List<string> Frames { get; } = [];
    public string Output { get; private set; } = string.Empty;
    public double Dpi { get; private set; } = 144;
    public string? Cache { get; private set; }
    public string? Corpus { get; private set; }
    public string? Profile { get; private set; } = "escape-academy";
    public string? Blocks { get; private set; }
    public string Placement { get; private set; } = "cover";
    public double FontSize { get; private set; }
    public string FontFamily { get; private set; } = "auto";
    public double Opacity { get; private set; } = 0.4;
    public int Zoom { get; private set; } = 3;
    public string Cover { get; private set; } = "crisp";

    public static PreviewOptions Parse(string[] args)
    {
        var options = new PreviewOptions();
        var framesDirs = new List<string>();
        var pattern = "*.png;*.jpg;*.jpeg";
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Oczekiwano opcji, jest: {name}");
            if (i + 1 >= args.Length) throw new ArgumentException($"Brak wartości opcji {name}.");
            var value = args[++i];
            switch (name)
            {
                case "--frame": options.Frames.Add(Path.GetFullPath(value)); break;
                case "--frames-dir": framesDirs.Add(Path.GetFullPath(value)); break;
                case "--pattern": pattern = value; break;
                case "--out": options.Output = Path.GetFullPath(value); break;
                case "--dpi": options.Dpi = Number(name, value, 96, 480); break;
                case "--cache": options.Cache = Path.GetFullPath(value); break;
                case "--corpus": options.Corpus = Path.GetFullPath(value); break;
                case "--profile": options.Profile = value.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : value; break;
                case "--blocks": options.Blocks = Path.GetFullPath(value); break;
                case "--placement": options.Placement = OneOf(name, value, "cover", "below"); break;
                case "--font-size": options.FontSize = Number(name, value, 0, 200); break;
                case "--font-family": options.FontFamily = value; break;
                case "--opacity": options.Opacity = Number(name, value, 0, 1); break;
                case "--zoom": options.Zoom = (int)Number(name, value, 1, 6); break;
                case "--cover": options.Cover = OneOf(name, value, "crisp", "soft", "off"); break;
                default: throw new ArgumentException($"Nieznana opcja: {name}");
            }
        }

        foreach (var directory in framesDirs)
        {
            if (!Directory.Exists(directory)) throw new ArgumentException($"Nie ma katalogu klatek: {directory}");
            var found = pattern
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(p => Directory.EnumerateFiles(directory, p, SearchOption.AllDirectories))
                .Select(Path.GetFullPath)
                .Where(path => !path.StartsWith(options.Output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase);
            options.Frames.AddRange(found);
        }

        if (options.Output.Length == 0) throw new ArgumentException("Brak opcji --out.");
        if (options.Frames.Count == 0) throw new ArgumentException("Podaj klatkę (--frame) albo katalog klatek (--frames-dir).");
        foreach (var frame in options.Frames)
        {
            if (!File.Exists(frame)) throw new ArgumentException($"Nie ma pliku klatki: {frame}");
        }
        if (options.Blocks is not null && options.Frames.Count != 1)
            throw new ArgumentException("Opcja --blocks działa z dokładnie jedną klatką.");
        if (options.Blocks is not null && !File.Exists(options.Blocks)) throw new ArgumentException($"Nie ma pliku bloków: {options.Blocks}");
        if (options.Cache is not null && !File.Exists(options.Cache)) throw new ArgumentException($"Nie ma bazy: {options.Cache}");
        if (options.Corpus is not null && !File.Exists(options.Corpus)) throw new ArgumentException($"Nie ma korpusu: {options.Corpus}");
        if (options.Corpus is not null && options.Profile is null) throw new ArgumentException("Korpus wymaga profilu (--profile).");
        return options;
    }

    private static double Number(string name, string value, double min, double max)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
            throw new ArgumentException($"{name}: oczekiwano liczby {min.ToString(CultureInfo.InvariantCulture)}–{max.ToString(CultureInfo.InvariantCulture)}, jest „{value}”.");
        return number;
    }

    private static string OneOf(string name, string value, params string[] allowed) =>
        allowed.FirstOrDefault(a => a.Equals(value, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"{name}: dozwolone {string.Join(", ", allowed)}.");
}
