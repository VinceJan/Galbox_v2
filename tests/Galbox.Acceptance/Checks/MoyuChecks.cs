using System.Diagnostics;
using System.Text;
using Galbox.Core.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// Shared helpers for the A60+ block: the moyu patch-source online discovery layer.
///
/// <para>
/// The new production types are resolved <b>by name</b> instead of by compiling against them.
/// That is deliberate: it keeps this project buildable while the feature is still being
/// implemented, so the "before" run produces real, measured FAIL lines ("the type does not
/// exist") instead of a compiler error. A compiler error would prove nothing about what was
/// measured, and the whole point of A60+ is to record what was actually observed.
/// </para>
///
/// <para>
/// Every check in this block must be able to run with <b>no network</b> and <b>no API key</b>.
/// The one check that genuinely needs a live <c>nmk_</c> key reports SKIP with its reason
/// rather than pretending to pass.
/// </para>
/// </summary>
internal static class MoyuCheckSupport
{
    /// <summary>Assembly-qualified name of the moyu API client.</summary>
    internal const string ApiTypeName = "Galbox.Core.Api.MoyuApi, Galbox.Core";

    /// <summary>Assembly-qualified name of the moyu options object.</summary>
    internal const string OptionsTypeName = "Galbox.Core.Api.MoyuOptions, Galbox.Core";

    /// <summary>Assembly-qualified name of the DPAPI-backed key store abstraction.</summary>
    internal const string KeyStoreTypeName = "Galbox.Core.Api.IMoyuKeyStore, Galbox.Core";

    /// <summary>Assembly-qualified name of the DPAPI-backed key store implementation.</summary>
    internal const string DpapiKeyStoreTypeName = "Galbox.Core.Api.MoyuDpapiKeyStore, Galbox.Core";

    /// <summary>Assembly-qualified name of the downloads-folder watcher.</summary>
    internal const string WatcherTypeName = "Galbox.Core.Api.MoyuDownloadWatcher, Galbox.Core";

    /// <summary>Assembly-qualified name of the browser launcher.</summary>
    internal const string LauncherTypeName = "Galbox.Core.Api.MoyuBrowserLauncher, Galbox.Core";

    /// <summary>Assembly-qualified name of the compliance guard.</summary>
    internal const string GuardTypeName = "Galbox.Core.Api.MoyuComplianceGuard, Galbox.Core";

    /// <summary>
    /// Environment variable that overrides where the API key is stored. The shipping app never
    /// sets it; the acceptance harness points it at a throw-away directory so a check can prove
    /// the "no key configured" behaviour without reading - or writing - the user's real key.
    /// </summary>
    internal const string KeyStorePathVariable = "GALBOX_MOYU_KEYSTORE";

    /// <summary>
    /// Resolves a freshly built moyu service from the container, or reports the reason it cannot
    /// be resolved. The caller turns a non-null return into a FAIL with that reason attached.
    /// </summary>
    internal static (object? Service, string? Reason) ResolveApi(AcceptanceContext context)
    {
        var type = Type.GetType(ApiTypeName);
        if (type is null)
        {
            return (null, "the type Galbox.Core.Api.MoyuApi is not present in Galbox.Core");
        }

        try
        {
            var service = context.Services.GetService(type);
            return service is null
                ? (null, $"{type.FullName} resolved to null from the DI container")
                : (service, null);
        }
        catch (Exception ex)
        {
            return (null, $"{type.FullName} failed to resolve: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Reads a public property by name, returning null when it is absent.</summary>
    internal static object? ReadProperty(object instance, string propertyName)
        => instance.GetType().GetProperty(propertyName)?.GetValue(instance);

    /// <summary>Renders a property value for the report, tolerating null.</summary>
    internal static string DescribeProperty(object instance, string propertyName)
    {
        var value = ReadProperty(instance, propertyName);
        return value switch
        {
            null => "(null)",
            string text when string.IsNullOrEmpty(text) => "(empty)",
            _ => value.ToString() ?? "(null)"
        };
    }

    /// <summary>Reads an enum-valued property as its name.</summary>
    internal static string DescribeEnumProperty(object instance, string propertyName)
        => ReadProperty(instance, propertyName)?.ToString() ?? "(absent)";

    /// <summary>
    /// Cuts a string down to <paramref name="max"/> characters, appending a marker when it was
    /// shortened, so raw upstream bodies can be printed without flooding the report.
    /// </summary>
    internal static string Truncate(string? value, int max = 400)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        return value.Length <= max ? value : value[..max] + $" ... [TRUNCATED, {value.Length} chars total]";
    }

    /// <summary>Creates a throw-away directory under the system temp folder.</summary>
    internal static string NewScratchDirectory(string tag)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "GalboxMoyuAcceptance",
            $"{tag}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Best-effort recursive delete; never throws.</summary>
    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A leftover temp directory is not a test result.
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with <see cref="KeyStorePathVariable"/> pointed at
    /// <paramref name="keyStorePath"/>, restoring the previous value afterwards.
    /// </summary>
    internal static void WithKeyStorePath(string keyStorePath, Action action)
    {
        var previous = Environment.GetEnvironmentVariable(KeyStorePathVariable);
        try
        {
            Environment.SetEnvironmentVariable(KeyStorePathVariable, keyStorePath);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(KeyStorePathVariable, previous);
        }
    }

    /// <summary>Shared expectation text used by the "guard" failures.</summary>
    internal static CheckResult Missing(string id, string title, string expected, string reason)
        => CheckResult.Fail(id, title, expected, $"the feature is absent: {reason}");
}

/// <summary>
/// A60 - the moyu client is resolvable from the same DI container the shipping app builds, and
/// the typed HttpClient behind it is configured the way the integration requires: the official
/// <c>/v2/moyu</c> base address, an identifying User-Agent, and a bounded timeout.
/// </summary>
public sealed class A60MoyuServiceResolutionCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A60";

    /// <inheritdoc />
    public string Title => "MoyuApi resolves from DI with the official /v2/moyu HttpClient configuration";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "MoyuApi + IMoyuKeyStore + MoyuOptions resolve from the container, "
            + "BaseAddress=https://api.nextmoe.dev/, UA identifies Galbox, Timeout in (0, 60]s";

        var details = new List<string>();

        var (service, reason) = MoyuCheckSupport.ResolveApi(context);
        if (service is null)
        {
            return Task.FromResult(MoyuCheckSupport.Missing(Id, Title, expected, reason!));
        }

        details.Add($"Resolved service type : {service.GetType().FullName}");

        // The key store and the options object are resolved by name too, for the same reason.
        foreach (var typeName in new[] { MoyuCheckSupport.KeyStoreTypeName, MoyuCheckSupport.OptionsTypeName })
        {
            var type = Type.GetType(typeName);
            if (type is null)
            {
                return Task.FromResult(MoyuCheckSupport.Missing(
                    Id, Title, expected, $"type {typeName} is not present in Galbox.Core"));
            }

            var instance = context.Services.GetService(type);
            if (instance is null)
            {
                return Task.FromResult(MoyuCheckSupport.Missing(
                    Id, Title, expected, $"{type.FullName} resolved to null from the DI container"));
            }

            details.Add($"Resolved service type : {instance.GetType().FullName}");
        }

        // The HttpClient configuration is read back off the live pipeline.
        var wrapperType = Type.GetType("Galbox.Core.Api.MoyuHttpClient, Galbox.Core");
        if (wrapperType is null)
        {
            return Task.FromResult(MoyuCheckSupport.Missing(
                Id, Title, expected, "type Galbox.Core.Api.MoyuHttpClient is not present in Galbox.Core"));
        }

        object? wrapper;
        try
        {
            wrapper = context.Services.GetService(wrapperType);
        }
        catch (Exception ex)
        {
            return Task.FromResult(MoyuCheckSupport.Missing(
                Id, Title, expected, $"MoyuHttpClient failed to resolve: {ex.GetType().Name}: {ex.Message}"));
        }

        if (wrapper is null)
        {
            return Task.FromResult(MoyuCheckSupport.Missing(
                Id, Title, expected, "MoyuHttpClient resolved to null from the DI container"));
        }

        var httpClient = (HttpClient?)MoyuCheckSupport.ReadProperty(wrapper, "HttpClient");
        if (httpClient is null)
        {
            return Task.FromResult(MoyuCheckSupport.Missing(
                Id, Title, expected, "MoyuHttpClient.HttpClient is null"));
        }

        var baseAddress = httpClient.BaseAddress?.ToString() ?? "(null)";
        var userAgent = httpClient.DefaultRequestHeaders.UserAgent.ToString();
        var timeout = httpClient.Timeout;

        details.Add($"HttpClient.BaseAddress : {baseAddress}");
        details.Add($"HttpClient.User-Agent  : {(string.IsNullOrEmpty(userAgent) ? "(absent)" : userAgent)}");
        details.Add($"HttpClient.Timeout     : {timeout.TotalSeconds:F0}s");

        var problems = new List<string>();

        if (httpClient.BaseAddress is null
            || !httpClient.BaseAddress.Host.Equals("api.nextmoe.dev", StringComparison.OrdinalIgnoreCase)
            || httpClient.BaseAddress.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add($"BaseAddress must be the HTTPS https://api.nextmoe.dev/ origin, measured \"{baseAddress}\"");
        }

        if (string.IsNullOrWhiteSpace(userAgent))
        {
            problems.Add("User-Agent is absent; the public face must be able to identify the client");
        }
        else if (!userAgent.Contains("Galbox", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"User-Agent \"{userAgent}\" does not identify Galbox");
        }

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(60))
        {
            problems.Add($"Timeout {timeout.TotalSeconds:F0}s is outside the sane (0, 60]s window");
        }

        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                details.Add($"PROBLEM: {problem}");
            }

            return Task.FromResult(CheckResult.Fail(
                Id, Title, expected, $"{problems.Count} configuration problem(s): {string.Join("; ", problems)}")
                .With(details.ToArray()));
        }

        return Task.FromResult(CheckResult.Pass(
            Id, Title, expected,
            $"MoyuApi resolved ({service.GetType().Name}); BaseAddress={baseAddress}, "
            + $"UA=\"{userAgent}\", Timeout={timeout.TotalSeconds:F0}s")
            .With(details.ToArray()));
    }
}

/// <summary>
/// A61 - the defect this whole block exists for: with no API key configured, the client must
/// return a failure that says "no key configured", and it must not send a request at all.
/// The historical failure mode is a silent empty result, which is indistinguishable from
/// "this game genuinely has no patches".
/// </summary>
public sealed class A61MoyuMissingKeyCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A61";

    /// <inheritdoc />
    public string Title => "No configured nmk_ key is reported as a distinct failure, never as an empty result";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "FindPatchesAsync returns a result whose Failure.Code == NotConfigured, whose message "
            + "names the missing key, and that sends ZERO HTTP requests";

        var details = new List<string>();

        // The key store is pointed at an empty throw-away file for the duration of this check,
        // so "no key configured" is guaranteed regardless of what the developer has on disk.
        var scratch = MoyuCheckSupport.NewScratchDirectory("nokey");
        var keyFile = Path.Combine(scratch, "moyu-api-key.bin");
        var emptyKeyStoreDirectory = MoyuCheckSupport.NewScratchDirectory("emptystore");
        var previousKeyStore = Environment.GetEnvironmentVariable(MoyuCheckSupport.KeyStorePathVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                MoyuCheckSupport.KeyStorePathVariable, Path.Combine(emptyKeyStoreDirectory, "key.bin"));

            var (service, reason) = MoyuCheckSupport.ResolveApi(context);
            if (service is null)
            {
                return MoyuCheckSupport.Missing(Id, Title, expected, reason!);
            }

            var optionsType = Type.GetType(MoyuCheckSupport.OptionsTypeName);
            if (optionsType is null)
            {
                return MoyuCheckSupport.Missing(
                    Id, Title, expected, "MoyuOptions is not present in Galbox.Core");
            }

            // Re-resolve the options from the container with the empty key store in force.
            var options = context.Services.GetService(optionsType);
            if (options is null)
            {
                return MoyuCheckSupport.Missing(Id, Title, expected, "MoyuOptions resolved to null");
            }

            var apiKey = MoyuCheckSupport.ReadProperty(options, "ApiKey") as string;
            details.Add($"Configured ApiKey : {(string.IsNullOrEmpty(apiKey) ? "(none)" : "(non-empty)")}");
            details.Add($"Key store path     : {Path.Combine(emptyKeyStoreDirectory, "key.bin")} (deliberately empty)");
            details.Add($"Protected key file : {keyFile} (never written by this check)");

            context.Traffic.Clear();

            var findMethod = service.GetType().GetMethod("FindPatchesAsync");
            if (findMethod is null)
            {
                return MoyuCheckSupport.Missing(
                    Id, Title, expected, $"{service.GetType().Name} has no FindPatchesAsync method");
            }

            // The anchor is a valid one: the check must fail on the missing KEY, not on a bad ref.
            var refsArg = BuildRefsArgument(findMethod, "vndb:v4");
            if (refsArg is null)
            {
                return MoyuCheckSupport.Missing(
                    Id, Title, expected, "FindPatchesAsync does not take an enumerable of moyu refs");
            }

            var invocationArgs = BuildInvocationArguments(findMethod, refsArg, cancellationToken);

            object? result;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var task = (Task)findMethod.Invoke(service, invocationArgs)!;
                await task.ConfigureAwait(false);
                result = task.GetType().GetProperty("Result")?.GetValue(task);
            }
            catch (Exception ex)
            {
                return CheckResult.Fail(Id, Title, expected,
                    $"FindPatchesAsync threw {ex.GetType().Name}: {ex.Message}").With(details.ToArray());
            }
            finally
            {
                stopwatch.Stop();
            }

            details.Add($"Elapsed            : {stopwatch.ElapsedMilliseconds} ms");

            if (result is null)
            {
                return CheckResult.Fail(Id, Title, expected,
                    "FindPatchesAsync returned null, which is not a distinguishable 'no key' answer")
                    .With(details.ToArray());
            }

            var failed = (bool?)MoyuCheckSupport.ReadProperty(result, "Failed") ?? false;
            var failure = MoyuCheckSupport.ReadProperty(result, "Failure");
            var code = failure is null ? "(absent)" : MoyuCheckSupport.DescribeEnumProperty(failure, "Code");
            var message = failure is null ? "(absent)" : MoyuCheckSupport.DescribeProperty(failure, "Message");

            details.Add($"Result.Failed      : {failed}");
            details.Add($"Result.Failure.Code: {code}");
            details.Add($"Result.Failure.Message: {message}");
            details.Add($"Result row count   : {MoyuCheckSupport.DescribeProperty(result, "Rows")}");

            var moyuTraffic = context.Traffic.Exchanges
                .Where(e => e.Url.Contains("nextmoe.dev", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            details.Add($"HTTP requests to nextmoe.dev during the call: {moyuTraffic.Length}");
            foreach (var exchange in moyuTraffic)
            {
                details.Add($"   {exchange.Method} {exchange.Url} -> {exchange.StatusCode}");
            }

            var problems = new List<string>();
            if (!failed)
            {
                problems.Add("the result is NOT a failure (Failed=false) - a missing key was swallowed");
            }

            if (!string.Equals(code, "NotConfigured", StringComparison.Ordinal))
            {
                problems.Add($"Failure.Code is \"{code}\", expected \"NotConfigured\"");
            }

            if (failure is null || string.IsNullOrWhiteSpace(message))
            {
                problems.Add("Failure.Message is empty - the user could not tell what to fix");
            }
            else if (!message.Contains("nmk_", StringComparison.OrdinalIgnoreCase)
                     && !message.Contains("密钥", StringComparison.Ordinal)
                     && !message.Contains("key", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"Failure.Message \"{message}\" does not mention the missing key");
            }

            if (moyuTraffic.Length > 0)
            {
                problems.Add($"{moyuTraffic.Length} request(s) were sent despite having no API key");
            }

            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                {
                    details.Add($"PROBLEM: {problem}");
                }

                return CheckResult.Fail(Id, Title, expected, string.Join("; ", problems))
                    .With(details.ToArray());
            }

            return CheckResult.Pass(Id, Title, expected,
                $"Failed=true, Code={code}, 0 HTTP request(s) sent, {stopwatch.ElapsedMilliseconds} ms")
                .With(details.ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(MoyuCheckSupport.KeyStorePathVariable, previousKeyStore);
            MoyuCheckSupport.TryDeleteDirectory(scratch);
            MoyuCheckSupport.TryDeleteDirectory(emptyKeyStoreDirectory);
        }
    }

    /// <summary>Builds the <c>IReadOnlyList&lt;MoyuRef&gt;</c> argument the method expects.</summary>
    private static object? BuildRefsArgument(System.Reflection.MethodInfo method, string refText)
    {
        var parameter = method.GetParameters().FirstOrDefault();
        if (parameter is null)
        {
            return null;
        }

        var elementType = parameter.ParameterType.IsGenericType
            ? parameter.ParameterType.GetGenericArguments()[0]
            : null;

        if (elementType is null)
        {
            return null;
        }

        var parse = elementType.GetMethod("Parse", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (parse is null)
        {
            return null;
        }

        var parsed = parse.Invoke(null, new object?[] { refText });
        if (parsed is null)
        {
            return null;
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        list.Add(parsed);
        return list;
    }

    /// <summary>Fills in the optional parameters with defaults so Invoke only needs what matters.</summary>
    private static object?[] BuildInvocationArguments(
        System.Reflection.MethodInfo method,
        object refs,
        CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (i == 0 && parameter.ParameterType.IsAssignableFrom(refs.GetType()))
            {
                args[i] = refs;
            }
            else if (parameter.ParameterType == typeof(CancellationToken))
            {
                args[i] = cancellationToken;
            }
            else
            {
                args[i] = parameter.HasDefaultValue ? parameter.DefaultValue : null;
            }
        }

        return args;
    }
}

/// <summary>
/// A62 - the <c>nmk_</c> key is sealed with DPAPI before it touches the disk, and the seal is a
/// real one: the plaintext must not appear anywhere in the stored bytes, and the value must
/// round-trip through <see cref="System.Security.Cryptography.DataProtectionScope.CurrentUser"/>.
/// </summary>
public sealed class A62MoyuKeyProtectionCheck : IAcceptanceCheck
{
    private const string ProbeKey = "nmk_live_A62DPAPIprobe_DO_NOT_USE_0000000000";

    /// <inheritdoc />
    public string Id => "A62";

    /// <inheritdoc />
    public string Title => "The nmk_ key is DPAPI-encrypted on disk and never stored in plaintext";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "Set stores an opaque blob that does not contain the plaintext; reload returns the "
            + "same secret; IsConfigured/fingerprint never leak it";

        var details = new List<string>();
        var scratch = MoyuCheckSupport.NewScratchDirectory("dpapi");
        var storePath = Path.Combine(scratch, "secrets", "moyu-api-key.bin");

        try
        {
            var storeType = Type.GetType(MoyuCheckSupport.DpapiKeyStoreTypeName);
            if (storeType is null)
            {
                return Task.FromResult(MoyuCheckSupport.Missing(
                    Id, Title, expected, "type Galbox.Core.Api.MoyuDpapiKeyStore is not present in Galbox.Core"));
            }

            var constructor = storeType.GetConstructor(new[] { typeof(string) })
                              ?? storeType.GetConstructor(Type.EmptyTypes);
            if (constructor is null)
            {
                return Task.FromResult(MoyuCheckSupport.Missing(
                    Id, Title, expected, "MoyuDpapiKeyStore has no usable constructor"));
            }

            var arguments = constructor.GetParameters().Length == 0
                ? Array.Empty<object?>()
                : new object?[] { storePath };

            var store = constructor.Invoke(arguments);
            var configuredBefore = (bool?)Invoke(store, "IsConfigured") ?? false;
            details.Add($"IsConfigured before Set : {configuredBefore}");

            Invoke(store, "Set", ProbeKey);

            if (!File.Exists(storePath))
            {
                return Task.FromResult(CheckResult.Fail(Id, Title, expected,
                    $"Set did not create {storePath}").With(details.ToArray()));
            }

            var rawBytes = File.ReadAllBytes(storePath);
            details.Add($"Ciphertext file        : {storePath}");
            details.Add($"Ciphertext length      : {rawBytes.Length} bytes");
            details.Add($"First 16 bytes (hex)   : {Convert.ToHexString(rawBytes.AsSpan(0, Math.Min(16, rawBytes.Length)))}");

            var asText = Encoding.UTF8.GetString(rawBytes);
            var plaintext_leaked = asText.Contains(ProbeKey, StringComparison.Ordinal)
                                   || ContainsBytes(rawBytes, Encoding.UTF8.GetBytes(ProbeKey))
                                   || ContainsBytes(rawBytes, Encoding.Unicode.GetBytes(ProbeKey));

            details.Add($"Plaintext present in raw file : {plaintext_leaked}");

            // Cross-check with .NET's own DPAPI so the claim is not just "the file looks random".
            var dpapiReadable = false;
            try
            {
                var unprotect = System.Security.Cryptography.ProtectedData.Unprotect(
                    rawBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                dpapiReadable = Encoding.UTF8.GetString(unprotect).Contains(ProbeKey, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                details.Add($"ProtectedData.Unprotect on the file threw: {ex.GetType().Name}");
            }

            details.Add($"File is decryptable by DPAPI (CurrentUser) : {dpapiReadable}");

            var configuredAfter = (bool?)Invoke(store, "IsConfigured") ?? false;
            details.Add($"IsConfigured after Set  : {configuredAfter}");

            // Reload from a brand-new instance to prove the value round-trips through the disk.
            var reloaded = constructor.Invoke(arguments);
            var readBack = (string?)Invoke(reloaded, "Get");
            details.Add($"Get() after reload     : {(string.Equals(readBack, ProbeKey, StringComparison.Ordinal) ? "(matches the key that was set)" : "(MISMATCH)")}");

            // The diagnostic surface must describe the key without revealing it.
            var fingerprint = Invoke(reloaded, "GetFingerprint") as string
                              ?? MoyuCheckSupport.ReadProperty(reloaded, "Fingerprint") as string;
            details.Add($"Fingerprint            : {fingerprint ?? "(absent)"}");

            var problems = new List<string>();
            if (configuredBefore)
            {
                problems.Add("the throw-away store reported IsConfigured=true before anything was stored");
            }

            if (plaintext_leaked)
            {
                problems.Add("the plaintext key is present in the stored file");
            }

            if (!dpapiReadable)
            {
                problems.Add("the stored blob is not a DPAPI(CurrentUser) ciphertext of the key");
            }

            if (!configuredAfter || !string.Equals(readBack, ProbeKey, StringComparison.Ordinal))
            {
                problems.Add("the key did not round-trip through Set/Get");
            }

            if (!string.IsNullOrEmpty(fingerprint) && fingerprint.Contains(ProbeKey, StringComparison.Ordinal))
            {
                problems.Add("the fingerprint contains the plaintext key");
            }

            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                {
                    details.Add($"PROBLEM: {problem}");
                }

                return Task.FromResult(CheckResult.Fail(Id, Title, expected, string.Join("; ", problems))
                    .With(details.ToArray()));
            }

            return Task.FromResult(CheckResult.Pass(Id, Title, expected,
                $"ciphertext {rawBytes.Length} bytes, plaintext absent from the file, DPAPI(CurrentUser) round-trip OK, "
                + "fingerprint carries no secret")
                .With(details.ToArray()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CheckResult.Error(Id, Title, expected,
                $"unhandled {ex.GetType().Name}", ex).With(details.ToArray()));
        }
        finally
        {
            MoyuCheckSupport.TryDeleteDirectory(scratch);
        }
    }

    /// <summary>
    /// Invokes a method on an instance by name. The exact parameter types are looked up first and
    /// a name-plus-arity match is used as the fallback, so this keeps working if a signature
    /// gains an optional parameter.
    /// </summary>
    private static object? Invoke(object instance, string methodName, params object?[] args)
    {
        var candidates = instance.GetType().GetMethods().Where(m => m.Name == methodName).ToArray();

        var method = candidates.FirstOrDefault(m =>
        {
            var parameters = m.GetParameters();
            if (parameters.Length != args.Length)
            {
                return false;
            }

            for (var i = 0; i < parameters.Length; i++)
            {
                if (args[i] is null)
                {
                    continue;
                }

                if (!parameters[i].ParameterType.IsInstanceOfType(args[i]))
                {
                    return false;
                }
            }

            return true;
        });

        method ??= candidates.FirstOrDefault(m => m.GetParameters().Length == args.Length);
        if (method is null)
        {
            return null;
        }

        return method.Invoke(instance, args);
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A63 - the compliance guard. <c>robots.txt</c> on moyu.moe carries <c>Disallow: /api</c> and
/// every usable first-party endpoint lives under <c>/api/v1/*</c>, so the client must be
/// structurally incapable of asking for one. This check turns that promise into an assertion.
/// </summary>
public sealed class A63MoyuComplianceGuardCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A63";

    /// <inheritdoc />
    public string Title => "No request path under /api is reachable (robots.txt Disallow: /api)";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "MoyuComplianceGuard rejects every /api/v1/* path and non-v2 api URLs, accepts the "
            + "/v2/moyu face, and no recorded moyu request targets /api";

        var details = new List<string>();

        var guardType = Type.GetType(MoyuCheckSupport.GuardTypeName);
        if (guardType is null)
        {
            return Task.FromResult(MoyuCheckSupport.Missing(
                Id, Title, expected, "type Galbox.Core.Api.MoyuComplianceGuard is not present in Galbox.Core"));
        }

        var uriMethod = guardType.GetMethod("IsAllowedApiUri", new[] { typeof(Uri) })
                        ?? guardType.GetMethod("ValidateApiUri", new[] { typeof(Uri) });
        if (uriMethod is null)
        {
            return Task.FromResult(MoyuCheckSupport.Missing(
                Id, Title, expected, "MoyuComplianceGuard exposes no IsAllowedApiUri(Uri) probe"));
        }

        var mustReject = new[]
        {
            "https://www.moyu.moe/api/v1/search?keywords=CLANNAD&type=resource&page=1&limit=3",
            "https://www.moyu.moe/api/v1/patch/86/resource",
            "https://www.moyu.moe/api/v1/patch/resource/223/link",
            "https://www.moyu.moe/api/v1/galgame?page=1",
            "https://www.moyu.moe/API/V1/search",
            "https://api.nextmoe.dev/api/v1/moyu/patches",
            "http://api.nextmoe.dev/v2/moyu/patches",
            "https://evil.example/v2/moyu/patches"
        };

        var mustAccept = new[]
        {
            "https://api.nextmoe.dev/v2/moyu/patches?refs=vndb%3Av4",
            "https://api.nextmoe.dev/v2/moyu/patches/86?include=resources",
            "https://api.nextmoe.dev/v2/moyu/patches/86/resources?limit=50",
            "https://api.nextmoe.dev/v2/moyu/resources/6262"
        };

        var problems = new List<string>();

        foreach (var url in mustReject)
        {
            var allowed = (bool?)uriMethod.Invoke(null, new object[] { new Uri(url) });
            details.Add($"REJECT expected : {(allowed == false ? "rejected" : "ALLOWED")}  {url}");
            if (allowed != false)
            {
                problems.Add($"the guard allows the forbidden URL {url}");
            }
        }

        foreach (var url in mustAccept)
        {
            var allowed = (bool?)uriMethod.Invoke(null, new object[] { new Uri(url) });
            details.Add($"ACCEPT expected : {(allowed == true ? "accepted" : "REJECTED")}  {url}");
            if (allowed != true)
            {
                problems.Add($"the guard wrongly rejects the legitimate URL {url}");
            }
        }

        // Every URI the application's own moyu pipeline actually produced during this run.
        var observed = context.Traffic.Exchanges
            .Where(e => e.Url.Contains("moyu", StringComparison.OrdinalIgnoreCase)
                        || e.Url.Contains("nextmoe.dev", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        details.Add(string.Empty);
        details.Add($"Recorded moyu/nextmoe requests this run: {observed.Length}");
        foreach (var exchange in observed)
        {
            details.Add($"   [{exchange.ClientName}] {exchange.Method} {exchange.Url} -> {exchange.StatusCode}");
        }

        foreach (var exchange in observed)
        {
            if (Uri.TryCreate(exchange.Url, UriKind.Absolute, out var uri) && !IsAllowed(uriMethod, uri))
            {
                problems.Add($"an actual request left the allow-listed surface: {exchange.Method} {exchange.Url}");
            }
        }

        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                details.Add($"PROBLEM: {problem}");
            }

            return Task.FromResult(CheckResult.Fail(Id, Title, expected,
                $"{problems.Count} compliance violation(s): {string.Join("; ", problems)}")
                .With(details.ToArray()));
        }

        return Task.FromResult(CheckResult.Pass(Id, Title, expected,
            $"{mustReject.Length}/{mustReject.Length} forbidden paths rejected, "
            + $"{mustAccept.Length}/{mustAccept.Length} legitimate paths accepted, "
            + $"{observed.Length} recorded moyu request(s) all inside the allow-list")
            .With(details.ToArray()));
    }

    private static bool IsAllowed(System.Reflection.MethodInfo method, Uri uri)
        => (bool?)method.Invoke(null, new object[] { uri }) ?? false;
}

/// <summary>
/// A64 - the two pure parsers the online layer depends on: the binary-unit <c>size</c> string
/// (calibrated by the research report at <c>17.953 MB == 18825520</c> bytes) and the game anchor
/// (the public face only accepts <c>vndb:vXXXX</c> and <c>catalog:&lt;id&gt;</c>, never a Bangumi id).
/// </summary>
public sealed class A64MoyuSizeAndAnchorCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A64";

    /// <inheritdoc />
    public string Title => "size strings parse as binary units and game anchors accept only vndb:/catalog:";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "\"17.953 MB\" and \"17.953MB\" -> 18825520 bytes; junk -> 0 without throwing; "
            + "Parse accepts vndb:v4 and catalog:86 and refuses a bare Bangumi id";

        var details = new List<string>();

        var parserType = Type.GetType("Galbox.Core.Api.MoyuSize, Galbox.Core")
                         ?? Type.GetType("Galbox.Core.Api.MoyuSizeParser, Galbox.Core");

        var refType = Type.GetType("Galbox.Core.Api.MoyuRef, Galbox.Core");

        if (parserType is null)
        {
            return Task.FromResult(MoyuCheckSupport.Missing(
                Id, Title, expected, "no MoyuSize/MoyuSizeParser type in Galbox.Core"));
        }

        var tryParse = parserType.GetMethod("TryParseBytes", new[] { typeof(string), typeof(long).MakeByRefType() })
                       ?? parserType.GetMethod("ParseBytes", new[] { typeof(string) });

        if (tryParse is null)
        {
            return Task.FromResult(MoyuCheckSupport.Missing(
                Id, Title, expected, "the size parser exposes neither TryParseBytes(string, out long) nor ParseBytes(string)"));
        }

        var problems = new List<string>();
        var samples = new (string Raw, long Expected)[]
        {
            ("17.953 MB", 18825520L),
            ("17.953MB", 18825520L),
            ("823.82MB", 863819448L),
            ("1.5 GB", 1610612736L),
            ("0.571 MB", 598737L),
            ("512 B", 512L),
            ("2 KB", 2048L),
            ("", 0L),
            ("abc", 0L),
            ("17.953 MB extra", 0L),
            ("-5 MB", 0L),              // A negative size is meaningless; report 0, do not abs() it.
            ("1e3 MB", 0L)              // Scientific notation is not part of the contract.
        };

        foreach (var (raw, wanted) in samples)
        {
            var measured = CallParser(tryParse, raw, out var threw);
            var marker = threw || measured != wanted ? "   <-- MISMATCH" : string.Empty;
            details.Add($"parse(\"{raw}\") = {measured} (expected {wanted}){marker}{(threw ? " [threw]" : string.Empty)}");
            if (threw)
            {
                problems.Add($"parsing \"{raw}\" threw instead of returning 0");
            }
            else if (measured != wanted)
            {
                problems.Add($"parsing \"{raw}\" gave {measured}, expected {wanted}");
            }
        }

        // ---- Anchor parsing -----------------------------------------------------------
        if (refType is null)
        {
            problems.Add("type Galbox.Core.Api.MoyuRef is not present in Galbox.Core");
        }
        else
        {
            var parse = refType.GetMethod("Parse", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (parse is null)
            {
                problems.Add("MoyuRef has no static Parse(string)");
            }
            else
            {
                details.Add(string.Empty);
                var accepted = new[] { "vndb:v4", "vndb:v65869", "catalog:86", "catalog:61311" };
                var refused = new[] { "1", "86", "bangumi:1234", "vndb:", "vndb:abc", "catalog:", "", "v4" };

                foreach (var text in accepted)
                {
                    var parsed = parse.Invoke(null, new object?[] { text });
                    details.Add($"anchor \"{text}\" -> {(parsed is null ? "REJECTED" : $"accepted (Source={MoyuCheckSupport.DescribeProperty(parsed, "Source")}, ExternalId={MoyuCheckSupport.DescribeProperty(parsed, "ExternalId")})")}");
                    if (parsed is null)
                    {
                        problems.Add($"anchor \"{text}\" was refused but is legal on the public face");
                    }
                }

                foreach (var text in refused)
                {
                    var parsed = parse.Invoke(null, new object?[] { text });
                    details.Add($"anchor \"{text}\" -> {(parsed is null ? "rejected (correct)" : "ACCEPTED <-- should have been refused")}");
                    if (parsed is not null)
                    {
                        problems.Add($"anchor \"{text}\" was accepted but is not a legal ref");
                    }
                }
            }
        }

        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                details.Add($"PROBLEM: {problem}");
            }

            return Task.FromResult(CheckResult.Fail(Id, Title, expected,
                $"{problems.Count} parser problem(s): {string.Join("; ", problems)}").With(details.ToArray()));
        }

        return Task.FromResult(CheckResult.Pass(Id, Title, expected,
            $"{samples.Length}/{samples.Length} size samples correct, anchor accept/refuse matrix correct")
            .With(details.ToArray()));
    }

    private static long CallParser(System.Reflection.MethodInfo method, string raw, out bool threw)
    {
        threw = false;
        try
        {
            if (method.ReturnType == typeof(bool))
            {
                var args = new object?[] { raw, 0L };
                var ok = (bool)method.Invoke(null, args)!;
                return ok ? (long)args[1]! : 0L;
            }

            return (long)method.Invoke(null, new object?[] { raw })!;
        }
        catch (Exception ex)
        {
            threw = true;
            _ = ex;
            return -1L;
        }
    }
}

/// <summary>
/// A65 - the downloads-folder takeover. The compliant path cannot hand out a direct link, so the
/// flow is "open the browser, the user downloads it, Galbox adopts the file". This check drives
/// the watcher against a throw-away directory and a synthetic file: no site is contacted.
/// </summary>
public sealed class A65MoyuDownloadWatchCheck : IAcceptanceCheck
{
    /// <summary>Bytes of the synthetic "patch package" written by this check.</summary>
    private const int SyntheticFileBytes = 4096;

    /// <inheritdoc />
    public string Id => "A65";

    /// <inheritdoc />
    public string Title => "Downloads-folder watcher adopts a new file by size and gives up on a timeout";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "a file appearing in the watched folder is adopted (state=Found) once its size is "
            + "stable, and an unrelated timeout returns state=TimedOut without throwing";

        var details = new List<string>();

        var watcherType = Type.GetType(MoyuCheckSupport.WatcherTypeName);
        if (watcherType is null)
        {
            return MoyuCheckSupport.Missing(
                Id, Title, expected, "type Galbox.Core.Api.MoyuDownloadWatcher is not present in Galbox.Core");
        }

        var scratch = MoyuCheckSupport.NewScratchDirectory("watch");
        try
        {
            var watcher = Activator.CreateInstance(watcherType);
            if (watcher is null)
            {
                return MoyuCheckSupport.Missing(Id, Title, expected, "MoyuDownloadWatcher has no parameterless constructor");
            }

            var requestType = Type.GetType("Galbox.Core.Api.MoyuDownloadWatchRequest, Galbox.Core");
            var optionsType = Type.GetType("Galbox.Core.Api.MoyuDownloadWatchOptions, Galbox.Core");
            var watchMethod = watcherType.GetMethod("WatchAsync");
            if (requestType is null || watchMethod is null)
            {
                return MoyuCheckSupport.Missing(
                    Id, Title, expected, "MoyuDownloadWatchRequest / WatchAsync is missing");
            }

            // ---- Positive case: the file shows up after the watch has started ----------------
            var request = Activator.CreateInstance(requestType)!;
            requestType.GetProperty("DirectoryPath")?.SetValue(request, scratch);
            requestType.GetProperty("ExpectedSizeBytes")?.SetValue(request, (long)SyntheticFileBytes);
            requestType.GetProperty("PatchName")?.SetValue(request, "A65 synthetic patch");
            requestType.GetProperty("WebUrl")?.SetValue(request, "https://www.moyu.moe/patch/86/introduction");
            requestType.GetProperty("Timeout")?.SetValue(request, TimeSpan.FromSeconds(25));
            requestType.GetProperty("PollInterval")?.SetValue(request, TimeSpan.FromMilliseconds(200));

            if (optionsType is not null)
            {
                var options = Activator.CreateInstance(optionsType)!;
                optionsType.GetProperty("PollInterval")?.SetValue(options, TimeSpan.FromMilliseconds(200));
                optionsType.GetProperty("StabilityWindow")?.SetValue(options, TimeSpan.FromMilliseconds(300));
                requestType.GetProperty("Options")?.SetValue(request, options);
                details.Add("Watch options : PollInterval=200ms, StabilityWindow=300ms");
            }
            else
            {
                details.Add("Watch options : (type absent; relying on request-level PollInterval)");
            }

            details.Add($"Watched folder: {scratch}");
            details.Add($"Expected size : {SyntheticFileBytes} bytes");

            var payload = new byte[SyntheticFileBytes];
            Random.Shared.NextBytes(payload);
            var filePath = Path.Combine(scratch, "A65-synthetic-patch.rar");

            var writeTask = Task.Run(async () =>
            {
                await Task.Delay(600, CancellationToken.None).ConfigureAwait(false);
                await File.WriteAllBytesAsync(filePath, payload, CancellationToken.None).ConfigureAwait(false);
            });

            var stopwatch = Stopwatch.StartNew();
            object? outcome;
            try
            {
                var task = (Task)watchMethod.Invoke(
                    watcher, BuildWatchArguments(watchMethod, request, cancellationToken))!;
                await task.ConfigureAwait(false);
                outcome = task.GetType().GetProperty("Result")?.GetValue(task);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return CheckResult.Fail(Id, Title, expected,
                    $"WatchAsync threw {ex.GetType().Name}: {ex.Message}").With(details.ToArray());
            }

            stopwatch.Stop();
            await writeTask.ConfigureAwait(false);

            if (outcome is null)
            {
                return CheckResult.Fail(Id, Title, expected, "WatchAsync returned null").With(details.ToArray());
            }

            var state = MoyuCheckSupport.ReadProperty(outcome, "State")?.ToString() ?? "(absent)";
            var file = MoyuCheckSupport.ReadProperty(outcome, "File") as string;
            var reason = MoyuCheckSupport.ReadProperty(outcome, "Reason") as string;

            details.Add($"Outcome.State  : {state}");
            details.Add($"Outcome.File   : {file ?? "(none)"}");
            details.Add($"Outcome.Reason : {reason ?? "(none)"}");
            details.Add($"Elapsed        : {stopwatch.ElapsedMilliseconds} ms");

            var problems = new List<string>();
            if (!string.Equals(state, "Found", StringComparison.Ordinal))
            {
                problems.Add($"expected State=Found, measured \"{state}\"");
            }

            if (!string.Equals(file, filePath, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"expected File=\"{filePath}\", measured \"{file ?? "(none)"}\"");
            }

            // ---- Negative case: nothing appears, so the watch must time out cleanly ----------
            var timeoutDirectory = MoyuCheckSupport.NewScratchDirectory("watch-timeout");
            try
            {
                var timeoutRequest = Activator.CreateInstance(requestType)!;
                requestType.GetProperty("DirectoryPath")?.SetValue(timeoutRequest, timeoutDirectory);
                requestType.GetProperty("ExpectedSizeBytes")?.SetValue(timeoutRequest, 1024L * 1024 * 1024);
                requestType.GetProperty("PatchName")?.SetValue(timeoutRequest, "A65 timeout probe");
                requestType.GetProperty("Timeout")?.SetValue(timeoutRequest, TimeSpan.FromMilliseconds(700));
                requestType.GetProperty("PollInterval")?.SetValue(timeoutRequest, TimeSpan.FromMilliseconds(100));

                var timeoutStopwatch = Stopwatch.StartNew();
                var timeoutTask = (Task)watchMethod.Invoke(
                    watcher, BuildWatchArguments(watchMethod, timeoutRequest, cancellationToken))!;
                await timeoutTask.ConfigureAwait(false);
                timeoutStopwatch.Stop();

                var timeoutOutcome = timeoutTask.GetType().GetProperty("Result")?.GetValue(timeoutTask);
                var timeoutState = timeoutOutcome is null
                    ? "(absent)"
                    : MoyuCheckSupport.ReadProperty(timeoutOutcome, "State")?.ToString() ?? "(absent)";

                details.Add(string.Empty);
                details.Add($"Timeout probe  : State={timeoutState}, elapsed={timeoutStopwatch.ElapsedMilliseconds} ms");

                if (!string.Equals(timeoutState, "TimedOut", StringComparison.Ordinal))
                {
                    problems.Add($"a watch that saw nothing returned \"{timeoutState}\", expected \"TimedOut\"");
                }

                if (timeoutStopwatch.ElapsedMilliseconds > 15000)
                {
                    problems.Add($"the timeout was not honoured (took {timeoutStopwatch.ElapsedMilliseconds} ms for a 700 ms timeout)");
                }
            }
            finally
            {
                MoyuCheckSupport.TryDeleteDirectory(timeoutDirectory);
            }

            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                {
                    details.Add($"PROBLEM: {problem}");
                }

                return CheckResult.Fail(Id, Title, expected, string.Join("; ", problems)).With(details.ToArray());
            }

            return CheckResult.Pass(Id, Title, expected,
                $"adopted \"{Path.GetFileName(file!)}\" after {stopwatch.ElapsedMilliseconds} ms by size match; "
                + "the empty watch reported TimedOut")
                .With(details.ToArray());
        }
        finally
        {
            MoyuCheckSupport.TryDeleteDirectory(scratch);
        }
    }

    private static object?[] BuildWatchArguments(
        System.Reflection.MethodInfo method,
        object request,
        CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            if (i == 0)
            {
                args[i] = request;
            }
            else if (parameters[i].ParameterType == typeof(CancellationToken))
            {
                args[i] = cancellationToken;
            }
            else if (parameters[i].ParameterType.IsGenericType
                     && parameters[i].ParameterType.GetGenericTypeDefinition() == typeof(IProgress<>))
            {
                args[i] = null;
            }
            else
            {
                args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
            }
        }

        return args;
    }
}

/// <summary>
/// A66 - what gets handed to the shell. The browser hop is the compliant substitute for a direct
/// link, so the launcher must accept only a real moyu page over HTTPS and refuse anything that
/// points into the <c>Disallow</c>-ed API surface. This check never spawns a browser.
/// </summary>
public sealed class A66MoyuBrowserLaunchCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A66";

    /// <inheritdoc />
    public string Title => "The browser hop accepts only HTTPS moyu pages and refuses API URLs";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "ValidateWebUrl accepts https://www.moyu.moe/patch/… and rejects http, /api, other hosts; "
            + "a dry run reports the URL without starting a process";

        var details = new List<string>();

        var launcherType = Type.GetType(MoyuCheckSupport.LauncherTypeName);
        var guardType = Type.GetType(MoyuCheckSupport.GuardTypeName);
        if (launcherType is null && guardType is null)
        {
            return Task.FromResult(MoyuCheckSupport.Missing(
                Id, Title, expected, "neither MoyuBrowserLauncher nor MoyuComplianceGuard is present in Galbox.Core"));
        }

        var validate = launcherType?.GetMethod(
                           "ValidateWebUrl",
                           System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                           new[] { typeof(string) })
                       ?? guardType?.GetMethod(
                           "IsAllowedWebUrl",
                           System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                           new[] { typeof(string) });

        if (validate is null)
        {
            return Task.FromResult(MoyuCheckSupport.Missing(
                Id, Title, expected, "no public Url validator on MoyuBrowserLauncher / MoyuComplianceGuard"));
        }

        var accepts = new[]
        {
            "https://www.moyu.moe/patch/86/introduction",
            "https://www.moyu.moe/patch/223/resources",
            "https://moyu.moe/patch/86/introduction"
        };

        var refuses = new[]
        {
            "http://www.moyu.moe/patch/86/introduction",
            "https://www.moyu.moe/api/v1/patch/resource/223/link",
            "https://api.nextmoe.dev/v2/moyu/patches",
            "https://dl.imoe.uk/moyu/ec3ecb28.rar",
            "https://oss.moyu.moe/patch/243/file.rar",
            "https://evil.example/patch/86",
            "file:///C:/Windows/system32/calc.exe",
            "javascript:alert(1)",
            "not a url",
            ""
        };

        var problems = new List<string>();

        foreach (var url in accepts)
        {
            var ok = (bool?)validate.Invoke(null, new object?[] { url }) ?? false;
            details.Add($"ACCEPT expected: {(ok ? "accepted" : "REJECTED")}  {url}");
            if (!ok)
            {
                problems.Add($"the launcher refuses a legitimate patch page: {url}");
            }
        }

        foreach (var url in refuses)
        {
            var ok = (bool?)validate.Invoke(null, new object?[] { url }) ?? false;
            details.Add($"REJECT expected: {(ok ? "ACCEPTED" : "rejected")}  {(string.IsNullOrEmpty(url) ? "(empty string)" : url)}");
            if (ok)
            {
                problems.Add($"the launcher accepts a URL it must refuse: \"{url}\"");
            }
        }

        // ---- Dry run: the URL is reported, no process is started -------------------------
        if (launcherType is not null)
        {
            var launch = launcherType.GetMethod("Launch")
                         ?? launcherType.GetMethod("OpenAsync")
                         ?? launcherType.GetMethod("LaunchAsync");

            if (launch is null)
            {
                problems.Add("MoyuBrowserLauncher exposes neither Launch nor OpenAsync");
            }
            else
            {
                object? instance = null;
                var dryRunOptions = Type.GetType("Galbox.Core.Api.MoyuBrowserLauncherOptions, Galbox.Core");
                try
                {
                    if (dryRunOptions is not null)
                    {
                        var options = Activator.CreateInstance(dryRunOptions)!;
                        dryRunOptions.GetProperty("DryRun")?.SetValue(options, true);
                        instance = Activator.CreateInstance(launcherType, options);
                    }
                    else
                    {
                        instance = Activator.CreateInstance(launcherType);
                        // No options type: ask the launcher for its own options object and flip DryRun.
                        var liveOptions = launcherType.GetProperty("Options")?.GetValue(instance);
                        liveOptions?.GetType().GetProperty("DryRun")?.SetValue(liveOptions, true);
                    }
                }
                catch (Exception ex)
                {
                    problems.Add($"MoyuBrowserLauncher could not be constructed for a dry run: {ex.GetType().Name}: {ex.Message}");
                }

                if (instance is not null)
                {
                    try
                    {
                        var launchResult = launch.Invoke(
                            instance,
                            BuildLaunchArguments(launch, "https://www.moyu.moe/patch/86/introduction", cancellationToken));

                        if (launchResult is Task launchTask)
                        {
                            launchTask.GetAwaiter().GetResult();
                            launchResult = launchTask.GetType().GetProperty("Result")?.GetValue(launchTask);
                        }

                        var succeeded = launchResult is not null
                                        && ((bool?)MoyuCheckSupport.ReadProperty(launchResult, "Succeeded") ?? false);

                        details.Add($"Dry-run Launch      : Succeeded={succeeded}, "
                                    + $"ProcessId={MoyuCheckSupport.DescribeProperty(launchResult!, "ProcessId")}, "
                                    + $"Url={MoyuCheckSupport.DescribeProperty(launchResult!, "Url")}");

                        if (!succeeded)
                        {
                            problems.Add(
                                "a dry-run Launch of a legitimate page did not report success: "
                                + MoyuCheckSupport.DescribeProperty(launchResult!, "Message"));
                        }
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"a dry-run Launch threw {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }

        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                details.Add($"PROBLEM: {problem}");
            }

            return Task.FromResult(CheckResult.Fail(Id, Title, expected,
                $"{problems.Count} launcher problem(s): {string.Join("; ", problems)}").With(details.ToArray()));
        }

        return Task.FromResult(CheckResult.Pass(Id, Title, expected,
            $"{accepts.Length} legitimate page URL(s) accepted, {refuses.Length} dangerous URL(s) refused, dry run OK")
            .With(details.ToArray()));
    }

    private static object?[] BuildLaunchArguments(
        System.Reflection.MethodInfo method,
        string url,
        CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType == typeof(string))
            {
                args[i] = url;
            }
            else if (parameters[i].ParameterType == typeof(CancellationToken))
            {
                args[i] = cancellationToken;
            }
            else
            {
                args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
            }
        }

        return args;
    }
}
