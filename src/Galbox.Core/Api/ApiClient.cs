namespace Galbox.Core.Api;

/// <summary>
/// Base API client for external data sources.
/// </summary>
public abstract class ApiClient
{
    protected readonly HttpClient _httpClient;

    protected ApiClient()
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    protected async Task<string> GetAsync(string url)
    {
        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}