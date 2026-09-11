namespace Galbox.Acceptance;

/// <summary>
/// Transparent <see cref="DelegatingHandler"/> that copies every exchange into the
/// <see cref="HttpTrafficRecorder"/> and forwards it untouched.
///
/// It only observes: the request is sent unchanged and the response is returned unchanged.
/// Reading the bodies is safe because <see cref="HttpContent"/> buffers its content after the
/// first read, so the application's own <c>ReadAsStringAsync</c> still returns the full body.
///
/// Registered per client with an explicit name, e.g.
/// <c>.AddHttpMessageHandler(sp =&gt; new RecordingHttpMessageHandler(recorder, "VndbHttpClient"))</c>.
/// </summary>
public sealed class RecordingHttpMessageHandler : DelegatingHandler
{
    private readonly HttpTrafficRecorder _recorder;
    private readonly string _clientName;

    /// <summary>Creates a recording handler that attributes traffic to <paramref name="clientName"/>.</summary>
    public RecordingHttpMessageHandler(HttpTrafficRecorder recorder, string clientName)
    {
        _recorder = recorder;
        _clientName = clientName;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? requestBody = null;
        if (request.Content is not null)
        {
            try
            {
                requestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                requestBody = $"<unreadable: {ex.Message}>";
            }
        }

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            string responseBody;
            try
            {
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                responseBody = $"<unreadable: {ex.Message}>";
            }

            _recorder.Add(new HttpExchange(
                _clientName,
                request.Method.Method,
                request.RequestUri?.ToString() ?? "(null uri)",
                requestBody,
                (int)response.StatusCode,
                responseBody,
                null));

            return response;
        }
        catch (Exception ex)
        {
            _recorder.Add(new HttpExchange(
                _clientName,
                request.Method.Method,
                request.RequestUri?.ToString() ?? "(null uri)",
                requestBody,
                -1,
                string.Empty,
                $"{ex.GetType().Name}: {ex.Message}"));
            throw;
        }
    }
}
