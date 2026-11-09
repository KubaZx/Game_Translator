using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTranslatorOverlay.Infrastructure.Settings;

public enum SettingsLoadStatus
{
    Ok,
    Missing,
    FieldsRepaired,
    Unreadable,
    Unavailable,
    NewerVersion,
}

public sealed record SettingsLoadResult(
    AppSettings Settings,
    SettingsLoadStatus Status,
    IReadOnlyList<string> RepairedFields,
    string? BackupPath)
{
    public bool NeedsAttention => Status is SettingsLoadStatus.FieldsRepaired or SettingsLoadStatus.Unreadable
        or SettingsLoadStatus.Unavailable or SettingsLoadStatus.NewerVersion;

    public bool OffersDefaults => Status is SettingsLoadStatus.Unreadable or SettingsLoadStatus.Unavailable;
}

public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Dictionary<string, PropertyInfo> KnownProperties = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(static p => p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always }
            && p.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
        .ToDictionary(static p => p.Name, StringComparer.OrdinalIgnoreCase);

    private readonly AppPaths _paths;
    private readonly ILogger _logger;
    private readonly int _readAttempts;
    private readonly TimeSpan _retryDelay;

    public JsonSettingsStore(AppPaths paths, ILogger<JsonSettingsStore>? logger = null, int readAttempts = 4, int retryDelayMs = 150)
    {
        _paths = paths;
        _logger = logger ?? NullLogger<JsonSettingsStore>.Instance;
        _readAttempts = Math.Max(1, readAttempts);
        _retryDelay = TimeSpan.FromMilliseconds(Math.Max(0, retryDelayMs));
    }

    public SettingsLoadResult? LastLoad { get; private set; }

    public bool SavingBlocked { get; private set; }

    public void AllowSaving() => SavingBlocked = false;

    public AppSettings Load() => LoadWithReport().Settings;

    public SettingsLoadResult LoadWithReport()
    {
        var result = ReadSettings();
        SavingBlocked = result.Status is SettingsLoadStatus.Unreadable or SettingsLoadStatus.Unavailable or SettingsLoadStatus.NewerVersion;
        result.Settings.PersistedActiveProfileId = result.Settings.ActiveProfileId;
        LastLoad = result;
        return result;
    }

    private SettingsLoadResult ReadSettings()
    {
        if (!File.Exists(_paths.SettingsPath))
        {
            return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.Missing, [], null);
        }

        if (!TryReadText(out var json))
        {
            _logger.LogWarning("Plik ustawień jest chwilowo niedostępny — start w trybie prywatnym i Cache-only, bez zapisu ustawień");
            return new SettingsLoadResult(SafeStartSettings(), SettingsLoadStatus.Unavailable, [], null);
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: DocumentOptions) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            root = null;
        }

        if (root is null)
        {
            return Unreadable();
        }

        var repaired = new List<string>();
        var clean = new JsonObject();
        foreach (var (name, value) in root)
        {
            if (KnownProperties.TryGetValue(name, out var property) && !IsAcceptable(property, value))
            {
                repaired.Add(JsonNamingPolicy.CamelCase.ConvertName(property.Name));
                continue;
            }
            clean[name] = value?.DeepClone();
        }

        AppSettings settings;
        try
        {
            settings = clean.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return Unreadable();
        }

        if (!root.Any(static pair => string.Equals(pair.Key, "overlayFontRevision", StringComparison.OrdinalIgnoreCase))) settings.OverlayFontRevision = 0;
        settings.MigrateOverlayFont();

        var status = repaired.Count > 0 ? SettingsLoadStatus.FieldsRepaired : SettingsLoadStatus.Ok;
        if (settings.SettingsVersion > AppSettings.CurrentSettingsVersion) status = SettingsLoadStatus.NewerVersion;
        if (repaired.Count > 0)
        {
            _logger.LogWarning("Nieprawidłowe pola w ustawieniach wróciły do domyślnych: {Fields}", string.Join(", ", repaired));
        }
        return new SettingsLoadResult(settings, status, repaired, null);
    }

    private SettingsLoadResult Unreadable()
    {
        var backup = CopyUnreadableFile();
        _logger.LogWarning(
            "Plik ustawień jest nieczytelny — kopia: {Backup}; start w trybie prywatnym i Cache-only, bez zapisu ustawień",
            backup is null ? "(brak)" : Path.GetFileName(backup));
        return new SettingsLoadResult(SafeStartSettings(), SettingsLoadStatus.Unreadable, [], backup);
    }

    private static AppSettings SafeStartSettings() => new() { PrivateMode = true, CacheOnlyMode = true };

    private bool TryReadText(out string json)
    {
        for (var attempt = 1; attempt <= _readAttempts; attempt++)
        {
            try
            {
                json = File.ReadAllText(_paths.SettingsPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < _readAttempts && _retryDelay > TimeSpan.Zero) Thread.Sleep(_retryDelay);
            }
        }
        json = string.Empty;
        return false;
    }

    private string? CopyUnreadableFile()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = $"{_paths.SettingsPath}.corrupt-{stamp}.bak";
        for (var attempt = 2; File.Exists(target); attempt++)
        {
            target = $"{_paths.SettingsPath}.corrupt-{stamp}-{attempt}.bak";
        }
        try
        {
            File.Copy(_paths.SettingsPath, target, overwrite: false);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsAcceptable(PropertyInfo property, JsonNode? value)
    {
        object? parsed;
        try
        {
            parsed = value.Deserialize(property.PropertyType, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }

        if (parsed is null)
        {
            return new NullabilityInfoContext().Create(property).WriteState != NullabilityState.NotNull;
        }
        return HasValidValue(property.Name, parsed);
    }

    private static bool HasValidValue(string name, object value) => name switch
    {
        nameof(AppSettings.SettingsVersion) => value is int version && version >= 1,
        nameof(AppSettings.SourceLanguage) or nameof(AppSettings.TargetLanguage) or nameof(AppSettings.Provider)
            or nameof(AppSettings.LlmEndpoint) or nameof(AppSettings.ClaudeModel) => value is string text && text.Trim().Length > 0,
        nameof(AppSettings.ResultDisplayMode) => value is string mode && mode is "panel" or "overlay",
        nameof(AppSettings.LiveDisplayMode) => value is string mode && mode is "at-source" or "subtitle",
        nameof(AppSettings.OverlayPlacement) => value is string mode && mode is "below" or "cover",
        nameof(AppSettings.OverlayBackgroundOpacity) => value is double opacity && opacity is >= 0 and <= 1,
        nameof(AppSettings.OverlayFontSize) => value is double size && size is >= 0 and <= 200,
        nameof(AppSettings.OcrUpscale) => value is double upscale && upscale is >= 0 and <= 8,
        nameof(AppSettings.ResultAutoHideSeconds) or nameof(AppSettings.SubtitleSeconds) => value is int seconds && seconds is >= 0 and <= 86400,
        nameof(AppSettings.SessionCharacterLimit) => value is long limit && limit >= 0,
        _ => true,
    };

    public bool Save(AppSettings settings)
    {
        if (SavingBlocked) return false;

        _paths.EnsureCreated();
        var node = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
        if (settings.PrivateMode)
        {
            const string profileField = "activeProfileId";
            if (settings.PersistedActiveProfileId is { } persisted) node[profileField] = persisted;
            else node.Remove(profileField);
        }

        // Zapis przez plik tymczasowy + atomowa podmiana: przerwanie w trakcie (crash,
        // zanik zasilania) nie może zostawić uciętego JSON-a.
        var temp = _paths.SettingsPath + ".tmp";
        File.WriteAllText(temp, node.ToJsonString(JsonOptions));
        File.Move(temp, _paths.SettingsPath, overwrite: true);
        if (!settings.PrivateMode) settings.PersistedActiveProfileId = settings.ActiveProfileId;
        return true;
    }
}
