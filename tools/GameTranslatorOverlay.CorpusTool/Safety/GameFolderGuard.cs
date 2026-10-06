using GameTranslatorOverlay.Core.Profiles;

namespace GameTranslatorOverlay.CorpusTool.Safety;

public sealed record GuardViolation(string Code, string Message);

public static class GameFolderGuard
{
    public const string AntiCheat = "anti-cheat";
    public const string SignedContainer = "signed-container";
    public const string EncryptedContainer = "encrypted-container";
    public const string ExcludedGame = "excluded-game";
    public const string OnlineGame = "online-game";

    public static readonly IReadOnlyList<string> ExcludedProfileIds = ["path-of-exile", "path-of-exile-2"];

    private static readonly string[] AntiCheatDirectoryMarkers = ["easyanticheat", "battleye"];
    private static readonly string[] AntiCheatFilePrefixes = ["easyanticheat", "beservice", "beclient", "battleye", "start_protected_game", "eac_launcher"];
    private static readonly string[] ExcludedPathSegmentPrefixes = ["path of exile", "grinding gear games"];
    private static readonly string[] ExcludedFilePrefixes = ["pathofexile"];
    private static readonly string[] ExcludedFileNames = ["content.ggpk", "_.index.bin"];
    private static readonly string[] ExcludedDirectoryNames = ["bundles2"];

    private static readonly string[][] LibraryMarkers = [["steamapps", "common"], ["Epic Games"], ["GOG Galaxy", "Games"]];

    private static readonly byte[] PakMagic = [0xE1, 0x12, 0x6F, 0x5A];
    private static readonly byte[] UtocMagic = "-==--==--==--==-"u8.ToArray();

    public static IReadOnlyList<GuardViolation> CheckProfile(GameProfile profile)
    {
        var violations = new List<GuardViolation>();
        if (ExcludedProfileIds.Contains(profile.Id, StringComparer.OrdinalIgnoreCase))
        {
            violations.Add(new GuardViolation(ExcludedGame,
                $"Profil „{profile.Id}” jest na liście wykluczeń: regulamin tej gry zabrania programów, które czytają jej pliki."));
        }
        if (profile.Online == true)
        {
            violations.Add(new GuardViolation(OnlineGame,
                $"Profil „{profile.Id}” jest oznaczony jako gra online — narzędzie nie czyta plików gier online."));
        }
        return violations;
    }

    public static IReadOnlyList<GuardViolation> CheckFolder(string gameDirectory, int maxEntries = 500_000, int maxViolations = 20,
        IReadOnlyCollection<string>? processNames = null)
    {
        var violations = new List<GuardViolation>();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        if (!Directory.Exists(root))
        {
            violations.Add(new GuardViolation("missing", $"Folder gry „{root}” nie istnieje."));
            return violations;
        }

        foreach (var segment in root.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (ExcludedPathSegmentPrefixes.Any(prefix => segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                violations.Add(new GuardViolation(ExcludedGame, $"Ścieżka „{root}” wskazuje grę z listy wykluczeń („{segment}”)."));
                break;
            }
        }

        var seen = 0;
        ScanTree(root, root, null, violations, ref seen, maxEntries, maxViolations);

        var gameRoot = ResolveGameRoot(root, processNames);
        if (!string.Equals(gameRoot, root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(gameRoot))
        {
            ScanTree(gameRoot, gameRoot, root, violations, ref seen, maxEntries, maxViolations);
        }

        return violations;
    }

    private static void ScanTree(string tree, string root, string? skip, List<GuardViolation> violations, ref int seen,
        int maxEntries, int maxViolations)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        foreach (var directory in Directory.EnumerateDirectories(tree, "*", options))
        {
            if (++seen > maxEntries || violations.Count >= maxViolations) return;
            if (skip is not null && OutputLocationGuard.IsInside(directory, skip)) continue;
            var name = Path.GetFileName(directory);
            if (AntiCheatDirectoryMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                violations.Add(new GuardViolation(AntiCheat, $"Gra ma zabezpieczenie anti-cheat (folder „{Relative(root, directory)}”)."));
            }
            else if (ExcludedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                violations.Add(new GuardViolation(ExcludedGame, $"Folder „{Relative(root, directory)}” należy do gry z listy wykluczeń."));
            }
        }

        foreach (var file in Directory.EnumerateFiles(tree, "*", options))
        {
            if (++seen > maxEntries || violations.Count >= maxViolations) return;
            if (skip is not null && OutputLocationGuard.IsInside(file, skip)) continue;
            var violation = CheckFile(root, file);
            if (violation is not null) violations.Add(violation);
        }
    }

    public static string ResolveGameRoot(string gameDirectory, IReadOnlyCollection<string>? processNames = null)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        return LibraryGameRoot(full) ?? FindDirectoryWithExecutable(full, processNames) ?? full;
    }

    public static string? LibraryDirectory(string path)
    {
        for (var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (IsLibraryDirectory(current)) return current;
        }
        return null;
    }

    public static string? LibraryGameRoot(string path)
    {
        for (var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (Path.GetDirectoryName(current) is { } parent && IsLibraryDirectory(parent)) return current;
        }
        return null;
    }

    public static string? FindDirectoryWithExecutable(string? directory, IReadOnlyCollection<string>? processNames)
    {
        if (directory is null || processNames is null || processNames.Count == 0) return null;
        var executables = processNames
            .Select(static name => name.Trim())
            .Where(static name => name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            .Select(static name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe")
            .ToList();
        if (executables.Count == 0) return null;

        string? found = null;
        for (var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            var candidate = current;
            if (executables.Any(exe => File.Exists(Path.Combine(candidate, exe)))) found = candidate;
        }
        return found;
    }

    private static bool IsLibraryDirectory(string directory)
    {
        foreach (var marker in LibraryMarkers)
        {
            string? current = directory;
            var matches = true;
            for (var k = marker.Length - 1; k >= 0 && matches; k--)
            {
                matches = !string.IsNullOrEmpty(current) && Path.GetFileName(current).Equals(marker[k], StringComparison.OrdinalIgnoreCase);
                current = matches ? Path.GetDirectoryName(current) : null;
            }
            if (matches) return true;
        }
        return false;
    }

    private static GuardViolation? CheckFile(string root, string file)
    {
        var name = Path.GetFileName(file);
        var extension = Path.GetExtension(file);
        var relative = Relative(root, file);

        if (AntiCheatFilePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return new GuardViolation(AntiCheat, $"Gra ma zabezpieczenie anti-cheat (plik „{relative}”).");
        }
        if (ExcludedFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || ExcludedFilePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return new GuardViolation(ExcludedGame, $"Plik „{relative}” należy do gry z listy wykluczeń.");
        }
        if (extension.Equals(".sig", StringComparison.OrdinalIgnoreCase))
        {
            return new GuardViolation(SignedContainer, $"Gra ma podpisane kontenery (plik „{relative}”).");
        }
        try
        {
            if (extension.Equals(".pak", StringComparison.OrdinalIgnoreCase) && IsPakIndexEncrypted(file))
            {
                return new GuardViolation(EncryptedContainer, $"Kontener „{relative}” ma zaszyfrowany indeks.");
            }
            if (extension.Equals(".utoc", StringComparison.OrdinalIgnoreCase) && UtocProtection(file) is { } protection)
            {
                return new GuardViolation(protection, $"Kontener „{relative}” jest {(protection == EncryptedContainer ? "zaszyfrowany" : "podpisany")}.");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return null;
    }

    public static bool IsPakIndexEncrypted(string path) => IsPakIndexEncrypted(ReadOnlyGameFile.ReadTail(path, 4096));

    public static bool IsPakIndexEncrypted(ReadOnlySpan<byte> tail)
    {
        var position = tail.LastIndexOf(PakMagic);
        return position > 0 && tail[position - 1] != 0;
    }

    public static string? UtocProtection(string path) => UtocProtection(ReadOnlyGameFile.ReadHead(path, 128));

    public static string? UtocProtection(ReadOnlySpan<byte> head)
    {
        if (head.Length <= 80 || !head.StartsWith(UtocMagic)) return null;
        var flags = head[80];
        if ((flags & 0x02) != 0) return EncryptedContainer;
        if ((flags & 0x04) != 0) return SignedContainer;
        return null;
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path);
}
