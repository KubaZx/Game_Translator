using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.Core.Windows;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Content;

public sealed class UserProfileException(string message) : Exception(message);

public sealed class UserProfileStore
{
    public const string FileName = "profile.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IReadOnlyList<string> _protectedDirectories;

    public UserProfileStore(string rootDirectory, IReadOnlyList<string>? protectedDirectories = null)
    {
        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        _protectedDirectories = (protectedDirectories ?? [])
            .Select(static d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
            .ToList();
    }

    public static UserProfileStore CreateDefault(AppPaths paths) => new(paths.ProfilesDirectory, [ProfileCatalog.BundledDirectory]);

    public string RootDirectory { get; }

    public bool Owns(string? profileFile)
    {
        if (string.IsNullOrWhiteSpace(profileFile)) return false;
        if (_protectedDirectories.Any(p => SamePath(p, RootDirectory) || IsUnder(RootDirectory, p))) return false;
        var full = Path.GetFullPath(profileFile);
        return IsUnder(full, RootDirectory)
            && Path.GetFileName(full).Equals(FileName, StringComparison.OrdinalIgnoreCase)
            && !_protectedDirectories.Any(p => IsUnder(full, p));
    }

    public string FolderName(string profileFile) =>
        Path.GetRelativePath(RootDirectory, Path.GetDirectoryName(Path.GetFullPath(profileFile))!);

    public GameProfile Create(string name, string processName, string sourceLanguage, IEnumerable<GameProfile> loadedProfiles)
    {
        ArgumentNullException.ThrowIfNull(loadedProfiles);
        var known = loadedProfiles.ToList();

        var cleanName = ProfileNames.Clean(name);
        if (ProfileNames.Validate(cleanName, known.Select(static p => p.Name)) is { } nameError)
        {
            throw new UserProfileException(nameError);
        }

        var process = (processName ?? string.Empty).Trim();
        if (LiveTargetResolver.NormalizeProcessName(process).Length == 0)
        {
            throw new UserProfileException("Nie znam nazwy procesu tego okna — profil nie miałby się do czego dopasować.");
        }

        var language = (sourceLanguage ?? string.Empty).Trim();
        var folders = Directory.Exists(RootDirectory)
            ? Directory.EnumerateDirectories(RootDirectory).Select(static d => Path.GetFileName(d))
            : [];
        var id = ProfileIds.CreateUnique(cleanName, process, known.Select(static p => p.Id).Concat(folders));

        var profile = new GameProfile
        {
            Id = id,
            Name = cleanName,
            ProfileVersion = 1,
            SourceLanguage = language,
            ProcessNames = [process],
        };
        if (ProfileValidator.Validate(profile) is { Count: > 0 } errors)
        {
            throw new UserProfileException(string.Join(" ", errors));
        }

        var node = new JsonObject
        {
            ["id"] = profile.Id,
            ["name"] = profile.Name,
            ["profileVersion"] = profile.ProfileVersion,
            ["sourceLanguage"] = profile.SourceLanguage,
            ["processNames"] = new JsonArray(process),
        };

        var folder = Path.Combine(RootDirectory, id);
        Directory.CreateDirectory(folder);
        WriteAtomic(Path.Combine(folder, FileName), node.ToJsonString(WriteOptions));
        return profile;
    }

    public void Rename(string profileFile, string expectedId, string newName, IEnumerable<GameProfile> loadedProfiles)
    {
        ArgumentNullException.ThrowIfNull(loadedProfiles);
        var file = RequireOwned(profileFile);

        var cleanName = ProfileNames.Clean(newName);
        var others = loadedProfiles
            .Where(p => !p.Id.Equals(expectedId, StringComparison.OrdinalIgnoreCase))
            .Select(static p => p.Name);
        if (ProfileNames.Validate(cleanName, others) is { } nameError)
        {
            throw new UserProfileException(nameError);
        }

        var node = ReadOwnedNode(file, expectedId);
        node["name"] = cleanName;
        var json = node.ToJsonString(WriteOptions);

        var check = ProfileSerializer.FromJson(json);
        if (ProfileValidator.Validate(check) is { Count: > 0 } errors)
        {
            throw new UserProfileException(string.Join(" ", errors));
        }

        WriteAtomic(file, json);
    }

    public void Delete(string profileFile, string expectedId)
    {
        var file = RequireOwned(profileFile);
        var folder = Path.GetDirectoryName(file)!;
        if (!SamePath(Path.GetDirectoryName(folder), RootDirectory))
        {
            throw new UserProfileException("Ten profil leży w podfolderze — usuń go ręcznie w folderze danych.");
        }

        _ = ReadOwnedNode(file, expectedId);

        var otherProfiles = Directory.EnumerateFiles(folder, FileName, SearchOption.AllDirectories)
            .Count(other => !SamePath(other, file));
        if (otherProfiles > 0)
        {
            throw new UserProfileException("W folderze tego profilu są jeszcze inne profile — usuń go ręcznie w folderze danych.");
        }

        Directory.Delete(folder, recursive: true);
    }

    private string RequireOwned(string profileFile)
    {
        if (!Owns(profileFile))
        {
            throw new UserProfileException("To nie jest własny profil z folderu danych — profili z zestawu aplikacji nie zmieniam.");
        }
        var full = Path.GetFullPath(profileFile);
        if (!File.Exists(full))
        {
            throw new UserProfileException("Plik profilu zniknął z dysku — lista profili zostanie odświeżona.");
        }
        return full;
    }

    private static JsonObject ReadOwnedNode(string file, string expectedId)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(file), NodeOptions, DocumentOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new UserProfileException("Plik profilu jest uszkodzony — popraw go albo usuń ręcznie w folderze danych.");
        }

        if (node is not JsonObject root
            || root["id"] is not JsonValue idValue
            || !idValue.TryGetValue<string>(out var id)
            || !id.Equals(expectedId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserProfileException("Plik profilu zmienił się na dysku — lista profili zostanie odświeżona.");
        }
        return root;
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string? left, string? right) =>
        left is not null && right is not null
        && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
