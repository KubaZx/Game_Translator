using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using GameTranslatorOverlay.Infrastructure.Storage;

namespace GameTranslatorOverlay.Infrastructure.Secrets;

public interface ISecretsStore
{
    void Save(string name, string value);
    string? Load(string name);
    void Delete(string name);
}

/// <summary>
/// Przechowuje sekrety (np. klucz API DeepL) zaszyfrowane przez Windows DPAPI
/// w zakresie bieżącego użytkownika. Wartości nigdy nie trafiają do logów.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretsStore(AppPaths paths) : ISecretsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GameTranslatorOverlay.v1");

    public void Save(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        paths.EnsureCreated();
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GetPath(name), encrypted);
    }

    public string? Load(string name)
    {
        var path = GetPath(name);
        if (!File.Exists(path)) return null;

        try
        {
            var decrypted = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException)
        {
            // Plik zaszyfrowany na innym koncie użytkownika albo uszkodzony — traktujemy jak brak sekretu.
            return null;
        }
    }

    public void Delete(string name)
    {
        var path = GetPath(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetPath(string name)
    {
        var safe = new string(name.ToLowerInvariant().Select(static ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        return Path.Combine(paths.SecretsDirectory, safe + ".bin");
    }
}
