namespace GameTranslatorOverlay.CorpusTool.Safety;

public static class OutputLocationGuard
{
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string? Check(string outputPath, string gameDirectory, string? gameRoot = null, IReadOnlyCollection<string>? processNames = null) =>
        CheckOutsideGame(outputPath, "Plik wynikowy", processNames, gameDirectory, gameRoot) ?? CheckLocalData(outputPath);

    public static string? CheckOutsideGame(string path, string what, IReadOnlyCollection<string>? processNames, params string?[] gameDirectories)
    {
        var full = Path.GetFullPath(path);
        foreach (var directory in gameDirectories)
        {
            if (directory is not null && IsInside(full, directory))
            {
                return $"{what} nie może leżeć w folderze gry — narzędzie niczego tam nie zapisuje.";
            }
        }
        if (GameFolderGuard.LibraryDirectory(full) is { } library)
        {
            return $"{what} nie może leżeć w bibliotece gier („{library}”) — narzędzie niczego tam nie zapisuje.";
        }
        if (GameFolderGuard.FindDirectoryWithExecutable(Path.GetDirectoryName(full), processNames) is { } game)
        {
            return $"{what} nie może leżeć w folderze gry („{game}” zawiera plik gry z profilu) — narzędzie niczego tam nie zapisuje.";
        }
        return null;
    }

    public static string? CheckLocalData(string outputPath)
    {
        var output = Path.GetFullPath(outputPath);
        var repository = FindRepositoryRoot(Path.GetDirectoryName(output));
        if (repository is not null && !IsInside(output, Path.Combine(repository, "eval", "private")))
        {
            return $"Plik wynikowy leży w repozytorium („{repository}”). Teksty gier są chronione prawem autorskim — " +
                   "zapisuj je w danych lokalnych (domyślnie %LOCALAPPDATA%\\GameTranslatorOverlay) albo w eval/private/.";
        }
        return null;
    }

    public static bool IsInside(string path, string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (full.Equals(root, PathComparison)) return true;
        return full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    public static string? FindRepositoryRoot(string? directory)
    {
        for (var current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (IsGitRoot(current) || File.Exists(Path.Combine(current, "GameTranslatorOverlay.slnx")))
            {
                return current;
            }
        }
        return null;
    }

    private static bool IsGitRoot(string directory)
    {
        var git = Path.Combine(directory, ".git");
        if (Directory.Exists(git)) return File.Exists(Path.Combine(git, "HEAD"));
        if (!File.Exists(git)) return false;
        try
        {
            using var reader = new StreamReader(git);
            return reader.ReadLine()?.StartsWith("gitdir:", StringComparison.Ordinal) == true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
