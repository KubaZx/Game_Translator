using System.Security.Cryptography;
using System.Text;

namespace GameTranslatorOverlay.Core.Text;

/// <summary>Stabilny identyfikator tekstu — ten sam znormalizowany tekst zawsze daje ten sam skrót.</summary>
public static class TextHasher
{
    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }
}
