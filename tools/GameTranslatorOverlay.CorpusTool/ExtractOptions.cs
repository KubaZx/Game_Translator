namespace GameTranslatorOverlay.CorpusTool;

public sealed record ExtractOptions
{
    public bool Help { get; init; }
    public string? Command { get; init; }
    public string? ProfileId { get; init; }
    public string? ProfileFile { get; init; }
    public string? ProfilesDirectory { get; init; }
    public string? GameDirectory { get; init; }
    public string? OutputPath { get; init; }
    public string? DataDirectory { get; init; }
    public string? StatsPath { get; init; }

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameTranslatorOverlay");

    public string ResolvedDataDirectory => Path.GetFullPath(DataDirectory ?? DefaultDataDirectory);

    public string ResolveOutputPath(string profileId) =>
        Path.GetFullPath(OutputPath ?? Path.Combine(ResolvedDataDirectory, "corpus", $"{profileId}.corpus.jsonl"));

    public static ExtractOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new ExtractOptions { Help = true };
        var options = new ExtractOptions();
        var start = 0;
        if (!args[0].StartsWith('-'))
        {
            options = options with { Command = args[0].ToLowerInvariant() };
            start = 1;
        }

        for (var i = start; i < args.Count; i++)
        {
            var name = args[i];
            if (name is "--help" or "-h" or "/?")
            {
                options = options with { Help = true };
                continue;
            }
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Opcja {name} wymaga wartości.");
            }
            var value = args[++i];
            options = name switch
            {
                "--profile" => options with { ProfileId = value },
                "--profile-file" => options with { ProfileFile = value },
                "--profiles-dir" => options with { ProfilesDirectory = value },
                "--game-dir" => options with { GameDirectory = value },
                "--out" => options with { OutputPath = value },
                "--data-dir" => options with { DataDirectory = value },
                "--stats" => options with { StatsPath = value },
                _ => throw new ArgumentException($"Nieznana opcja: {name}."),
            };
        }

        if (options.Help) return options;
        if (options.Command != "extract")
        {
            throw new ArgumentException(options.Command is null ? "Brak polecenia (dostępne: extract, translate)." : $"Nieznane polecenie: {options.Command}.");
        }
        if (options.ProfileId is null && options.ProfileFile is null) throw new ArgumentException("Podaj --profile <id>.");
        if (options.GameDirectory is null) throw new ArgumentException("Podaj --game-dir <folder gry>.");
        return options;
    }
}
