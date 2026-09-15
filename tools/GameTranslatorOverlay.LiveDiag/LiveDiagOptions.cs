using System.Globalization;

namespace GameTranslatorOverlay.LiveDiag;

/// <summary>Pure command-line parsing; no desktop, files, settings or services.</summary>
internal sealed record LiveDiagOptions(
    string? AttachTitle, int Seconds, string? ProfileId, double Upscale,
    string? OutputPath, bool DumpFrames, bool IncludeText, bool ListWindows, bool Help)
{
    public static LiveDiagOptions Parse(string[] args)
    {
        var positional = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? profileId = null;
        string? outputPath = null;
        double upscale = 0;
        bool dumpFrames = false, includeText = false, listWindows = false, help = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("-", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }
            if (!seen.Add(arg)) throw new ArgumentException($"Powtórzona opcja: {arg}.");
            string Value()
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Brak wartości opcji {arg}.");
                return args[i];
            }
            switch (arg)
            {
                case "--profile":
                    profileId = Value();
                    if (profileId.Equals("none", StringComparison.OrdinalIgnoreCase)) profileId = null;
                    break;
                case "--upscale":
                    if (!double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out upscale)
                        || !double.IsFinite(upscale) || upscale < 0 || upscale > 4)
                        throw new ArgumentException("--upscale wymaga liczby od 0 do 4 (kropka dziesiętna).");
                    break;
                case "--output": outputPath = Value(); break;
                case "--dump-frames": dumpFrames = true; break;
                case "--include-text": includeText = true; break;
                case "--list-windows": listWindows = true; break;
                case "--help": help = true; break;
                default: throw new ArgumentException($"Nieznana opcja: {arg}.");
            }
        }

        if (positional.Count > 2) throw new ArgumentException("Podaj najwyżej fragment tytułu i czas w sekundach.");
        string? attachTitle = null;
        var seconds = 36;
        if (positional.Count > 0)
        {
            if (int.TryParse(positional[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration))
            {
                if (positional.Count > 1) throw new ArgumentException("Po czasie nie może wystąpić kolejny argument pozycyjny.");
                seconds = duration;
            }
            else
            {
                attachTitle = positional[0];
                if (string.IsNullOrWhiteSpace(attachTitle)) throw new ArgumentException("Fragment tytułu nie może być pusty.");
                seconds = 25;
                if (positional.Count > 1 && !int.TryParse(positional[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
                    throw new ArgumentException("Czas musi być całkowitą liczbą sekund.");
            }
        }
        if (seconds is < 1 or > 86400) throw new ArgumentException("Czas musi wynosić od 1 do 86400 sekund.");
        if (listWindows && positional.Count > 0) throw new ArgumentException("--list-windows nie przyjmuje tytułu ani czasu.");
        if (profileId is not null && seen.Contains("--upscale"))
            throw new ArgumentException("--upscale dotyczy wyłącznie --profile none; wybrany profil ustala skalę OCR.");
        return new(attachTitle, seconds, profileId, upscale, outputPath, dumpFrames, includeText, listWindows, help);
    }
}
