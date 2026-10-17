using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using GameTranslatorOverlay.Infrastructure.Providers;

internal static class LocalModelServer
{
    public const int DefaultMaxRequests = 12;

    private static readonly string[] SecretHeaders = ["Authorization", "Proxy-Authorization", "x-api-key", "api-key"];

    public static bool IsAllowedHost(string? host) =>
        host is "127.0.0.1" or "[::1]" || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

    public static bool TryParseEndpoint(string? raw, out Uri? baseUri, out string error)
    {
        baseUri = null;
        var text = raw?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "--endpoint wymaga pelnego adresu serwera na tym komputerze, np. http://127.0.0.1:1234/v1.";
            return false;
        }
        if (!IsAllowedHost(uri.Host))
        {
            error = "--endpoint: odmowa. Dozwolony jest tylko serwer na tym komputerze: 127.0.0.1, localhost albo [::1].";
            return false;
        }
        if (!LlmEndpoint.TryNormalize(text, out var normalized, out error)) return false;
        baseUri = normalized;
        return true;
    }

    public static void EnsureAllowed(HttpRequestMessage request, Uri baseUri)
    {
        var target = request.RequestUri;
        if (target is null || !target.IsAbsoluteUri || !IsAllowedHost(target.Host)
            || !string.Equals(target.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(target.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SceneReplay openai-local: zapytanie poza wskazany serwer lokalny zostalo zablokowane.");
        }
        foreach (var header in SecretHeaders)
        {
            if (request.Headers.Contains(header))
                throw new InvalidOperationException("SceneReplay openai-local: zapytanie z kluczem API zostalo zablokowane.");
        }
    }

    public static HttpMessageHandler CreateHandler(Uri baseUri, int maxRequests, Func<HttpMessageHandler, HttpMessageHandler>? wrap = null)
    {
        var sockets = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectLoopbackAsync,
        };
        return new GuardHandler(baseUri, maxRequests, wrap is null ? sockets : wrap(sockets));
    }

    private static async ValueTask<System.IO.Stream> ConnectLoopbackAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        if (!IsAllowedHost(host) && !IsAllowedHost($"[{host}]"))
            throw new HttpRequestException("SceneReplay openai-local: polaczenie poza serwer lokalny zostalo zablokowane.");
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var loopback = addresses.Where(IPAddress.IsLoopback).ToArray();
        if (loopback.Length == 0)
            throw new HttpRequestException("SceneReplay openai-local: adres nie wskazuje na ten komputer, polaczenie zablokowane.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(loopback, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class GuardHandler(Uri baseUri, int maxRequests, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            EnsureAllowed(request, baseUri);
            if (Interlocked.Increment(ref _requests) > maxRequests)
                throw new InvalidOperationException($"SceneReplay openai-local: przekroczono limit {maxRequests} zapytan na przebieg.");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
