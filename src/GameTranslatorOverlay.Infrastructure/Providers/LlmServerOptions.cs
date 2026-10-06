using System.Globalization;

namespace GameTranslatorOverlay.Infrastructure.Providers;

public sealed record LlmServerOptions
{
    public const int MaxValueLength = 32;

    public string? Host { get; init; }
    public string? Thinking { get; init; }
    public string? ReasoningEffort { get; init; }
    public int? MaxTokens { get; init; }
    public string? ResponseFormat { get; init; }

    public bool HasRequestFields =>
        Clean(Thinking) is not null || Clean(ReasoningEffort) is not null || MaxTokens is > 0 || Clean(ResponseFormat) is not null;

    public bool AppliesTo(Uri endpoint) =>
        !string.IsNullOrWhiteSpace(Host) && Host.Trim().Equals(endpoint.Authority, StringComparison.OrdinalIgnoreCase);

    public LlmServerOptions ForHost(string host) => this with { Host = host };

    public LlmServerOptions Sanitized() => new()
    {
        Host = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim(),
        Thinking = Clean(Thinking),
        ReasoningEffort = Clean(ReasoningEffort),
        MaxTokens = MaxTokens is > 0 ? MaxTokens : null,
        ResponseFormat = Clean(ResponseFormat),
    };

    public string Describe()
    {
        var clean = Sanitized();
        var parts = new List<string>();
        if (clean.Thinking is { } thinking) parts.Add($"thinking={thinking}");
        if (clean.ReasoningEffort is { } effort) parts.Add($"reasoning_effort={effort}");
        if (clean.MaxTokens is { } maxTokens) parts.Add(string.Create(CultureInfo.InvariantCulture, $"max_tokens={maxTokens}"));
        if (clean.ResponseFormat is { } format) parts.Add($"response_format={format}");
        return parts.Count == 0 ? "brak" : string.Join(", ", parts);
    }

    public static bool IsValidValue(string? value) => Clean(value) is not null;

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxValueLength) return null;
        return trimmed.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.') ? trimmed : null;
    }
}

public sealed record LlmTokenUsage(long PromptTokens, long CompletionTokens, long ReasoningTokens, long CachedPromptTokens);

public sealed class LlmUsageTotals
{
    private long _requests;
    private long _requestsWithUsage;
    private long _prompt;
    private long _completion;
    private long _reasoning;
    private long _cached;

    public long Requests => Interlocked.Read(ref _requests);
    public long RequestsWithUsage => Interlocked.Read(ref _requestsWithUsage);
    public long PromptTokens => Interlocked.Read(ref _prompt);
    public long CompletionTokens => Interlocked.Read(ref _completion);
    public long ReasoningTokens => Interlocked.Read(ref _reasoning);
    public long CachedPromptTokens => Interlocked.Read(ref _cached);

    public void Add(LlmTokenUsage? usage)
    {
        Interlocked.Increment(ref _requests);
        if (usage is null) return;
        Interlocked.Increment(ref _requestsWithUsage);
        Interlocked.Add(ref _prompt, usage.PromptTokens);
        Interlocked.Add(ref _completion, usage.CompletionTokens);
        Interlocked.Add(ref _reasoning, usage.ReasoningTokens);
        Interlocked.Add(ref _cached, usage.CachedPromptTokens);
    }
}
