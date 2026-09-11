using Galbox.App.Services;
using Galbox.Data.Entities;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A3 - <see cref="ISaveManagementService.DetectSaveLocationAsync"/> for the persisted
/// Ren'Py game. Every field of the returned <see cref="SaveLocationResult"/> is printed so a
/// failure can be diagnosed from the report alone.
///
/// The real game stores its saves in <c>&lt;install&gt;\game\saves</c> (12 <c>*.save</c>
/// files plus <c>persistent</c>), which the Ren'Py branch exposes through
/// <see cref="SaveLocationResult.AlternativePaths"/>, and only promotes to
/// <see cref="SaveLocationResult.PrimarySavePath"/> when a matching folder also exists under
/// %APPDATA%. The check therefore accepts either location, but requires the path to end in
/// <c>saves</c> and the save-file list to be non-empty.
/// </summary>
public sealed class A3SaveLocationCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A3";

    /// <inheritdoc />
    public string Title => "DetectSaveLocationAsync finds the Ren'Py save folder";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var game = context.PersistedGame ?? A0DatabaseCheck.BuildTestGame(context.Options);
        var saves = context.Get<ISaveManagementService>();
        var details = new List<string>();

        var expected = $"Success == true, EngineType == {context.Options.ExpectedEngine}, "
                     + "a detected path ending in 'saves', and a non-empty save-file list";

        details.Add($"GameInfo.Id        : {game.Id}");
        details.Add($"GameInfo.NameCn    : {game.NameCn ?? "(null)"}");
        details.Add($"GameInfo.NameOriginal : {game.NameOriginal}");
        details.Add($"GameInfo.InstallPath  : {game.InstallPath}");
        details.Add($"GameInfo.MainExecutable: {game.MainExecutable}");
        details.Add($"GameInfo.EngineType (stored, pre-detection) : {game.EngineType}");
        details.Add($"Expected engine    : {context.Options.ExpectedEngine}");

        // What the detector is going to compare the folder name against.
        var expectedFolderKey = Path.GetFileName(game.InstallPath.TrimEnd(Path.DirectorySeparatorChar));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        details.Add($"Detector folder key: {expectedFolderKey}");
        details.Add($"AppData Roaming    : {appData}");

        var appDataMatches = Array.Empty<string>();
        try
        {
            appDataMatches = Directory.GetDirectories(appData)
                .Where(d => Path.GetFileName(d).Equals(expectedFolderKey, StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileName(d).StartsWith(expectedFolderKey + "-", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception ex)
        {
            details.Add($"AppData enumeration failed: {ex.Message}");
        }

        details.Add($"AppData folders matching '{expectedFolderKey}' / '{expectedFolderKey}-*' : {appDataMatches.Length}");
        foreach (var match in appDataMatches)
        {
            var candidate = Path.Combine(match, "saves");
            details.Add($"  {match}  (has 'saves' subfolder: {Directory.Exists(candidate)})");
        }

        var gameSavesFolder = Path.Combine(game.InstallPath, "game", "saves");
        details.Add($"game\\saves folder exists            : {Directory.Exists(gameSavesFolder)}");
        if (Directory.Exists(gameSavesFolder))
        {
            details.Add($"game\\saves *.save file count        : {Directory.GetFiles(gameSavesFolder, "*.save").Length}");
        }

        // --- The call under test -------------------------------------------------------
        var result = await saves.DetectSaveLocationAsync(game, cancellationToken).ConfigureAwait(false);

        details.Add("--- DetectSaveLocationAsync result (raw) ---");
        details.Add($"Success            : {result.Success}");
        details.Add($"EngineType         : {result.EngineType}");
        details.Add($"PrimarySavePath    : {result.PrimarySavePath ?? "(null)"}");
        details.Add($"AlternativePaths   : {result.AlternativePaths.Count}");
        for (var i = 0; i < result.AlternativePaths.Count; i++)
        {
            details.Add($"  alt[{i}]          : {result.AlternativePaths[i]} (exists: {Directory.Exists(result.AlternativePaths[i])})");
        }

        details.Add($"SaveFiles          : {result.SaveFiles.Count}");
        foreach (var saveFile in result.SaveFiles.Take(20))
        {
            details.Add($"  {saveFile}");
        }

        if (result.SaveFiles.Count > 20)
        {
            details.Add($"  ... {result.SaveFiles.Count - 20} more");
        }

        details.Add($"ErrorMessage       : {result.ErrorMessage ?? "(null)"}");
        details.Add($"ExtendedInfo       : {result.ExtendedInfo.Count} entries");
        foreach (var pair in result.ExtendedInfo)
        {
            details.Add($"  {pair.Key} = {pair.Value}");
        }

        // --- Verdict -------------------------------------------------------------------
        if (!result.Success)
        {
            details.Add("FAIL REASON: Success == false. The service reported no usable save location.");
            return CheckResult.Fail(Id, Title, expected, $"Success=false, ErrorMessage={result.ErrorMessage ?? "(null)"}")
                .With(details.ToArray());
        }

        if (result.EngineType != context.Options.ExpectedEngine)
        {
            details.Add($"FAIL REASON: EngineType was {result.EngineType}, expected {context.Options.ExpectedEngine}.");
            return CheckResult.Fail(Id, Title, expected, $"EngineType={result.EngineType}")
                .With(details.ToArray());
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.PrimarySavePath))
        {
            candidates.Add(result.PrimarySavePath!);
        }

        candidates.AddRange(result.AlternativePaths);

        var pointsAtSaves = candidates.Any(p =>
            Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar))
                .Equals("saves", StringComparison.OrdinalIgnoreCase));

        if (!pointsAtSaves)
        {
            details.Add("FAIL REASON: none of the reported paths ends in 'saves'.");
            return CheckResult.Fail(Id, Title, expected, $"paths={string.Join(" | ", candidates)}")
                .With(details.ToArray());
        }

        if (result.SaveFiles.Count == 0)
        {
            details.Add("FAIL REASON: save-file list is empty even though a saves folder was found.");
            return CheckResult.Fail(Id, Title, expected, "SaveFiles=0").With(details.ToArray());
        }

        var actual = $"Success=true, EngineType={result.EngineType}, "
                   + $"PrimarySavePath={result.PrimarySavePath ?? "(null)"}, "
                   + $"AlternativePaths={result.AlternativePaths.Count}, SaveFiles={result.SaveFiles.Count}";

        return CheckResult.Pass(Id, Title, expected, actual).With(details.ToArray());
    }
}
