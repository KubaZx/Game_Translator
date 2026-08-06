using GameTranslatorOverlay.Core.Glossary;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Content;

public sealed record CatalogIssue(string FilePath, string Message);

/// <summary>
/// Wczytuje profile gier z folderów „profiles” (obok aplikacji oraz w danych użytkownika).
/// Błędne pliki nie wysypują aplikacji — trafiają na listę problemów do diagnostyki.
/// </summary>
public sealed class ProfileCatalog(IReadOnlyList<string> rootDirectories)
{
    public static ProfileCatalog CreateDefault(AppPaths paths) => new([
        Path.Combine(AppContext.BaseDirectory, "profiles"),
        Path.Combine(paths.RootDirectory, "profiles"),
    ]);

    public (IReadOnlyList<GameProfile> Profiles, IReadOnlyList<CatalogIssue> Issues) LoadAll()
    {
        var profiles = new List<GameProfile>();
        var issues = new List<CatalogIssue>();

        foreach (var root in rootDirectories)
        {
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "profile.json", SearchOption.AllDirectories))
            {
                try
                {
                    var profile = ProfileSerializer.FromJson(File.ReadAllText(file));
                    var errors = ProfileValidator.Validate(profile);
                    if (errors.Count > 0)
                    {
                        issues.AddRange(errors.Select(e => new CatalogIssue(file, e)));
                        continue;
                    }
                    if (profiles.Any(p => p.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        issues.Add(new CatalogIssue(file, $"Profil o identyfikatorze „{profile.Id}” już istnieje — pomijam duplikat."));
                        continue;
                    }
                    profiles.Add(profile);
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

        foreach (var root in rootDirectories)
        {
            var path = Path.Combine(root, glossaryId, fileName);
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
}

/// <summary>
/// Prywatny słownik użytkownika — terminy dodawane z poziomu aplikacji.
/// Trzymany w danych użytkownika, poza repozytorium.
/// </summary>
public sealed class UserGlossaryStore(AppPaths paths)
{
    private readonly Lock _gate = new();

    public GlossaryDocument Load(string sourceLanguage, string targetLanguage)
    {
        lock (_gate)
        {
            var path = paths.GetUserGlossaryPath(sourceLanguage, targetLanguage);
            if (!File.Exists(path))
            {
                return CreateEmpty(sourceLanguage, targetLanguage);
            }

            try
            {
                var document = GlossarySerializer.FromJson(File.ReadAllText(path));

                // Terminy z innej pary językowej podmieniałyby tłumaczenia po cichu —
                // niedopasowany dokument traktujemy jak brak słownika.
                if (!document.SourceLanguage.Equals(sourceLanguage, StringComparison.OrdinalIgnoreCase)
                    || !document.TargetLanguage.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    return CreateEmpty(sourceLanguage, targetLanguage);
                }

                return document;
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or IOException)
            {
                return CreateEmpty(sourceLanguage, targetLanguage);
            }
        }
    }

    public void AddTerm(GlossaryTerm term, string sourceLanguage, string targetLanguage)
    {
        lock (_gate)
        {
            var document = Load(sourceLanguage, targetLanguage);
            document.SourceLanguage = sourceLanguage;
            document.TargetLanguage = targetLanguage;
            document.Terms.RemoveAll(t =>
                t.Source.Equals(term.Source, StringComparison.OrdinalIgnoreCase) && t.CaseSensitive == term.CaseSensitive);
            document.Terms.Add(term);
            paths.EnsureCreated();
            File.WriteAllText(paths.GetUserGlossaryPath(sourceLanguage, targetLanguage), GlossarySerializer.ToJson(document));
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
            File.WriteAllText(paths.GetUserGlossaryPath(sourceLanguage, targetLanguage), GlossarySerializer.ToJson(document));
        }
    }

    private static GlossaryDocument CreateEmpty(string sourceLanguage, string targetLanguage) => new()
    {
        Name = "user",
        SourceLanguage = sourceLanguage,
        TargetLanguage = targetLanguage,
        Description = "Prywatny słownik użytkownika — terminy dodane w aplikacji.",
    };
}
