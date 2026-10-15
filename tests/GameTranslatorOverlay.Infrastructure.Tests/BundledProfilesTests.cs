using GameTranslatorOverlay.Core.Profiles;
using GameTranslatorOverlay.Core.Windows;
using GameTranslatorOverlay.Infrastructure.Content;

namespace GameTranslatorOverlay.Infrastructure.Tests;

public sealed class BundledProfilesTests
{
    private static readonly string RepoProfiles =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "profiles"));

    private static IReadOnlyList<GameProfile> LoadBundled()
    {
        Assert.True(Directory.Exists(RepoProfiles), RepoProfiles);
        var (profiles, issues) = new ProfileCatalog([RepoProfiles]).LoadAll();
        Assert.Empty(issues);
        return profiles;
    }

    [Fact]
    public void Kazdy_dolaczony_profil_wczytuje_sie_bez_ostrzezen()
    {
        var files = Directory.GetFiles(RepoProfiles, "profile.json", SearchOption.AllDirectories);

        var profiles = LoadBundled();

        Assert.Equal(files.Length, profiles.Count);
        Assert.True(profiles.Count >= 3, $"Wczytano {profiles.Count} profili.");
    }

    [Fact]
    public void W_zestawie_sa_tylko_profile_ogolny_Escape_Academy_i_PoE2()
    {
        var ids = LoadBundled().Select(static p => p.Id).OrderBy(static id => id, StringComparer.Ordinal);

        Assert.Equal(["escape-academy", "generic", "path-of-exile-2"], ids);
    }

    [Fact]
    public void Folder_profilu_nazywa_sie_jak_jego_identyfikator()
    {
        foreach (var file in Directory.GetFiles(RepoProfiles, "profile.json", SearchOption.AllDirectories))
        {
            var profile = ProfileSerializer.FromJson(File.ReadAllText(file));
            Assert.Equal(Path.GetFileName(Path.GetDirectoryName(file)), profile.Id);
        }
    }

    [Fact]
    public void Identyfikatory_i_nazwy_profili_sa_unikalne()
    {
        var profiles = LoadBundled();

        Assert.Equal(profiles.Count, profiles.Select(static p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(profiles.Count, profiles.Select(static p => p.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Nazwy_procesow_nie_powtarzaja_sie_miedzy_profilami_ani_w_profilu()
    {
        var profiles = LoadBundled();

        var claims = profiles
            .SelectMany(static p => p.ProcessNames.Select(name => (Process: LiveTargetResolver.NormalizeProcessName(name), p.Id)))
            .ToList();
        var duplicates = claims
            .GroupBy(static c => c.Process, StringComparer.OrdinalIgnoreCase)
            .Where(static g => g.Count() > 1)
            .Select(static g => $"{g.Key}: {string.Join(", ", g.Select(static c => c.Id))}")
            .ToList();

        Assert.DoesNotContain(claims, static c => c.Process.Length == 0);
        Assert.Empty(duplicates);
    }

    [Theory]
    [InlineData("Escape Academy.exe", "escape-academy")]
    [InlineData("escape academy", "escape-academy")]
    [InlineData("PathOfExileSteam.exe", "path-of-exile-2")]
    public void Autodetekcja_wybiera_profil_po_nazwie_procesu(string processName, string expectedId)
    {
        var match = ProfileProcessMatcher.FindByProcess(LoadBundled(), processName);

        Assert.NotNull(match);
        Assert.Equal(expectedId, match.Id);
    }

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("firefox.exe")]
    [InlineData("GraCrashHandler64.exe")]
    [InlineData("CrashReporter.exe")]
    [InlineData("LauncherHelper.exe")]
    [InlineData("InnaGra-Win64-Shipping.exe")]
    [InlineData("")]
    public void Autodetekcja_nie_wybiera_profilu_dla_innych_procesow(string processName)
    {
        Assert.Null(ProfileProcessMatcher.FindByProcess(LoadBundled(), processName));
    }
}
