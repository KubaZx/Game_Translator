using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.CorpusTool.Parsing;
using GameTranslatorOverlay.CorpusTool.Safety;
using GameTranslatorOverlay.CorpusTool.Unity;

namespace GameTranslatorOverlay.CorpusTool;

public sealed record KindStats(int Entries, int UniqueTexts, int UniqueCharacters, int Speakers);

public sealed record ExtractionReport
{
    public required string ProfileId { get; init; }
    public required string OutputPath { get; init; }
    public required string ContainerKind { get; init; }
    public string? UnityVersion { get; init; }
    public int SerializedVersion { get; init; }
    public int ObjectCount { get; init; }
    public int TextAssets { get; init; }
    public int Entries { get; init; }
    public int UniqueTexts { get; init; }
    public int UniqueCharacters { get; init; }
    public int Speakers { get; init; }
    public required IReadOnlyDictionary<string, KindStats> Kinds { get; init; }
    public required IReadOnlyList<SourceStats> Sources { get; init; }
    public long ReadMs { get; init; }
    public long ParseMs { get; init; }
    public long TotalMs { get; init; }
}

public sealed class CorpusExtractor(IProcessLister processLister)
{
    public ExtractionReport Run(ExtractOptions options)
    {
        var total = Stopwatch.StartNew();
        var profile = ProfileLocator.Load(options);
        var profileErrors = ProfileValidator.Validate(profile);
        if (profileErrors.Count > 0) throw new RefusedException("Profil jest niepoprawny: " + string.Join(" ", profileErrors));
        var recipe = profile.Corpus ?? throw new RefusedException($"Profil „{profile.Id}” nie ma recepty korpusu (sekcja „corpus”).");

        var refusals = GameFolderGuard.CheckProfile(profile).ToList();
        if (refusals.Count > 0) throw Refuse(refusals);

        var gameDirectory = Path.GetFullPath(options.GameDirectory!);
        refusals.AddRange(GameFolderGuard.CheckFolder(gameDirectory));
        if (refusals.Count > 0) throw Refuse(refusals);

        var running = RunningGameGuard.FindRunning(profile, gameDirectory, processLister);
        if (running.Count > 0)
        {
            throw new RefusedException($"Gra jest uruchomiona (proces: {string.Join(", ", running)}). Zamknij ją i spróbuj ponownie.");
        }

        var outputPath = options.ResolveOutputPath(profile.Id);
        if (OutputLocationGuard.Check(outputPath, gameDirectory) is { } outputProblem) throw new RefusedException(outputProblem);
        if (options.StatsPath is { } statsPath && OutputLocationGuard.IsInside(statsPath, gameDirectory))
        {
            throw new RefusedException("Plik statystyk nie może leżeć w folderze gry.");
        }

        if (!recipe.Format.Equals(UnityTextAssetReader.FormatId, StringComparison.OrdinalIgnoreCase))
        {
            throw new RefusedException($"Nieobsługiwana rodzina formatów „{recipe.Format}” (obsługiwane: {UnityTextAssetReader.FormatId}).");
        }
        var containerPath = Path.GetFullPath(Path.Combine(gameDirectory, recipe.Container));
        if (!OutputLocationGuard.IsInside(containerPath, gameDirectory))
        {
            throw new RefusedException("Kontener z recepty leży poza folderem gry.");
        }
        if (!File.Exists(containerPath)) throw new RefusedException($"Nie ma kontenera „{recipe.Container}” w folderze gry.");

        var read = Stopwatch.StartNew();
        UnityReadResult unity;
        try
        {
            unity = UnityTextAssetReader.Read(containerPath, recipe.File);
        }
        catch (ProtectedContainerException ex)
        {
            throw new RefusedException(ex.Message);
        }
        read.Stop();

        var parse = Stopwatch.StartNew();
        var result = RecipeRunner.Run(recipe, unity.TextAssets);
        parse.Stop();

        WriteAtomically(outputPath, writer => CorpusJsonl.Write(writer, result.Entries));
        total.Stop();

        var report = BuildReport(profile.Id, outputPath, unity, result, read.ElapsedMilliseconds, parse.ElapsedMilliseconds, total.ElapsedMilliseconds);
        if (options.StatsPath is { } stats)
        {
            WriteAtomically(Path.GetFullPath(stats), writer => writer.Write(JsonSerializer.Serialize(report, StatsJson)));
        }
        return report;
    }

    private static readonly JsonSerializerOptions StatsJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ExtractionReport BuildReport(string profileId, string outputPath, UnityReadResult unity, RecipeResult result,
        long readMs, long parseMs, long totalMs)
    {
        var kinds = new Dictionary<string, KindStats>();
        foreach (var group in result.Entries.GroupBy(static e => e.Kind))
        {
            var unique = group.Select(static e => CorpusText.MatchKey(e.En)).Distinct(StringComparer.Ordinal).ToList();
            kinds[group.Key.ToString().ToLowerInvariant()] = new KindStats(
                group.Count(), unique.Count, unique.Sum(static k => k.Length),
                group.Where(static e => e.Speaker is not null).Select(static e => e.Speaker!).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        var allUnique = result.Entries.Select(static e => CorpusText.MatchKey(e.En)).Distinct(StringComparer.Ordinal).ToList();
        return new ExtractionReport
        {
            ProfileId = profileId,
            OutputPath = outputPath,
            ContainerKind = unity.ContainerKind,
            UnityVersion = unity.UnityVersion,
            SerializedVersion = unity.SerializedVersion,
            ObjectCount = unity.ObjectCount,
            TextAssets = unity.TextAssets.Count,
            Entries = result.Entries.Count,
            UniqueTexts = allUnique.Count,
            UniqueCharacters = allUnique.Sum(static k => k.Length),
            Speakers = result.Entries.Where(static e => e.Speaker is not null).Select(static e => e.Speaker!).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Kinds = kinds,
            Sources = result.Sources,
            ReadMs = readMs,
            ParseMs = parseMs,
            TotalMs = totalMs,
        };
    }

    private static RefusedException Refuse(IEnumerable<GuardViolation> violations) =>
        new("Odmowa (ADR-014): " + string.Join(" ", violations.Select(static v => v.Message)));

    private static void WriteAtomically(string path, Action<TextWriter> write)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                write(writer);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public static class ProfileLocator
{
    public static GameProfile Load(ExtractOptions options) =>
        Load(options.ProfileId, options.ProfileFile, options.ProfilesDirectory, options.ResolvedDataDirectory);

    public static GameProfile Load(string? profileId, string? profileFile, string? profilesDirectory, string dataDirectory)
    {
        if (profileFile is { } file)
        {
            return ProfileSerializer.FromJson(File.ReadAllText(file));
        }

        var roots = new List<string>();
        if (profilesDirectory is { } custom) roots.Add(custom);
        roots.Add(Path.Combine(dataDirectory, "profiles"));
        roots.Add(Path.Combine(AppContext.BaseDirectory, "profiles"));

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(root, "profile.json", SearchOption.AllDirectories))
            {
                GameProfile profile;
                try
                {
                    profile = ProfileSerializer.FromJson(File.ReadAllText(path));
                }
                catch (Exception ex) when (ex is FormatException or JsonException or IOException)
                {
                    continue;
                }
                if (profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase)) return profile;
            }
        }
        throw new RefusedException($"Nie znaleziono profilu „{profileId}” (szukano w: {string.Join(", ", roots)}).");
    }
}
