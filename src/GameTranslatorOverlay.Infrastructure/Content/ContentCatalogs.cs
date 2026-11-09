using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Content;

public sealed record CatalogIssue(string FilePath, string Message);

public sealed record CatalogProfile(GameProfile Profile, string FilePath);

/// <summary>
/// Wczytuje profile gier z folderów „profiles” (obok aplikacji oraz w danych użytkownika).
/// Błędne pliki nie wysypują aplikacji — trafiają na listę problemów do diagnostyki.
/// </summary>
public sealed class ProfileCatalog(IReadOnlyList<string> rootDirectories, Version? appVersion = null)
{
    /// <summary>Wersja aplikacji do sprawdzania „minAppVersion” profili (domyślnie wersja tego wydania).</summary>
    private readonly Version? _appVersion = appVersion ?? typeof(ProfileCatalog).Assembly.GetName().Version;

    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "profiles");

    public static ProfileCatalog CreateDefault(AppPaths paths) => new([
        BundledDirectory,
        paths.ProfilesDirectory,
    ]);

    public (IReadOnlyList<GameProfile> Profiles, IReadOnlyList<CatalogIssue> Issues) LoadAll()
    {
        var (entries, issues) = LoadEntries();
        return (entries.Select(static e => e.Profile).ToList(), issues);
    }

    public (IReadOnlyList<CatalogProfile> Entries, IReadOnlyList<CatalogIssue> Issues) LoadEntries()
    {
        var profiles = new List<CatalogProfile>();
        var issues = new List<CatalogIssue>();

        foreach (var root in rootDirectories)
        {
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "profile.json", SearchOption.AllDirectories))
            {
                try
                {
                    var profile = ProfileSerializer.FromJson(File.ReadAllText(file));
                    var errors = ProfileValidator.Validate(profile, _appVersion);
                    if (errors.Count > 0)
                    {
                        issues.AddRange(errors.Select(e => new CatalogIssue(file, e)));
                        continue;
                    }
                    if (profiles.Any(p => p.Profile.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        issues.Add(new CatalogIssue(file, $"Profil o identyfikatorze „{profile.Id}” już istnieje — pomijam duplikat."));
                        continue;
                    }
                    profiles.Add(new CatalogProfile(profile, Path.GetFullPath(file)));
                }
                catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or IOException)
                {
                    issues.Add(new CatalogIssue(file, ex.Message));
                }
            }
        }

        return (profiles, issues);
    }
}

/// <summary>
/// Wczytuje słowniki z folderów „glossaries” — plik o nazwie „{źródłowy}-{docelowy}.json”
/// w podfolderze o identyfikatorze słownika (np. glossaries/path-of-exile-2/en-pl.json).
/// </summary>
public sealed class GlossaryCatalog(IReadOnlyList<string> rootDirectories)
{
    public static GlossaryCatalog CreateDefault(AppPaths paths) => new([
        Path.Combine(AppContext.BaseDirectory, "glossaries"),
        Path.Combine(paths.RootDirectory, "glossaries"),
    ]);

    public (GlossaryDocument? Document, CatalogIssue? Issue) TryLoad(string glossaryId, string sourceLanguage, string targetLanguage)
    {
        var fileName = $"{sourceLanguage}-{targetLanguage}.json";

        if (!ProfileTuningRules.IsContentId(glossaryId))
        {
            return (null, new CatalogIssue(fileName, "Nazwa słownika musi być nazwą folderu (litery, cyfry, myślniki), a nie ścieżką ani adresem."));
        }

        foreach (var root in rootDirectories)
        {
            var path = Path.Combine(root, glossaryId, fileName);
            if (!IsUnder(root, path)) continue;
            if (!File.Exists(path)) continue;

            try
            {
                var document = GlossarySerializer.FromJson(File.ReadAllText(path));
                var errors = GlossaryValidator.Validate(document);
                if (errors.Count > 0)
                {
                    return (null, new CatalogIssue(path, string.Join(" ", errors)));
                }
                return (document, null);
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or IOException)
            {
                return (null, new CatalogIssue(path, ex.Message));
            }
        }

        return (null, new CatalogIssue(fileName, $"Nie znaleziono słownika „{glossaryId}” ({fileName})."));
    }

    private static bool IsUnder(string root, string path)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> ListIds(string sourceLanguage, string targetLanguage)
    {
        var fileName = $"{sourceLanguage}-{targetLanguage}.json";
        var ids = new List<string>();
        foreach (var root in rootDirectories)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                ids.AddRange(Directory.EnumerateDirectories(root)
                    .Where(directory => File.Exists(Path.Combine(directory, fileName)))
                    .Select(static directory => Path.GetFileName(directory)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }
        return ids
            .Where(static id => ProfileTuningRules.IsContentId(id) && !id.Equals("global", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public enum UserGlossaryStatus
{
    Ok,
    Missing,
    Unreadable,
    Locked,
    OtherLanguagePair,
}

public sealed record UserGlossaryLoad(GlossaryDocument Document, UserGlossaryStatus Status, string Path)
{
    public bool CanWrite => Status is UserGlossaryStatus.Ok or UserGlossaryStatus.Missing;

    private string? Reason => Status switch
    {
        UserGlossaryStatus.Unreadable => "nie jest poprawnym plikiem słownika (uszkodzony JSON)",
        UserGlossaryStatus.Locked => "jest zajęty przez inny program",
        UserGlossaryStatus.OtherLanguagePair => "jest słownikiem innej pary języków",
        _ => null,
    };

    public string? SkippedNotice => Reason is { } reason
        ? $"⚠ Słownik użytkownika został pominięty: plik {System.IO.Path.GetFileName(Path)} {reason}. Popraw go albo przenieś w inne miejsce."
        : null;

    public string? EditorBlockMessage => Reason is { } reason
        ? $"Nie otwieram słownika do edycji, bo plik {reason}. Zapis jest zablokowany, żeby go nie nadpisać. Plik leży tutaj: {Path}"
        : null;
}

/// <summary>
/// Prywatny słownik użytkownika — terminy dodawane z poziomu aplikacji.
/// Trzymany w danych użytkownika, poza repozytorium.
/// </summary>
public sealed class UserGlossaryStore(AppPaths paths)
{
    private readonly Lock _gate = new();

    public GlossaryDocument Load(string sourceLanguage, string targetLanguage) =>
        LoadChecked(sourceLanguage, targetLanguage).Document;

    public string GetBackupPath(string sourceLanguage, string targetLanguage) =>
        paths.GetUserGlossaryPath(sourceLanguage, targetLanguage) + ".bak";

    public string GetFilePath(string sourceLanguage, string targetLanguage) =>
        paths.GetUserGlossaryPath(sourceLanguage, targetLanguage);

    public UserGlossaryLoad LoadChecked(string sourceLanguage, string targetLanguage)
    {
        lock (_gate)
        {
            var path = paths.GetUserGlossaryPath(sourceLanguage, targetLanguage);
            if (!File.Exists(path))
            {
                return new UserGlossaryLoad(CreateEmpty(sourceLanguage, targetLanguage), UserGlossaryStatus.Missing, path);
            }

            try
            {
                var document = GlossarySerializer.FromJson(File.ReadAllText(path));

                // Terminy z innej pary językowej podmieniałyby tłumaczenia po cichu —
                // niedopasowany dokument traktujemy jak brak słownika.
                if (!document.SourceLanguage.Equals(sourceLanguage, StringComparison.OrdinalIgnoreCase)
                    || !document.TargetLanguage.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    return new UserGlossaryLoad(CreateEmpty(sourceLanguage, targetLanguage), UserGlossaryStatus.OtherLanguagePair, path);
                }

                return new UserGlossaryLoad(document, UserGlossaryStatus.Ok, path);
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
            {
                return new UserGlossaryLoad(CreateEmpty(sourceLanguage, targetLanguage), UserGlossaryStatus.Unreadable, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new UserGlossaryLoad(CreateEmpty(sourceLanguage, targetLanguage), UserGlossaryStatus.Locked, path);
            }
        }
    }

    public void AddTerm(GlossaryTerm term, string sourceLanguage, string targetLanguage)
    {
        lock (_gate)
        {
            var document = LoadForWrite(sourceLanguage, targetLanguage);
            document.SourceLanguage = sourceLanguage;
            document.TargetLanguage = targetLanguage;
            document.Terms.RemoveAll(t =>
                t.Source.Equals(term.Source, StringComparison.OrdinalIgnoreCase) && t.CaseSensitive == term.CaseSensitive);
            document.Terms.Add(term);
            paths.EnsureCreated();
            WriteAtomic(paths.GetUserGlossaryPath(sourceLanguage, targetLanguage), GlossarySerializer.ToJson(document));
        }
    }

    /// <summary>Zastępuje cały słownik pary językowej (używane przez edytor słownika).</summary>
    public void ReplaceAll(GlossaryDocument document, string sourceLanguage, string targetLanguage)
    {
        lock (_gate)
        {
            document.SourceLanguage = sourceLanguage;
            document.TargetLanguage = targetLanguage;
            if (string.IsNullOrWhiteSpace(document.Name))
            {
                document.Name = "user";
            }
            paths.EnsureCreated();
            var path = paths.GetUserGlossaryPath(sourceLanguage, targetLanguage);
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            WriteAtomic(path, GlossarySerializer.ToJson(document));
        }
    }

    /// <summary>
    /// Wariant odczytu dla ścieżki ZAPISU: przejściowy błąd IO (plik zablokowany przez
    /// antywirus/backup/drugą instancję) musi przerwać operację — Load() zwróciłby pusty
    /// dokument i najbliższy zapis skasowałby cały słownik użytkownika. Plik uszkodzony
    /// (nie-JSON) odkładamy jako kopię .corrupt.bak zamiast po cichu nadpisywać.
    /// </summary>
    private GlossaryDocument LoadForWrite(string sourceLanguage, string targetLanguage)
    {
        var path = paths.GetUserGlossaryPath(sourceLanguage, targetLanguage);
        if (!File.Exists(path))
        {
            return CreateEmpty(sourceLanguage, targetLanguage);
        }

        try
        {
            var document = GlossarySerializer.FromJson(File.ReadAllText(path));
            if (!document.SourceLanguage.Equals(sourceLanguage, StringComparison.OrdinalIgnoreCase)
                || !document.TargetLanguage.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return CreateEmpty(sourceLanguage, targetLanguage);
            }
            return document;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            try
            {
                File.Copy(path, path + ".corrupt.bak", overwrite: true);
            }
            catch (IOException)
            {
                // Kopia jest tylko ułatwieniem diagnostyki.
            }
            return CreateEmpty(sourceLanguage, targetLanguage);
        }
    }

    /// <summary>Zapis przez plik tymczasowy + atomową podmianę — crash nie zostawia uciętego słownika.</summary>
    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static GlossaryDocument CreateEmpty(string sourceLanguage, string targetLanguage) => new()
    {
        Name = "user",
        SourceLanguage = sourceLanguage,
        TargetLanguage = targetLanguage,
        Description = "Prywatny słownik użytkownika — terminy dodane w aplikacji.",
    };
}
