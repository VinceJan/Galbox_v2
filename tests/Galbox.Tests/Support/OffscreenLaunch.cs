using System.Diagnostics;

namespace Galbox.Tests.Support;

/// <summary>
/// Asks an application this project is about to start to open its window off every monitor.
/// </summary>
/// <remarks>
/// <para>
/// Same rule as the acceptance suite (see <c>docs/DEVELOPER-GUIDE.md</c> §6.1): a test that starts
/// the real <c>Galbox.App.exe</c> may not put a window on the desktop of whoever is using the
/// machine. <c>dotnet test</c> is a developer's everyday command, so this project disturbs people
/// far more often than the acceptance runner does.
/// </para>
/// <para>
/// It weakens nothing. The window is still real: <c>IsWindowVisible</c> stays TRUE,
/// <c>Process.MainWindowHandle</c> stays non-zero, <c>NativeWindows.VisibleTopLevelWindows</c> still
/// finds it (it filters on <c>GW_OWNER</c> and visibility, not on position), and UI Automation still
/// sees it as a child of the desktop root. Every assertion in
/// <see cref="AppLaunchSmokeTests"/> and <see cref="GalboxFunctionalTests"/> keeps its full meaning -
/// only the window's rectangle moves to (-32000, -32000).
/// </para>
/// <para>
/// The variable name and its accepted value come from the application itself
/// (<c>Galbox.App.MainWindow.OffscreenWindowVariable</c> / <c>OffscreenWindowEnabledValue</c>), never
/// from a copy pasted here, so the two sides cannot drift apart without the build failing.
/// </para>
/// </remarks>
internal static class OffscreenLaunch
{
    /// <summary>Applies the off-screen request to a launch this project is about to perform.</summary>
    internal static void Apply(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        startInfo.Environment[Galbox.App.MainWindow.OffscreenWindowVariable] =
            Galbox.App.MainWindow.OffscreenWindowEnabledValue;
    }
}
