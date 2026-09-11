using Galbox.App.Services;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A1 - <see cref="IGameUtilityService.FindExecutableInFolder"/> against the real game
/// folder. The folder contains both <c>dreaminher.exe</c> and <c>dreaminher-32.exe</c>, so
/// this check measures whether the heuristic picks the intended launcher.
///
/// Raw enumeration order is printed because the implementation returns
/// <c>gameExecutables.FirstOrDefault()</c>, which makes the OS directory order part of the
/// observable behaviour.
/// </summary>
public sealed class A1ExecutableCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A1";

    /// <inheritdoc />
    public string Title => "FindExecutableInFolder picks the intended launcher";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var folder = context.Options.GameFolder;
        var utility = context.Get<IGameUtilityService>();
        var details = new List<string>();

        var expected = "Path.GetFileName(result) == dreaminher.exe";
        details.Add($"Folder             : {folder}");
        details.Add($"Folder exists      : {Directory.Exists(folder)}");

        if (!Directory.Exists(folder))
        {
            return Task.FromResult(CheckResult.Fail(Id, Title, expected, "test game folder does not exist"));
        }

        // Diagnostics: the raw OS order the implementation iterates over.
        var rawOrder = Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly);
        details.Add($"*.exe found        : {rawOrder.Length}");
        for (var i = 0; i < rawOrder.Length; i++)
        {
            details.Add($"  raw[{i}] (OS order, first is what the service returns): {Path.GetFileName(rawOrder[i])}");
        }

        details.Add($"exe files (sorted) : {string.Join(", ", rawOrder.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal))}");

        var result = utility.FindExecutableInFolder(folder);
        var resultName = result is null ? "(null)" : Path.GetFileName(result);

        details.Add($"Service result     : {result ?? "(null)"}");
        details.Add($"Service result name: {resultName}");

        if (result is not null)
        {
            var info = new FileInfo(result);
            details.Add($"  exists           : {info.Exists}");
            details.Add($"  size             : {info.Length} bytes");
        }

        var pass = result is not null
                && string.Equals(resultName, "dreaminher.exe", StringComparison.OrdinalIgnoreCase);

        var checkResult = pass
            ? CheckResult.Pass(Id, Title, expected, resultName)
            : CheckResult.Fail(Id, Title, expected, resultName);

        return Task.FromResult(checkResult.With(details.ToArray()));
    }
}
