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

    /// <summary>
    /// Optional canned answer. When set, it is returned instead of forwarding the request up the
    /// pipeline, so this handler can be the <b>end</b> of a client's chain rather than a link in it.
    /// </summary>
    /// <remarks>
    /// Added for the moyu online-source checks (A110+): they must drive the real
    /// <c>PatchCenterViewModel</c> with a real <c>MoyuApi</c> while guaranteeing that <b>no</b>
    /// request can reach <c>api.nextmoe.dev</c> - the site is a free community service and the
    /// acceptance harness runs dozens of times a day. Null (the default) keeps the original
    /// transparent-recorder behaviour that A7/A63 depend on: forward untouched, record, return.
    /// </remarks>
    public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

    /// <summary>
    /// When true, <see cref="Responder"/> answers and nothing is forwarded. When false the request is
    /// passed up the pipeline exactly as before.
    /// </summary>
    public bool StopAtRecording { get; set; }

    /// <summary>
    /// Every exchange this handler recorded, in order, as <c>(method, uri, recorded)</c>. Unlike the
    /// shared <see cref="HttpTrafficRecorder"/> this is not cleared between checks.
    /// </summary>
    public List<(string Method, Uri Uri)> Everything { get; } = new();

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

        // Recorded BEFORE the call so that a request which never gets an answer (a refused URI, a
        // failing transport) is still counted. "0 requests sent" is the assertion several of the
        // moyu checks make, and it must not be satisfiable by an exception path.
        Everything.Add((request.Method.Method, request.RequestUri!));

        try
        {
            var response = StopAtRecording && Responder is not null
                ? Responder(request)
                : await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

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
