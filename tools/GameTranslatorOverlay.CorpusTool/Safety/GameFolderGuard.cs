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

    public static IReadOnlyList<GuardViolation> CheckFolder(string gameDirectory, int maxEntries = 500_000, int maxViolations = 20)
    {
        var violations = new List<GuardViolation>();
        var root = Path.GetFullPath(gameDirectory);
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

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        var seen = 0;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", options))
        {
            if (++seen > maxEntries || violations.Count >= maxViolations) break;
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

        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            if (++seen > maxEntries || violations.Count >= maxViolations) break;
            var violation = CheckFile(root, file);
            if (violation is not null) violations.Add(violation);
        }

        return violations;
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
