using System.Reflection;

namespace Galbox.Acceptance.Support;

/// <summary>
/// Binds to the patch centre ViewModel's presentation surface by member name.
///
/// Why reflection: these checks were written <b>before</b> the wiring existed, so a typed call would
/// not have compiled and the "fails because the feature is missing" baseline could never have been
/// recorded. Every <i>assertion</i> they make is still on a strongly typed engine object
/// (<c>OverwritePreview</c>, <c>PatchInstallResult</c>, ...) or on real bytes on disk; only the member
/// lookup goes through this binder, and a missing member is reported by name rather than by a
/// <c>NullReferenceException</c>.
/// </summary>
public sealed class PatchCenterProbe
{
    private readonly object _viewModel;

    /// <summary>Wraps a ViewModel instance.</summary>
    public PatchCenterProbe(object viewModel) => _viewModel = viewModel;

    /// <summary>Type of the wrapped ViewModel.</summary>
    public Type ViewModelType => _viewModel.GetType();

    /// <summary>Names of the members this probe could not find on the ViewModel.</summary>
    public List<string> Missing { get; } = new();

    /// <summary>True when every named member exists.</summary>
    public bool HasAll(params string[] memberNames)
        => memberNames.All(name =>
            ViewModelType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null ||
            ViewModelType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance) is not null);

    /// <summary>Reads a public property; records the name as missing when it does not exist.</summary>
    public object? Get(string propertyName)
    {
        var property = ViewModelType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
        {
            Missing.Add($"property {ViewModelType.Name}.{propertyName}");
            return null;
        }

        return property.GetValue(_viewModel);
    }

    /// <summary>Reads a property as <typeparamref name="T"/>; null when the member is absent.</summary>
    public T? Get<T>(string propertyName) where T : class => Get(propertyName) as T;

    /// <summary>Number of items in a collection property; -1 when the member is absent.</summary>
    public int Count(string collectionPropertyName)
    {
        var value = Get(collectionPropertyName);
        if (value is null)
        {
            if (!Missing.Contains($"property {ViewModelType.Name}.{collectionPropertyName}"))
            {
                Missing.Add($"property {ViewModelType.Name}.{collectionPropertyName}");
            }

            return -1;
        }

        var countProperty = value.GetType().GetProperty("Count");
        return countProperty?.GetValue(value) as int? ?? -1;
    }

    /// <summary>
    /// Invokes an async method by name. A missing method is recorded in <see cref="Missing"/> and
    /// surfaces as <c>null</c>, so the caller can produce a FAIL that names the missing member.
    /// </summary>
    public async Task<object?> CallAsync(string methodName, params object?[] arguments)
    {
        var method = ViewModelType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        if (method is null)
        {
            Missing.Add($"method {ViewModelType.Name}.{methodName}");
            return null;
        }

        object? result;
        try
        {
            result = method.Invoke(_viewModel, arguments);
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

    /// <summary>Calls a void method by name. Missing methods are recorded, not thrown.</summary>
    public void Call(string methodName, params object?[] arguments)
    {
        var method = ViewModelType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        if (method is null)
        {
            Missing.Add($"method {ViewModelType.Name}.{methodName}");
            return;
        }

        try
        {
            method.Invoke(_viewModel, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>Booleans read straight off the ViewModel, with the usual missing-member handling.</summary>
    public bool? GetBool(string propertyName) => Get(propertyName) as bool?;

    /// <summary>A printable summary of everything the probe could not find.</summary>
    public string MissingSummary() => Missing.Count == 0
        ? "(none)"
        : string.Join("; ", Missing.Distinct(StringComparer.Ordinal));
}
