using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// The one rule every check in this suite follows before it starts the shipping application: the
/// window must be off every monitor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Four checks (A9, A18, A19, A50) start the real <c>Galbox.App.exe</c>, two of them
/// drive every navigation destination inside it and A50 switches the menu two hundred times back to
/// back. The suite is run repeatedly while several work lines build in parallel, and until this
/// guard existed each of those runs put a real window on the desktop of whoever was using the
/// machine - popping up, flashing, and paging through the product by itself.
/// </para>
/// <para>
/// <b>What it does not do.</b> It does not hide the window, minimise it or weaken a single
/// assertion. The application is asked (through
/// <see cref="Galbox.App.MainWindow.OffscreenWindowVariable"/>) to place its window at a large
/// negative coordinate. Win32 does not care where a window is: an off-screen window still reports
/// <c>IsWindowVisible = TRUE</c>, still has a non-zero <c>Process.MainWindowHandle</c>, still has its
/// title (<see cref="Measure"/> reads it), still appears under the UI Automation root, and still
/// loads and switches pages. Every existing verdict therefore keeps exactly the meaning it had - the
/// only thing that changes is that nobody can see it.
/// </para>
/// <para>
/// <b>It fails the check when the window would have been visible.</b> "Off-screen" is measured, not
/// assumed: <see cref="Measure"/> intersects the real <c>GetWindowRect</c> with every monitor
/// rectangle, and the checks refuse to pass when that intersection is non-empty. A silent regression
/// in the switch would otherwise turn every acceptance run into a flashing window again.
/// </para>
/// </remarks>
internal static class OffscreenWindow
{
    /// <summary>
    /// Name of the variable read by the application.
    /// </summary>
    /// <remarks>
    /// This is the application's own constant, not a copy of its text: the harness and the shipping
    /// executable cannot drift apart without the build failing. That matters because a typo here
    /// would silently put every GUI check back on the desktop.
    /// </remarks>
    internal const string Variable = Galbox.App.MainWindow.OffscreenWindowVariable;

    /// <summary>The only value that enables the switch.</summary>
    internal const string EnabledValue = Galbox.App.MainWindow.OffscreenWindowEnabledValue;

    /// <summary>
    /// Failure text used by every GUI check when its window turned out to be on a monitor.
    /// </summary>
    internal const string WouldBeVisibleReason =
        "the window this check started was ON a monitor, so the run would have flashed a real window "
        + "(and, in A19/A50, paged through the navigation menu) in front of whoever is using this "
        + "machine. The application is asked to open off-screen through " + Variable + "=" + EnabledValue
        + " and did not honour it - the acceptance suite is not allowed to disturb the developer's "
        + "desktop (docs/DEVELOPER-GUIDE.md).";

    /// <summary>
    /// Asks the application about to be started to open its window off-screen.
    /// </summary>
    /// <remarks>
    /// The value is written into this child's environment block, so it always wins over whatever the
    /// acceptance host itself was launched with.
    /// </remarks>
    internal static void Request(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.Environment[Variable] = EnabledValue;
    }

    /// <summary>
    /// Measures where a window really is: its screen rectangle, whether the shell considers it
    /// visible, and whether that rectangle touches any monitor.
    /// </summary>
    internal static WindowPlacement Measure(IntPtr windowHandle)
    {
        var monitors = EnumerateMonitors();

        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return new WindowPlacement(
                windowHandle,
                default,
                IsWindowVisible: false,
                IntersectsAnyMonitor: false,
                WindowTitle: string.Empty,
                MonitorSummary: Describe(monitors));
        }

        GetWindowRect(windowHandle, out var rect);

        return new WindowPlacement(
            windowHandle,
            rect,
            IsWindowVisible(windowHandle),
            IntersectsAnyMonitor: monitors.Any(monitor => Intersects(rect, monitor.Monitor)),
            WindowTitle: ReadWindowTitle(windowHandle),
            MonitorSummary: Describe(monitors));
    }

    /// <summary>Every monitor's full rectangle, as Win32 reports it.</summary>
    internal static List<MonitorBounds> EnumerateMonitors()
    {
        var monitors = new List<MonitorBounds>();

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
            {
                var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    monitors.Add(new MonitorBounds(info.rcMonitor, info.rcWork));
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // A failed enumeration leaves the list empty, which makes every placement look
            // off-screen. That would be a false pass, so the caller is told about it.
        }

        return monitors;
    }

    /// <summary>Two rectangles overlap (the standard Win32 intersection test).</summary>
    internal static bool Intersects(NativeRect a, NativeRect b)
        => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    private static string Describe(List<MonitorBounds> monitors)
        => monitors.Count == 0
            ? "(no monitor could be enumerated)"
            : string.Join(" | ", monitors.Select(m =>
                $"{Format(m.Monitor)} work={Format(m.Work)}"));

    private static string Format(NativeRect rect)
        => $"(left={rect.Left}, top={rect.Top}, right={rect.Right}, bottom={rect.Bottom})";

    private static string ReadWindowTitle(IntPtr hWnd)
    {
        try
        {
            var length = GetWindowTextLengthW(hWnd);
            if (length <= 0)
            {
                return string.Empty;
            }

            var buffer = new StringBuilder(length + 1);
            GetWindowTextW(hWnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW", SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}

/// <summary>A Win32 rectangle, in physical screen pixels.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;

    internal int Width => Right - Left;

    internal int Height => Bottom - Top;

    public override string ToString() => $"(left={Left}, top={Top}, right={Right}, bottom={Bottom})";
}

/// <summary>One monitor's full and work rectangles.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    internal int cbSize;
    internal NativeRect rcMonitor;
    internal NativeRect rcWork;
    internal uint dwFlags;
}

/// <summary>A monitor as enumerated by the guard.</summary>
internal sealed class MonitorBounds
{
    internal MonitorBounds(NativeRect monitor, NativeRect work)
    {
        Monitor = monitor;
        Work = work;
    }

    internal NativeRect Monitor { get; }

    internal NativeRect Work { get; }
}

/// <summary>
/// The measured placement of one application window: the facts the GUI checks report instead of
/// assuming that "off-screen" happened.
/// </summary>
internal sealed class WindowPlacement
{
    internal WindowPlacement(
        IntPtr handle,
        NativeRect rect,
        bool IsWindowVisible,
        bool IntersectsAnyMonitor,
        string WindowTitle,
        string MonitorSummary)
    {
        Handle = handle;
        Rect = rect;
        this.IsWindowVisible = IsWindowVisible;
        this.IntersectsAnyMonitor = IntersectsAnyMonitor;
        this.WindowTitle = WindowTitle;
        this.MonitorSummary = MonitorSummary;
    }

    /// <summary>The window handle the measurement was taken on.</summary>
    internal IntPtr Handle { get; }

    /// <summary>The real rectangle from <c>GetWindowRect</c>, in physical screen pixels.</summary>
    internal NativeRect Rect { get; }

    /// <summary>The value Win32 reports for the window; position does not affect it.</summary>
    internal bool IsWindowVisible { get; }

    /// <summary>Whether the window rectangle touches any monitor.</summary>
    internal bool IntersectsAnyMonitor { get; }

    /// <summary>The caption the shell shows for this window.</summary>
    internal string WindowTitle { get; }

    /// <summary>Every monitor the machine reported, so the verdict can be re-checked by hand.</summary>
    internal string MonitorSummary { get; }

    /// <summary>True when the window is entirely outside every monitor.</summary>
    internal bool IsOffscreen => !IntersectsAnyMonitor;

    /// <summary>
    /// The requirement every GUI check enforces: a real, visible window (the reason these checks
    /// start the application at all) that is nevertheless nowhere on the desktop.
    /// </summary>
    internal bool IsVisibleAndOffscreen => IsWindowVisible && IsOffscreen;

    internal string Describe() =>
        $"hwnd=0x{Handle.ToInt64():X}, GetWindowRect={Rect} [{Rect.Width}x{Rect.Height}], "
        + $"IsWindowVisible={IsWindowVisible}, intersects a monitor={IntersectsAnyMonitor} "
        + $"=> offscreen={IsOffscreen}";

    public override string ToString() => Describe();
}
