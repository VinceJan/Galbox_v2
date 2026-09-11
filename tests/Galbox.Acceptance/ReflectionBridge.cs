using System.Reflection;
using System.Threading.Tasks;

namespace Galbox.Acceptance;

/// <summary>
/// Reflection helpers used by the checks for features whose "before the fix" behaviour has to stay
/// measurable.
/// </summary>
/// <remarks>
/// A check for a brand-new feature cannot call it by name: the harness would stop compiling against
/// the revision that does not contain the feature yet, which is exactly the revision the check must
/// be able to FAIL against. Resolving the new types and members by name keeps one single harness
/// able to run against both revisions, so the "FAIL before / PASS after" evidence is produced by
/// the same code.
/// </remarks>
internal static class ReflectionBridge
{
    private const string AppAssemblyName = "Galbox.App";

    /// <summary>Resolves a type from the Galbox.App assembly, or null when it does not exist there.</summary>
    internal static Type? FindType(string fullName) => Type.GetType($"{fullName}, {AppAssemblyName}");

    /// <summary>Resolves a service from the container by interface name, or null when absent.</summary>
    internal static object? ResolveService(IServiceProvider services, string serviceTypeName)
    {
        var serviceType = FindType(serviceTypeName);
        return serviceType is null ? null : services.GetService(serviceType);
    }

    /// <summary>
    /// Invokes an instance method by name and awaits it when it returns a Task, giving back the
    /// Task's Result (or null for a non-generic Task).
    /// </summary>
    internal static async Task<(bool Ok, object? Result, string? Error)> CallAsync(
        object target,
        string methodName,
        params object?[] arguments)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(candidate => candidate.Name == methodName)
            .Where(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == arguments.Length && parameters.All(p => !p.IsOptional || true);
            })
            .OrderBy(candidate => candidate.GetParameters().Length)
            .FirstOrDefault();

        if (method is null)
        {
            return (false, null, $"method '{methodName}' with {arguments.Length} argument(s) not found on {target.GetType().FullName}");
        }

        // Trailing optional arguments (e.g. CancellationToken) may be omitted by the caller.
        var parameters = method.GetParameters();
        var invocationArguments = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            invocationArguments[i] = i < arguments.Length
                ? arguments[i]
                : parameters[i].HasDefaultValue
                    ? parameters[i].DefaultValue
                    : parameters[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(parameters[i].ParameterType)
                        : null;
        }

        try
        {
            var value = method.Invoke(target, invocationArguments);

            if (value is Task task)
            {
                await task.ConfigureAwait(false);

                var resultProperty = task.GetType().GetProperty("Result");
                return (true, resultProperty?.GetValue(task), null);
            }

            return (true, value, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return (false, null, ex.InnerException.ToString());
        }
        catch (Exception ex)
        {
            return (false, null, ex.ToString());
        }
    }

    /// <summary>Invokes a static method by name and awaits it when it returns a Task.</summary>
    internal static Task<(bool Ok, object? Result, string? Error)> CallStaticAsync(
        Type type,
        string methodName,
        params object?[] arguments)
    {
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(candidate => candidate.Name == methodName)
            .Where(candidate => candidate.GetParameters().Length == arguments.Length)
            .FirstOrDefault();

        if (method is null)
        {
            return Task.FromResult<(bool, object?, string?)>(
                (false, null, $"static method '{methodName}' with {arguments.Length} argument(s) not found on {type.FullName}"));
        }

        try
        {
            var value = method.Invoke(null, arguments);
            if (value is Task task)
            {
                return AwaitTaskAsync(task);
            }

            return Task.FromResult<(bool, object?, string?)>((true, value, null));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return Task.FromResult<(bool, object?, string?)>((false, null, ex.InnerException.ToString()));
        }
        catch (Exception ex)
        {
            return Task.FromResult<(bool, object?, string?)>((false, null, ex.ToString()));
        }
    }

    /// <summary>Invokes a private instance method (used to drive the restore verifier directly).</summary>
    internal static async Task<(bool Ok, object? Result, string? Error)> CallPrivateAsync(
        object target,
        string methodName,
        params object?[] arguments)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(candidate => candidate.Name == methodName)
            .Where(candidate => candidate.GetParameters().Length == arguments.Length)
            .FirstOrDefault();

        if (method is null)
        {
            return (false, null, $"private method '{methodName}' with {arguments.Length} argument(s) not found on {target.GetType().FullName}");
        }

        try
        {
            var value = method.Invoke(target, arguments);
            if (value is Task task)
            {
                await task.ConfigureAwait(false);
                var resultProperty = task.GetType().GetProperty("Result");
                return (true, resultProperty?.GetValue(task), null);
            }

            return (true, value, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return (false, null, ex.InnerException.ToString());
        }
        catch (Exception ex)
        {
            return (false, null, ex.ToString());
        }
    }

    private static async Task<(bool Ok, object? Result, string? Error)> AwaitTaskAsync(Task task)
    {
        await task.ConfigureAwait(false);
        var resultProperty = task.GetType().GetProperty("Result");
        return (true, resultProperty?.GetValue(task), null);
    }

    /// <summary>Reads a public property, returning null when it does not exist.</summary>
    internal static object? Property(object? instance, string name)
    {
        if (instance is null)
        {
            return null;
        }

        return instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance);
    }

    /// <summary>Reads a public string property.</summary>
    internal static string? String(object? instance, string name) => Property(instance, name) as string;

    /// <summary>Reads a public boolean property (false when absent).</summary>
    internal static bool Bool(object? instance, string name) => Property(instance, name) as bool? ?? false;

    /// <summary>Reads a public integer property (0 when absent).</summary>
    internal static int Int(object? instance, string name) => Property(instance, name) as int? ?? 0;
}
