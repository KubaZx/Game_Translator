using System.Text.Json.Nodes;

namespace GameTranslatorOverlay.Core.Profiles;

public sealed record ProfileTuning
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
    public IReadOnlyList<string> WindowTitles { get; init; } = [];
    public string SourceLanguage { get; init; } = "en";
    public string? Glossary { get; init; }
    public double? OcrUpscale { get; init; }
    public int? OcrMinTextHeight { get; init; }
    public double? Fps { get; init; }
    public double? ChangeThreshold { get; init; }
    public string? FontFamily { get; init; }
    public string? RecommendedMode { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<RelativeRegion> IgnoreRegions { get; init; } = [];

    public bool Equals(ProfileTuning? other) =>
        other is not null
        && Name == other.Name
        && ProcessNames.SequenceEqual(other.ProcessNames, StringComparer.Ordinal)
        && WindowTitles.SequenceEqual(other.WindowTitles, StringComparer.Ordinal)
        && SourceLanguage == other.SourceLanguage
        && Glossary == other.Glossary
        && OcrUpscale == other.OcrUpscale
        && OcrMinTextHeight == other.OcrMinTextHeight
        && Fps == other.Fps
        && ChangeThreshold == other.ChangeThreshold
        && FontFamily == other.FontFamily
        && RecommendedMode == other.RecommendedMode
        && Description == other.Description
        && ProfileTuningJson.SameRegions(IgnoreRegions, other.IgnoreRegions);

    public override int GetHashCode() => HashCode.Combine(Name, SourceLanguage, ProcessNames.Count, WindowTitles.Count, Fps, OcrUpscale);
}

public static class ProfileTuningJson
{
    public static ProfileTuning Read(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var profile = ProfileSerializer.FromJson(root.ToJsonString());
        var ocr = Section(root, "ocr");
        return new ProfileTuning
        {
            Name = profile.Name,
            ProcessNames = [.. profile.ProcessNames],
            WindowTitles = [.. profile.WindowTitles],
            SourceLanguage = profile.SourceLanguage,
            Glossary = Blank(profile.Glossary),
            OcrUpscale = profile.Ocr?.Upscale,
            OcrMinTextHeight = ocr is not null && KeyOf(ocr, "minTextHeight") is not null ? profile.Ocr?.MinTextHeight : null,
            Fps = profile.ChangeDetection?.Fps,
            ChangeThreshold = profile.ChangeDetection?.Threshold,
            FontFamily = Blank(profile.Overlay?.FontFamily),
            RecommendedMode = Blank(profile.RecommendedMode),
            Description = Blank(profile.Description),
            IgnoreRegions = [.. (profile.Live?.IgnoreRegions ?? []).Where(static r => r is not null)],
        };
    }

    public static void Apply(JsonObject root, ProfileTuning before, ProfileTuning after)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (after.Name != before.Name) Set(root, "name", after.Name);
        if (!after.ProcessNames.SequenceEqual(before.ProcessNames, StringComparer.Ordinal)) SetList(root, "processNames", after.ProcessNames);
        if (!after.WindowTitles.SequenceEqual(before.WindowTitles, StringComparer.Ordinal)) SetList(root, "windowTitles", after.WindowTitles);
        if (after.SourceLanguage != before.SourceLanguage) Set(root, "sourceLanguage", after.SourceLanguage);
        if (after.Glossary != before.Glossary) SetOrRemove(root, "glossary", after.Glossary);
        if (after.RecommendedMode != before.RecommendedMode) SetOrRemove(root, "recommendedMode", after.RecommendedMode);
        if (after.Description != before.Description) SetOrRemove(root, "description", after.Description);

        if (after.OcrUpscale != before.OcrUpscale)
            SetInSection(root, "ocr", "upscale", after.OcrUpscale is { } upscale ? JsonValue.Create(upscale) : null);
        if (after.OcrMinTextHeight != before.OcrMinTextHeight)
            SetInSection(root, "ocr", "minTextHeight", after.OcrMinTextHeight is { } height ? JsonValue.Create(height) : null);

        if (after.Fps != before.Fps || after.ChangeThreshold != before.ChangeThreshold) ApplyChangeDetection(root, after);

        if (after.FontFamily != before.FontFamily)
            SetInSection(root, "overlay", "fontFamily", after.FontFamily is { } font ? JsonValue.Create(font) : null);

        if (!SameRegions(after.IgnoreRegions, before.IgnoreRegions))
        {
            SetInSection(root, "live", "ignoreRegions", after.IgnoreRegions.Count == 0 ? null : RegionsArray(after.IgnoreRegions));
        }
    }

    public static void PrepareCopy(JsonObject root, string id, string name)
    {
        ArgumentNullException.ThrowIfNull(root);
        var source = Blank(TextOf(root, "basedOn")) ?? Blank(TextOf(root, "id"));
        Set(root, "id", id);
        Set(root, "name", name);
        Remove(root, "corpus");
        if (source is not null) Set(root, "basedOn", source);
    }

    public static bool SameRegions(IReadOnlyList<RelativeRegion> left, IReadOnlyList<RelativeRegion> right) =>
        left.Count == right.Count
        && left.Zip(right).All(static pair =>
            pair.First.X == pair.Second.X && pair.First.Y == pair.Second.Y
            && pair.First.Width == pair.Second.Width && pair.First.Height == pair.Second.Height);

    private static void ApplyChangeDetection(JsonObject root, ProfileTuning after)
    {
        if (after.Fps is null && after.ChangeThreshold is null)
        {
            if (Section(root, "changeDetection") is not { } existing) return;
            Remove(existing, "fps");
            Remove(existing, "threshold");
            if (existing.Count == 0)
            {
                Remove(root, "changeDetection");
                return;
            }
        }

        var section = EnsureSection(root, "changeDetection");
        Set(section, "fps", after.Fps ?? ChangeDetectionProfileSettings.AppDefaultFps);
        Set(section, "threshold", after.ChangeThreshold ?? ChangeDetectionProfileSettings.AppDefaultThreshold);
    }

    private static JsonArray RegionsArray(IReadOnlyList<RelativeRegion> regions)
    {
        var array = new JsonArray();
        foreach (var region in regions)
        {
            array.Add(new JsonObject
            {
                ["x"] = region.X,
                ["y"] = region.Y,
                ["width"] = region.Width,
                ["height"] = region.Height,
            });
        }
        return array;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static string? TextOf(JsonObject node, string name) =>
        KeyOf(node, name) is { } key && node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? KeyOf(JsonObject node, string name) =>
        node.Select(static p => p.Key).FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static JsonObject? Section(JsonObject root, string name) =>
        KeyOf(root, name) is { } key ? root[key] as JsonObject : null;

    private static JsonObject EnsureSection(JsonObject root, string name)
    {
        if (Section(root, name) is { } existing) return existing;
        var created = new JsonObject();
        Set(root, name, created);
        return created;
    }

    private static void Set(JsonObject node, string name, JsonNode? value) => node[KeyOf(node, name) ?? name] = value;

    private static void Remove(JsonObject node, string name)
    {
        if (KeyOf(node, name) is { } key) node.Remove(key);
    }

    private static void SetOrRemove(JsonObject node, string name, string? value)
    {
        if (value is null) Remove(node, name);
        else Set(node, name, value);
    }

    private static void SetList(JsonObject node, string name, IReadOnlyList<string> values)
    {
        if (values.Count == 0 && KeyOf(node, name) is null) return;
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        Set(node, name, array);
    }

    private static void SetInSection(JsonObject root, string section, string name, JsonNode? value)
    {
        if (value is null)
        {
            if (Section(root, section) is not { } existing) return;
            Remove(existing, name);
            if (existing.Count == 0) Remove(root, section);
            return;
        }
        Set(EnsureSection(root, section), name, value);
    }
}
