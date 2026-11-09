using System.Xml.Linq;

namespace GameTranslatorOverlay.Core.Tests;

public sealed class ThirdPartyNoticesTests
{
    private static readonly string[] ShippedProjects =
    [
        Path.Combine("src", "GameTranslatorOverlay.App", "GameTranslatorOverlay.App.csproj"),
        Path.Combine("src", "GameTranslatorOverlay.Core", "GameTranslatorOverlay.Core.csproj"),
        Path.Combine("src", "GameTranslatorOverlay.Infrastructure", "GameTranslatorOverlay.Infrastructure.csproj"),
    ];

    [Fact]
    public void EveryDirectPackageReferenceIsListedInThirdPartyNotices()
    {
        var root = RepositoryRoot();
        var notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md"));
        var missing = DirectPackages(root).Where(name => !notices.Contains(name, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "Brak w THIRD-PARTY-NOTICES.md: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryDirectPackageReferenceHasEntryInShippedLicenseList()
    {
        var root = RepositoryRoot();
        var list = File.ReadAllText(Path.Combine(root, "src", "GameTranslatorOverlay.App", "ThirdPartyLicenses", "Packages.txt"));
        var missing = DirectPackages(root).Where(name => !list.Contains(name + " ", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "Brak w ThirdPartyLicenses/Packages.txt: " + string.Join(", ", missing));
    }

    [Fact]
    public void LicenseTextsRequiredByThePackageAreShippedFromTheAppProject()
    {
        var root = RepositoryRoot();
        var directory = Path.Combine(root, "src", "GameTranslatorOverlay.App", "ThirdPartyLicenses");
        foreach (var file in new[] { "MIT.txt", "Apache-2.0.txt", "Packages.txt", "DotNet-Runtime-LICENSE.txt", "DotNet-Runtime-THIRD-PARTY-NOTICES.txt" })
        {
            Assert.True(new FileInfo(Path.Combine(directory, file)).Length > 0, file);
        }
        Assert.Contains("MIT License", File.ReadAllText(Path.Combine(root, "LICENSE")), StringComparison.Ordinal);
    }

    private static List<string> DirectPackages(string root) =>
        ShippedProjects
            .SelectMany(project => XDocument.Load(Path.Combine(root, project)).Descendants("PackageReference"))
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GameTranslatorOverlay.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Nie znaleziono katalogu repozytorium (GameTranslatorOverlay.slnx).");
    }
}
