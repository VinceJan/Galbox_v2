using System.Runtime.InteropServices;
using System.Text;

namespace Galbox.Tests.Support;

/// <summary>
/// Thin Win32 window inspection layer.
///
/// The launch smoke test deliberately reads the window state through Win32 rather than through
/// <c>Process.MainWindowHandle</c> alone: both historical startup defects produced a process that
/// stayed alive with a working message loop and NO window, and <c>MainWindowHandle</c> alone has
/// process-wide semantics that a second Galbox instance could confuse. Every observation here is
/// filtered by process id, so the verdict can only ever describe the process this test started.
/// </summary>
internal static class NativeWindows
{
    private const uint GW_OWNER = 4;

    /// <summary>One window observed on the desktop.</summary>
    internal readonly record struct WindowInfo(
        IntPtr Handle,
        string ClassName,
        string Title,
        int Width,
        int Height)
    {
        /// <summary>Human readable one-line description used in failure messages.</summary>
        public override string ToString() =>
            $"hwnd=0x{Handle.ToInt64():X} class='{ClassName}' title='{Title}' size={Width}x{Height}";
    }

    /// <summary>A window plus the pid that owns it, used by the diagnostic scans.</summary>
    internal readonly record struct OwnedWindow(int ProcessId, WindowInfo Window);

    /// <summary>
    /// Enumerates the visible, unowned (i.e. real application) top-level windows of one process.
    ///
    /// Owned windows are skipped on purpose: a modal failure message box is owned by its parent
    /// and its presence must never be mistaken for the main window. That distinction is exactly
    /// what separates "the application started" from "the application reported that it could not
    /// start".
    /// </summary>
    internal static IReadOnlyList<WindowInfo> VisibleTopLevelWindows(int processId)
    {
        var found = new List<WindowInfo>();

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var ownerPid);
            if (ownerPid != (uint)processId)
            {
                return true;
            }

            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero || !IsWindowVisible(hWnd))
            {
                return true;
            }

            found.Add(Describe(hWnd));
            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// Enumerates every window of one process, including invisible and owned ones. Used only to
    /// build the failure report: when no window appears the diagnostic value is in seeing that the
    /// process owns nothing at all, or exactly one modal dialog.
    /// </summary>
    internal static IReadOnlyList<OwnedWindow> AllWindows(int processId)
    {
        var found = new List<OwnedWindow>();

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var ownerPid);
            if (ownerPid == (uint)processId)
            {
                var owner = GetWindow(hWnd, GW_OWNER);
                var info = Describe(hWnd);
                found.Add(new OwnedWindow(processId, info with
                {
                    ClassName = owner == IntPtr.Zero
                        ? info.ClassName
                        : $"{info.ClassName} (owned by 0x{owner.ToInt64():X})"
                }));
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>Reads class name, title and size for one window; never throws.</summary>
    internal static WindowInfo Describe(IntPtr hWnd)
    {
        var width = 0;
        var height = 0;

        if (GetWindowRect(hWnd, out var rect))
        {
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
        }

        return new WindowInfo(hWnd, GetClassNameSafe(hWnd), GetTitleSafe(hWnd), width, height);
    }

    /// <summary>Counts modal dialogs (<c>#32770</c>) owned by the process, i.e. crash/failure boxes.</summary>
    internal static int DialogCount(int processId) =>
        AllWindows(processId).Count(w => w.Window.ClassName.StartsWith("#32770", StringComparison.Ordinal));

    /// <summary>Reads the window class name; never throws.</summary>
    internal static string GetClassNameSafe(IntPtr hWnd)
    {
        try
        {
            var buffer = new StringBuilder(256);
            var length = GetClassNameW(hWnd, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString(0, length) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Reads the window title; never throws.</summary>
    internal static string GetTitleSafe(IntPtr hWnd)
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder className, int maxCount);
}
