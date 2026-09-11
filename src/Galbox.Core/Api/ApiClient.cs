using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galbox.Core.Api;

/// <summary>
/// Base class for API clients providing common HTTP functionality.
/// Includes retry logic, timeout handling, and proper error handling.
/// </summary>
public abstract class ApiClient
{
    /// <summary>
    /// Gets the HttpClient instance for making API requests.
    /// </summary>
    protected HttpClient HttpClient { get; }

    /// <summary>
    /// Maximum number of retry attempts for transient failures.
    /// </summary>
    protected int MaxRetryCount { get; set; } = 3;

    /// <summary>
    /// Initial delay for retry (exponential backoff base in milliseconds).
    /// </summary>
    protected int RetryBaseDelayMs { get; set; } = 1000;

    /// <summary>
    /// Creates an ApiClient with the specified HttpClient.
    /// </summary>
    protected ApiClient(HttpClient httpClient)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        // Set default timeout if not configured (default HttpClient timeout is 100 seconds)
        if (HttpClient.Timeout == TimeSpan.FromSeconds(100))
        {
            HttpClient.Timeout = TimeSpan.FromSeconds(30);
        }
    }

    /// <summary>
    /// Gets the content from the specified URL.
    /// </summary>
    protected async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        return await ExecuteWithRetryAsync(
            async () => await HttpClient.GetStringAsync(url, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Gets the content from the specified URL as JSON.
    /// </summary>
    protected async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        var content = await ExecuteWithRetryAsync(async () =>
        {
            var response = await HttpClient.GetAsync(url, cancellationToken);
            await HandleResponseAsync(response);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }, cancellationToken);

        if (string.IsNullOrEmpty(content))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(content, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Log JSON parsing error but don't throw - return null
            System.Diagnostics.Debug.WriteLine($"JSON parsing error: {ex.Message}");
            return default;
        }
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

        var responseContent = await ExecuteWithRetryAsync(async () =>
        {
            var response = await HttpClient.PostAsync(url, content, cancellationToken);
            await HandleResponseAsync(response);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }, cancellationToken);

        if (string.IsNullOrEmpty(responseContent))
            return default;

        try
        {
            return JsonSerializer.Deserialize<TResponse>(responseContent, JsonOptions);
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"JSON parsing error: {ex.Message}");
            return default;
        }
    }

    /// <summary>
    /// Executes an HTTP operation with retry logic for transient failures.
    /// Uses exponential backoff strategy with jitter.
    /// </summary>
    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        Exception? lastException = null;

        while (attempt < MaxRetryCount)
        {
            attempt++;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await operation();
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout occurred (not user cancellation) - retry
                lastException = new HttpRequestException("Request timeout", ex);
                if (attempt < MaxRetryCount)
                {
                    await DelayWithJitterAsync(attempt, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // User cancellation - don't retry, throw immediately
                throw;
            }
            catch (HttpRequestException ex) when (ShouldRetry(ex.StatusCode))
            {
                // Transient error (5xx or 429) - retry
                lastException = ex;
                if (attempt < MaxRetryCount)
                {
                    // Use longer delay for rate limiting (429)
                    var multiplier = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? 2 : 1;
                    await DelayWithJitterAsync(attempt, cancellationToken, multiplier);
                }
            }
            catch (HttpRequestException)
            {
                // Non-transient error (4xx except 429) - don't retry, throw immediately
                throw;
            }
        }

        throw lastException ?? new HttpRequestException("Max retry attempts exceeded");
    }

    /// <summary>
    /// Handles HTTP response, throwing appropriate exception for non-success status codes.
    /// </summary>
    private async Task HandleResponseAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var statusCode = response.StatusCode;
        var reasonPhrase = response.ReasonPhrase ?? statusCode.ToString();

        // Get response body for error details
        var errorBody = string.Empty;
        try
        {
            errorBody = await response.Content.ReadAsStringAsync();
        }
        catch
        {
            // Ignore content reading errors
        }

        throw new HttpRequestException(
            $"API error: {statusCode} ({reasonPhrase}). Response: {errorBody}",
            null,
            statusCode);
    }

    /// <summary>
    /// Determines if the HTTP status code should trigger a retry.
    /// </summary>
    private static bool ShouldRetry(System.Net.HttpStatusCode? statusCode)
    {
        if (statusCode == null)
            return true; // Network-level errors should retry

        var code = (int)statusCode.Value;
        return code >= 500 || code == 429; // 5xx server errors or rate limiting
    }

    /// <summary>
    /// Delays with exponential backoff and jitter.
    /// </summary>
    private async Task DelayWithJitterAsync(int attempt, CancellationToken cancellationToken, int multiplier = 1)
    {
        // Exponential backoff: 1s, 2s, 4s, etc.
        var baseDelay = RetryBaseDelayMs * Math.Pow(2, attempt - 1);

        // Add jitter (±20%) to avoid synchronized retries
        var jitter = baseDelay * 0.2 * (Random.Shared.NextDouble() - 0.5);

        var delay = (int)Math.Min(baseDelay + jitter, 30000) * multiplier; // Cap at 30 seconds

        await Task.Delay(delay, cancellationToken);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}