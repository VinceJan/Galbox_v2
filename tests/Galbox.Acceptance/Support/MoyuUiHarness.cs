using System.Reflection;
using Galbox.App.Services;
using Galbox.App.ViewModels;
using Galbox.Core.Api;
using Galbox.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Galbox.Acceptance.Support;

/// <summary>
/// Builds a real <see cref="PatchCenterViewModel"/> for the online-source checks: the shipping
/// ViewModel, the shipping services, and a <see cref="RecordingHttpMessageHandler"/> in front of the
/// moyu transport so a check can assert <b>which requests were actually sent</b> — including asserting
/// that none were.
///
/// <para>
/// Why the handler is not the container's: the container's moyu client points at
/// <c>https://api.nextmoe.dev</c>. A check must not put a request on that wire (the site is a free
/// community service and the harness is run dozens of times a day), so the client under test is
/// constructed with the same <see cref="MoyuApi"/> type, the same options type and the same
/// production pacing limiter, but a transport that records instead of dialling. The ViewModel itself
/// is the real one, resolved the same way the page resolves it.
/// </para>
/// </summary>
internal sealed class MoyuUiHarness : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _trafficClientName;

    private MoyuUiHarness(
        ServiceProvider services,
        string trafficClientName,
        MoyuApi api,
        MoyuOptions options,
        RecordingHttpMessageHandler transport)
    {
        _services = services;
        _trafficClientName = trafficClientName;
        Api = api;
        Options = options;
        Transport = transport;
    }

    /// <summary>The moyu client the ViewModel will hold. Its transport is <see cref="Transport"/>.</summary>
    public MoyuApi Api { get; }

    /// <summary>The options behind <see cref="Api"/> (key presence, pacing).</summary>
    public MoyuOptions Options { get; }

    /// <summary>The recording transport. <c>Transport.Everything</c> is what a check asserts on.</summary>
    public RecordingHttpMessageHandler Transport { get; }

    /// <summary>Requests this harness's moyu client actually put on the wire.</summary>
    public IReadOnlyList<Uri> SentUris => Transport.Everything.Select(e => e.Uri).ToArray();

    /// <summary>
    /// Builds a harness.
    /// </summary>
    /// <param name="context">The run's container (source of the patch engine, the DB factory, loggers).</param>
    /// <param name="apiKey">The <c>nmk_</c> key to configure, or null for a key-less client.</param>
    /// <param name="responder">What the stub transport answers with.</param>
    /// <param name="launcher">The browser hop the ViewModel will use.</param>
    /// <param name="watcher">The download watcher the ViewModel will use.</param>
    /// <param name="keyStore">The key store the ViewModel will write to; a fresh in-memory one by default.</param>
    /// <param name="trafficClientName">Name this client's exchanges carry in the run's traffic recorder.</param>
    public static MoyuUiHarness Build(
        AcceptanceContext context,
        string? apiKey,
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        MoyuBrowserLauncher? launcher = null,
        MoyuDownloadWatcher? watcher = null,
        IMoyuKeyStore? keyStore = null,
        string trafficClientName = "MoyuUiProbe")
    {
        var transport = new RecordingHttpMessageHandler(context.Traffic, trafficClientName)
        {
            Responder = responder,
            StopAtRecording = true
        };

        var httpClient = new HttpClient(transport) { BaseAddress = MoyuOptions.DefaultBaseAddress };
        httpClient.DefaultRequestHeaders.Add("User-Agent", MoyuComplianceGuard.UserAgent);
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        var options = new MoyuOptions { ApiKey = apiKey };
        var limiter = new MoyuRateLimiter(TimeSpan.Zero, 1000);
        var api = new MoyuApi(new MoyuHttpClient(httpClient), options, limiter);

        return new MoyuUiHarness(
            context.Services,
            trafficClientName,
            api,
            options,
            transport)
        {
            Launcher = launcher ?? new MoyuBrowserLauncher(new MoyuBrowserLauncherOptions { DryRun = true }),
            Watcher = watcher ?? new MoyuDownloadWatcher(options),
            KeyStore = keyStore ?? new MoyuMemoryKeyStore(apiKey)
        };
    }

    /// <summary>The browser hop handed to the ViewModel.</summary>
    public MoyuBrowserLauncher Launcher { get; private init; } = null!;

    /// <summary>The download watcher handed to the ViewModel.</summary>
    public MoyuDownloadWatcher Watcher { get; private init; } = null!;

    /// <summary>The key store handed to the ViewModel.</summary>
    public IMoyuKeyStore KeyStore { get; private init; } = null!;

    /// <summary>
    /// Creates the page's ViewModel exactly the way the page does — from the real container, with the
    /// only substitution being the headless navigation service the harness always uses. The moyu
    /// arguments are the container's own registrations (so the wiring is the thing under test) except
    /// when a check supplies its own.
    /// </summary>
    public PatchCenterViewModel CreateViewModel(MoyuBrowserLauncher? launcher = null, MoyuDownloadWatcher? watcher = null)
    {
        var navigation = new HeadlessNavigationService();

        return ActivatorUtilities.CreateInstance<PatchCenterViewModel>(
            _services,
            navigation,
            Api,
            KeyStore,
            launcher ?? Launcher,
            watcher ?? Watcher);
    }

    /// <summary>Every moyu exchange the run's shared recorder saw for this harness's client.</summary>
    public IReadOnlyList<HttpExchange> RecordedByThisHarness =>
        _services.GetRequiredService<HttpTrafficRecorder>().For(_trafficClientName);

    /// <inheritdoc />
    public void Dispose()
    {
        // The harness owns no provider of its own; the run's container belongs to the runner.
    }

    /// <summary>A game row shaped like the library's, with or without a vndb id.</summary>
    public static GameInfo MakeGame(string? vndbId, string? sourceType = null, string? sourceId = null) => new()
    {
        Id = Math.Abs(Guid.NewGuid().GetHashCode() % 100000) + 1,
        NameOriginal = "Acceptance Fixture (moyu ui)",
        InstallPath = Path.GetTempPath(),
        MainExecutable = "game.exe",
        VndbId = vndbId,
        SourceType = sourceType,
        SourceId = sourceId
    };

    /// <summary>Reads a property off the ViewModel by name, for the members a check has to drive.</summary>
    public static object? Read(object viewModel, string propertyName) =>
        viewModel.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(viewModel);
}
