namespace GameTranslatorOverlay.Infrastructure.Storage;

/// <summary>
/// Jedyne miejsce w kodzie znające nazwę folderu danych aplikacji —
/// przy zmianie nazwy produktu wystarczy zmienić <see cref="AppFolderName"/>.
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "GameTranslatorOverlay";

    public AppPaths(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);
    }

    public string RootDirectory { get; }
    public string LogsDirectory => Path.Combine(RootDirectory, "logs");
    public string SecretsDirectory => Path.Combine(RootDirectory, "secrets");
    public string DebugCapturesDirectory => Path.Combine(RootDirectory, "debug-captures");
    public string DatabasePath => Path.Combine(RootDirectory, "cache.db");
    public string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    /// <summary>Prywatny słownik użytkownika — osobny plik dla każdej pary językowej.</summary>
    public string GetUserGlossaryPath(string sourceLanguage, string targetLanguage) =>
        Path.Combine(RootDirectory, $"user-glossary.{sourceLanguage.ToLowerInvariant()}-{targetLanguage.ToLowerInvariant()}.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(SecretsDirectory);
    }
}
