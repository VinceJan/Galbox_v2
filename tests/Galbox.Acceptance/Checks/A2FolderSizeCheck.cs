using Galbox.App.Services;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A2 - <see cref="IGameUtilityService.CalculateFolderSize"/> against the real ~1 GB game.
///
/// The service swallows <see cref="UnauthorizedAccessException"/> and returns 0, so a plain
/// "greater than 900 MB" test would not distinguish "works" from "silently failed". An
/// independent recursive walk is therefore measured in parallel and the deviation between
/// the two is reported, which makes a silently truncated enumeration visible.
/// </summary>
public sealed class A2FolderSizeCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A2";

    /// <inheritdoc />
    public string Title => "CalculateFolderSize reports the real folder size";

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var folder = context.Options.GameFolder;
        var utility = context.Get<IGameUtilityService>();
        var details = new List<string>();

        var threshold = context.Options.MinimumFolderSizeBytes;
        var expected = $"> {FormatBytes(threshold)} ({threshold} bytes)";

        var measured = utility.CalculateFolderSize(folder);
        details.Add($"Folder             : {folder}");
        details.Add($"Service result     : {measured} bytes ({FormatBytes(measured)})");

        // Independent cross-check: explicit stack walk, no LINQ Sum, no swallowed errors.
        var (crossCheck, fileCount, crossCheckError) = ManualFolderSize(folder);
        details.Add($"Independent walk   : {crossCheck} bytes ({FormatBytes(crossCheck)}) over {fileCount} files");
        if (crossCheckError is not null)
        {
            details.Add($"Independent walk error: {crossCheckError}");
        }

        if (crossCheck > 0)
        {
            var delta = measured - crossCheck;
            var deltaPercent = Math.Round(delta * 100.0 / crossCheck, 4);
            details.Add($"Deviation          : {delta} bytes ({deltaPercent}%)");
            if (Math.Abs(deltaPercent) > 0.01)
            {
                details.Add("NOTE: the service and the independent walk disagree - the service may be");
                details.Add("      skipping files it cannot read, or counting hard links differently.");
            }
        }

        var pass = measured > threshold;

        var actual = $"{measured} bytes ({FormatBytes(measured)})";
        var checkResult = pass
            ? CheckResult.Pass(Id, Title, expected, actual)
            : CheckResult.Fail(Id, Title, expected, actual);

        return Task.FromResult(checkResult.With(details.ToArray()));
    }

    private static (long Total, int FileCount, string? Error) ManualFolderSize(string root)
    {
        long total = 0;
        var count = 0;
        var stack = new Stack<string>();
        stack.Push(root);

        try
        {
            while (stack.Count > 0)
            {
                var current = stack.Pop();

                foreach (var file in Directory.EnumerateFiles(current))
                {
                    total += new FileInfo(file).Length;
                    count++;
                }

                foreach (var dir in Directory.EnumerateDirectories(current))
                {
                    stack.Push(dir);
                }
            }
        }
        catch (Exception ex)
        {
            return (total, count, ex.Message);
        }

        return (total, count, null);
    }

    private static string FormatBytes(long bytes)
    {
        const double KB = 1024d;
        const double MB = KB * 1024;
        const double GB = MB * 1024;

        if (bytes >= GB) return $"{bytes / GB:F3} GB";
        if (bytes >= MB) return $"{bytes / MB:F2} MB";
        if (bytes >= KB) return $"{bytes / KB:F2} KB";
        return $"{bytes} B";
    }
}
