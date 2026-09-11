using Galbox.App.Models;

namespace Galbox.App.Services;

/// <summary>
/// Repairs the game-health problems that can be repaired without asking the user to do anything.
/// </summary>
/// <remarks>
/// Only two of the eight diagnosis items qualify (see the classification in
/// <c>ErrorCheckingService</c> and the reasoning in <c>GameErrorInfo</c>):
/// <list type="bullet">
///   <item><b>路径含中文字符</b> - the game folder is renamed to an ASCII name and the database
///         follows in the same operation;</item>
///   <item><b>Windows 兼容性</b> - a per-executable compatibility layer is written to HKCU.</item>
/// </list>
/// Everything else needs software this application must not install silently (Locale Emulator, the
/// DirectX runtime, a codec pack, the VC++ redistributables), needs administrator rights, or is a
/// property of the machine rather than of the game. Those stay manual/external on purpose: a button
/// that cannot do what it says is worse than no button.
///
/// Every repair is <b>undoable</b> and <b>auditable</b>: it reports the value it replaced, and it
/// records what it did in an undo journal under <c>%LocalAppData%\Galbox\health-fixes</c> so that the
/// undo still works after the application is restarted.
/// </remarks>
public interface IGameHealthFixService
{
    /// <summary>True when <paramref name="category"/> has a real, implemented repair.</summary>
    bool CanAutoFix(ErrorCategory category);

    /// <summary>
    /// True when the executable already carries a per-application compatibility layer in HKCU.
    /// </summary>
    /// <remarks>
    /// Read-only companion of <see cref="ApplyWindowsCompatibilityModeAsync(int, CancellationToken)"/>,
    /// used by the diagnosis so that an already-repaired game stops being reported as a Critical
    /// problem. Without it the report page would show a "一键修复" button for something that is already
    /// fixed, and pressing it would repair nothing while telling the user it did.
    /// </remarks>
    bool IsCompatibilityModeApplied(string? executablePath);

    /// <summary>
    /// Renames the game folder so that its path no longer contains Chinese characters, and keeps the
    /// database in sync. Refuses while the game is running and rolls the rename back if the database
    /// cannot be updated.
    /// </summary>
    Task<GameHealthFixResult> FixChineseInstallPathAsync(int gameId, CancellationToken cancellationToken = default);

    /// <summary>Undoes <see cref="FixChineseInstallPathAsync"/> (renames the folder back).</summary>
    Task<GameHealthFixResult> RevertChineseInstallPathAsync(int gameId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the compatibility layer the diagnosis suggests for this game (Windows 7) into
    /// <c>HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers</c>.
    /// </summary>
    Task<GameHealthFixResult> ApplyWindowsCompatibilityModeAsync(int gameId, CancellationToken cancellationToken = default);

    /// <summary>Writes an explicitly chosen compatibility layer.</summary>
    Task<GameHealthFixResult> ApplyWindowsCompatibilityModeAsync(int gameId, string compatibilityMode, CancellationToken cancellationToken = default);

    /// <summary>Removes the compatibility layer again, restoring whatever value was there before.</summary>
    Task<GameHealthFixResult> RevertWindowsCompatibilityModeAsync(int gameId, CancellationToken cancellationToken = default);
}
