using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Galbox.Acceptance;

/// <summary>
/// Executes the ordered list of checks, prints one auditable block per check (expectation,
/// measured value, raw details, and any service WARNING/ERROR logged during that check) and
/// computes the process exit code: 0 only when every check passed.
/// </summary>
public static class CheckRunner
{
    private const int Width = 100;

    /// <summary>Runs every check in order and returns the process exit code.</summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<IAcceptanceCheck> checks,
        AcceptanceContext context,
        CancellationToken cancellationToken)
    {
        var results = new List<CheckResult>();

        foreach (var check in checks)
        {
            // Log capture is scoped per check so a service's own diagnostics can be
            // attributed to the check that provoked them.
            context.Logs.Clear();

            var stopwatch = Stopwatch.StartNew();
            CheckResult result;
            try
            {
                result = await check.RunAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = CheckResult.Error(check.Id, check.Title, "(not measured)",
                    "the acceptance run timed out", new TimeoutException("global acceptance timeout reached"));
            }
            catch (Exception ex)
            {
                result = CheckResult.Error(check.Id, check.Title, "(not measured)",
                    $"unhandled {ex.GetType().Name}", ex);
            }

            stopwatch.Stop();
            result.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            results.Add(result);

            PrintResult(result, context.Options.Verbose ? context.Logs.Records : SignificantLogs(context));
        }

        PrintSummary(results, context);

        var failed = results.Count(r => r.Status != CheckStatus.Pass);
        return failed == 0 ? 0 : 1;
    }

    private static IReadOnlyList<CollectingLoggerProvider.LogRecord> SignificantLogs(AcceptanceContext context) =>
        context.Logs.Records.Where(r => r.Level >= LogLevel.Warning).ToArray();

    private static void PrintResult(
        CheckResult result,
        IReadOnlyList<CollectingLoggerProvider.LogRecord> logs)
    {
        Console.WriteLine();
        Console.WriteLine(new string('#', Width));
        Console.WriteLine($"# [{result.Id}] {result.Title}");
        Console.WriteLine(new string('#', Width));
        Console.WriteLine($"EXPECTED : {result.Expected}");
        Console.WriteLine($"ACTUAL   : {result.Actual}");

        if (result.Details.Count > 0)
        {
            Console.WriteLine("DETAILS  :");
            foreach (var line in result.Details)
            {
                Console.WriteLine($"  {line}");
            }
        }

        if (result.Exception is not null)
        {
            Console.WriteLine("EXCEPTION:");
            foreach (var line in result.Exception.Split('\n'))
            {
                Console.WriteLine($"  {line.TrimEnd('\r')}");
            }
        }

        if (logs.Count > 0)
        {
            Console.WriteLine($"SERVICE LOG ({logs.Count} record(s), WARNING and above):");
            foreach (var record in logs)
            {
                Console.WriteLine($"  [{record.Level}] {record.Category}: {record.Message}");
                if (record.Exception is not null)
                {
                    foreach (var line in record.Exception.Split('\n').Take(12))
                    {
                        Console.WriteLine($"      {line.TrimEnd('\r')}");
                    }
                }
            }
        }

        Console.WriteLine($"RESULT   : {StatusText(result.Status)}  ({result.ElapsedMilliseconds} ms)");
    }

    private static void PrintSummary(IReadOnlyList<CheckResult> results, AcceptanceContext context)
    {
        var passed = results.Count(r => r.Status == CheckStatus.Pass);
        var failed = results.Count(r => r.Status == CheckStatus.Fail);
        var errors = results.Count(r => r.Status == CheckStatus.Error);

        Console.WriteLine();
        Console.WriteLine(new string('=', Width));
        Console.WriteLine(" ACCEPTANCE SUMMARY");
        Console.WriteLine(new string('=', Width));
        Console.WriteLine($" {"ID",-4} {"STATUS",-7} {"TIME",10}  CHECK");
        Console.WriteLine(new string('-', Width));

        foreach (var result in results)
        {
            Console.WriteLine($" {result.Id,-4} {StatusText(result.Status),-7} {result.ElapsedMilliseconds + " ms",10}  {result.Title}");
        }

        Console.WriteLine(new string('-', Width));
        Console.WriteLine($" PASSED : {passed}");
        Console.WriteLine($" FAILED : {failed}");
        Console.WriteLine($" ERRORS : {errors}");
        Console.WriteLine($" TOTAL  : {results.Count}");

        if (failed + errors > 0)
        {
            Console.WriteLine(" FAILING CHECKS:");
            foreach (var result in results.Where(r => r.Status != CheckStatus.Pass))
            {
                Console.WriteLine($"   {result.Id}: expected [{result.Expected}] but measured [{result.Actual}]");
            }
        }

        var exitCode = failed + errors == 0 ? 0 : 1;
        Console.WriteLine();
        Console.WriteLine($" EXIT CODE: {exitCode}  ({(exitCode == 0 ? "all checks PASS" : "at least one check did not PASS")})");
        Console.WriteLine($" DATABASE : {AcceptanceContainer.DatabasePath}");
        Console.WriteLine($" GAME     : {context.Options.GameFolder}");
        Console.WriteLine(new string('=', Width));
    }

    private static string StatusText(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "PASS",
        CheckStatus.Fail => "FAIL",
        _ => "ERROR"
    };
}
