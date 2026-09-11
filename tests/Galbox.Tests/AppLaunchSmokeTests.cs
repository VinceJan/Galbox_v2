using Galbox.App.Services;
using Galbox.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Galbox.Tests;

/// <summary>
/// The one test this project was missing: start the shipping executable and require that a real,
/// visible, owned-by-that-process top-level window appears.
///
/// Both of the severe startup defects found in this code base by hand had the same observable
/// signature - a live process with no window and no error - and both would have been caught by
/// this test within 30 seconds:
///
///   * the three window-subclass P/Invokes were declared against <c>user32.dll</c> although
///     <c>comctl32.dll</c> exports them, so the first call inside the <c>MainWindow</c> constructor
///     threw <c>EntryPointNotFoundException</c>;
///   * a <c>ConfigureAwait(false)</c> in the startup path resumed the continuation on a thread-pool
///     thread, so <c>new MainWindow()</c> threw <c>COMException 0x8001010E (RPC_E_WRONG_THREAD)</c>.
///
/// The verdict is built exclusively from state keyed by the launched process id, so it cannot be
/// influenced by another Galbox instance on the same machine. See
/// <see cref="Support.AppLaunchObserver"/> for that reasoning.
/// </summary>
public sealed class AppLaunchSmokeTests
{
    private readonly ITestOutputHelper _output;

    public AppLaunchSmokeTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// THE regression guard for "double-clicking the application does nothing": the built
    /// application must be startable and must own a usable top-level window.
    /// </summary>
    [Fact]
    public void Application_starts_and_shows_a_real_top_level_window()
    {
        var observations = AppLaunchObserver.Launch(
            AppLaunchObserver.DefaultWindowTimeout,
            AppLaunchObserver.DefaultSettlePeriod);

        _output.WriteLine(observations.Report());

        // ---------------------------------------------------------------- the binary is current
        Assert.False(
            observations.BuildIsStale,
            "The application binary is OLDER than the newest source file of src/Galbox.App, so it "
          + "cannot be evidence about the current sources. Rebuild before trusting this test "
          + "(dotnet build Galbox.sln -c Debug).\n" + observations.Report());

        Assert.False(
            observations.StartFailed,
            $"The application could not even be started: {observations.StartError}\n" + observations.Report());

        // ------------------------------------------------------- the process must not die first
        Assert.False(
            observations.ExitedBeforeWindow,
            "The application exited before any window appeared. This is the " +
            "'starts and immediately disappears' failure.\n" + observations.Report());

        // ------------------------------------------------------ a window must actually appear
        Assert.True(
            observations.CandidateWindow is not null,
            $"No window appeared within {AppLaunchObserver.DefaultWindowTimeout.TotalSeconds:F0} s. "
          + "The process is (or was) running, so the startup exception was swallowed: this is the "
          + "classic 'double-click does nothing' defect. Check that MainWindowHandle is non-zero "
          + "and that no exception is hidden in the shared startup log below.\n"
          + observations.Report());

        // ------------------------------------------- and it must still be there after settling
        Assert.False(
            observations.WindowsAfterSettle.Count == 0,
            "A window was observed during startup but no visible top-level window existed "
          + $"{AppLaunchObserver.DefaultSettlePeriod.TotalSeconds:F0} s later, so the window did "
          + "not survive the first frames.\n" + observations.Report());

        Assert.True(
            observations.AliveAfterSettle,
            "The application terminated within the settle period after showing its window.\n"
          + observations.Report());

        var window = observations.WindowsAfterSettle[0];

        // ---------------------------------- the window must be the application, not an error box
        // A window alone is NOT success. The startup failure handler raises its own top-most modal
        // message box, and accepting it would invert this test's verdict exactly when the
        // application failed to start.
        Assert.NotEqual(StartupDiagnostics.StartupFailureCaption, window.Title);

        Assert.False(
            string.IsNullOrWhiteSpace(window.Title),
            "The application window has no title, which means the expected window was not fully "
          + "realised.\n" + observations.Report());

        Assert.True(
            window.Width > 0 && window.Height > 0,
            $"The application window has no area ({window.Width}x{window.Height}), so nothing is "
          + "actually displayed to the user.\n" + observations.Report());

        Assert.Equal(0, observations.DialogCount);

        // ------------------------------- the startup sequence ran to completion for THIS process
        // The window handle is unique among live windows, so the "created and activated" log line
        // carrying this process's own handle can only have been written by this process - unlike
        // the rest of that shared log file.
        Assert.True(
            observations.LogConfirmsThisProcessActivatedItsWindow,
            "No startup-log line reports that THIS process activated its main window "
          + $"(hwnd 0x{observations.ObservedHandle.ToInt64():X}). A window exists, but the startup "
          + "sequence did not report reaching the activation step.\n" + observations.Report());
    }
}
