using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galbox.Core.Api;

/// <summary>
/// Base class for API clients providing common HTTP functionality.
/// </summary>
public abstract class ApiClient
{
    /// <summary>
    /// Gets the HttpClient instance for making API requests.
    /// </summary>
    protected HttpClient HttpClient { get; }

    /// <summary>
    /// Creates an ApiClient with the specified HttpClient.
    /// </summary>
    protected ApiClient(HttpClient httpClient)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Gets the content from the specified URL.
    /// </summary>
    protected async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        return await HttpClient.GetStringAsync(url, cancellationToken);
    }

    /// <summary>
    /// Gets the content from the specified URL as JSON.
    /// </summary>
    protected async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(content, JsonOptions);
    }

    /// <summary>
    /// Posts the content to the specified URL as JSON.
    /// </summary>
    protected async Task<TResponse?> PostJsonAsync<TRequest, TResponse>(
        string url,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await HttpClient.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<TResponse>(responseContent, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}