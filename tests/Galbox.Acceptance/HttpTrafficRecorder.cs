using System.Collections.Concurrent;

namespace Galbox.Acceptance;

/// <summary>
/// One HTTP exchange observed on a Galbox <see cref="HttpClient"/> pipeline.
/// </summary>
/// <param name="ClientName">Which typed/named client produced the request.</param>
/// <param name="Method">HTTP method.</param>
/// <param name="Url">Absolute request URI.</param>
/// <param name="RequestBody">Request body as sent, or null.</param>
/// <param name="StatusCode">Response status, or -1 on transport failure.</param>
/// <param name="ResponseBody">Response body as received, or empty.</param>
/// <param name="Error">Transport error text, or null.</param>
public sealed record HttpExchange(
    string ClientName,
    string Method,
    string Url,
    string? RequestBody,
    int StatusCode,
    string ResponseBody,
    string? Error);

/// <summary>
/// Tiny passive observer shared by the recording handlers.
///
/// Recording the traffic the application's own API clients produce is what makes the A7
/// diagnostic drift-proof: it reports the request shape Galbox actually sends right now,
/// instead of a copy of a source literal that silently goes stale when a client is fixed.
/// </summary>
public sealed class HttpTrafficRecorder
{
    private readonly ConcurrentQueue<HttpExchange> _exchanges = new();

    /// <summary>Everything recorded since the last <see cref="Clear"/>.</summary>
    public IReadOnlyList<HttpExchange> Exchanges => _exchanges.ToArray();

    /// <summary>Records one exchange.</summary>
    public void Add(HttpExchange exchange) => _exchanges.Enqueue(exchange);

    /// <summary>Drops the recorded exchanges.</summary>
    public void Clear()
    {
        while (_exchanges.TryDequeue(out _))
        {
            // drain
        }
    }

    /// <summary>Exchanges produced by one client (matched by handler name prefix).</summary>
    public IReadOnlyList<HttpExchange> For(string clientName) =>
        _exchanges.Where(e => e.ClientName.Equals(clientName, StringComparison.OrdinalIgnoreCase)).ToArray();
}
