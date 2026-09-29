using System.Net;
using System.Text;

namespace GameTranslatorOverlay.Infrastructure.Tests;

/// <summary>Zapytanie przechwycone przez <see cref="FakeHttpHandler"/> — bez sieci.</summary>
internal sealed record CapturedRequest(
    HttpMethod Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    CancellationToken CancellationToken)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}

internal sealed class FakeHttpHandler(Func<CapturedRequest, int, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<CapturedRequest> _requests = [];

    public IReadOnlyList<CapturedRequest> Requests
    {
        get { lock (_gate) return _requests.ToList(); }
    }

    public int Calls
    {
        get { lock (_gate) return _requests.Count; }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(static h => h.Key, static h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var captured = new CapturedRequest(request.Method, request.RequestUri!.ToString(), headers, body, cancellationToken);

        int attempt;
        lock (_gate)
        {
            attempt = _requests.Count;
            _requests.Add(captured);
        }
        return await responder(captured, attempt);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(int status, string body = "") =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
