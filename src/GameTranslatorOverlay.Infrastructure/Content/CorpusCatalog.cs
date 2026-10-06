using GameTranslatorOverlay.Core.Corpus;
using GameTranslatorOverlay.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameTranslatorOverlay.Infrastructure.Content;

public sealed record CorpusLoadResult(CorpusSnapper? Snapper, string? Path, int Entries, int Texts, string? Issue)
{
    public static CorpusLoadResult None { get; } = new(null, null, 0, 0, null);

    public bool IsLoaded => Snapper is not null;
}

public sealed class CorpusCatalog(string corpusDirectory, ILogger<CorpusCatalog>? logger = null)
{
    public const string FileSuffix = ".corpus.jsonl";

    private readonly ILogger _logger = logger ?? NullLogger<CorpusCatalog>.Instance;
    private readonly Lock _gate = new();
    private (string Path, long Length, DateTime WriteTimeUtc, CorpusLoadResult Result)? _last;

    public static CorpusCatalog CreateDefault(AppPaths paths, ILogger<CorpusCatalog>? logger = null) =>
        new(paths.CorpusDirectory, logger);

    public string CorpusDirectory { get; } = corpusDirectory;

    public string? PathFor(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return null;
        var id = profileId.Trim();
        if (id is "." or ".." || id.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
            || id.Contains('/') || id.Contains('\\') || id.Contains(':'))
        {
            return null;
        }
        return System.IO.Path.Combine(CorpusDirectory, id + FileSuffix);
    }

    public CorpusLoadResult Load(string? profileId, CorpusSnapOptions? options = null)
    {
        if (PathFor(profileId) is not { } path) return CorpusLoadResult.None;

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return CorpusLoadResult.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return CorpusLoadResult.None;
        }

        lock (_gate)
        {
            if (_last is { } last && last.Path == path && last.Length == info.Length && last.WriteTimeUtc == info.LastWriteTimeUtc
                && (options is null || ReferenceEquals(last.Result.Snapper?.Options, options) || last.Result.Snapper?.Options == options))
            {
                return last.Result;
            }

            CorpusLoadResult result;
            try
            {
                var entries = CorpusJsonl.ReadFile(path);
                var index = CorpusIndex.Build(entries);
                result = index.IsEmpty
                    ? new CorpusLoadResult(null, path, entries.Count, 0, "Korpus nie zawiera żadnego tekstu.")
                    : new CorpusLoadResult(new CorpusSnapper(index, options), path, entries.Count, index.TextCount, null);
                _logger.LogInformation("Korpus profilu {Profile}: {Entries} wpisów, {Texts} tekstów", profileId, entries.Count, index.TextCount);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
            {
                result = new CorpusLoadResult(null, path, 0, 0, $"Nie udało się wczytać korpusu: {ex.GetType().Name}.");
                _logger.LogWarning("Nie udało się wczytać korpusu profilu {Profile} ({Error})", profileId, ex.GetType().Name);
            }
            _last = (path, info.Length, info.LastWriteTimeUtc, result);
            return result;
        }
    }
}
