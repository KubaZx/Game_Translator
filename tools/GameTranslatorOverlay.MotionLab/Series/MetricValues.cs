using System.Text.Json;

namespace GameTranslatorOverlay.MotionLab.Series;

internal static class MetricValues
{
    private static readonly HashSet<string> SkippedRoots = new(StringComparer.Ordinal) { "recording", "truth", "worst" };

    public static readonly IReadOnlyList<(string Path, string Label)> Key =
    [
        ("coverage.all.percent", "Pokrycie [%]"),
        ("coverage.static.percent", "Pokrycie HUD [%]"),
        ("coverage.moving.percent", "Pokrycie tekstu ruchomego [%]"),
        ("coverage.cameraMotion.percent", "Pokrycie: kamera w ruchu [%]"),
        ("coverage.cameraRest.percent", "Pokrycie: kamera w spoczynku [%]"),
        ("coverage.staticCameraMotion.percent", "Pokrycie HUD: kamera w ruchu [%]"),
        ("coverage.movingCameraMotion.percent", "Pokrycie ruchomego: kamera w ruchu [%]"),
        ("flicker.holes", "Miganie: dziury"),
        ("flicker.holeSeconds", "Miganie: czas dziur [s]"),
        ("stale.seconds", "Nieaktualne [s]"),
        ("stale.events", "Nieaktualne: zdarzenia ≥200 ms"),
        ("hud.losses", "HUD znika z nakładki"),
        ("latency.nonTyping.median", "Opóźnienie: mediana [ms]"),
        ("latency.all.median", "Opóźnienie (wszystkie): mediana [ms]"),
        ("latency.startedInMotion.median", "Opóźnienie: start w ruchu [ms]"),
        ("latency.startedAtRest.median", "Opóźnienie: start w spoczynku [ms]"),
        ("latency.neverShown", "Nigdy nie pokazane"),
        ("position.covered.p90", "Położenie p90 [px]"),
        ("patches.seamCurrentMotion.median", "Łatki: błąd szwu w ruchu"),
        ("cost.ocrCalls", "OCR wywołań"),
    ];

    private static readonly (string Pattern, int Direction)[] Rules =
    [
        ("coverage.*.seconds", 0),
        ("coverage.", 1),
        ("hud.coverage", 1),
        ("angle.coverage", 1),
        ("hud.staticTracks", 0),
        ("hud.", -1),
        ("flicker.", -1),
        ("stale.", -1),
        ("latency.episodes", 0),
        ("latency.*.count", 1),
        ("latency.", -1),
        ("position.*.count", 0),
        ("position.", -1),
        ("patches.patches", 0),
        ("patches.samples", 0),
        ("patches.frozenThreshold", 0),
        ("patches.patchSeconds", 0),
        ("patches.*.count", 0),
        ("patches.", -1),
        ("problemsByType.", -1),
        ("cost.captureToUpdateMs.count", 0),
        ("cost.captureToUpdateMs.", -1),
        ("cost.ocrGap", -1),
        ("cost.whiffFrames", -1),
        ("cost.screenFallbackFrames", -1),
        ("display.elementRecreations", -1),
    ];

    public static SortedDictionary<string, double?> Flatten(JsonElement root)
    {
        var result = new SortedDictionary<string, double?>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object) return result;
        foreach (var property in root.EnumerateObject())
        {
            if (SkippedRoots.Contains(property.Name)) continue;
            Walk(property.Value, property.Name, result);
        }
        return result;
    }

    private static void Walk(JsonElement element, string path, SortedDictionary<string, double?> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) Walk(property.Value, path + "." + property.Name, result);
                break;
            case JsonValueKind.Number:
                result[path] = element.GetDouble();
                break;
            case JsonValueKind.String when element.GetString() is "NaN" or "Infinity" or "-Infinity":
                result[path] = null;
                break;
        }
    }

    public static int Direction(string path)
    {
        foreach (var (pattern, direction) in Rules)
            if (Matches(pattern, path)) return direction;
        return 0;
    }

    private static bool Matches(string pattern, string path)
    {
        var star = pattern.IndexOf('*');
        if (star < 0) return path.StartsWith(pattern, StringComparison.Ordinal);
        var prefix = pattern[..star];
        var suffix = pattern[(star + 1)..];
        return path.Length >= prefix.Length + suffix.Length
            && path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal);
    }

    public static string? LabelOf(string path) => Key.FirstOrDefault(k => k.Path == path).Label;

    private static readonly string[] CounterPrefixes = ["problemsByType.", "hud.lossesByCause."];

    public static bool IsCounter(string path) => CounterPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));
}
