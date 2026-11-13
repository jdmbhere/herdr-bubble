using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace HerdrBubble;

public sealed record TerminalWindowInfo(
    IntPtr Handle,
    string Title,
    string ProcessName,
    bool IsMinimized,
    bool IsHerdr);

public static class Win32WindowHelper
{
    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;
    private const int SW_SHOW = 5;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONULL = 0x00000000;
    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    public const int WM_HOTKEY = 0x0312;
    public const int ToggleHotkeyId = 0x4842;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_H = 0x48;

    private static readonly string[] TerminalProcessNames =
    {
        "WindowsTerminal",
        "cmd",
        "pwsh",
        "powershell",
        "conhost",
        "alacritty",
        "wezterm-gui",
        "mintty",
        "Hyper",
        "herdr"
    };

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// Returns the working areas of all active monitors in physical screen pixels.
    /// </summary>
    public static List<Rect> GetAllMonitorWorkAreasPixels()
    {
        var areas = new List<Rect>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                RECT w = info.rcWork;
                if (w.Right > w.Left && w.Bottom > w.Top)
                {
                    areas.Add(new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top));
                }
            }
            return true;
        }, IntPtr.Zero);

        return areas;
    }

    public static void EnsureTopmostNoActivate(IntPtr hWnd)
    {
        if (IsHandleValid(hWnd))
        {
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    public static Point GetCursorPosPixels()
    {
        return GetCursorPos(out POINT pt) ? new Point(pt.X, pt.Y) : new Point(0, 0);
    }

    public static Rect GetWindowRectPixels(IntPtr hWnd)
    {
        if (IsHandleValid(hWnd) && GetWindowRect(hWnd, out RECT r))
        {
            return new Rect(r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
        }

        return Rect.Empty;
    }

    public static void MoveWindowPixels(IntPtr hWnd, int x, int y)
    {
        if (IsHandleValid(hWnd))
        {
            SetWindowPos(hWnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    public static Rect GetPrimaryMonitorWorkAreaPixels()
    {
        IntPtr hMonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref info))
        {
            RECT w = info.rcWork;
            return new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
        }

        return SystemParameters.WorkArea;
    }

    public static Rect GetMonitorWorkAreaForPointPixels(double x, double y)
    {
        var pt = new POINT { X = (int)Math.Round(x), Y = (int)Math.Round(y) };
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref info))
        {
            RECT w = info.rcWork;
            return new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
        }

        return GetPrimaryMonitorWorkAreaPixels();
    }

    public static bool IsPointOnAnyMonitor(double x, double y)
    {
        var pt = new POINT { X = (int)Math.Round(x), Y = (int)Math.Round(y) };
        return MonitorFromPoint(pt, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }

    public static bool RegisterGlobalToggleHotkey(IntPtr hWnd)
    {
        return IsHandleValid(hWnd) && RegisterHotKey(hWnd, ToggleHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_H);
    }

    public static void UnregisterGlobalToggleHotkey(IntPtr hWnd)
    {
        if (IsHandleValid(hWnd))
        {
            UnregisterHotKey(hWnd, ToggleHotkeyId);
        }
    }

    public static List<TerminalWindowInfo> FindTerminalWindows(string? targetKeyword = null)
    {
        var results = new List<TerminalWindowInfo>();
        int selfPid = Environment.ProcessId;
        string keyword = string.IsNullOrWhiteSpace(targetKeyword) ? "herdr" : targetKeyword.Trim();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || IsWindowCloaked(hWnd) || IsToolWindow(hWnd))
            {
                return true;
            }

            if (!IsIconic(hWnd) && GetWindowRect(hWnd, out RECT rect))
            {
                if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0)
                {
                    return true;
                }
            }

            int length = GetWindowTextLength(hWnd);
            if (length <= 0)
            {
                return true;
            }

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == selfPid)
            {
                return true;
            }

            string procName = TryGetProcessName(pid);

            bool matchesProcess = TerminalProcessNames.Any(p =>
                string.Equals(p, procName, StringComparison.OrdinalIgnoreCase));
            bool isTargetMatch = string.Equals(procName, keyword, StringComparison.OrdinalIgnoreCase)
                || title.Contains(keyword, StringComparison.OrdinalIgnoreCase);

            if (matchesProcess || isTargetMatch)
            {
                results.Add(new TerminalWindowInfo(hWnd, title, procName, IsIconic(hWnd), isTargetMatch));
            }

            return true;
        }, IntPtr.Zero);

        return results
            .OrderByDescending(w => w.IsHerdr)
            .ThenByDescending(w => string.Equals(w.ProcessName, "WindowsTerminal", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => string.Equals(w.ProcessName, "cmd", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static bool IsHandleValid(IntPtr hWnd) => hWnd != IntPtr.Zero && IsWindow(hWnd);

    public static bool IsMinimized(IntPtr hWnd) => IsHandleValid(hWnd) && IsIconic(hWnd);

    public static bool IsForeground(IntPtr hWnd) => IsHandleValid(hWnd) && GetForegroundWindow() == hWnd;

    public static IntPtr GetForegroundHandle() => GetForegroundWindow();

    public static void MinimizeWindow(IntPtr hWnd)
    {
        if (IsHandleValid(hWnd))
        {
            ShowWindow(hWnd, SW_MINIMIZE);
        }
    }

    public static void BringToFront(IntPtr hWnd)
    {
        if (!IsHandleValid(hWnd))
        {
            return;
        }

        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
        }
        else
        {
            ShowWindow(hWnd, SW_SHOW);
        }

        IntPtr foreground = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(hWnd, out _);
        uint foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;

        bool attachedForeground = false;
        bool attachedTarget = false;

        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
            }
            if (targetThread != 0 && targetThread != currentThread)
            {
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);
            }

            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attachedTarget)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
            if (attachedForeground)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    public static void LaunchHerdrTerminal(string? launchCommand = null, string? windowTitle = null)
    {
        string cmd = string.IsNullOrWhiteSpace(launchCommand) ? "herdr" : launchCommand.Trim();
        string title = string.IsNullOrWhiteSpace(windowTitle) ? "Herdr" : windowTitle.Trim();

        if (ExistsOnPath("wt.exe"))
        {
            try
            {
                Process.Start(new ProcessStartInfo("wt.exe", $"--title \"{title}\" cmd /k {cmd}") { UseShellExecute = true });
                return;
            }
            catch
            {
                // Fall back to cmd.exe when Windows Terminal alias fails to start.
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k title {title} & {cmd}") { UseShellExecute = true });
        }
        catch
        {
            // Avoid crashing the floating bubble if terminal launch fails.
        }
    }

    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        try
        {
            int hr = DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
            return hr == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsToolWindow(IntPtr hWnd)
    {
        long exStyle = IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE).ToInt64()
            : GetWindowLong32(hWnd, GWL_EXSTYLE);
        return (exStyle & WS_EX_TOOLWINDOW) != 0;
    }

    private static string TryGetProcessName(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ExistsOnPath(string fileName)
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar))
        {
            return false;
        }

        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            string full = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(full))
            {
                return true;
            }
        }

        return false;
    }
}
