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
    /// only substitution being the headless navigation service the harness always uses.
    /// </summary>
    /// <returns>
    /// The live ViewModel. The full constructor (with the four moyu services) is preferred; when it
    /// does not accept them, the plain one is used so the check can still run and <b>report</b> the
    /// missing wiring instead of failing to build. <see cref="MoyuUiProbe.BuiltWithMoyuDependencies"/>
    /// says which happened.
    /// </returns>
    public object CreateViewModel(MoyuBrowserLauncher? launcher = null, MoyuDownloadWatcher? watcher = null)
    {
        var navigation = new HeadlessNavigationService();

        if (PatchCenterViewModelAcceptsMoyu())
        {
            var built = ActivatorUtilities.CreateInstance<PatchCenterViewModel>(
                _services, navigation, Api, KeyStore, launcher ?? Launcher, watcher ?? Watcher);

            LastProbe = new MoyuUiProbe(built) { BuiltWithMoyuDependencies = true };
            return built;
        }

        // No moyu constructor. The page still resolves, so every downstream assertion runs against a
        // real ViewModel and reports what is missing by name.
        var plain = ActivatorUtilities.CreateInstance<PatchCenterViewModel>(_services, navigation);
        LastProbe = new MoyuUiProbe(plain) { BuiltWithMoyuDependencies = false };
        return plain;
    }

    /// <summary>The probe for the most recently created ViewModel (carries the construction verdict).</summary>
    public MoyuUiProbe? LastProbe { get; private set; }

    private static bool PatchCenterViewModelAcceptsMoyu() =>
        typeof(PatchCenterViewModel)
            .GetConstructors()
            .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(MoyuApi)));

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

/// <summary>
/// Binds to the online-source surface of <c>PatchCenterViewModel</c> <b>by member name</b>.
///
/// <para>
/// Why reflection, when the acceptance project references <c>Galbox.App</c> directly: these checks
/// had to be runnable <b>before</b> the online source was wired, so that the FAIL the task requires
/// is a measurement rather than a claim. Typed calls would not compile against the revision where
/// <c>PatchCenterViewModel</c> has no moyu members at all — the harness would not build, and there
/// would be no fail-first run to record. Every <i>assertion</i> below is still an assertion about a
/// measured value: a request count off the real transport, the real XAML text, or the real
/// <see cref="MoyuApi"/> result. Only the member lookup goes through this binder, and a missing
/// member is reported by name instead of as a <c>NullReferenceException</c>.
/// </para>
///
/// <para>
/// The enum values are looked up the same way, so "the four outcomes are distinct" is asserted on
/// the <i>names</i> the shipping code uses. A rename that collapsed two states into one would show up
/// here as a missing name, not as a silent pass.
/// </para>
/// </summary>
internal sealed class MoyuUiProbe
{
    private readonly object _viewModel;
    private readonly Type _type;

    /// <summary>Wraps a live ViewModel (or, for the row checks, a row).</summary>
    public MoyuUiProbe(object instance)
    {
        _viewModel = instance;
        _type = instance.GetType();
    }

    /// <summary>The live ViewModel instance this probe wraps.</summary>
    public object Instance => _viewModel;

    /// <summary>Member names this probe could not find.</summary>
    public List<string> Missing { get; } = new();

    /// <summary>
    /// The constructor that was actually used to build the ViewModel, and whether it accepted the
    /// moyu arguments. "No" is the measurement A110 makes: it means the page's own construction path
    /// does not reach the moyu layer.
    /// </summary>
    public bool BuiltWithMoyuDependencies { get; internal set; }

    /// <summary>Names of every public member, for the diagnostic block.</summary>
    public IEnumerable<string> MemberNames =>
        _type.GetMembers(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name).Distinct().OrderBy(n => n);

    /// <summary>Reads a property by name; a missing member is recorded and answers null.</summary>
    public object? Get(string propertyName)
    {
        // Walks the type hierarchy: an [ObservableProperty] setter lives on the derived partial class
        // but the backing member can be declared on the base.
        for (var type = _type; type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property is not null)
            {
                return property.GetValue(_viewModel);
            }
        }

        if (!Missing.Contains(propertyName))
        {
            Missing.Add($"property {_type.Name}.{propertyName}");
        }

        return null;
    }

    /// <summary>Reads a property as a string, or null.</summary>
    public string? GetString(string propertyName) => Get(propertyName) as string;

    /// <summary>Reads a property as a bool, or null.</summary>
    public bool? GetBool(string propertyName) => Get(propertyName) as bool?;

    /// <summary>Number of items in a collection property, or -1 when the member is absent.</summary>
    public int Count(string collectionPropertyName)
    {
        var value = Get(collectionPropertyName);
        if (value is null)
        {
            return -1;
        }

        return value.GetType().GetProperty("Count")?.GetValue(value) as int? ?? -1;
    }

    /// <summary>The items of a collection property, or an empty list when the member is absent.</summary>
    public IReadOnlyList<object> Items(string collectionPropertyName)
    {
        if (Get(collectionPropertyName) is not System.Collections.IEnumerable sequence)
        {
            return Array.Empty<object>();
        }

        return sequence.Cast<object>().ToArray();
    }

    /// <summary>Sets a property by name. Missing members are recorded, not thrown.</summary>
    public bool Set(string propertyName, object? value)
    {
        for (var type = _type; type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property is not null && property.CanWrite)
            {
                property.SetValue(_viewModel, value);
                return true;
            }
        }

        if (!Missing.Contains(propertyName))
        {
            Missing.Add($"settable property {_type.Name}.{propertyName}");
        }

        return false;
    }

    /// <summary>
    /// Name of the enum value held by a property (e.g. <c>"NoAnchor"</c>), or null when the member is
    /// absent. Comparing names rather than integers is deliberate: it asserts the vocabulary the
    /// shipping code defines, not a numeric value nobody reads.
    /// </summary>
    public string? StateName(string propertyName) => Get(propertyName)?.ToString();

    /// <summary>
    /// Invokes a method by name and awaits it. A missing method is recorded and answers null, so the
    /// caller produces a FAIL that names what is missing instead of failing to build.
    /// </summary>
    public async Task<object?> CallAsync(string methodName, params object?[] arguments)
    {
        arguments ??= Array.Empty<object?>();
        var method = FindMethod(methodName, arguments);
        if (method is null)
        {
            if (!Missing.Contains(methodName))
            {
                Missing.Add($"method {_type.Name}.{methodName}");
            }

            return null;
        }

        object? result;
        try
        {
            result = method.Invoke(_viewModel, ReflectionBridge.PadOptionalArguments(method, arguments));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }

        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            var taskType = task.GetType();
            return taskType.IsGenericType ? taskType.GetProperty("Result")?.GetValue(task) : null;
        }

        return result;
    }

    /// <summary>Invokes a void method by name; a missing method is recorded.</summary>
    public void Call(string methodName, params object?[] arguments)
    {
        arguments ??= Array.Empty<object?>();
        var method = FindMethod(methodName, arguments);
        if (method is null)
        {
            if (!Missing.Contains(methodName))
            {
                Missing.Add($"method {_type.Name}.{methodName}");
            }

            return;
        }

        try
        {
            method.Invoke(_viewModel, ReflectionBridge.PadOptionalArguments(method, arguments));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>True when the member (property or method) exists — used to name what is missing.</summary>
    public bool Has(string memberName) =>
        _type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance) is not null
        || _type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(m => m.Name == memberName);

    /// <summary>A printable list of everything the probe could not find.</summary>
    public string MissingSummary() => Missing.Count == 0
        ? "(none)"
        : string.Join("; ", Missing.Distinct(StringComparer.Ordinal));

    private MethodInfo? FindMethod(string name, object?[] arguments) =>
        ReflectionBridge.FindCallableInstanceMethod(_type, name, arguments.Length, includeNonPublic: true);
}
